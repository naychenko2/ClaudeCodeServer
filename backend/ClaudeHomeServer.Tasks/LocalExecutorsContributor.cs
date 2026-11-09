using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;
using Microsoft.Extensions.DependencyInjection;
using ClaudeHomeServer.Services.Turn;

namespace ClaudeHomeServer.Services.Tasks;

// Счётчик локальных исполнителей в хвосте хода оркестратора: одна строка вида
// «Локальные исполнители сейчас: занято N из M — Кира (фича «X», S2), Денис (фича «Y», R-3);
// свободны: Кузьма. Локальных ходов на движке: K — …» Оркестратор просыпается только от доклада
// своего чата и не видит чужих занятых персон — строка даёт ему картину по ВСЕМ фичам и чатам
// владельца. Числа два и они разные: персоны ростера (кого занять нельзя) и физические слоты
// локального движка (любой локальный ход, включая ревью и доработки вне ростера) — по уроку
// 06.11: при лимите 4 разом ушло до 12 ходов, потому что ревью на слабой модели в счёт не входило.
//
// Секция едет ХВОСТОМ хода (InTurnTail): текст меняется от хода к ходу и в системном блоке
// обнулял бы prefix cache. Состав tools/list не трогаем (McpToolsetStabilityTests). Признак
// «сессия — ключевой чат открытой фичи» — тот же FeatureKeyChain.FirstLive, что у голоса фичи.
// Локальных персон нет или локальный провайдер выключен (ILocalExecutorRoster пуст) — секции нет.
// Сервисы берутся лениво из IServiceProvider: контрибьюторов собирает шина Turn, которую строит
// сам SessionManager (прямая зависимость даёт цикл DI).
public sealed class LocalExecutorsContributor(TaskManager tasks, IServiceProvider services) : IPromptSectionContributor
{
    public const string SectionKey = "local-executors-load";

    public string Key => SectionKey;
    public string Title => "Локальные исполнители";
    // Сразу после напоминания о голосе фичи (720)
    public int Order => 725;
    public string Group => "misc";

    public bool IsEnabled(PromptSessionContext sessionContext) =>
        sessionContext.OwnerId is { Length: > 0 };

    public Task<PromptSectionContribution?> BuildAsync(PromptSessionContext sessionContext, string? turnText)
    {
        // Исключения гасим: счётчик не должен ронять ход
        try
        {
            if (sessionContext.OwnerId is not { Length: > 0 } ownerId) return Task.FromResult<PromptSectionContribution?>(null);
            var text = Render(ownerId, sessionContext.Session);
            return Task.FromResult<PromptSectionContribution?>(text is null ? null : new PromptSectionContribution(
                [new PromptSection(Key, text, Title, InTurnTail: true)]));
        }
        catch
        {
            return Task.FromResult<PromptSectionContribution?>(null);
        }
    }

    private string? Render(string ownerId, Session session)
    {
        var roster = services.GetService<ILocalExecutorRoster>();
        if (roster is null) return null;
        var sessions = services.GetRequiredService<ISessionDirectory>();
        var owner = tasks.GetByOwner(ownerId);
        if (!IsKeyChatOfOpenFeature(owner, sessions, ownerId, session.Id)) return null;

        var locals = roster.For(ownerId, session.ProjectId);
        if (locals.Count == 0) return null;
        return Format(locals, LocalExecutorLoad.BusyBy(owner, locals, sessions),
            LocalExecutorLoad.LocalTurnsBy(owner, sessions, roster, ownerId));
    }

    // Сессия — ключевой чат хотя бы одной открытой (эпик не закрыт) фичи владельца
    internal static bool IsKeyChatOfOpenFeature(
        IReadOnlyCollection<TaskItem> owner, ISessionDirectory sessions, string ownerId, string sessionId)
    {
        foreach (var epic in owner.Where(t => t.Role == TaskRoles.Feature && t.Status != TaskItemStatus.Done))
        {
            var featureTasks = owner.Where(t => t.FeatureId == epic.Id).Append(epic).ToList();
            if (FeatureKeyChain.FirstLive(FeatureKeyChain.Of(epic, featureTasks), sessions, ownerId) == sessionId)
                return true;
        }
        return false;
    }

    // Две разные величины в одной строке: занятые персоны-исполнители (ростер) и локальные
    // ходы на движке (любой задачи с локальной эффективной моделью, включая ревью вне ростера).
    // «занято 3 из 3» не читается как «движок близок к потолку»: три ревью на слабой модели
    // плюс три исполнителя — это шесть ходов, и видно какие.
    internal static string Format(IReadOnlyList<LocalExecutor> locals,
        IReadOnlyDictionary<string, List<LocalExecutorLoad.BusyEntry>> busy,
        IReadOnlyList<LocalExecutorLoad.LocalTurn> turns)
    {
        var busyParts = locals.Where(l => busy.ContainsKey(l.PersonaId))
            .SelectMany(l => busy[l.PersonaId].Select(b => $"{l.Name} (фича «{b.FeatureTitle}», {b.Step})"))
            .ToList();
        var busyCount = locals.Count(l => busy.ContainsKey(l.PersonaId));
        var free = locals.Where(l => !busy.ContainsKey(l.PersonaId)).Select(l => l.Name).ToList();
        var text = $"Локальные исполнители сейчас: занято {busyCount} из {locals.Count}";
        if (busyParts.Count > 0) text += " — " + string.Join(", ", busyParts);
        if (free.Count > 0) text += "; свободны: " + string.Join(", ", free);
        text += ". Локальных ходов на движке: " + turns.Count;
        if (turns.Count > 0)
            text += " — " + string.Join(", ", turns.Select(t => $"{t.Name} (фича «{t.FeatureTitle}», {t.Step})"));
        return text + ".";
    }
}
