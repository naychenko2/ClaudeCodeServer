using ClaudeHomeServer.Controllers;
using ClaudeHomeServer.Services.Tasks;
using ClaudeHomeServer.Services.Tasks.Projector;

namespace ClaudeHomeServer.Services;

// Реализация шва IFeatureChatLinks: членство чата в фичах считает единое правило
// FeatureChatMembership по задачам и снимку сессий владельца (машинный Snapshot — правилу
// нужны все чаты, приватные тоже).
public sealed class FeatureChatLinks(SessionManager sessions, TaskManager tasks) : IFeatureChatLinks
{
    public bool IsBoundToFeature(string ownerId, string chatId) =>
        FeatureChatMembership.Build(tasks.GetByOwner(ownerId).ToList(), FeatureChatDoor.Snapshot(sessions, ownerId))
            .ContainsKey(chatId);
}
