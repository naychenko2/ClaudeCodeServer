using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;
using Microsoft.Extensions.DependencyInjection;
using ClaudeHomeServer.Services.Turn;

namespace ClaudeHomeServer.Services.Tasks;

// Счётчик локальной модели в хвосте хода оркестратора: одна строка вида
// «Локальная модель: свободно слотов 2 из 6. Локальных ходов задач на движке: 3 — Кира (фича «X»,
// S2), … Локальные исполнители: Денис, Кира — одного можно запускать на несколько шагов
// параллельно.» Оркестратор просыпается только от доклада своего чата и не видит чужих ходов —
// строка даёт ему картину по ВСЕМ фичам и чатам владельца. Упор — в слоты движка (счётчик
// допуска, тот же, что пускает ходы; в нём и чаты, и фон), а не в персоны: персону можно
// занять несколькими шагами сразу. Ходы задач перечислены отдельно — по уроку 06.11: при лимите
// 4 разом ушло до 12 ходов, потому что ревью на слабой модели в счёт не входило.
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
        return Format(locals, roster.SlotsFor(ownerId, session.ProjectId),
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

    // Слоты движка (счётчик допуска: все ходы и фон) и ходы задач (любой задачи с локальной
    // эффективной моделью, включая ревью вне ростера) — разные величины: слотов занято может быть
    // больше, чем ходов задач (чаты человека, фоновые действия).
    internal static string Format(IReadOnlyList<LocalExecutor> locals, LocalEngineSlots? slots,
        IReadOnlyList<LocalExecutorLoad.LocalTurn> turns)
    {
        var text = slots is null ? "" : $"Локальная модель: свободно слотов {slots.Free} из {slots.Limit}" +
            (slots.Queued > 0 ? $", в очереди {slots.Queued}" : "") + ". ";
        text += "Локальных ходов задач на движке: " + turns.Count;
        if (turns.Count > 0)
            text += " — " + string.Join(", ", turns.Select(t => $"{t.Name} (фича «{t.FeatureTitle}», {t.Step})"));
        return text + ". Локальные исполнители: " + string.Join(", ", locals.Select(l => l.Name)) +
            " — одного можно запускать на несколько шагов параллельно.";
    }
}
