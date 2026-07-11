using System.Collections.Concurrent;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services;

// Совещание персон (P7, cross-attack): три фазы one-shot вызовов —
//   1. independent — участники независимо формулируют позиции (параллельно, ≤3);
//   2. attack — каждый видит позиции остальных: критикует слабое, защищает своё,
//      явно уступает более сильному аргументу; скорректированная позиция;
//   3. synthesis — ведущая (первая в списке) сводит итог: консенсус / разногласия / рекомендация.
// Итоги фаз пишутся в историю чата вне хода (StoredMeetingPhaseMessage) и
// транслируются live (meeting_phase + meeting_progress). Всего ~2N+1 вызовов.
public sealed class PersonaMeetingService(
    SessionManager sessions,
    PersonaManager personas,
    PersonaAskService ask,
    IConfiguration config,
    ILogger<PersonaMeetingService> log)
{
    private sealed class MeetingHandle
    {
        public required CancellationTokenSource Cts;
        public Task Run = Task.CompletedTask;
    }

    // Активные совещания per-session: в одном чате — не больше одного
    private readonly ConcurrentDictionary<string, MeetingHandle> _active = new();

    public const string PhaseIndependent = "independent";
    public const string PhaseAttack = "attack";
    public const string PhaseSynthesis = "synthesis";

    // Запуск совещания (fire-and-forget). Повторный Start в том же чате — InvalidOperationException.
    public string Start(string ownerId, string sessionId, string question, IReadOnlyList<string> personaIds)
    {
        if (string.IsNullOrWhiteSpace(question))
            throw new InvalidOperationException("Пустой вопрос совещания");

        var ids = (personaIds ?? Array.Empty<string>())
            .Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList();
        if (ids.Count is < 2 or > 4)
            throw new InvalidOperationException("В совещании участвуют от 2 до 4 персон");
        var members = ids.Select(id => personas.Get(id, ownerId)
            ?? throw new KeyNotFoundException($"Персона не найдена: {id}")).ToList();

        var handle = new MeetingHandle { Cts = new CancellationTokenSource() };
        if (!_active.TryAdd(sessionId, handle))
        {
            handle.Cts.Dispose();
            throw new InvalidOperationException("В этом чате уже идёт совещание");
        }

        // Общий колпак длительности совещания
        var totalMs = int.TryParse(config["Persona:MeetingTimeoutMs"], out var t) ? t : 600_000;
        handle.Cts.CancelAfter(totalMs);

        var meetingId = Guid.NewGuid().ToString("N");
        handle.Run = Task.Run(async () =>
        {
            try { await RunAsync(ownerId, sessionId, meetingId, question.Trim(), members, handle.Cts.Token); }
            catch (Exception ex)
            {
                // Свой catch внутри RunAsync; сюда попадают только неожиданные ошибки пайплайна
                log.LogError(ex, "Совещание {Meeting} в чате {Session} упало", meetingId, sessionId);
            }
            finally
            {
                _active.TryRemove(sessionId, out _);
                handle.Cts.Dispose();
            }
        });
        return meetingId;
    }

    // Отменить совещание чата (нет активного — false)
    public bool Cancel(string sessionId)
    {
        if (!_active.TryGetValue(sessionId, out var handle)) return false;
        try { handle.Cts.Cancel(); } catch (ObjectDisposedException) { return false; }
        return true;
    }

    // Задача активного совещания чата (для тестов и graceful-ожиданий); нет — завершённая
    internal Task WhenDoneAsync(string sessionId) =>
        _active.TryGetValue(sessionId, out var handle) ? handle.Run : Task.CompletedTask;

    private async Task RunAsync(string ownerId, string sessionId, string meetingId,
        string question, List<Persona> members, CancellationToken ct)
    {
        try
        {
            // --- Фаза 1: независимые позиции (параллельно, ≤3) ---
            var jobs1 = members.Select(p => (Persona: p,
                Question: $"Совещание команды. Вопрос: {question}\n\n" +
                          "Сформулируй свою НЕЗАВИСИМУЮ позицию: 2-5 тезисов с аргументами. " +
                          "Мнения других участников ты пока не знаешь — не выдумывай их.",
                Context: (string?)null)).ToList();
            var phase1 = await RunPhaseAsync(sessionId, meetingId, PhaseIndependent, ownerId, jobs1, ct);
            await PublishPhaseAsync(sessionId, meetingId, PhaseIndependent, question, phase1);

            var alive = members.Where(p => phase1.First(e => e.PersonaId == p.Id) is { IsError: false }).ToList();
            if (alive.Count < 2)
            {
                await ProgressAsync(sessionId, meetingId, "error",
                    error: "Меньше двух участников ответили — совещание прервано");
                return;
            }

            // --- Фаза 2: перекрёстная атака (каждому — позиции остальных).
            // Формулировки — из hyperplan OmO (раунды 2-3: атака + защита/уточнение/уступка) ---
            var positions = phase1.Where(e => !e.IsError).ToDictionary(e => e.PersonaId, e => e.Text);
            var jobs2 = alive.Select(p => (Persona: p,
                Question: "Перекрёстная атака на совещании. Вопрос совещания: " + question + "\n\n" +
                          "Позиции коллег:\n" + PositionsBlock(alive.Where(x => x.Id != p.Id), positions) + "\n\n" +
                          "Твоя прежняя позиция:\n" + positions[p.Id] + "\n\n" +
                          "АТАКУЙ тезисы коллег со своей позиции: по 1-3 конкретных атаки на оппонента " +
                          "(каждая ≤3 предложений, с доказательствами или рассуждением). Никакой коллегиальной " +
                          "вежливости: слабый тезис — разноси; сильный отмечай «УСТОЯЛ — причина» и двигайся дальше.\n" +
                          "Затем пройдись по СВОИМ тезисам глазами оппонентов и для каждого спорного выбери: " +
                          "ЗАЩИТА (опровергни конкретикой), УТОЧНЕНИЕ (атака попала — переформулируй сильнее) " +
                          "или УСТУПКА (признай и скажи, что уцелело). Будь честен: гордость здесь враг, " +
                          "выживают только обоснованные позиции.\n" +
                          "В конце — твоя скорректированная позиция.",
                Context: (string?)null)).ToList();
            var phase2 = await RunPhaseAsync(sessionId, meetingId, PhaseAttack, ownerId, jobs2, ct);
            await PublishPhaseAsync(sessionId, meetingId, PhaseAttack, question, phase2);

            // --- Фаза 3: синтез от ведущей (первая в списке; её модель) ---
            var leader = members[0];
            var critique = phase2.Where(e => !e.IsError).ToDictionary(e => e.PersonaId, e => e.Text);
            // Синтез — дистилляция инсайтов по hyperplan (фаза 5): выживает только обоснованное
            var synthesisQuestion =
                $"Ты вёл(а) совещание по вопросу: {question}\n\n" +
                "Независимые позиции участников:\n" + PositionsBlock(alive, positions) + "\n\n" +
                "Перекрёстная атака и скорректированные позиции:\n" +
                PositionsBlock(alive.Where(p => critique.ContainsKey(p.Id)), critique) + "\n\n" +
                "Дистиллируй итог, оставив только обоснованные инсайты: тезисы, которые не были атакованы, " +
                "были защищены конкретикой или уточнены до более сильной формы; всё, что уступлено, — выброси.\n" +
                "Разложи результат: 1) консенсус и принятые решения (с цепочкой рассуждений); " +
                "2) риски и способы их смягчения; 3) открытые вопросы — разногласия, которые не сошлись, " +
                "с аргументами сторон; 4) твоя рекомендация — что делать. Пиши от своего лица.";
            await ProgressAsync(sessionId, meetingId, PhaseSynthesis, leader.Id, "running");
            MeetingEntry synthesis;
            try
            {
                var text = await ask.AskAsync(ownerId, leader, synthesisQuestion, context: null, ct);
                synthesis = new MeetingEntry { PersonaId = leader.Id, Text = text };
                await ProgressAsync(sessionId, meetingId, PhaseSynthesis, leader.Id, "done");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                synthesis = new MeetingEntry { PersonaId = leader.Id, Text = ex.Message, IsError = true };
                await ProgressAsync(sessionId, meetingId, PhaseSynthesis, leader.Id, "error");
            }
            await PublishPhaseAsync(sessionId, meetingId, PhaseSynthesis, question, [synthesis]);

            await ProgressAsync(sessionId, meetingId, "done");
        }
        catch (OperationCanceledException)
        {
            await ProgressAsync(sessionId, meetingId, "error",
                error: "Совещание отменено или превысило лимит времени");
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Совещание {Meeting}: ошибка пайплайна", meetingId);
            await ProgressAsync(sessionId, meetingId, "error", error: ex.Message);
        }
    }

    // Параллельный прогон фазы (не больше 3 одновременных one-shot). Порядок entries —
    // порядок jobs; упавшая персона → IsError-entry (совещание продолжается без неё).
    private async Task<List<MeetingEntry>> RunPhaseAsync(string sessionId, string meetingId,
        string phase, string ownerId, List<(Persona Persona, string Question, string? Context)> jobs,
        CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(3);
        var entries = new MeetingEntry[jobs.Count];
        var tasks = jobs.Select(async (job, i) =>
        {
            await gate.WaitAsync(ct);
            try
            {
                await ProgressAsync(sessionId, meetingId, phase, job.Persona.Id, "running");
                var answer = await ask.AskAsync(ownerId, job.Persona, job.Question, job.Context, ct);
                entries[i] = new MeetingEntry { PersonaId = job.Persona.Id, Text = answer };
                await ProgressAsync(sessionId, meetingId, phase, job.Persona.Id, "done");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                entries[i] = new MeetingEntry { PersonaId = job.Persona.Id, Text = ex.Message, IsError = true };
                await ProgressAsync(sessionId, meetingId, phase, job.Persona.Id, "error");
            }
            finally { gate.Release(); }
        }).ToArray();
        await Task.WhenAll(tasks);
        return entries.ToList();
    }

    // Блок «@handle (Роль): позиция» для промптов фаз 2-3
    private static string PositionsBlock(IEnumerable<Persona> people, IReadOnlyDictionary<string, string> texts)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var p in people)
        {
            var title = string.IsNullOrWhiteSpace(p.Role) ? p.Name : $"{p.Role} ({p.Name})";
            sb.AppendLine($"--- @{p.Handle} — {title} ---");
            sb.AppendLine(texts.TryGetValue(p.Id, out var text) ? text : "(нет ответа)");
        }
        return sb.ToString().TrimEnd();
    }

    // Фаза завершена: карточка в историю (переживает перезагрузку) + live-сообщение с
    // содержимым + прогресс «фаза done»
    private async Task PublishPhaseAsync(string sessionId, string meetingId, string phase,
        string question, List<MeetingEntry> entries)
    {
        await sessions.AppendStoredAsync(sessionId,
            new StoredMeetingPhaseMessage { MeetingId = meetingId, Phase = phase, Question = question, Entries = entries },
            new MeetingPhaseMessage(meetingId, phase, question, entries));
        await ProgressAsync(sessionId, meetingId, phase, personaId: null, status: "done");
    }

    private Task ProgressAsync(string sessionId, string meetingId, string phase,
        string? personaId = null, string? status = null, string? error = null) =>
        sessions.BroadcastSessionMessageAsync(sessionId,
            new MeetingProgressMessage(meetingId, phase, personaId, status, error));
}
