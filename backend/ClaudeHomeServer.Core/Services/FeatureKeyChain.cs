using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Tasks;

// Цепочка ключевого чата в порядке звеньев (без дублей): epic.SourceSessionId →
// epic.ExecutorLaunchedBySessionId → самый частый ExecutorLaunchedBySessionId у дочерних
// (эпик исключён; при равенстве частот — лексикографически меньший id, детерминизм).
//
// Перенесена из FeatureProjector в Core (дефект 79d0fa9a, волна 4 «Обзор фич»): чистая
// функция на TaskItem нужна и вертикали Tasks (KeySession), и корневому
// TaskExecutionService — а ссылка корня на FeatureProjector ловится
// RootSubsystemBoundaryTests. Namespace вертикали сохранён: для вызывающих из Tasks
// ничего не меняется.
public static class FeatureKeyChain
{
    public static List<string> Of(TaskItem epic, IReadOnlyList<TaskItem> featureTasks)
    {
        var chain = new List<string>();
        if (epic.SourceSessionId is not null) chain.Add(epic.SourceSessionId);
        if (epic.ExecutorLaunchedBySessionId is not null) chain.Add(epic.ExecutorLaunchedBySessionId);
        var mostFrequent = featureTasks
            .Where(t => !ReferenceEquals(t, epic) && t.ExecutorLaunchedBySessionId is not null)
            .GroupBy(t => t.ExecutorLaunchedBySessionId!)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key)
            .Select(g => g.Key)
            .FirstOrDefault();
        if (mostFrequent is not null) chain.Add(mostFrequent);
        return chain.Distinct().ToList();
    }
}
