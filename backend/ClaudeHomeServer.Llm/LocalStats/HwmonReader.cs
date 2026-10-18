using System.Globalization;
using System.Text;

namespace ClaudeHomeServer.Services.Llm;

// CPU (k10temp): Tctl — главный датчик, Tccd1/Tccd2 — кристаллы (деталь плитки «Процессор»).
public sealed record HwmonCpu
{
    public int? Tctl { get; init; }
    public int? Tccd1 { get; init; }
    public int? Tccd2 { get; init; }
}

// Плата (asusec): чипсет + VRM + вентилятор чипсета. «Процессор по датчику платы» (asusec CPU)
// не показывается (ADR-029 «Железо»): плитка CPU берёт Tctl из k10temp.
public sealed record HwmonBoard
{
    public int? Chipset { get; init; }
    public int? Vrm { get; init; }
    // Вентилятор чипсета, об/мин; null — датчика нет.
    public int? ChipsetFanRpm { get; init; }
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

// Снимок hwmon: CPU, плата, диски, вентиляторы корпуса. null-поля — датчика нет.
public sealed record HwmonSnapshot
{
    public HwmonCpu? Cpu { get; init; }
    public HwmonBoard? Board { get; init; }
    public HwmonDisk[]? Disks { get; init; }
    public HwmonFan[]? CaseFans { get; init; }
}

// Читатель /sys/class/hwmon/* (без процессов и без root). Имена hwmon определяются по `name`
// и меткам, НЕ по номерам hwmonN (они плавают между загрузками). Корни подменяются в тестах:
// hwmonRoot (/sys/class/hwmon), sysBlockRoot (/sys/block), mountinfoPath (/proc/self/mountinfo).
// Любой сбой чтения → null-поле (недоверенный источник, не роняем BackgroundService).
public sealed class HwmonReader
{
    private readonly string _hwmonRoot;
    private readonly string _sysBlockRoot;
    private readonly string _mountinfoPath;

    public HwmonReader() : this("/sys/class/hwmon", "/sys/block", "/proc/self/mountinfo") { }

    public HwmonReader(string hwmonRoot, string sysBlockRoot, string mountinfoPath)
    {
        _hwmonRoot = hwmonRoot;
        _sysBlockRoot = sysBlockRoot;
        _mountinfoPath = mountinfoPath;
    }

    // Читает снимок. null — hwmonRoot не существует (чужая машина) — все поля null/пусто.
    public HwmonSnapshot? Read()
    {
        if (!Directory.Exists(_hwmonRoot))
            return null;

        var kvController = FindKvController();
        var snap = new HwmonSnapshot
        {
            Cpu = ReadCpu(),
            Board = ReadBoard(),
            Disks = ReadDisks(kvController),
            CaseFans = ReadCaseFans(),
        };
        return snap;
    }

    // ─── CPU (k10temp): Tctl, Tccd1, Tccd2 ─────────────────────────────────────────

    private HwmonCpu? ReadCpu()
    {
        var dir = FindHwmon("k10temp");
        if (dir is null) return null;
        return new HwmonCpu
        {
            Tctl = ReadTemp(dir, "temp1"),
            Tccd1 = ReadTemp(dir, "temp3"),
            Tccd2 = ReadTemp(dir, "temp4"),
        };
    }

    // ─── Плата (asusec): Chipset, VRM, вентилятор чипсета ───────────────────────────

    private HwmonBoard? ReadBoard()
    {
        var dir = FindHwmon("asusec");
        if (dir is null) return null;
        // Метки asusec: temp1=Chipset, temp2=CPU, temp3=Motherboard, temp5=VRM, fan2=Chipset.
        // Определяем по label, не по номеру (номера могут отличаться на другой плате).
        return new HwmonBoard
        {
            Chipset = ReadTempByLabel(dir, "Chipset"),
            Vrm = ReadTempByLabel(dir, "VRM"),
            ChipsetFanRpm = ReadFanByLabel(dir, "Chipset"),
        };
    }

    // ─── Диски (nvme, по контроллеру): температура, датчики, предел, роль ───────────

    private HwmonDisk[]? ReadDisks(string? kvController)
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

            disks.Add(new HwmonDisk
            {
                Controller = controller,
                Role = controller == kvController ? "kv" : "system",
                TempC = hottest,
                Temp2C = second,
                CritC = ReadCrit(dir),
                UsedGb = ReadUsedGb(controller),
                TotalGb = ReadTotalGb(controller),
            });
        }
        return disks.ToArray();
    }

    // Предел диска (БЕЗ root): temp*_max (WCTEMP контроллера), иначе temp*_crit; округление
    // до целого (83,85 → 84); null — не сообщает (пороги 70/80).
    private static int? ReadCrit(string dir)
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

    private HwmonFan[]? ReadCaseFans()
    {
        var dir = FindHwmon("nct6798");
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

    // Каталог hwmon с заданным name (первый); null — нет такого датчика.
    private string? FindHwmon(string name)
    {
        foreach (var dir in EnumerateHwmonDirs())
        {
            if (ReadName(dir) == name)
                return dir;
        }
        return null;
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
            foreach (var dir in Directory.EnumerateDirectories(_hwmonRoot))
            {
                if (File.Exists(Path.Combine(dir, "name")))
                    result.Add(dir);
            }
        }
        catch { /* недоверенная ФС: не роняем */ }
        return result;
    }

    private static string? ReadName(string dir)
    {
        try { return File.ReadAllText(Path.Combine(dir, "name")).Trim(); }
        catch { return null; }
    }

    // Значение датчика temp*_input (милли-°C) → °C; null — нет/не число.
    private static int? ReadTemp(string dir, string sensor)
    {
        var f = Path.Combine(dir, sensor + "_input");
        return ReadMilliC(f);
    }

    // Датчик по метке (temp*_label == label): ищет tempN, у которого label совпадает.
    private static int? ReadTempByLabel(string dir, string label)
    {
        foreach (var f in EnumerateFiles(dir, "temp*_input"))
        {
            if (ReadLabel(f) == label)
                return ReadMilliC(f);
        }
        return null;
    }

    // Вентилятор по метке (fan*_label == label): об/мин; null — нет.
    private static int? ReadFanByLabel(string dir, string label)
    {
        foreach (var f in EnumerateFiles(dir, "fan*_input"))
        {
            if (ReadLabel(f) == label)
                return ReadInt(f);
        }
        return null;
    }

    // Все датчики temp*_input (милли-°C → °C), в порядке номеров.
    private static List<int?> ReadAllTemps(string dir)
    {
        var result = new List<int?>();
        foreach (var f in EnumerateFiles(dir, "temp*_input"))
            result.Add(ReadMilliC(f));
        return result;
    }

    // Метка датчика/вентилятора (temp*_label / fan*_label); null — нет метки.
    private static string? ReadLabel(string inputPath)
    {
        var labelPath = inputPath.Replace("_input", "_label");
        try
        {
            if (File.Exists(labelPath))
                return File.ReadAllText(labelPath).Trim();
        }
        catch { }
        return null;
    }

    // Файлы dir, имя которых совпадает с glob-подобным шаблоном (поддержка *), по возрастанию
    // числового суффикса (temp1, temp2, …; fan1, fan2, …).
    private static IEnumerable<string> EnumerateFiles(string dir, string pattern)
    {
        var star = pattern.IndexOf('*');
        var prefix = pattern.Substring(0, star);
        var suffix = pattern.Substring(star + 1);
        var candidates = new List<(int num, string path)>();
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir))
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
    private static string? ResolveNvmeController(string dir)
    {
        try
        {
            var deviceLink = Path.Combine(dir, "device");
            var target = new DirectoryInfo(deviceLink).LinkTarget; // "../../nvmeX"
            if (target is null) return null;
            var name = Path.GetFileName(target); // "nvmeX"
            return name.StartsWith("nvme") ? name : null;
        }
        catch { return null; }
    }

    // Контроллер kvdisk: из mountinfo строка с точкой монтирования /home/an/llm/kvdisk →
    // устройство /dev/nvmeXn1p1 → контроллер nvmeX. null — kvdisk не смонтирован.
    private string? FindKvController()
    {
        try
        {
            if (!File.Exists(_mountinfoPath)) return null;
            foreach (var line in File.ReadAllLines(_mountinfoPath))
            {
                // Формат mountinfo: … <mountpoint> … - <fstype> <source> <opts>
                // Ищем mountpoint = /home/an/llm/kvdisk, source = /dev/nvmeXn1p1.
                var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var mountpoint = fields.Length >= 5 ? fields[4] : null;
                if (mountpoint != "/home/an/llm/kvdisk") continue;
                var source = fields[^2]; // /dev/nvmeXn1p1 (… - fstype source super-opts)
                var dev = Path.GetFileName(source); // nvmeXn1p1
                var m = System.Text.RegularExpressions.Regex.Match(dev, @"^nvme(\d+)");
                if (m.Success)
                    return "nvme" + m.Groups[1].Value;
                return null;
            }
        }
        catch { }
        return null;
    }

    // Общий размер блока nvmeXn1 (512-байтовые сектора) → ГБ; null — нет.
    private long? ReadTotalGb(string controller)
    {
        try
        {
            var sizeFile = Path.Combine(_sysBlockRoot, controller + "n1", "size");
            if (!File.Exists(sizeFile)) return null;
            var sectors = long.Parse(File.ReadAllText(sizeFile).Trim(), CultureInfo.InvariantCulture);
            return sectors * 512L / (1024L * 1024 * 1024);
        }
        catch { return null; }
    }

    // Занято на ФС контроллера: среди смонтированных разделов (nvmeXn1pY) берётся ФС с
    // наибольшим размером (не /boot/efi); statvfs точки монтирования → ГБ; null — нет.
    private long? ReadUsedGb(string controller)
    {
        long? best = null;
        long bestTotal = 0;
        try
        {
            if (!File.Exists(_mountinfoPath)) return null;
            foreach (var line in File.ReadAllLines(_mountinfoPath))
            {
                var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length < 8) continue;
                var source = Path.GetFileName(fields[^2]);
                if (!source.StartsWith(controller + "n")) continue;
                var drive = new DriveInfo(fields[4]);
                var total = drive.TotalSize;
                if (total <= bestTotal) continue;
                bestTotal = total;
                best = (total - drive.TotalFreeSpace) / (1024L * 1024 * 1024);
            }
        }
        catch { }
        return best;
    }

    // ─── Чтение чисел ──────────────────────────────────────────────────────────────

    private static int? ReadInt(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return int.TryParse(File.ReadAllText(path).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
        }
        catch { return null; }
    }

    // милли-°C → °C.
    private static int? ReadMilliC(string path)
    {
        var milli = ReadInt(path);
        return milli is null ? null : milli / 1000;
    }
}
