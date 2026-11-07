using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Sessions;

// Namespace намеренно Composition, а не Sessions: Program.cs уже импортирует Composition (его
// строка остаётся ровно одной), а сторож формы ядра запрещает ссылки на типы Services.Sessions
// из чего-либо, кроме SessionManager и самих Services.Sessions.
namespace ClaudeHomeServer.Services.Composition;

// Регистрация ядра сессий: роли, вынесенные из SessionManager (docs/research/session-manager-split-2026-11.md).
// Все классы — синглтоны (у SessionManager тоже синглтон-время жизни, а состояние общее).
// Каждый следующий шаг S3 дописывает свои классы сюда же; в Program.cs — одна строка AddSessionCore().
public static class SessionCoreRegistration
{
    public static IServiceCollection AddSessionCore(this IServiceCollection services)
    {
        // Стор метаданных сессий (S3-1): единственный реестр сессий процесса. ITaskLookup (адаптер
        // TaskLookupAdapter над TaskManager, раньше его строил сам SessionManager) уже в DI —
        // регистрация в Program.cs для Spend; второй регистрации того же адаптера не заводим:
        // её ловит DuplicateSingletonRegistrationTests.
        services.AddSingleton(sp => new SessionStore(
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<ProjectManager>(),
            sp.GetRequiredService<ISessionBroadcaster>(),
            sp.GetRequiredService<ILogger<SessionStore>>(),
            sp.GetRequiredService<ITaskLookup>()));

        // --- Роли ядра (S3-2) ---
        // Шов «очередь → приём хода»: ленивый на синглтон SessionManager — прямая зависимость
        // дала бы цикл PendingQueueService → SessionManager → PendingQueueService; на резолве ленивого
        // шва SessionManager ещё не строится, цикл разрывается отложенным вызовом (S3-0 §3).
        services.AddSingleton(sp => new Lazy<ITurnStarter>(() => sp.GetRequiredService<SessionManager>()));

        // Стор копий транскриптов архивных чатов и учёт лимитов подписок — общие экземпляры
        // с ролями: без них TurnCostLedger и CliRoute собрали бы СВОИ, и счётчик лимитов
        // (он же у шлюза LLM) стал бы вторым.
        services.AddSingleton(sp => new ArchivedTranscriptStore(sp.GetRequiredService<IConfiguration>()));
        services.AddSingleton(sp => new SubscriptionLimitRecorder(
            sp.GetRequiredService<UsageService>(),
            sp.GetRequiredService<ClaudeSubscriptionPool>(),
            sp.GetService<SubscriptionActivityTracker>()));

        services.AddSingleton(sp => new SessionHistory(
            sp.GetRequiredService<ChatHistoryService>(),
            sp.GetRequiredService<SessionStore>(),
            sp.GetRequiredService<ILogger<SessionHistory>>()));
        // ISpendCollector опционален (Spend-подсистема может не быть): та же семантика, что у
        // параметра SessionManager — без него аналитика расхода просто не пишется.
        services.AddSingleton(sp => new TurnCostLedger(
            sp.GetRequiredService<FalCostService>(),
            sp.GetRequiredService<GlifAccountService>(),
            sp.GetService<Spend.ISpendCollector>(),
            sp.GetRequiredService<SubscriptionLimitRecorder>(),
            sp.GetRequiredService<SessionHistory>(),
            sp.GetRequiredService<ChatHistoryService>(),
            sp.GetRequiredService<SessionStore>(),
            sp.GetRequiredService<ILogger<TurnCostLedger>>()));
        services.AddSingleton(sp => new PendingQueueService(
            sp.GetRequiredService<SessionStore>(),
            sp.GetRequiredService<ChatHistoryService>(),
            sp.GetRequiredService<SessionHistory>(),
            sp.GetRequiredService<Lazy<ITurnStarter>>(),
            sp.GetRequiredService<ILogger<PendingQueueService>>()));
        services.AddSingleton(sp => new CliRoute(
            sp.GetRequiredService<LlmProviderRegistry>(),
            sp.GetRequiredService<ClaudeSubscriptionPool>(),
            sp.GetRequiredService<ModelAssignmentResolver>(),
            sp.GetRequiredService<UserStore>(),
            sp.GetRequiredService<UserHomeResolver>(),
            sp.GetRequiredService<ClaudeHomeServer.Services.Execution.ILauncherFactory>(),
            sp.GetRequiredService<ClaudeHomeServer.Services.Execution.SandboxManager>(),
            sp.GetRequiredService<ProjectManager>(),
            sp.GetRequiredService<SessionStore>(),
            sp.GetRequiredService<ArchivedTranscriptStore>(),
            sp.GetRequiredService<ILogger<CliRoute>>()));
        return services;
    }
}
