using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

// Цепочка ключевого чата в порядке звеньев (без дублей): (1) самый частый
// ExecutorLaunchedBySessionId у дочерних (эпик исключён; при равенстве частот —
// лексикографически меньший id, детерминизм) → (2) epic.SourceSessionId →
// (3) epic.ExecutorLaunchedBySessionId.
// Почему первым идёт чат-запускающий: по этой цепочке уходят ВСЕ доклады задач фичи, а эпик
// часто заводят в чате архитектора или человека, тогда как фичу ведёт оркестратор в своём
// чате, откуда и запускают шаги. Чат создания эпика — лишь запасное звено.
//
// Перенесена из FeatureProjector в Core (дефект 79d0fa9a, волна 4 «Обзор фич»): чистая
// функция на TaskItem нужна и вертикали Tasks (KeySession), и корневому
// TaskExecutionService — а ссылка корня на FeatureProjector ловится
// RootSubsystemBoundaryTests. По прецеденту TaskUrl — в корневном namespace
// ClaudeHomeServer.Services (вердикт W4-GATE2): вызывающие из Tasks видят её через
// внешний namespace без using; широкой записи namespace в allow-list нет.
public static class FeatureKeyChain
{
    public static List<string> Of(TaskItem epic, IReadOnlyList<TaskItem> featureTasks)
    {
        var chain = new List<string>();
        var mostFrequent = featureTasks
            .Where(t => !ReferenceEquals(t, epic) && t.ExecutorLaunchedBySessionId is not null)
            .GroupBy(t => t.ExecutorLaunchedBySessionId!)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key)
            .Select(g => g.Key)
            .FirstOrDefault();
        if (mostFrequent is not null) chain.Add(mostFrequent);
        if (epic.SourceSessionId is not null) chain.Add(epic.SourceSessionId);
        if (epic.ExecutorLaunchedBySessionId is not null) chain.Add(epic.ExecutorLaunchedBySessionId);
        return chain.Distinct().ToList();
    }

    // ЕДИНСТВЕННАЯ точка правды «ключевой чат фичи»: первое ЖИВОЕ звено цепочки.
    // isLive(id) → false, если сессии нет или она чужая (мёртвое звено пропускается).
    // Живых звеньев нет → null. Зовут все, кто берёт «первое живое звено»: карточки
    // TaskExecutionService, KeySession проектора, FeatureVoiceReminderContributor,
    // маршрутизация доклада, пинок простоя.
    public static string? FirstLive(IEnumerable<string> chain, Func<string, bool> isLive) =>
        chain.FirstOrDefault(isLive);

    // Вариант по живому справочнику сессий: «живая» — есть в справочнике и принадлежит ownerId.
    public static string? FirstLive(IEnumerable<string> chain, ISessionDirectory sessions, string ownerId) =>
        FirstLive(chain, id => sessions.GetById(id) is { } s && sessions.ResolveOwnerId(s) == ownerId);
}
