using System.Globalization;
using System.Text;

namespace ClaudeHomeServer.Services.Llm;

// CPU (k10temp, один сокет): Tctl — главный датчик, Ccd — кристаллы (деталь плитки «Процессор»).
// Кристаллы — все temp* с меткой Tccd<k> в порядке k, подряд, без дыр: нумерация k у EPYC не
// сплошная (Tccd1, 2, 3, 5, 7, 8), номера файлов temp3/temp4 не зашиты.
public sealed record HwmonCpu
{
    public int? Tctl { get; init; }
    public int?[] Ccd { get; init; } = [];
}

// Плата (asusec): чипсет + VRM + вентилятор чипсета. «Процессор по датчику платы» (asusec CPU)
// не показывается (ADR-029 «Железо»): плитка CPU берёт Tctl из k10temp.
public sealed record HwmonBoard
{
    public int? Chipset { get; init; }
    // Температура материнской платы (метка Motherboard); null — датчика нет или он не подключён.
    public int? Motherboard { get; init; }
    public int? Vrm { get; init; }
    // Вентилятор чипсета, об/мин; null — датчика нет.
    public int? ChipsetFanRpm { get; init; }
    // Вода СЖО: датчик в резервуаре на разъёме T_SENSOR; null — не подключён.
    public int? Water { get; init; }
}

// Диск (nvme, по контроллеру): самый горячий датчик + дополнительный + предел.
// Role: "kv" — диск, смонтированный в kvdisk; "system" — остальные.
public sealed record HwmonDisk
{
    public string Controller { get; init; } = "";
    public string Role { get; init; } = "system";
    // Самый горячий из доступных датчиков (Composite и др.), °C; null — датчиков нет.
    public int? TempC { get; init; }
    // Дополнительный датчик (Sensor 2), °C; null — нет.
    public int? Temp2C { get; init; }
    // Предел (temp*_crit из hwmon, БЕЗ root); null — не сообщает (пороги 70/80).
    public int? CritC { get; init; }
    // Заполненность файловой системы, ГБ; null — не определена.
    public long? UsedGb { get; init; }
    public long? TotalGb { get; init; }
}

// Вентилятор (nct6798, только подключённые входы — rpm > 0).
public sealed record HwmonFan
{
    public string Label { get; init; } = "";
    public int Rpm { get; init; }
}

// Снимок hwmon: CPU по сокетам, плата, диски, вентиляторы корпуса. null-поля — датчика нет.
public sealed record HwmonSnapshot
{
    // Сокеты в порядке PCI-адреса (последний сегмент цели ссылки hwmonN/device), НЕ по номеру hwmonN;
    // null — k10temp нет. Индекс в массиве = номер сокета.
    public HwmonCpu[]? Cpus { get; init; }
    public HwmonBoard? Board { get; init; }
    public HwmonDisk[]? Disks { get; init; }
    public HwmonFan[]? CaseFans { get; init; }
    // Имена чипов платы, которых в hwmon оказалось несколько (берётся первый по порядку device);
    // предупреждение с троттлингом пишет вызывающий — у читателя логгера нет.
    public string[] AmbiguousChips { get; init; } = [];
}

// Читатель /sys/class/hwmon/* (без процессов и без root). Имена hwmon определяются по `name`
// и меткам, НЕ по номерам hwmonN (они плавают между загрузками). Корни подменяются в тестах:
// hwmonRoot (/sys/class/hwmon), mountinfoPath (/proc/self/mountinfo); DriveInfo — делегатом fsSize.
// Любой сбой чтения → null-поле (недоверенный источник, не роняем BackgroundService).
public sealed class HwmonReader
{
    // Источник файлов: диск (DiskFileSource, размер ФС — DriveInfo/подмена в тестах) или срез агента.
    private readonly ISysFileSource _fs;

    private const long GiB = 1024L * 1024 * 1024;

    private readonly string _hwmonRoot;
    private readonly string _mountinfoPath;

    public HwmonReader() : this("/sys/class/hwmon", "/sys/block", "/proc/self/mountinfo") { }

    // sysBlockRoot больше не используется (total — с той же ФС, не из /sys/block), но остаётся
    // в сигнатуре для стабильности; fsSize — подмена DriveInfo в тестах.
    public HwmonReader(string hwmonRoot, string sysBlockRoot, string mountinfoPath,
        Func<string, (long total, long free)>? fsSize = null)
    {
        _hwmonRoot = hwmonRoot;
        _mountinfoPath = mountinfoPath;
        _fs = new DiskFileSource(fsSize);
    }

    // Разбор по произвольному источнику файлов (срез агента); корни — стандартные.
    public HwmonReader(ISysFileSource source,
        string hwmonRoot = "/sys/class/hwmon", string mountinfoPath = "/proc/self/mountinfo")
    {
        _hwmonRoot = hwmonRoot;
        _mountinfoPath = mountinfoPath;
        _fs = source;
    }

    // Читает снимок. null — hwmonRoot не существует (чужая машина) — все поля null/пусто.
    public HwmonSnapshot? Read()
    {
        if (!_fs.Exists(_hwmonRoot))
            return null;

        var (kvController, kvFs) = FindKvDisk();
        var ambiguous = new List<string>();
        var snap = new HwmonSnapshot
        {
            Cpus = ReadCpus(),
            Board = ReadBoard(ambiguous),
            Disks = ReadDisks(kvController, kvFs),
            CaseFans = ReadCaseFans(ambiguous),
        };
        return snap with { AmbiguousChips = ambiguous.Distinct().ToArray() };
    }

    // ─── CPU (k10temp): Tctl и кристаллы по сокетам ────────────────────────────────

    // Все k10temp в порядке сокетов (см. OrderedHwmon); null — ни одного.
    private HwmonCpu[]? ReadCpus()
    {
        var dirs = OrderedHwmon("k10temp");
        if (dirs.Count == 0) return null;
        return dirs.Select(ReadCpu).ToArray();
    }

    private HwmonCpu ReadCpu(string dir)
    {
        int? tctl = null;
        var ccd = new List<(int k, int? temp)>();
        foreach (var f in EnumerateFiles(dir, "temp*_input"))
        {
            var label = ReadLabel(f);
            if (label is null) continue;
            if (label == "Tctl")
                tctl ??= ReadMilliC(f);
            else if (label.StartsWith("Tccd", StringComparison.Ordinal)
                     && int.TryParse(label.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out var k))
                ccd.Add((k, ReadMilliC(f)));
        }
        return new HwmonCpu { Tctl = tctl, Ccd = ccd.OrderBy(c => c.k).Select(c => c.temp).ToArray() };
    }

    // ─── Плата (asusec): Chipset, VRM, вентилятор чипсета ───────────────────────────

    private HwmonBoard? ReadBoard(List<string> ambiguous)
    {
        var dir = FindBoardChip("asusec", ambiguous);
        if (dir is null) return null;
        // Метки asusec: temp1=Chipset, temp2=CPU, temp3=Motherboard, temp5=VRM, fan2=Chipset.
        // Определяем по label, не по номеру (номера могут отличаться на другой плате).
        return new HwmonBoard
        {
            Chipset = ReadTempByLabel(dir, "Chipset"),
            Motherboard = ConnectedProbe(ReadTempByLabel(dir, "Motherboard")),
            Vrm = ReadTempByLabel(dir, "VRM"),
            ChipsetFanRpm = ReadFanByLabel(dir, "Chipset"),
            Water = ConnectedProbe(ReadTempByLabel(dir, "T_Sensor")),
        };
    }

    // Неподключённый выносной датчик asusec отдаёт −40 °C — это «нет датчика», а не мороз.
    private static int? ConnectedProbe(int? c) => c is > -30 ? c : null;

    // ─── Диски (nvme, по контроллеру): температура, датчики, предел, роль ───────────

    private HwmonDisk[]? ReadDisks(string? kvController, (long total, long free)? kvFs)
    {
        var dirs = FindAllHwmon("nvme");
        if (dirs.Count == 0) return null;

        var disks = new List<HwmonDisk>();
        foreach (var dir in dirs)
        {
            var controller = ResolveNvmeController(dir);
            if (controller is null) continue;

            // Все датчики (temp1..tempN): самый горячий → TempC, второй → Temp2C.
            var temps = ReadAllTemps(dir);
            int? hottest = null;
            int? second = null;
            foreach (var t in temps)
            {
                if (t is null) continue;
                if (hottest is null || t > hottest)
                {
                    second = hottest;
                    hottest = t;
                }
                else if (second is null || t > second)
                    second = t;
            }

            var isKv = controller == kvController;
            // kv — ФС, определившая роль (точка kvdisk); system — наибольшая ФС контроллера (не /boot/efi).
            // used/total — пара с ОДНОЙ файловой системы (не «занято» из одной ФС и «всего» из /sys/block).
            var fs = isKv ? kvFs : PickLargestFs(controller);
            long? usedGb = null;
            long? totalGb = null;
            if (fs is { } f)
            {
                usedGb = (f.total - f.free) / GiB;
                totalGb = f.total / GiB;
            }
            disks.Add(new HwmonDisk
            {
                Controller = controller,
                Role = isKv ? "kv" : "system",
                TempC = hottest,
                Temp2C = second,
                CritC = ReadCrit(dir),
                UsedGb = usedGb,
                TotalGb = totalGb,
            });
        }
        return disks.ToArray();
    }

    // Предел диска (БЕЗ root): temp*_max (WCTEMP контроллера), иначе temp*_crit; округление
    // до целого (83,85 → 84); null — не сообщает (пороги 70/80).
    private int? ReadCrit(string dir)
    {
        foreach (var pattern in new[] { "temp*_max", "temp*_crit" })
        {
            foreach (var f in EnumerateFiles(dir, pattern))
            {
                if (ReadInt(f) is { } milli && milli > 0)
                    return (int)Math.Round(milli / 1000.0);
            }
        }
        return null;
    }

    // ─── Вентиляторы корпуса (nct6798): только подключённые (rpm > 0) ──────────────

    private HwmonFan[]? ReadCaseFans(List<string> ambiguous)
    {
        var dir = FindBoardChip("nct6798", ambiguous);
        if (dir is null) return null;

        var fans = new List<HwmonFan>();
        foreach (var f in EnumerateFiles(dir, "fan*_input"))
        {
            var rpm = ReadInt(f);
            if (rpm is null || rpm <= 0) continue; // только подключённые входы
            // У nct6798 меток вентиляторов нет: подпись по номеру входа (fan2_input → «Вентилятор 2»).
            var label = ReadLabel(f) ?? "Вентилятор " + Path.GetFileName(f)[3..].Split('_')[0];
            fans.Add(new HwmonFan { Label = label, Rpm = rpm.Value });
        }
        return fans.ToArray();
    }

    // ─── Утилиты: hwmon по name, датчики, контроллер nvme, размер/занято диска ─────

    // Каталоги hwmon с заданным name в порядке сокетов: сначала чипы со ссылкой device — по
    // последнему сегменту цели (PCI-адрес, сравнение строкой), затем чипы без ссылки — по
    // возрастанию N. Номер hwmonN между загрузками плавает и порядком сокетов НЕ служит.
    private List<string> OrderedHwmon(string name)
    {
        var keyed = new List<(bool noLink, string device, int n, string dir)>();
        foreach (var dir in EnumerateHwmonDirs())
        {
            if (ReadName(dir) != name) continue;
            string? device = null;
            try
            {
                var target = _fs.LinkTarget(Path.Combine(dir, "device"));
                if (!string.IsNullOrEmpty(target))
                    device = target.TrimEnd('/').Split('/').Last();
            }
            catch { }
            keyed.Add((device is null, device ?? "", HwmonNumber(dir), dir));
        }
        return keyed
            .OrderBy(k => k.noLink)
            .ThenBy(k => k.device, StringComparer.Ordinal)
            .ThenBy(k => k.n)
            .Select(k => k.dir)
            .ToList();
    }

    // Номер из имени каталога hwmonN; не число — в конец (int.MaxValue).
    private static int HwmonNumber(string dir)
    {
        var leaf = Path.GetFileName(dir.TrimEnd('/'));
        return leaf.StartsWith("hwmon", StringComparison.Ordinal)
               && int.TryParse(leaf.AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            ? n : int.MaxValue;
    }

    // Чип платы: первый в порядке OrderedHwmon; если их несколько — имя уходит в ambiguous.
    private string? FindBoardChip(string name, List<string> ambiguous)
    {
        var dirs = OrderedHwmon(name);
        if (dirs.Count > 1) ambiguous.Add(name);
        return dirs.Count > 0 ? dirs[0] : null;
    }

    // Все каталоги hwmon с заданным name.
    private List<string> FindAllHwmon(string name)
    {
        var result = new List<string>();
        foreach (var dir in EnumerateHwmonDirs())
        {
            if (ReadName(dir) == name)
                result.Add(dir);
        }
        return result;
    }

    private List<string> EnumerateHwmonDirs()
    {
        var result = new List<string>();
        try
        {
            foreach (var dir in _fs.EnumerateDirectories(_hwmonRoot))
            {
                if (_fs.Exists(Path.Combine(dir, "name")))
                    result.Add(dir);
            }
        }
        catch { /* недоверенная ФС: не роняем */ }
        return result;
    }

    private string? ReadName(string dir)
    {
        return _fs.ReadText(Path.Combine(dir, "name"))?.Trim();
    }

    // Значение датчика temp*_input (милли-°C) → °C; null — нет/не число.
    private int? ReadTemp(string dir, string sensor)
    {
        var f = Path.Combine(dir, sensor + "_input");
        return ReadMilliC(f);
    }

    // Датчик по метке (temp*_label == label): ищет tempN, у которого label совпадает.
    private int? ReadTempByLabel(string dir, string label)
    {
        foreach (var f in EnumerateFiles(dir, "temp*_input"))
        {
            if (ReadLabel(f) == label)
                return ReadMilliC(f);
        }
        return null;
    }

    // Вентилятор по метке (fan*_label == label): об/мин; null — нет.
    private int? ReadFanByLabel(string dir, string label)
    {
        foreach (var f in EnumerateFiles(dir, "fan*_input"))
        {
            if (ReadLabel(f) == label)
                return ReadInt(f);
        }
        return null;
    }

    // Все датчики temp*_input (милли-°C → °C), в порядке номеров.
    private List<int?> ReadAllTemps(string dir)
    {
        var result = new List<int?>();
        foreach (var f in EnumerateFiles(dir, "temp*_input"))
            result.Add(ReadMilliC(f));
        return result;
    }

    // Метка датчика/вентилятора (temp*_label / fan*_label); null — нет метки.
    private string? ReadLabel(string inputPath)
    {
        var labelPath = inputPath.Replace("_input", "_label");
        return _fs.ReadText(labelPath)?.Trim();
    }

    // Файлы dir, имя которых совпадает с glob-подобным шаблоном (поддержка *), по возрастанию
    // числового суффикса (temp1, temp2, …; fan1, fan2, …).
    private IEnumerable<string> EnumerateFiles(string dir, string pattern)
    {
        var star = pattern.IndexOf('*');
        var prefix = pattern.Substring(0, star);
        var suffix = pattern.Substring(star + 1);
        var candidates = new List<(int num, string path)>();
        try
        {
            foreach (var f in _fs.EnumerateFiles(dir))
            {
                var name = Path.GetFileName(f);
                if (name.Length < prefix.Length + suffix.Length || !name.StartsWith(prefix) || !name.EndsWith(suffix))
                    continue;
                var mid = name.Substring(prefix.Length, name.Length - prefix.Length - suffix.Length);
                if (int.TryParse(mid, out var num))
                    candidates.Add((num, f));
            }
        }
        catch { }
        foreach (var (num, path) in candidates.OrderBy(x => x.num))
            yield return path;
    }

    // Контроллер nvme (nvme0/nvme1/…) по каталогу hwmon: через device-ссылку → nvmeX.
    private string? ResolveNvmeController(string dir)
    {
        try
        {
            var deviceLink = Path.Combine(dir, "device");
            var target = _fs.LinkTarget(deviceLink); // "../../nvmeX"
            if (target is null) return null;
            var name = Path.GetFileName(target); // "nvmeX"
            return name.StartsWith("nvme") ? name : null;
        }
        catch { return null; }
    }

    // kvdisk: контроллер, на котором он живёт, и заполненность ФС, смонтированной в
    // /home/an/llm/kvdisk. Оба — из ОДНОЙ строки mountinfo (одно правило — один источник):
    // точка монтирования → устройство /dev/nvmeXn1p1 → контроллер nvmeX. null — kvdisk не смонтирован.
    private (string? controller, (long total, long free)? fs) FindKvDisk()
    {
        try
        {
            foreach (var line in ReadMountinfoLines())
            {
                // Формат mountinfo: … <mountpoint> … - <fstype> <source> <opts>
                // Ищем mountpoint = /home/an/llm/kvdisk, source = /dev/nvmeXn1p1.
                var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length < 8) continue;
                var mountpoint = fields[4];
                if (mountpoint != "/home/an/llm/kvdisk") continue;
                var source = Path.GetFileName(fields[^2]); // nvmeXn1p1
                var m = System.Text.RegularExpressions.Regex.Match(source, @"^nvme(\d+)");
                if (!m.Success) return (null, null);
                return ("nvme" + m.Groups[1].Value, ReadFsUsage(mountpoint));
            }
        }
        catch { }
        return (null, null);
    }

    // Наибольшая ФС контроллера (среди смонтированных разделов nvmeXn1pY, не /boot/efi):
    // всего и свободно (байты); null — смонтированных ФС нет.
    private (long total, long free)? PickLargestFs(string controller)
    {
        (long total, long free)? best = null;
        try
        {
            foreach (var line in ReadMountinfoLines())
            {
                var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length < 8) continue;
                var source = Path.GetFileName(fields[^2]);
                if (!source.StartsWith(controller + "n")) continue;
                var mountpoint = fields[4];
                if (mountpoint == "/boot/efi") continue;
                var fs = ReadFsUsage(mountpoint);
                if (fs is null) continue;
                if (best is null || fs.Value.total > best.Value.total)
                    best = fs;
            }
        }
        catch { }
        return best;
    }

    // Заполненность ФС (statvfs точки монтирования): всего и свободно (байты); null — не читается
    // или значения противоречивы: free вне [0, total] — недоверенный источник (срез агента),
    // из него UsedGb = total − free дал бы отрицательное или больше total.
    private (long total, long free)? ReadFsUsage(string mountpoint)
    {
        try
        {
            var (total, free) = _fs.FsSize(mountpoint);
            if (total <= 0) return null;
            if (free < 0 || free > total) return null;
            return (total, free);
        }
        catch { return null; }
    }

    // ─── Чтение чисел ──────────────────────────────────────────────────────────────

    private int? ReadInt(string path)
    {
        var text = _fs.ReadText(path);
        if (text is null) return null;
        return int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    // Строки mountinfo; нет файла — пусто (как ReadAllLines: разделители \n и \r\n).
    private IEnumerable<string> ReadMountinfoLines() =>
        _fs.ReadText(_mountinfoPath)?.Split('\n').Select(l => l.TrimEnd('\r')) ?? [];

    // милли-°C → °C.
    private int? ReadMilliC(string path)
    {
        var milli = ReadInt(path);
        return milli is null ? null : milli / 1000;
    }
}
