using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Tasks.Projector;

// Агрегат фичи (режим «Исполнение фичи», docs/research/turn-plan-feature-mode-cut-2026-10.md):
// чистая проекция задач трекера (эпик + дочерние) в конвейер фичи. НЕ второй источник правды —
// держит ссылки на TaskItem'ы трекера и вычисляет только то, что не лежит в полях: счётчики
// вердиктов, прогресс, готовность. Эпик и дочерние — те же TaskItem, что в сторе.
public sealed class FeatureRun
{
    // Id задачи-эпика фичи.
    public string FeatureId { get; init; } = "";
    // Эпик фичи (role=feature, FeatureId=null).
    public TaskItem Epic { get; init; } = null!;
    // Шаги и доработки (role=step/rework) — в порядке, в котором задачи дал вызывающий.
    public List<TaskItem> Steps { get; init; } = [];
    // Ревью (role=review) — в порядке вызывающего.
    public List<TaskItem> Reviews { get; init; } = [];
    // Проверки (role=qa) — в порядке вызывающего.
    public List<TaskItem> Qa { get; init; } = [];
    // Финал (role=final) — последний; null — финала нет.
    public TaskItem? Final { get; init; }
    // Мерж (role=merge) — последний; null — мержа нет.
    public TaskItem? Merge { get; init; }
    // «Ждёт вас»: с какого момента и почему (у эпика).
    public DateTime? AwaitingHumanSince { get; init; }
    public string? AwaitingHumanReason { get; init; }
    // Счётчики исходов витков (вердиктов) по ревью+проверке+финалу.
    public FeatureVerdicts Verdicts { get; init; } = new();
    // Доля решённых витков: (ревью/проверка/финал с вердиктом) / (все такие задачи). 0..1.
    public double Progress { get; init; }
    // Готово: финал закрыт с accepted/accepted_with_remarks.
    public bool Ready { get; init; }
}

// Счётчики исходов витков (вердиктов) ревью/проверки/финала.
public sealed class FeatureVerdicts
{
    public int Accepted { get; init; }
    public int AcceptedWithRemarks { get; init; }
    public int Returned { get; init; }
    public int Withdrawn { get; init; }
    // ↺: возвраты + «с правками» (работа вернулась к исполнителю).
    public int Loops => Returned + AcceptedWithRemarks;
}
