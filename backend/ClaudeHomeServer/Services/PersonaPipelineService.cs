using System.Collections.Concurrent;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services;

// Конвейер пантеона (флаг persona-pipeline): эстафета ролей OmO от задачи до исполнения.
//   1. analysis — Метида: классифицирует намерение, вскрывает риски, даёт директивы;
//   2. plan     — Прометей: пишет план, полный по решениям (с учётом анализа);
//   3. review   — Мом: вердикт [OKAY]/[REJECT]; при REJECT план дорабатывается (до 2 кругов);
//   4. execute  — Сизиф/Гефест: план уходит исполнителю реальным ходом с циклом «до готово».
// Фазы 1-3 — one-shot через PersonaAskService (как совещание). Роли материализуются
// идемпотентно (PersonaManager.ConnectPantheon). Итоги фаз пишутся в историю
// (StoredPipelinePhaseMessage) и транслируются live (pipeline_phase + pipeline_progress).
public class PersonaPipelineService(
    SessionManager sessions,
    PersonaManager personas,
    PersonaAskService ask,
    IConfiguration config,
    ILogger<PersonaPipelineService> log)
{
    private sealed class PipelineHandle
    {
        public required CancellationTokenSource Cts;
        public Task Run = Task.CompletedTask;
    }

    // Активные конвейеры per-session: в одном чате — не больше одного
    private readonly ConcurrentDictionary<string, PipelineHandle> _active = new();

    public const string PhaseAnalysis = "analysis";
    public const string PhasePlan = "plan";
    public const string PhaseReview = "review";
    public const string PhaseExecute = "execute";

    // Максимум кругов доработки плана по вердикту REJECT
    private const int MaxReviewRounds = 2;

    // Допустимые роли-исполнители финальной фазы
    private static readonly string[] ExecutorKeys = ["omo-sisyphus", "omo-hephaestus"];

    // Запуск конвейера (fire-and-forget). Повторный Start в том же чате — InvalidOperationException.
    public string Start(string ownerId, string sessionId, string task, string? executorKey)
    {
        if (string.IsNullOrWhiteSpace(task))
            throw new InvalidOperationException("Пустая задача конвейера");

        var executor = string.IsNullOrWhiteSpace(executorKey) ? "omo-hephaestus" : executorKey.Trim();
        if (!ExecutorKeys.Contains(executor))
            throw new InvalidOperationException("Исполнитель — omo-sisyphus или omo-hephaestus");

        var handle = new PipelineHandle { Cts = new CancellationTokenSource() };
        if (!_active.TryAdd(sessionId, handle))
        {
            handle.Cts.Dispose();
            throw new InvalidOperationException("В этом чате уже идёт конвейер");
        }

        var totalMs = int.TryParse(config["Persona:PipelineTimeoutMs"], out var t) ? t : 900_000;
        handle.Cts.CancelAfter(totalMs);

        var pipelineId = Guid.NewGuid().ToString("N");
        handle.Run = Task.Run(async () =>
        {
            try { await RunAsync(ownerId, sessionId, pipelineId, task.Trim(), executor, handle.Cts.Token); }
            catch (Exception ex)
            {
                log.LogError(ex, "Конвейер {Pipeline} в чате {Session} упал", pipelineId, sessionId);
            }
            finally
            {
                _active.TryRemove(sessionId, out _);
                handle.Cts.Dispose();
            }
        });
        return pipelineId;
    }

    // Отменить конвейер чата (нет активного — false). Запущенное исполнение (цикл «до готово»)
    // не трогает — его останавливают кнопкой «Стоп» композера.
    public bool Cancel(string sessionId)
    {
        if (!_active.TryGetValue(sessionId, out var handle)) return false;
        try { handle.Cts.Cancel(); } catch (ObjectDisposedException) { return false; }
        return true;
    }

    // Задача активного конвейера чата (для тестов и graceful-ожиданий); нет — завершённая
    internal Task WhenDoneAsync(string sessionId) =>
        _active.TryGetValue(sessionId, out var handle) ? handle.Run : Task.CompletedTask;

    private async Task RunAsync(string ownerId, string sessionId, string pipelineId,
        string task, string executorKey, CancellationToken ct)
    {
        try
        {
            // Материализуем роли эстафеты (идемпотентно): аналитик, планировщик, ревьюер, исполнитель
            var roles = personas.ConnectPantheon(ownerId,
                ["omo-metis", "omo-prometheus", "omo-momus", executorKey]);
            var metis = roles.First(p => p.TemplateKey == "omo-metis");
            var prometheus = roles.First(p => p.TemplateKey == "omo-prometheus");
            var momus = roles.First(p => p.TemplateKey == "omo-momus");
            var executor = roles.First(p => p.TemplateKey == executorKey);

            // --- Фаза 1: анализ (Метида) ---
            var analysis = await AskPhaseAsync(sessionId, pipelineId, PhaseAnalysis, ownerId, metis, task,
                "Проанализируй задачу перед планированием: классифицируй намерение, вскрой риски и " +
                "скрытые сложности, сформулируй директивы ОБЯЗАТЕЛЬНО / ЗАПРЕЩЕНО и критичные вопросы. " +
                "На каждый вопрос, где ответа нет, выбери разумный дефолт и явно пометь его.\n\n" +
                $"Задача: {task}", round: 1, ct);
            if (analysis is null) return;

            // --- Фаза 2-3: план (Прометей) → ревью (Мом), до MaxReviewRounds кругов ---
            string? plan = null;
            var lastReview = ""; // последний вердикт — для промпта доработки
            for (var round = 1; round <= MaxReviewRounds; round++)
            {
                var planPrompt = round == 1
                    ? "Составь план решения задачи с учётом анализа Аналитика. План должен быть полным " +
                      "по решениям: исполнителю не остаётся ни одного суждения на его усмотрение — " +
                      "конкретные шаги, файлы, критерии готовности.\n\n" +
                      $"Задача: {task}\n\nАнализ Аналитика:\n{analysis}"
                    : "Доработай план: ревьюер нашёл блокеры. Устрани их, сохранив полноту по решениям.\n\n" +
                      $"Задача: {task}\n\nПрежний план:\n{plan}\n\nБлокеры ревьюера:\n{lastReview}";
                plan = await AskPhaseAsync(sessionId, pipelineId, PhasePlan, ownerId, prometheus, task,
                    planPrompt, round, ct);
                if (plan is null) return;

                var review = await AskPhaseAsync(sessionId, pipelineId, PhaseReview, ownerId, momus, task,
                    "Отревьюй план на исполнимость: сможет ли толковый исполнитель пройти его, не застряв. " +
                    "Вердикт [OKAY] или [REJECT] первой строкой; при REJECT — до трёх конкретных блокеров. " +
                    "Сомневаешься — одобряй.\n\n" +
                    $"Задача: {task}\n\nПлан:\n{plan}", round, ct);
                if (review is null) return;

                // Детект вердикта: наличие [REJECT] → отклонён (иначе одобрено, default to approval)
                if (review.IndexOf("[REJECT]", StringComparison.OrdinalIgnoreCase) < 0)
                    break; // OKAY — план принят

                lastReview = review;
                if (round == MaxReviewRounds)
                {
                    await ProgressAsync(sessionId, pipelineId, "error",
                        error: $"План не прошёл ревью за {MaxReviewRounds} круга — исполнение не запущено");
                    return;
                }
            }

            // --- Фаза 4: исполнение (Сизиф/Гефест) реальным ходом с циклом «до готово» ---
            var session = sessions.GetOwned(sessionId, ownerId);
            if (session is null)
            {
                await ProgressAsync(sessionId, pipelineId, "error", error: "Чат недоступен для исполнения");
                return;
            }

            await ProgressAsync(sessionId, pipelineId, PhaseExecute, status: "running");

            var label = string.IsNullOrWhiteSpace(executor.Role) ? executor.Name : $"{executor.Role} ({executor.Name})";
            // Публикуем карточку фазы ДО хода, потом передаём план исполнителю
            await PublishPhaseAsync(sessionId, pipelineId, PhaseExecute, task, executor.Id,
                $"План передан исполнителю {label}; включён цикл «до готово».", round: 1);
            await ExecuteAsync(sessionId, ownerId, executor, task, plan!, isGroup: session.Participants is not null);

            await ProgressAsync(sessionId, pipelineId, "done");
        }
        catch (OperationCanceledException)
        {
            await ProgressAsync(sessionId, pipelineId, "error",
                error: "Конвейер отменён или превысил лимит времени");
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Конвейер {Pipeline}: ошибка пайплайна", pipelineId);
            await ProgressAsync(sessionId, pipelineId, "error", error: ex.Message);
        }
    }

    // Передача одобренного плана исполнителю реальным ходом с циклом «до готово».
    // Вынесено в virtual-метод как seam: в тестах переопределяется, чтобы не спавнить
    // процесс claude (оркестрация фаз 1-3 проверяется детерминированно).
    // В групповом чате исполняет текущий спикер — персону не меняем.
    internal virtual async Task ExecuteAsync(string sessionId, string ownerId, Persona executor,
        string task, string plan, bool isGroup)
    {
        if (!isGroup)
            sessions.SetPersona(sessionId, ownerId, executor.Id);
        await sessions.SetWorkLoopAsync(sessionId, true);
        await sessions.SendMessageAsync(sessionId,
            $"Выполни план полностью.\n\nЗадача: {task}\n\nПлан (одобрен ревью):\n{plan}", [],
            auto: true, senderPersonaId: executor.Id);
    }

    // One-shot фаза: спрашиваем роль, публикуем результат в историю + live. null — фаза упала.
    private async Task<string?> AskPhaseAsync(string sessionId, string pipelineId, string phase,
        string ownerId, Persona persona, string task, string prompt, int round, CancellationToken ct)
    {
        await ProgressAsync(sessionId, pipelineId, phase, status: "running");
        try
        {
            var answer = await ask.AskAsync(ownerId, persona, prompt, context: null, ct);
            await PublishPhaseAsync(sessionId, pipelineId, phase, task, persona.Id, answer, round);
            await ProgressAsync(sessionId, pipelineId, phase, status: "done");
            return answer;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            await PublishPhaseAsync(sessionId, pipelineId, phase, task, persona.Id, ex.Message, round);
            await ProgressAsync(sessionId, pipelineId, "error", error: $"Фаза «{phase}»: {ex.Message}");
            return null;
        }
    }

    // Фаза завершена: карточка в историю (переживает перезагрузку) + live-сообщение с содержимым
    private async Task PublishPhaseAsync(string sessionId, string pipelineId, string phase,
        string task, string personaId, string text, int round) =>
        await sessions.AppendStoredAsync(sessionId,
            new StoredPipelinePhaseMessage
            {
                PipelineId = pipelineId, Phase = phase, Task = task,
                PersonaId = personaId, Text = text, Round = round,
            },
            new PipelinePhaseMessage(pipelineId, phase, task, personaId, text, round));

    private Task ProgressAsync(string sessionId, string pipelineId, string phase,
        string? personaId = null, string? status = null, string? error = null) =>
        sessions.BroadcastSessionMessageAsync(sessionId,
            new PipelineProgressMessage(pipelineId, phase, status, error));
}
