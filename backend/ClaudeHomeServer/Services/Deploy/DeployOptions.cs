namespace ClaudeHomeServer.Services.Deploy;

// Настройки выкатки прода из чата (ADR-010) — секция «Deploy». Правятся руками в
// appsettings.Local.json: все пути машинно-специфичны, а сам контур есть далеко не на
// каждой машине. Дефолт Enabled=false — на машине без контура эндпоинты отказывают,
// а не дёргают планировщик вслепую.
//
// "Deploy": {
//   "Enabled": true,
//   "RepoDir":     "C:/Sources/ClaudeCodeServer",     // репозиторий-источник (git-guard)
//   "AgentDir":    "C:/deploy/ccs-deploy",            // ВНЕ PublishDir — иначе агент залочит себя
//   "PublishDir":  "C:/deploy/claude",                // куда публикуется прод
//   "StagingDir":  "C:/deploy/claude.staging",        // сборка идёт сюда при живом сервере
//   "ReleasesDir": "C:/deploy/claude.releases",       // снимки релизов + deploy-state.json
//   "KeepReleases": 3,
//   "HealthTimeoutSec": 90,
//   "StaleQueuedMinutes": 15,                        // TTL заявки, не сдвинутой агентом
//   "TaskName": "CCS-Deploy"
// }
public sealed class DeployOptions
{
    public bool Enabled { get; init; }
    public string RepoDir { get; init; } = "";
    public string AgentDir { get; init; } = "";
    public string PublishDir { get; init; } = "";
    public string StagingDir { get; init; } = "";
    public string ReleasesDir { get; init; } = "";
    public int KeepReleases { get; init; } = 3;
    public int HealthTimeoutSec { get; init; } = 90;

    /// <summary>
    /// Через сколько минут заявка, которую агент так и не сдвинул с «queued», считается
    /// протухшей. Агент мог не стартовать вовсе (отказ планировщика, собственный guard) —
    /// без TTL такая запись навсегда осталась бы «идущей выкаткой» и давала 409 на всё
    /// следующее, до ручной правки журнала на боевой машине.
    /// </summary>
    public int StaleQueuedMinutes { get; init; } = 15;

    public string TaskName { get; init; } = "CCS-Deploy";

    /// <summary>Журнал выкатки — единственная точка истины и шов с агентом (ADR-010).</summary>
    public string StatePath => Path.Combine(ReleasesDir, DeployState.FileName);

    /// <summary>
    /// Маркер «итог выкатки доложен» — ОТДЕЛЬНЫЙ файл рядом с журналом, а не поле в нём.
    /// Журнал переписывает целиком агент (из своей копии в памяти), и делает это, пока новый
    /// инстанс уже поднялся: отметка в общем файле либо затиралась бы (доклад повторился), либо
    /// затирала бы result агента. Отдельный файл пишет только сервер — записи не пересекаются.
    /// null — идентификатор пришёл из журнала в неожиданном виде: путь по нему не строим.
    /// </summary>
    public string? ReportedMarkerPath(string? deployId) =>
        DeployValidation.IsValidBuildId(deployId)
            ? Path.Combine(ReleasesDir, $"reported-{deployId}")
            : null;

    /// <summary>
    /// Чего не хватает для работы контура; null — всё на месте. Проверяем ДО обращения к
    /// планировщику: «включено, но пути пустые» обязано быть внятным отказом, а не молчаливым
    /// запуском задачи, которой некуда писать журнал.
    /// </summary>
    public string? Misconfiguration()
    {
        if (string.IsNullOrWhiteSpace(RepoDir)) return "не задан Deploy:RepoDir (репозиторий-источник)";
        if (string.IsNullOrWhiteSpace(PublishDir)) return "не задан Deploy:PublishDir";
        if (string.IsNullOrWhiteSpace(ReleasesDir)) return "не задан Deploy:ReleasesDir";
        if (string.IsNullOrWhiteSpace(TaskName)) return "не задано Deploy:TaskName";
        // Имя задачи уходит в командную строку schtasks — принимаем только безопасный набор
        if (!DeployValidation.IsValidTaskName(TaskName))
            return $"недопустимое имя задачи планировщика «{TaskName}»";
        return null;
    }

    public static DeployOptions From(IConfiguration config)
    {
        var keep = config.GetValue<int?>("Deploy:KeepReleases") ?? 3;
        var health = config.GetValue<int?>("Deploy:HealthTimeoutSec") ?? 90;
        var stale = config.GetValue<int?>("Deploy:StaleQueuedMinutes") ?? 15;
        return new DeployOptions
        {
            Enabled = config.GetValue<bool?>("Deploy:Enabled") ?? false,
            RepoDir = config["Deploy:RepoDir"] ?? "",
            AgentDir = config["Deploy:AgentDir"] ?? "",
            PublishDir = config["Deploy:PublishDir"] ?? "",
            StagingDir = config["Deploy:StagingDir"] ?? "",
            ReleasesDir = config["Deploy:ReleasesDir"] ?? "",
            KeepReleases = keep is > 0 and <= 20 ? keep : 3,
            HealthTimeoutSec = health is > 0 and <= 600 ? health : 90,
            StaleQueuedMinutes = stale is > 0 and <= 1440 ? stale : 15,
            TaskName = (config["Deploy:TaskName"] ?? "").Trim() is { Length: > 0 } t ? t : "CCS-Deploy",
        };
    }
}
