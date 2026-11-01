using ClaudeHomeServer.Models;
using Microsoft.Extensions.DependencyInjection;
using ClaudeHomeServer.Services.Tasks.Projector;
using ClaudeHomeServer.Services.Turn;

namespace ClaudeHomeServer.Services.Tasks;

// Напоминание оркестратору об устаревшем голосе фичи (макет 2 «Сводка фичи», B2). Секция едет
// ХВОСТОМ хода (InTurnTail), в системный блок не попадает никогда: текст меняется от хода к
// ходу, и в системном блоке он обнулял бы prefix cache всей истории (RecallInTurnText,
// docs/architecture/llm-providers.md). Состав tools/list не трогаем — только текст.
//
// Условие: сессия — ключевой чат фичи (первое ЖИВОЕ звено FeatureKeyChain, как keySessionId
// проектора), а голос эпика отсутствует или устарел (brief.note.stale — тот же StaleOf, второго
// правила нет). Закрытые и смерженные фичи молчат.
//
// P2 (фича «Правдивый процент и гант фич»): в том же хвосте — по одной строке на фичу о
// расхождениях FeatureTracker: «В трекере фичи «X» N расхождений: <вид> ×k». Только виды и
// количества: список задач — блок «Сверка трекера» в tasks_get (TasksToolset.cs:1017), там же те
// же имена видов. Нет расхождений — строки нет (хвост не раздувается без причины — тот же мотив,
// что у InTurnTail). Мерж и принятие — из P1 (TerminalState/WaveAfterMerge): смерженная и
// закрытая фича молчит, а «эпик открыт после мержа» и волна после мержа — говорят: их правит
// оркестратор, сервер только показывает.
//
// ISessionDirectory (SessionManager) берётся лениво из IServiceProvider: контрибьюторов собирает
// шина Turn, которую строит сам SessionManager, — прямая зависимость в конструкторе даёт цикл
// DI и зависание резолва при старте хоста.
public sealed class FeatureVoiceReminderContributor(
    TaskManager tasks, FeatureProjector features, IServiceProvider services) : IPromptSectionContributor
{
    public const string SectionKey = "feature-voice-reminder";
    // Больше трёх фич на один ключевой чат — аномалия; хвост держим коротким. Счёт — по фичам,
    // не по строкам: на фичу их теперь до двух (голос + сверка трекера).
    private const int MaxFeatures = 3;

    public string Key => SectionKey;
    public string Title => "Голос фичи и сверка трекера";
    // После правил про локальную модель (710), перед рекомендациями персоны (900)
    public int Order => 720;
    public string Group => "misc";

    public bool IsEnabled(PromptSessionContext sessionContext) =>
        sessionContext.OwnerId is { Length: > 0 };

    public Task<PromptSectionContribution?> BuildAsync(PromptSessionContext sessionContext, string? turnText)
    {
        // Исключения контрибьютор гасит: напоминание не должно ронять ход
        try
        {
            if (sessionContext.OwnerId is not { Length: > 0 } ownerId) return Task.FromResult<PromptSectionContribution?>(null);
            var text = Render(ownerId, sessionContext.Session.Id);
            return Task.FromResult<PromptSectionContribution?>(text is null ? null : new PromptSectionContribution(
                [new PromptSection(Key, text, Title, InTurnTail: true)]));
        }
        catch
        {
            return Task.FromResult<PromptSectionContribution?>(null);
        }
    }

    private string? Render(string ownerId, string sessionId)
    {
        var owner = tasks.GetByOwner(ownerId).ToList();
        // Один проход: задачи фич по FeatureId, чтобы не перебирать весь список на каждый эпик
        var byFeature = owner.Where(t => t.FeatureId is not null)
            .GroupBy(t => t.FeatureId!).ToDictionary(g => g.Key, g => g.ToList());
        var sessions = services.GetRequiredService<ISessionDirectory>();
        var snapshot = OwnerSnapshot(sessions, ownerId);
        var lines = new List<string>();
        var shown = 0;
        foreach (var epic in owner.Where(t => t.Role == TaskRoles.Feature))
        {
            if (shown >= MaxFeatures) break;
            var featureTasks = (byFeature.TryGetValue(epic.Id, out var linked) ? linked : []).Append(epic).ToList();
            var chain = FeatureKeyChain.Of(epic, featureTasks);
            // Дешёвый отсев до проекции: сессии нет в цепочке — фича не наша
            if (!chain.Contains(sessionId)) continue;
            if (chain.FirstOrDefault(id => IsLive(sessions, id, ownerId)) != sessionId) continue;

            var run = features.Project(epic.Id, ownerId, snapshot);
            if (run is null) continue;
            var brief = FeatureBrief.Build(run, null, null, DateTime.UtcNow);
            if (brief.Kind is FeatureBrief.Kinds.Closed or FeatureBrief.Kinds.Merged) continue;

            var title = epic.Title;
            var before = lines.Count;
            if (brief.Note is null)
                lines.Add($"Голоса фичи «{title}» ещё нет — запиши `featureNote` и `featureEstimate` в этом ходе.");
            else if (brief.Note.Stale)
                lines.Add($"Голос фичи «{title}» устарел (этап сменился после записи) — обнови `featureNote` и `featureEstimate` в этом ходе.");
            if (TrackerLine(title, run.Tracker) is { } tracker) lines.Add(tracker);
            if (lines.Count > before) shown++;
        }
        return lines.Count == 0 ? null : string.Join("\n", lines);
    }

    // Строка о расхождениях трекера (P2): «В трекере фичи «X» 3 расхождения: unreviewed ×2,
    // dead_verdict ×1». run.Tracker как есть — своего вывода и второй точки правды нет; группы
    // по числу, затем по имени вида (детерминизм при любом порядке задач в сторе). Задач здесь
    // нет: id/stepRef/момент — в блоке «Сверка трекера» tasks_get, там же те же имена видов.
    // Пустой список — null: строки нет, хвост не раздувается без причины.
    private static string? TrackerLine(string title, List<FeatureTrackerIssue> issues)
    {
        if (issues.Count == 0) return null;
        var kinds = issues.GroupBy(i => i.Kind, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Key} ×{g.Count()}");
        return $"В трекере фичи «{title}» {issues.Count} {Plural(issues.Count, "расхождение", "расхождения", "расхождений")}: {string.Join(", ", kinds)}";
    }

    // Склонение при числе (тот же приём, что PluralSteps/PluralFiles): 1 расхождение,
    // 2 расхождения, 5 расхождений; 11–14 — множественное.
    private static string Plural(int n, string one, string few, string many)
    {
        int d = n % 10, h = n % 100;
        return d == 1 && h != 11 ? one
            : d is >= 2 and <= 4 && (h < 10 || h >= 20) ? few
            : many;
    }

    // Узкий снимок сессий владельца для проекции: те же поля и тот же признак занятости, что у
    // FeatureChatDoor.Snapshot (двери REST/MCP) — «занят» = Starting/Working/Waiting (как
    // ClassifyStall), остальные статусы свободны. Сборка своя: Tasks не видит Controllers
    // (граница вертикалей). Без снимка reported_open не выдаётся вовсе (fail-closed) —
    // с ним хвост называет все шесть видов, что и страница фичи.
    private static List<FeatureIndexSession> OwnerSnapshot(ISessionDirectory sessions, string ownerId) =>
        sessions.GetAll()
            .Where(s => sessions.ResolveOwnerId(s) == ownerId)
            .Select(s => new FeatureIndexSession(s.Id, s.TaskId, s.ProjectId, s.IsArchived, s.Model, s.PersonaId,
                s.Status is SessionStatus.Starting or SessionStatus.Working or SessionStatus.Waiting))
            .ToList();

    // Живое звено: сессия есть и принадлежит владельцу (чужая/удалённая пропускается, как в KeySession)
    private static bool IsLive(ISessionDirectory sessions, string id, string ownerId) =>
        sessions.GetById(id) is { } s && sessions.ResolveOwnerId(s) == ownerId;
}
