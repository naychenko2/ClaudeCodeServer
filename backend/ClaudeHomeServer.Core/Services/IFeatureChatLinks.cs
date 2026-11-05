namespace ClaudeHomeServer.Services;

// Узкий шов «чат привязан к фиче?» (S2d-7b-2). Нужен SessionManager.SetChatVisibility: чат,
// уже входящий в фичу (ключевой, шага, исполнитель — единое правило FeatureChatMembership),
// приватным не делается. Знание о привязках живёт в вертикали Tasks, прямой зависимости
// ядро → Tasks нет: SessionManager берёт реализацию лениво из IServiceProvider (не из
// конструктора), поэтому цикла DI нет.
public interface IFeatureChatLinks
{
    // true — чат входит хотя бы в одну фичу владельца ownerId.
    bool IsBoundToFeature(string ownerId, string chatId);
}
