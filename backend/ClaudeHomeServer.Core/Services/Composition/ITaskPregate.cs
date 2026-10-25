using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Composition;

// Шов «предгейт шага»: детерминированные проверки рабочего дерева без модели (tsc, линты,
// сборка с предупреждениями-как-ошибками, сторожа границ, замер интерфейса). Реализует
// вертикаль TestRuns, потребители (переход шага в Done, NC-P1b) берут шов из DI и вертикаль
// напрямую не знают (ADR-014). Подсистема выключена — шов не зарегистрирован, потребитель
// обязан это пережить (GetService, а не GetRequiredService).
public interface ITaskPregate
{
    // Прогнать набор проверок из конфига. ChangedFiles — изменённые файлы от корня дерева через «/»:
    // проверка с фильтром WhenChanged пропускается, если ни один файл не подходит; null — фильтр
    // не применяется (идут все проверки)
    Task<PregateVerdict> RunAsync(PregateRequest request, CancellationToken ct);

    // Потолок одного прогона, с. Потребитель ставит его ДО своих проверок дерева (git): бюджет
    // хода (MCP_TOOL_TIMEOUT) должен считать и git, и проверки целиком, иначе сумма перевалит его.
    // Потолок обязателен быть МЕНЬШЕ MCP_TOOL_TIMEOUT — ответ модели должен успеть уйти.
    int CeilingSeconds { get; }
}

// Потолок прогона предгейта: значение, безопасное для CancelAfter и меньше MCP_TOOL_TIMEOUT
// (600 с). Потребитель берёт потолок из шва, а шов — чужая реализация: значение вне (0; 540]
// в CancelAfter не годится (0 — мгновенный отказ, отрицательное — исключение), а больше
// MCP_TOOL_TIMEOUT нарушает инвариант «ответ модели должен успеть уйти».
public static class PregateCeiling
{
    public const int MaxSeconds = 540;

    // Допустимое значение — как есть; мусор (0, отрицательное, слишком большое) — безопасный
    // потолок по умолчанию, а не исключение на ручке
    public static int Sanitize(int seconds) => seconds is > 0 and <= MaxSeconds ? seconds : MaxSeconds;
}

// UiRoutes — маршруты для ui-probe из постановки задачи (строка «UI-маршруты: …»); пусто — замер невозможен
public sealed record PregateRequest(Project Project, string WorkingDirectory, string SessionId,
    IReadOnlyCollection<string>? ChangedFiles = null, IReadOnlyList<string>? UiRoutes = null);

public enum PregateStatus
{
    Green,
    Red,
    // Проверка не применима к изменениям (фильтр WhenChanged) или честно не смогла замерить
    // по причине вне кода (стенд не отвечает): вердикт она не краснит
    Skipped,
    // Проверка применима к изменениям, но замера не было (адрес стенда не задан, замер недоступен):
    // это не «зелёный» и не «нечего проверять» — вердикт без красных тогда неубедителен
    NotRun,
}

// Detail — для красной проверки текст проблемы (строки с предупреждением или ошибкой), для
// пропущенной — причина; для зелёной пусто
public sealed record PregateCheckResult(string Name, PregateStatus Status, string Detail = "");

// Refusal — предгейт не состоялся (дерево занято, оборван по «Стопу» или потолку): вердикт тогда
// красный, а проверки — те, что успели. Пустой набор проверок — зелёный: проверять нечего
public sealed record PregateVerdict(IReadOnlyList<PregateCheckResult> Checks, string? Refusal = null)
{
    public bool IsGreen => Refusal is null && Checks.All(c => c.Status != PregateStatus.Red);

    // (б) Красных нет, но применимая проверка осталась без замера (NotRun: нет маршрута, адреса стенда): зелёный
    // записывать нельзя и закрывать шаг нельзя. Текст говорит, чего не хватает. null — такого нет
    public string? Unmeasured
    {
        get
        {
            if (Refusal is not null || Checks.Any(c => c.Status == PregateStatus.Red)) return null;
            var unmeasured = Checks.Where(c => c.Status == PregateStatus.NotRun).ToList();
            return unmeasured.Count == 0
                ? null
                : "не выполнен замер: " + string.Join("; ", unmeasured.Select(c => $"{c.Name} ({c.Detail})"));
        }
    }

    // (а) Заданы проверки, но ни одна не применима к изменениям (все Skipped, например правки только в docs/):
    // вердикт NotRun записывается, закрывать можно. Пустой набор (в конфиге нет проверок) сюда не относится
    public bool NothingApplicable => Refusal is null && Checks.Count > 0 && Checks.All(c => c.Status == PregateStatus.Skipped);

    public static PregateVerdict Refused(string reason, IReadOnlyList<PregateCheckResult>? done = null) =>
        new(done ?? [], reason);
}
