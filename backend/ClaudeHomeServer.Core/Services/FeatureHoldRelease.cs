using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

// Снятие удержания фичи — единый путь для ручки DELETE hold и автоснятия очереди. Узкий шов, чтобы
// сервис очереди не тащил за собой весь TaskExecutionService, а тесты подставляли записывающую
// реализацию; в проде это сам TaskExecutionService (другой копии тела снятия быть не должно).
//
// Шов перенесён в Core (дефект 6c71a827, волна 6 «Очередь фич», шаг Q6-BOUND-1): сервис очереди
// переехал в вертикаль Tasks, а Tasks не видит Main; контракт снятия — общий протокол гейта паузы,
// а не логика очереди. По образцу FeatureHoldException/ITaskExecutor: тип шва живёт в спинке,
// реализацию в него ставит Main (Program.cs).

public interface IFeatureHoldReleaser
{
    Task<FeatureHoldRelease> ReleaseFeatureHold(string featureId, string ownerId, FeatureHold? hold, string? by = null);
}

/// <summary>Исход единого снятия удержания фичи.</summary>
public enum FeatureHoldReleaseOutcome { Released, Repeated, NotHeld, NotFound }

/// <summary>Результат снятия удержания фичи (реализация шва — Main: Services/TaskExecutionService.cs,
/// ReleaseFeatureHold): исход и пробуждение очереди (id задач).</summary>
public sealed record FeatureHoldRelease(FeatureHoldReleaseOutcome Outcome, IReadOnlyList<string> Resumed, IReadOnlyList<string> Expired);
