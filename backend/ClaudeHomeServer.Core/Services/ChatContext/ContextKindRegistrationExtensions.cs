namespace ClaudeHomeServer.Services.ChatContext;

// Регистрация провайдера видов контекста в DI — по образцу AddPromptSectionContributor:
// и по своему типу (прямой резолв в тестах), и в набор IContextKindProvider, из которого
// спина собирает реестр видов. Вызывается из *Subsystem.Register вертикали-владельца.
public static class ContextKindRegistrationExtensions
{
    public static IServiceCollection AddContextKindProvider<T>(this IServiceCollection services)
        where T : class, IContextKindProvider
    {
        services.AddSingleton<T>();
        services.AddSingleton<IContextKindProvider>(sp => sp.GetRequiredService<T>());
        return services;
    }
}
