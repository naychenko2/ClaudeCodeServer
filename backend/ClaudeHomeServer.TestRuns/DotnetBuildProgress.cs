using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace ClaudeHomeServer.Services.TestRuns;

// Прогресс `dotnet build` по строкам `  Проект -> путь.dll` (docs/research/build-stand-progress-2026-10.md,
// раздел 1). Строку `->` MSBuild печатает на КАЖДЫЙ проект замыкания, в том числе актуальный,
// поэтому их число — честный счётчик «N из M». А вот процент по числу проектов врёт: последний
// проект (у нас — сам ClaudeHomeServer) идёт треть сборки, и «39 из 40» повисло бы на 97%.
// Поэтому процент — по весам прошлого прогона той же цели в том же дереве:
//  • доля прошлого времени, к которой был готов самый поздний из уже готовых проектов;
//  • между строками полоса ползёт по времени (доля прошлого времени), но не дальше доли, к
//    которой в прошлый раз был готов следующий ещё не готовый проект — иначе на медленной сборке
//    она обогнала бы саму сборку;
//  • потолок 99 — «готово» скажет результат.
// Без прошлого прогона M — оценка обходом ProjectReference по XML (условий MSBuild сервер не
// вычисляет), подпись «≈N из M», процент с потолком 90, пока не готов последний проект. Ни
// прошлого прогона, ни оценки — только «N проектов» без процента. Exact всегда false: процент
// у сборки — оценка, карточка рисует его пунктиром.
// Не потокобезопасен: синхронизация — у вызывающего (строки идут из двух потоков сразу).
public sealed partial class DotnetBuildProgress
{
    // Потолок процента, пока M — оценка по XML, а не память прошлого прогона
    public const int EstimateCeiling = 90;

    private readonly BuildRunMemory? _previous;
    private readonly int? _estimate;
    // Готовые проекты: имя → секунда готовности в этом прогоне
    private readonly Dictionary<string, double> _finished = new(StringComparer.Ordinal);

    public DotnetBuildProgress(BuildRunMemory? previous, int? estimatedTotal)
    {
        _previous = previous is { Total: > 0, Seconds: > 0 } ? previous : null;
        _estimate = estimatedTotal is > 0 ? estimatedTotal : null;
    }

    // Строк `->` в этом прогоне (у мульти-таргетного проекта — по строке на TFM)
    public int Done { get; private set; }

    // M: из прошлого прогона, иначе оценка; сборка насчитала больше — растёт до Done
    public int? Total => (_previous?.Total ?? _estimate) is { } m ? Math.Max(m, Done) : null;

    // M взято из прошлого прогона (точный счётчик), а не из оценки
    public bool FromMemory => _previous is not null;

    // Имя — буквы любого алфавита, цифры, `_.-` и пробелы внутри (не по краям). Двоеточие и
    // слеши в имя не входят: так строка ошибки `путь(1,1): error …: a -> b` проектом не станет
    [GeneratedRegex(@"^\s+(?<name>[\p{L}\p{N}_.\-](?:[\p{L}\p{N}_.\- ]*[\p{L}\p{N}_.\-])?) -> \S",
        RegexOptions.None, VsTestConsoleParser.RegexTimeoutMs)]
    private static partial Regex ProjectLine();

    // Имя проекта из строки `  Проект -> путь`; не она (строка длиннее потолка, сработал потолок
    // сопоставления) — null. Чистая функция: вызывающий зовёт её ВНЕ замка своего состояния
    public static string? ParseProjectLine(string line)
    {
        if (line.Length > VsTestConsoleParser.MaxOutcomeLineLength) return null;
        try { return ProjectLine().Match(line) is { Success: true } m ? m.Groups["name"].Value : null; }
        catch (RegexMatchTimeoutException) { return null; }
    }

    // Строка вывода; true — это строка готового проекта
    public bool Feed(string line, TimeSpan elapsed) => Apply(ParseProjectLine(line), elapsed);

    // Итог разбора строки (ParseProjectLine) — в счётчик; null — строка не про проект
    public bool Apply(string? name, TimeSpan elapsed)
    {
        if (name is null) return false;
        Done++;
        _finished[name] = elapsed.TotalSeconds;
        return true;
    }

    public int? Percent(TimeSpan elapsed)
    {
        if (_previous is { } previous) return MemoryPercent(previous, elapsed);
        if (Total is not { } total) return null;
        return Math.Min(EstimateCeiling, Done * 100 / total);
    }

    private int MemoryPercent(BuildRunMemory previous, TimeSpan elapsed)
    {
        if (Done >= Total) return 99;
        var done = 0.0;
        var next = 1.0;
        foreach (var (name, seconds) in previous.Finished)
        {
            var fraction = Math.Clamp(seconds / previous.Seconds, 0, 1);
            if (_finished.ContainsKey(name)) done = Math.Max(done, fraction);
            else next = Math.Min(next, fraction);
        }
        // Следующий проект в прошлый раз был готов раньше уже готового — граница не держит
        next = Math.Max(next, done);
        var byTime = Math.Min(elapsed.TotalSeconds / previous.Seconds, next);
        var share = Math.Max(done, byTime);
        return Math.Clamp((int)(share * 100), 0, 99);
    }

    public string Label() => Total switch
    {
        null => $"{Done} {Projects(Done)}",
        { } total when FromMemory => $"{Done} из {total} {ProjectsOf(total)}",
        { } total => $"≈{Done} из {total} {ProjectsOf(total)}",
    };

    public TestRunProgress Snapshot(TimeSpan elapsed) => new("build", Label(), Percent(elapsed), Exact: false);

    // Память этого прогона для следующего: только по успешной сборке (вызывающий)
    public BuildRunMemory ToMemory(TimeSpan elapsed) =>
        new(Done, Math.Max(elapsed.TotalSeconds, 0.001), new Dictionary<string, double>(_finished, StringComparer.Ordinal));

    // «проект/проекта/проектов» по последнему числу
    internal static string Projects(int n)
    {
        var mod100 = n % 100;
        var mod10 = n % 10;
        if (mod100 is >= 11 and <= 14) return "проектов";
        return mod10 switch { 1 => "проект", >= 2 and <= 4 => "проекта", _ => "проектов" };
    }

    // После «из N» — родительный падеж: «из 21 проекта», «из 3 проектов»
    internal static string ProjectsOf(int n) => n % 10 == 1 && n % 100 != 11 ? "проекта" : "проектов";

    // --- Оценка M обходом ProjectReference (первый прогон цели) ---

    // Потолок обхода: битое или гигантское дерево ссылок не повесит вызов
    private const int MaxProjects = 2000;

    // Потолок размера файла проекта/решения: больше — не проект, а подлог агента
    internal const int MaxProjectFileBytes = 4 * 1024 * 1024;

    private static readonly string[] ProjectExtensions = [".csproj", ".fsproj", ".vbproj"];

    // XML из рабочего дерева пишет агент: DTD (и разворот сущностей) запрещён, внешнего
    // резолвера нет, документ не больше потолка файла
    private static readonly XmlReaderSettings XmlSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = MaxProjectFileBytes,
    };

    [GeneratedRegex(@"^Project\(""\{[^}]+\}""\)\s*=\s*""[^""]*""\s*,\s*""(?<path>[^""]+\.(?:cs|fs|vb)proj)""",
        RegexOptions.Multiline | RegexOptions.IgnoreCase, VsTestConsoleParser.RegexTimeoutMs)]
    private static partial Regex SlnProject();

    // Оценка числа строк `->`: проект — его замыкание по ProjectReference (сам плюс ссылки),
    // решение — объединение замыканий его проектов, каталог (пустая цель — корень дерева) — его
    // единственный проект или решение, как выбирает сам dotnet. Условия MSBuild
    // (Condition, ReferenceOutputAssembly) не вычисляются — это оценка. null — не смогли.
    // Обход идёт на ХОСТЕ по файлам, которые пишет агент (у container-владельца — из песочницы),
    // поэтому каждый путь — цель, проекты решения, ProjectReference — проходит FileInTree: за
    // дерево (`..`, абсолютный, UNC, ссылка наружу) не ходим, не обычный файл и гигант не читаем.
    // Битый XML (в том числе с DTD) — оценки нет вовсе. ct проверяется на каждом файле
    public static int? EstimateTotal(string workingDirectory, string? target, CancellationToken ct = default)
    {
        try
        {
            var root = Path.GetFullPath(workingDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var path = string.IsNullOrWhiteSpace(target) ? root : SafePath.Join(root, target);
            if (Directory.Exists(path) && TestRunService.ResolvesInside(root, path) && PickInDirectory(path) is { } picked)
                path = picked;
            if (FileInTree(root, path) is not { } file) return null;

            var ext = Path.GetExtension(file).ToLowerInvariant();
            IEnumerable<string> roots = ext switch
            {
                ".slnx" => LoadXml(root, file).Descendants("Project")
                    .Select(p => (string?)p.Attribute("Path"))
                    .OfType<string>()
                    .Select(p => Resolve(root, file, p))
                    .OfType<string>()
                    .ToList(),
                ".sln" => SlnProject().Matches(TreeFiles.ReadTextInTree(root, file, MaxProjectFileBytes)
                        ?? throw new IOException("Решение не читается как обычный файл дерева"))
                    .Select(m => Resolve(root, file, m.Groups["path"].Value))
                    .OfType<string>()
                    .ToList(),
                _ when ProjectExtensions.Contains(ext) => [file],
                _ => [],
            };
            var closure = Closure(root, roots, ct);
            return closure > 0 ? closure : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or XmlException or RegexMatchTimeoutException)
        {
            return null;
        }
    }

    // Как dotnet: в каталоге ровно один проект — он, иначе ровно одно решение; иначе ничего
    private static string? PickInDirectory(string dir)
    {
        var projects = ProjectExtensions.SelectMany(e => Directory.EnumerateFiles(dir, "*" + e)).ToList();
        if (projects.Count == 1) return projects[0];
        var solutions = Directory.EnumerateFiles(dir, "*.sln").Concat(Directory.EnumerateFiles(dir, "*.slnx")).ToList();
        return solutions.Count == 1 ? solutions[0] : null;
    }

    // Путь из решения или ProjectReference → полный путь; UNC (`\\host\share`) — null ДО любого
    // обращения к диску: File.Exists на нём открыл бы SMB-сессию с учёткой сервиса
    internal static string? Resolve(string root, string fromFile, string relative)
    {
        if (IsUnc(relative)) return null;
        var full = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(fromFile)!,
            relative.Replace('\\', Path.DirectorySeparatorChar)));
        return IsUnc(full) ? null : full;
    }

    private static bool IsUnc(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal);

    // Обычный файл проекта в дереве: лексически внутри, ссылки по пути не уводят наружу, не
    // каталог/устройство, размер 1..потолок (FIFO и устройства на Linux дают 0). Это отсев по
    // пути; окончательное слово — у чтения по дескриптору (TreeFiles.ReadInTree): файл могли
    // подменить после проверки. Проверяется цель ссылки, а путь возвращается как есть —
    // относительные ссылки внутри проекта dotnet считает от него. Не годится — null
    internal static string? FileInTree(string root, string path)
    {
        if (IsUnc(path) || !TestRunService.ResolvesInside(root, path)) return null;
        FileSystemInfo info = new FileInfo(path);
        if (info.LinkTarget is not null)
        {
            try { info = info.ResolveLinkTarget(returnFinalTarget: true) ?? info; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        }
        return info is FileInfo { Exists: true } file
            && (file.Attributes & (FileAttributes.Directory | FileAttributes.Device)) == 0
            && file.Length is > 0 and <= MaxProjectFileBytes
            ? Path.GetFullPath(path)
            : null;
    }

    // Файл читается одним дескриптором под потолком (TreeFiles): между FileInTree и чтением его
    // могли подменить FIFO, устройством или гигантом. Не прочёлся — оценки нет
    private static XDocument LoadXml(string root, string file)
    {
        var bytes = TreeFiles.ReadInTree(root, file, MaxProjectFileBytes)
            ?? throw new IOException("Файл проекта не читается как обычный файл дерева");
        using var reader = XmlReader.Create(new MemoryStream(bytes), XmlSettings);
        return XDocument.Load(reader);
    }

    private static int Closure(string root, IEnumerable<string> roots, CancellationToken ct)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var seen = new HashSet<string>(comparer);
        var queue = new Queue<string>(roots);
        while (queue.Count > 0 && seen.Count < MaxProjects)
        {
            ct.ThrowIfCancellationRequested();
            if (FileInTree(root, queue.Dequeue()) is not { } project || !seen.Add(project)) continue;
            foreach (var reference in LoadXml(root, project).Descendants().Where(e => e.Name.LocalName == "ProjectReference"))
                if ((string?)reference.Attribute("Include") is { Length: > 0 } include
                    && Resolve(root, project, include) is { } resolved)
                    queue.Enqueue(resolved);
        }
        return seen.Count;
    }
}

// Память успешной сборки цели: сколько было строк `->`, сколько секунд шла сборка и к какой
// секунде был готов каждый проект. Живёт в каталоге данных СЕРВЕРА (data/build-memory), а не в
// рабочем дереве: дерево пишет агент, и любая проверка пути там гонится с подменой ссылкой
// (TOCTOU). Сюда агент не пишет вовсе, поэтому ни ссылок, ни FIFO на месте файла не бывает
public sealed record BuildRunMemory(int Total, double Seconds, IReadOnlyDictionary<string, double> Finished)
{
    // Подкаталог data. В бэкап не едет (BackupPaths): ключ — хеш пути дерева этой машины, а
    // память — лишь подсказка для процента, следующая сборка запишет её заново
    public const string DirName = "build-memory";

    // Потолок файла памяти: настоящая память — сотни байт на проект, десятков КБ хватает с запасом
    internal const int MaxBytes = 64 * 1024;

    // Короткая сборка (no-op) перезаписывает память, только если длится не меньше этой доли прошлой:
    // иначе веса следующей холодной сборки стали бы бессмысленными
    internal const double ReplaceShare = 0.3;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // Файл памяти цели в memoryDirectory. Ключ — хеш нормализованного пути дерева плюс хеш
    // нормализованной цели (разные деревья и цели не склеиваются, как `a/b` и `a_b` при замене
    // символов), спереди — читаемый префикс цели для человека; пустая цель — корень дерева.
    // variant — что именно собирается в цели (у npm — имя скрипта): своя память на каждый
    public static string PathFor(string memoryDirectory, string workingDirectory, string? target, string? variant = null)
    {
        var normalized = Normalize(workingDirectory, target);
        var readable = normalized.Length == 0 ? "_root" : Regex.Replace(normalized, @"[^A-Za-z0-9._-]", "_");
        if (readable.Length > 40) readable = readable[..40];
        var suffix = variant is null ? "" : "-" + Hash(variant);
        return Path.Combine(memoryDirectory, $"{readable}-{Hash(NormalizeTree(workingDirectory))}-{Hash(normalized)}{suffix}.json");
    }

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];

    // Дерево в одной записи: полный путь без хвостового разделителя, регистр на Windows не различается
    private static string NormalizeTree(string workingDirectory)
    {
        var full = Path.GetFullPath(workingDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return OperatingSystem.IsWindows() ? full.ToLowerInvariant() : full;
    }

    // Цель относительно дерева в одной записи: `./a\b/` и `a/b` — одна цель, регистр на Windows
    // не различается. Цель за деревом (её отклонит сборка) — как написана
    private static string Normalize(string workingDirectory, string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return "";
        string relative;
        try
        {
            var root = Path.GetFullPath(workingDirectory);
            relative = Path.GetRelativePath(root, SafePath.Join(root, target.Trim()));
        }
        catch (Exception e) when (e is ArgumentException or UnauthorizedAccessException or NotSupportedException)
        {
            relative = target.Trim();
        }
        relative = relative.Replace('\\', '/').Trim('/');
        if (relative == ".") return "";
        return OperatingSystem.IsWindows() ? relative.ToLowerInvariant() : relative;
    }

    // Нет, битый, чужой формат, больше потолка — как будто памяти нет: оценка по XML лучше
    // вранья. Читается не больше потолка + 1 байт — каталог свой, но потолок дешевле доверия
    public static BuildRunMemory? Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (TreeFiles.ReadBounded(stream, MaxBytes) is not { } bytes) return null;
            var memory = JsonSerializer.Deserialize<BuildRunMemory>(bytes, Json);
            return memory is { Total: > 0, Seconds: > 0, Finished: not null } ? memory : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    // Стоит ли этой памятью заменять прошлую: no-op сборка за пару секунд не затирает холодную
    public bool Replaces(BuildRunMemory? previous) =>
        previous is null || Total != previous.Total || Seconds >= previous.Seconds * ReplaceShare;

    // Запись во временный файл и File.Move: обрыв не оставит полфайла, а читатель — даже
    // параллельный, из другого дерева с той же целью — видит старую или новую память целиком
    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir, $".{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
                JsonSerializer.Serialize(stream, this, Json);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
