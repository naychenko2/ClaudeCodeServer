using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeHomeServer.Services.Deploy;

// Журнал выкатки (ADR-010) — единственная точка истины о ходе деплоя и ШОВ с внешним
// агентом: сервер пишет только заявку и отметку «доложено», всё остальное пишет агент.
// Отсюда два требования к моделям: имена полей camelCase (агент читает файл как есть)
// и терпимость к незнакомым/отсутствующим полям — версии сервера и агента обновляются
// порознь, и лишнее поле не должно ронять разбор.

/// <summary>Фазы выкатки: queued → building → switching → verifying → succeeded | rolled_back | failed.</summary>
public static class DeployPhases
{
    public const string Queued = "queued";
    public const string Building = "building";
    public const string Switching = "switching";
    public const string Verifying = "verifying";
    public const string Succeeded = "succeeded";
    public const string RolledBack = "rolled_back";
    public const string Failed = "failed";

    /// <summary>Фаза, дальше которой выкатка не идёт (агент отработал).</summary>
    public static bool IsTerminal(string? phase) =>
        phase is Succeeded or RolledBack or Failed;
}

/// <summary>Кто заказал выкатку. sessionId — чат, в который новый инстанс доложит результат.</summary>
public sealed class DeployInitiator
{
    public string? UserId { get; set; }
    public string? SessionId { get; set; }
}

/// <summary>
/// Заявка: параметры, с которыми агент должен ехать. Передаются ЖУРНАЛОМ, а не командной
/// строкой — schtasks /run аргументов не передаёт, а сама командная строка сервером
/// не собирается вовсе (см. DeployService.WakeAgent).
/// </summary>
public sealed class DeployRequest
{
    public string? Ref { get; set; }
    public bool SkipFrontend { get; set; }
    public bool SkipSandbox { get; set; }
    public bool AllowDirty { get; set; }
    /// <summary>Только для kind=rollback: какой снимок вернуть; пусто — предыдущий.</summary>
    public string? ReleaseId { get; set; }
}

/// <summary>Шаг выкатки — как его записал агент.</summary>
public sealed class DeployStep
{
    public string Name { get; set; } = "";
    public string Status { get; set; } = "";
    public long Ms { get; set; }
}

/// <summary>Итог выкатки. Заполняется агентом; пока null — выкатка ещё идёт.</summary>
public sealed class DeployResult
{
    public bool Ok { get; set; }
    /// <summary>succeeded | rolled_back | failed — дублирует финальную фазу.</summary>
    public string Status { get; set; } = "";
    public string? Message { get; set; }
    /// <summary>Релиз, на котором остановились (при откате — куда вернулись).</summary>
    public string? ReleaseId { get; set; }
    public DateTime? FinishedAt { get; set; }
}

/// <summary>Одна выкатка.</summary>
public sealed class DeployRecord
{
    public string Id { get; set; } = "";
    /// <summary>deploy | rollback.</summary>
    public string Kind { get; set; } = DeployKinds.Deploy;
    public string Phase { get; set; } = DeployPhases.Queued;
    public string? Ref { get; set; }
    public string? Sha { get; set; }
    public bool Dirty { get; set; }
    /// <summary>Незакоммиченные файлы на момент заявки (при allowDirty) — для разбора постфактум.</summary>
    public List<string> DirtyFiles { get; set; } = [];
    public DeployRequest Request { get; set; } = new();
    public DeployInitiator? InitiatedBy { get; set; }
    public List<DeployStep> Steps { get; set; } = [];
    public DeployResult? Result { get; set; }
    /// <summary>Доложен ли итог в чат новым инстансом (ADR-010, «Отчёт о результате»).</summary>
    public bool Reported { get; set; }
    public DateTime? StartedAt { get; set; }

    /// <summary>Выкатка в работе: агент ещё не поставил итог и не дошёл до конечной фазы.</summary>
    [JsonIgnore]
    public bool IsActive => Result is null && !DeployPhases.IsTerminal(Phase);
}

/// <summary>Снимок релиза, пригодный для отката.</summary>
public sealed class DeployReleaseInfo
{
    public string Id { get; set; } = "";
    public string? Sha { get; set; }
    public string? Path { get; set; }
    public DateTime? CreatedAt { get; set; }
}

/// <summary>Файл журнала целиком.</summary>
public sealed class DeployState
{
    public const string FileName = "deploy-state.json";

    public DeployRecord? Current { get; set; }
    public List<DeployRecord> History { get; set; } = [];
    public List<DeployReleaseInfo> Releases { get; set; } = [];

    /// <summary>Сколько записей истории держим в журнале — файл читают и человек, и агент.</summary>
    public const int HistoryLimit = 30;

    // Единый формат для обеих сторон шва: camelCase на запись, регистр не важен на чтение,
    // отступы — журнал регулярно читают глазами и через git diff у агента.
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

public static class DeployKinds
{
    public const string Deploy = "deploy";
    public const string Rollback = "rollback";
}
