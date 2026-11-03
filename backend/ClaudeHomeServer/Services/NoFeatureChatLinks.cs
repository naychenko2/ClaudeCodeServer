namespace ClaudeHomeServer.Services;

// Шов IFeatureChatLinks при выключенной подсистеме tasks (TaskManager не зарегистрирован):
// задач нет — привязок нет. Настоящая реализация — Services.Tasks.FeatureChatLinks.
public sealed class NoFeatureChatLinks : IFeatureChatLinks
{
    public bool IsBoundToFeature(string ownerId, string chatId) => false;
}
