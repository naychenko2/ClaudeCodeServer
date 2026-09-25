namespace ClaudeHomeServer.Services.Desktop;

/// <summary>
/// Требуемая версия управляемой копии CLI на устройствах (ADR-016 §3 «Управляемая копия
/// CLI»). Источник — одна настройка сервера <see cref="CliVersionKey"/>, и читается она
/// только здесь; значение берётся вживую, смена версии не требует рестарта.
///
/// Вердикт «харнес готов» не хранится: он считается сверкой объявленной агентом версии с
/// требуемой в момент вопроса. Сравнение точное — «не той версии» значит любую другую, в
/// том числе более новую: сервер жёстко зависит от поведения CLI.
/// </summary>
public sealed class DeviceHarnessPolicy(IConfiguration config)
{
    public const string CliVersionKey = "DeviceAgent:CliVersion";

    public const string NotReadyPrefix = "Агент устройства не готов";

    public string? RequiredCliVersion => Normalize(config[CliVersionKey]);

    public (bool Ready, string? Problem) Evaluate(string? declaredCliVersion)
    {
        var required = RequiredCliVersion;
        if (required is null)
            return (false, $"{NotReadyPrefix}: на сервере не задана версия CLI для устройств ({CliVersionKey})");

        var declared = Normalize(declaredCliVersion);
        if (declared is null)
            return (false, $"{NotReadyPrefix}: на устройстве нет управляемой копии CLI (нужна {required})");

        if (!string.Equals(declared, required, StringComparison.Ordinal))
            return (false, $"{NotReadyPrefix}: на устройстве CLI {declared}, нужна {required}");

        return (true, null);
    }

    /// <summary>
    /// «2.1.281 (Claude Code)» и «v2.1.281» → «2.1.281»: агент может прислать строку как её
    /// печатает <c>claude --version</c>. Пусто — null.
    /// </summary>
    public static string? Normalize(string? version)
    {
        var token = (version ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrEmpty(token)) return null;
        if (token[0] is 'v' or 'V') token = token[1..];
        return token.Length == 0 ? null : token;
    }
}
