namespace ClaudeHomeServer.Services.TriggerSources;

// Регистрация вертикали TriggerSources (S6-2): резолвер корня и пять источников
// триггеров регистрируются здесь, а не точечно в Program.cs: это типы одной сборки,
// и их регистрация не смотрит ни в Main, ни в другую вертикаль — вертикаль владеет ими
// полностью (ADR-014 «Объём выноса меряется заглушками»). Подсистемы-рубильника у
// вертикали нет, поэтому точка входа — расширение IServiceCollection, как AddChatContext
// в Core. Порядок и состав — ровно те, что были в Program.cs.
//
// Сам rules-движок (AutomationStateStore, PersonaAutomationService) и шов
// IPersonaAutomationRunner живут в Main: они знают SessionManager и персон, — их
// регистрация остаётся в композиции (PersonasRegistration).
public static class TriggerSourcesRegistration
{
    public static IServiceCollection AddTriggerSources(this IServiceCollection services)
    {
        // Резолвер корня для файловых триггеров: путь правила → рабочая папка владельца.
        services.AddSingleton<AutomationRootResolver>();
        // @упоминание — push-триггер, его вызывает сервис из подписки на OnUserMessage
        // (не через ITriggerSource.EvaluateAsync), поэтому он зарегистрирован по своему типу.
        services.AddSingleton<MentionTriggerSource>();
        services.AddSingleton<ITriggerSource, TimerTriggerSource>();
        services.AddSingleton<ITriggerSource, FileTriggerSource>();
        services.AddSingleton<ITriggerSource, NoteTriggerSource>();
        services.AddSingleton<ITriggerSource, GitCommitTriggerSource>();
        services.AddSingleton<ITriggerSource, TaskStatusTriggerSource>();
        return services;
    }
}
