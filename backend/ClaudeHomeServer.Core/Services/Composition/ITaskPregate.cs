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
}

public sealed record PregateRequest(Project Project, string WorkingDirectory, string SessionId,
    IReadOnlyCollection<string>? ChangedFiles = null);

public enum PregateStatus
{
    Green,
    Red,
    // Проверка не применима к изменениям (фильтр WhenChanged) или честно не смогла замерить
    // по причине вне кода (стенд не отвечает): вердикт она не краснит
    Skipped,
}

// Detail — для красной проверки текст проблемы (строки с предупреждением или ошибкой), для
// пропущенной — причина; для зелёной пусто
public sealed record PregateCheckResult(string Name, PregateStatus Status, string Detail = "");

// Refusal — предгейт не состоялся (дерево занято, оборван по «Стопу» или потолку): вердикт тогда
// красный, а проверки — те, что успели. Пустой набор проверок — зелёный: проверять нечего
public sealed record PregateVerdict(IReadOnlyList<PregateCheckResult> Checks, string? Refusal = null)
{
    public bool IsGreen => Refusal is null && Checks.All(c => c.Status != PregateStatus.Red);

    public static PregateVerdict Refused(string reason, IReadOnlyList<PregateCheckResult>? done = null) =>
        new(done ?? [], reason);
}
