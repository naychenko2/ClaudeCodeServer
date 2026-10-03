using System.Net.NetworkInformation;
using System.Text.RegularExpressions;

namespace ClaudeHomeServer.Services.ProjectServices;

/// <summary>
/// Порт-политика и сборка аргументов запуска сервиса проекта. Чистые функции без
/// процессов — отдельно от <see cref="DevServerService"/>, чтобы их можно было проверить
/// тестом. Разбор и причины — docs/research/build-stand-progress-2026-10.md, раздел «Порты».
/// </summary>
internal static class DevServerLaunchPolicy
{
    /// <summary>
    /// Порты, которые сервис проекта не получит никогда: 80 и 443 — боевой инстанс продукта,
    /// 8080 — Dify. Отказ до запуска, независимо от конфига сервиса.
    /// </summary>
    public static readonly IReadOnlySet<int> ForbiddenPorts = new HashSet<int> { 80, 443, 8080 };

    /// <summary>Диапазон автопорта на хосте — та же конвенция 55xx–56xx, что у людей.</summary>
    public const int AutoPortFirst = 5500;
    public const int AutoPortLast = 5699;

    /// <summary>
    /// Сколько ждать, пока сервис начнёт слушать порт. 30 с не хватало `dotnet run` с
    /// холодной сборкой (78 с у нашего стенда) — процесс гасился посреди сборки.
    /// </summary>
    public static readonly TimeSpan DefaultReadyTimeout = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Потолок ожидания для `dotnet run`/`dotnet watch`: сборка идёт внутри ожидания порта, и
    /// холодная сборка нашего бэкенда на нагруженной машине не уложилась в 120 с (38 из 40
    /// проектов). Временная мера до выноса сборки из ожидания (этап 5 плана). Фронт ждёт ответ
    /// на `preview/start` не меньше этого значения — см. PREVIEW_START_TIMEOUT_MS в api.ts.
    /// </summary>
    public static readonly TimeSpan DotnetRunReadyTimeout = TimeSpan.FromSeconds(300);

    /// <summary>Потолок ожидания порта для команды: `dotnet run`/`watch` собирает перед стартом.</summary>
    public static TimeSpan ReadyTimeoutFor(string command, IReadOnlyList<string> args) =>
        IsDotnet(command) && args.Count > 0 && args[0] is "run" or "watch"
            ? DotnetRunReadyTimeout
            : DefaultReadyTimeout;

    /// <summary>Текст отказа по таймауту ожидания порта — с потолком и подсказкой про сборку.</summary>
    public static string ReadyTimeoutReason(TimeSpan timeout, bool builds) =>
        $"Таймаут {(int)timeout.TotalSeconds} с: сервис не начал слушать порт." +
        (builds ? " Если не успела холодная сборка — запустите ещё раз: собранное не пропадёт." : "");

    public static bool IsForbidden(int port) => ForbiddenPorts.Contains(port);

    /// <summary>Текст отказа для запрещённого порта.</summary>
    public static string ForbiddenReason(int port) =>
        $"Порт {port} запрещён для сервисов проекта (80/443 — боевой инстанс, 8080 — Dify). " +
        "Укажите другой порт или включите автопорт.";

    /// <summary>
    /// Первый свободный порт из 55xx–56xx: не занят нашими сервисами и не слушается никем
    /// в системе. Пусто — null.
    /// </summary>
    public static int? PickAutoPort(IEnumerable<int> busy)
    {
        var taken = new HashSet<int>(busy);
        for (var p = AutoPortFirst; p <= AutoPortLast; p++)
            if (!taken.Contains(p) && !IsForbidden(p)) return p;
        return null;
    }

    /// <summary>Порты, которые сейчас слушает кто-либо в системе (любой адрес).</summary>
    public static IEnumerable<int> SystemListeningPorts()
    {
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
                .Select(e => e.Port).ToList();
        }
        catch (NetworkInformationException) { return []; }
        catch (PlatformNotSupportedException) { return []; }
    }

    /// <summary>
    /// dotnet-сервис ли это, и куда ему класть аргументы приложения. Порт dotnet-приложению
    /// навязывается только аргументом `--urls`: `ASPNETCORE_URLS` из окружения перебивают
    /// `applicationUrl` профиля launchSettings.json и ключ `Urls` в appsettings.*.json.
    /// </summary>
    public static bool IsDotnetApp(string command, IReadOnlyList<string> args)
    {
        if (!IsDotnet(command) || args.Count == 0) return false;
        var verb = args[0];
        return verb is "run" or "watch" || verb.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDotnet(string command)
    {
        var name = Path.GetFileNameWithoutExtension(command.Trim().Trim('"'));
        return string.Equals(name, "dotnet", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Аргументы запуска с навязанным портом. Для `dotnet run`/`dotnet watch` `--urls` идёт
    /// после разделителя `--` (иначе его съест сама команда dotnet), для `dotnet X.dll` —
    /// сразу аргументом приложения. Не dotnet-сервис или `--urls` уже задан руками — аргументы
    /// как были.
    /// </summary>
    public static string[] BuildArgs(string command, string[] args, string url)
    {
        if (!IsDotnetApp(command, args)) return args;
        if (args.Any(a => a == "--urls" || a.StartsWith("--urls=", StringComparison.Ordinal))) return args;

        var verb = args[0];
        if (verb.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            return [.. args, "--urls", url];
        return args.Contains("--") ? [.. args, "--urls", url] : [.. args, "--", "--urls", url];
    }

    /// <summary>
    /// Адрес, который навязываем сервису. В песочнице — все интерфейсы контейнера: порт
    /// публикуется docker'ом, и loopback контейнера с хоста не виден. На хосте — localhost,
    /// чтобы дев-сервер не торчал в локальную сеть.
    /// </summary>
    public static string ListenUrl(int port, bool sandboxed) =>
        sandboxed ? $"http://0.0.0.0:{port}" : $"http://localhost:{port}";

    private static readonly Regex OutputPortRegex = new(
        @"https?://(?:localhost|127\.0\.0\.1|0\.0\.0\.0|\[::1\]):(\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Диагностики «порт занят» у разных рантаймов. Список неполон по определению: для
    // подсказки пользователю это не страшно, а порт из такой строки просто не берём.
    private static readonly string[] PortInUseMarkers =
    [
        "EADDRINUSE",                       // Node (Vite, webpack, express)
        "address already in use",           // Kestrel/Linux, Go, Python
        "only one usage of each socket",    // Winsock (WSAEADDRINUSE)
        "failed to bind to address",        // ASP.NET Core
        "port is already allocated",        // docker compose
    ];

    public static bool LooksLikePortInUse(string output) =>
        !string.IsNullOrEmpty(output) &&
        PortInUseMarkers.Any(m => output.Contains(m, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Порт, на котором сервис объявил себя в выводе (vite «Local:», Kestrel «Now listening on:»),
    /// иначе null. Строка ошибки привязки («Failed to bind to address …:8080») — не «слушаю»:
    /// адрес в ней чужой, его держит кто-то другой.
    /// </summary>
    public static int? PortFromOutput(string line)
    {
        if (LooksLikePortInUse(line)) return null;
        var m = OutputPortRegex.Match(line);
        return m.Success && int.TryParse(m.Groups[1].Value, out var p) ? p : null;
    }

    private static readonly Regex NowListeningRegex = new(
        @"Now listening on:\s*https?://(?:\[[^\]]*\]|[^\s:/]+):(\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Порт из строки Kestrel «Now listening on: http://…:порт», иначе null.</summary>
    public static int? ParseNowListening(string line)
    {
        var m = NowListeningRegex.Match(line);
        return m.Success && int.TryParse(m.Groups[1].Value, out var p) ? p : null;
    }

    /// <summary>Строка Kestrel «Application started» — все адреса уже объявлены.</summary>
    public static bool IsApplicationStarted(string line) =>
        line.Contains("Application started", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Приложение объявило свои адреса, и нашего порта среди них нет — значит, оно слушает
    /// своё (launchSettings, `Urls` в appsettings) и ждать порт бессмысленно. null — расхождения нет
    /// (или ещё рано судить).
    /// </summary>
    public static string? PortMismatch(int expected, IReadOnlyCollection<int> announced, bool appStarted)
    {
        if (!appStarted || announced.Count == 0 || announced.Contains(expected)) return null;
        var actual = string.Join(", ", announced.Select(p => ":" + p));
        return $"Сервис слушает {actual} вместо :{expected}.";
    }
}
