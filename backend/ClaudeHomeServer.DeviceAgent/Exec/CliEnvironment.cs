using ClaudeHomeServer.DeviceAgent.Cli;
using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.DeviceAgent.Exec;

/// <summary>
/// Окружение CLI хода собирается С НУЛЯ по allow-list (ADR-016 §2, сторож G3): от агента
/// наследуются только перечисленные системные переменные, всё остальное — прокси, токены
/// из профиля пользователя, чужие <c>ANTHROPIC_*</c> — в CLI не попадает.
///
/// Состав зафиксирован здесь и в тесте <c>CliEnvironmentTests</c>; расширять — только
/// осознанно, с правкой теста.
/// </summary>
internal static class CliEnvironment
{
    /// <summary>Заглушка авторизации: без неё CLI не пошлёт запрос, сайдкар её выбрасывает.</summary>
    public const string AuthPlaceholder = "ai-home-sidecar";

    public const string DefaultLang = "C.UTF-8";

    /// <summary>Наследуются от агента на любой ОС (если заданы).</summary>
    public static readonly IReadOnlyList<string> InheritedEverywhere = ["PATH", "HOME", "USERPROFILE", "LANG"];

    /// <summary>
    /// Дополнительно на Windows — минимум, без которого не стартуют сам CLI и его Bash:
    /// системный каталог, оболочка, расширения исполняемых, временные и профильные каталоги.
    /// </summary>
    public static readonly IReadOnlyList<string> InheritedOnWindows =
        ["SystemRoot", "ComSpec", "PATHEXT", "TEMP", "TMP", "APPDATA", "LOCALAPPDATA"];

    /// <summary>
    /// Ключи, которые сервер вправе прислать в spawn: только поведенческие. Всё прочее
    /// агент отбрасывает — даже скомпрометированный сервер не уведёт CLI на свой адрес.
    /// </summary>
    public static readonly IReadOnlySet<string> AllowedFromServer = new HashSet<string>(StringComparer.Ordinal)
    {
        "CLAUDE_CODE_DISABLE_CLAUDE_MDS",
    };

    /// <summary>Полный список имён, которые вообще могут оказаться в env CLI.</summary>
    public static IReadOnlySet<string> AllNames(bool windows)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        names.UnionWith(InheritedEverywhere);
        if (windows) names.UnionWith(InheritedOnWindows);
        names.UnionWith(["CLAUDE_CONFIG_DIR", "ANTHROPIC_BASE_URL", "ANTHROPIC_AUTH_TOKEN", "NO_PROXY", "HTTPS_PROXY"]);
        names.UnionWith(ManagedCliEnvironment.Variables.Keys);
        names.UnionWith(AllowedFromServer);
        return names;
    }

    /// <param name="inherited">Окружение агента (источник наследуемых значений).</param>
    /// <param name="sidecarTurnUrl">Адрес хода в сайдкаре: <c>http://127.0.0.1:{порт}/t/{ход}</c>.</param>
    /// <param name="sidecarProxyUrl">
    /// Адрес сайдкара как прокси, с учёткой хода: <c>http://turn:{ключ}@127.0.0.1:{порт}</c>
    /// (<see cref="DeviceEgressRoutes.ProxyUrl"/>) — по ней сайдкар опознаёт ход CONNECT.
    /// </param>
    public static Dictionary<string, string> Build(
        bool windows,
        IReadOnlyDictionary<string, string> inherited,
        string configDir,
        string sidecarProxyUrl,
        string sidecarTurnUrl,
        IReadOnlyDictionary<string, string>? fromServer)
    {
        var comparer = windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var source = new Dictionary<string, string>(inherited, comparer);
        var env = new Dictionary<string, string>(comparer);

        // Сначала то, что прислал сервер, — поверх ляжет всё фиксированное агентом
        foreach (var (key, value) in fromServer ?? new Dictionary<string, string>())
            if (AllowedFromServer.Contains(key)) env[key] = value;

        foreach (var name in windows ? InheritedEverywhere.Concat(InheritedOnWindows) : InheritedEverywhere)
            if (source.TryGetValue(name, out var value) && value.Length > 0) env[name] = value;

        env.TryAdd("LANG", DefaultLang);
        env["CLAUDE_CONFIG_DIR"] = configDir;
        env["ANTHROPIC_BASE_URL"] = $"{sidecarTurnUrl}/{DeviceSidecarRoutes.Llm}";
        env["ANTHROPIC_AUTH_TOKEN"] = AuthPlaceholder;
        env["HTTPS_PROXY"] = sidecarProxyUrl;
        // Сайдкар сам на loopback: запрос к нему через прокси ушёл бы по кругу
        env["NO_PROXY"] = "127.0.0.1,localhost";

        var managed = new Dictionary<string, string?>();
        ManagedCliEnvironment.ApplyTo(managed);
        foreach (var (key, value) in managed) env[key] = value ?? "";

        return env;
    }

    /// <summary>Снимок окружения процесса агента.</summary>
    public static Dictionary<string, string> CurrentProcess()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
            if (e.Key is string k && e.Value is string v) result[k] = v;
        return result;
    }
}
