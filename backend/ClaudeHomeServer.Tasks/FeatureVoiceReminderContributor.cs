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
// ISessionDirectory (SessionManager) берётся лениво из IServiceProvider: контрибьюторов собирает
// шина Turn, которую строит сам SessionManager, — прямая зависимость в конструкторе даёт цикл
// DI и зависание резолва при старте хоста.
public sealed class FeatureVoiceReminderContributor(
    TaskManager tasks, FeatureProjector features, IServiceProvider services) : IPromptSectionContributor
{
    public const string SectionKey = "feature-voice-reminder";
    // Больше трёх фич на один ключевой чат — аномалия; хвост держим коротким
    private const int MaxFeatures = 3;

    public string Key => SectionKey;
    public string Title => "Голос фичи";
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
        var lines = new List<string>();
        foreach (var epic in owner.Where(t => t.Role == TaskRoles.Feature))
        {
            if (lines.Count >= MaxFeatures) break;
            var featureTasks = (byFeature.TryGetValue(epic.Id, out var linked) ? linked : []).Append(epic).ToList();
            var chain = FeatureKeyChain.Of(epic, featureTasks);
            // Дешёвый отсев до проекции: сессии нет в цепочке — фича не наша
            if (!chain.Contains(sessionId)) continue;
            if (chain.FirstOrDefault(id => IsLive(sessions, id, ownerId)) != sessionId) continue;

            var run = features.Project(epic.Id, ownerId);
            if (run is null) continue;
            var brief = FeatureBrief.Build(run, null, null, DateTime.UtcNow);
            if (brief.Kind is FeatureBrief.Kinds.Closed or FeatureBrief.Kinds.Merged) continue;

            var title = epic.Title;
            if (brief.Note is null)
                lines.Add($"Голоса фичи «{title}» ещё нет — запиши `featureNote` и `featureEstimate` в этом ходе.");
            else if (brief.Note.Stale)
                lines.Add($"Голос фичи «{title}» устарел (этап сменился после записи) — обнови `featureNote` и `featureEstimate` в этом ходе.");
        }
        return lines.Count == 0 ? null : string.Join("\n", lines);
    }

    // Живое звено: сессия есть и принадлежит владельцу (чужая/удалённая пропускается, как в KeySession)
    private static bool IsLive(ISessionDirectory sessions, string id, string ownerId) =>
        sessions.GetById(id) is { } s && sessions.ResolveOwnerId(s) == ownerId;
}
