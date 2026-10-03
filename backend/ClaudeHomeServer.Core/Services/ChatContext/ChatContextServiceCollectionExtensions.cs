using ClaudeHomeServer.Services.Turn;

namespace ClaudeHomeServer.Services.ChatContext;

public static class ChatContextServiceCollectionExtensions
{
    // Композиция спины: реестр видов, рассылка, засев, зеркало фокуса, жизненный цикл и стор. Провайдеры
    // видов добавляют вертикали через AddContextKindProvider<T>(); project-file — встроенный вид спины
    public static IServiceCollection AddChatContext(this IServiceCollection services)
    {
        services.AddContextKindProvider<ProjectFileContextKind>();
        services.AddSingleton<ContextKindRegistry>();
        services.AddSingleton<ChatContextBroadcaster>();
        services.AddSingleton<ChatContextSeeder>();
        services.AddSingleton<IChatContextStore>(sp => new ChatContextStore(
            ChatContextStore.RootFromConfig(sp.GetRequiredService<IConfiguration>()),
            sp.GetRequiredService<ContextKindRegistry>(),
            sp.GetRequiredService<ChatContextBroadcaster>(),
            seeder: sp.GetRequiredService<ChatContextSeeder>()));
        services.AddSingleton<ChatContextFocusMirror>();
        services.AddHostedService(sp => new ChatContextLifecycle(
            ChatContextStore.RootFromConfig(sp.GetRequiredService<IConfiguration>()),
            sp.GetRequiredService<ILogger<ChatContextLifecycle>>(),
            sp.GetService<ITurnEventBus>()));
        return services;
    }
}
