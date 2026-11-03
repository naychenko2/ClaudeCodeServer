using ClaudeHomeServer.Controllers;
using ClaudeHomeServer.Services.Tasks;
using ClaudeHomeServer.Services.Tasks.Projector;

namespace ClaudeHomeServer.Services;

// Реализация шва IFeatureChatLinks: привязку чата считает то же правило, что передняя дверь
// (FeatureChatBinding.BoundFeatures: членство FeatureChatMembership ∪ легаси-ось SourceSessionId
// дочерних задач) — по задачам и снимку сессий владельца (машинный Snapshot — правилу нужны все
// чаты, приватные тоже). Два разных правила дали бы чат, который дверь считает привязанным,
// а обратное направление — свободным.
public sealed class FeatureChatLinks(SessionManager sessions, TaskManager tasks) : IFeatureChatLinks
{
    public bool IsBoundToFeature(string ownerId, string chatId) =>
        FeatureChatBinding.BoundFeatures(
            tasks.GetByOwner(ownerId).ToList(), FeatureChatDoor.Snapshot(sessions, ownerId), chatId).Count > 0;
}

// Шов при выключенной подсистеме tasks (TaskManager не зарегистрирован): задач нет — привязок нет.
public sealed class NoFeatureChatLinks : IFeatureChatLinks
{
    public bool IsBoundToFeature(string ownerId, string chatId) => false;
}
