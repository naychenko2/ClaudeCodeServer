namespace ClaudeHomeServer.Services.ChatContext;

public static class ChatContextServiceCollectionExtensions
{
    // Композиция спины: реестр видов, рассылка и стор. Провайдеры видов добавляют вертикали
    // через AddContextKindProvider<T>()
    public static IServiceCollection AddChatContext(this IServiceCollection services)
    {
        services.AddSingleton<ContextKindRegistry>();
        services.AddSingleton<ChatContextBroadcaster>();
        services.AddSingleton<IChatContextStore>(sp => new ChatContextStore(
            ChatContextStore.RootFromConfig(sp.GetRequiredService<IConfiguration>()),
            sp.GetRequiredService<ContextKindRegistry>(),
            sp.GetRequiredService<ChatContextBroadcaster>()));
        return services;
    }
}
