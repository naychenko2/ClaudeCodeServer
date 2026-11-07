using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Tasks.Projector;

namespace ClaudeHomeServer.Services.Tasks;

// Реализация шва IFeatureChatLinks (живёт в вертикали Tasks, регистрируется вместе с TaskManager):
// привязку чата считает то же правило, что передняя дверь (FeatureChatBinding.BoundFeatures:
// членство FeatureChatMembership ∪ легаси-ось SourceSessionId дочерних задач) — по задачам
// владельца и машинному снимку его сессий (правилу нужны все чаты, приватные тоже). Два разных
// правила дали бы чат, который дверь считает привязанным, а обратное направление — свободным.
public sealed class FeatureChatLinks(ISessionDirectory sessions, TaskManager tasks) : IFeatureChatLinks
{
    public bool IsBoundToFeature(string ownerId, string chatId)
    {
        var snapshot = sessions.GetAll()
            .Where(s => sessions.ResolveOwnerId(s) == ownerId)
            .Select(s => new FeatureIndexSession(s.Id, s.TaskId, s.ProjectId, s.IsArchived, s.Model, s.PersonaId,
                s.Status is SessionStatus.Starting or SessionStatus.Working or SessionStatus.Waiting))
            .ToList();
        return FeatureChatBinding.BoundFeatures(tasks.GetByOwner(ownerId).ToList(), snapshot, chatId).Count > 0;
    }
}
