using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services.Devices;

/// <summary>
/// Требуемая версия управляемой копии CLI на устройствах (ADR-016 §3 «Управляемая копия
/// CLI»). По умолчанию устройства идут за хостом — требуется та версия CLI, что стоит на
/// сервере (<see cref="IHostCliVersion"/>): прибитая в конфиге версия отставала от хоста при
/// каждом обновлении CLI. Настройка <see cref="CliVersionKey"/> — только аварийный пин поверх
/// версии хоста; читается она только здесь и вживую, смена не требует рестарта.
///
/// Вердикт «харнес готов» не хранится: он считается сверкой объявленной агентом версии с
/// требуемой в момент вопроса. Сравнение точное — «не той версии» значит любую другую, в
/// том числе более новую: сервер жёстко зависит от поведения CLI.
/// </summary>
public sealed class DeviceHarnessPolicy(IConfiguration config, IHostCliVersion? hostCli = null)
{
    public const string CliVersionKey = "DeviceAgent:CliVersion";

    public const string NotReadyPrefix = DeviceAgentCompatibility.NotReadyPrefix;

    public string? RequiredCliVersion => Normalize(config[CliVersionKey]) ?? Normalize(hostCli?.Current);

    public (bool Ready, string? Problem) Evaluate(string? declaredCliVersion)
    {
        var required = RequiredCliVersion;
        if (required is null)
            return (false, $"{NotReadyPrefix}: на сервере не определена версия CLI для устройств: CLI хоста не ответил, пин {CliVersionKey} не задан");

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
