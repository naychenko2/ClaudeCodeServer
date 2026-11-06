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
        return services;
    }
}
