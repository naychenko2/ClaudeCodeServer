using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Llm;

namespace ClaudeHomeServer.Services;

// Разовая миграция опции «Облегчённый контекст»: раньше режим включал провайдер модели
// (local-qwen: BareMode/Trim/KeepMcpTools), теперь у чата с персоной решает Persona.LightContext.
// Персонам, чья модель уходит в провайдер с облегчённым профилем (Кузьма и т.п.), выставляем
// true — иначе поведение молча сменилось бы на полный CLAUDE.md; остальным false.
// Идемпотентна без marker-файла: берутся только персоны с LightContext == null, после прохода
// их не остаётся. Ошибки не роняют старт (best-effort).
public class PersonaLightContextMigration(
    PersonaManager personas,
    ModelAssignmentResolver assignments,
    LlmProviderRegistry providers,
    ILogger<PersonaLightContextMigration> log) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var migrated = personas.MigrateLightContext(persona =>
                UsesLightProvider(persona, LocalActionCatalog.ChatPersona)
                || UsesLightProvider(persona, LocalActionCatalog.TasksExecutor));
            if (migrated > 0)
                log.LogInformation("Миграция «Облегчённого контекста»: решение принято для {Count} персон", migrated);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Миграция «Облегчённого контекста» не выполнена — старт продолжается");
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    // Эффективная модель персоны на месте (чат / исполнитель задач) → есть ли у её провайдера
    // собственный облегчённый профиль. Персона не передаётся в LightProfileFor намеренно:
    // нужен ответ «что было бы по старой логике», а не по новой.
    private bool UsesLightProvider(Persona persona, string usageKey)
    {
        var model = assignments.PersonaModel(persona, persona.OwnerId, LocalActionCatalog.DefaultTierOf(usageKey))
            ?? assignments.Resolve(usageKey, null, persona.OwnerId);
        return providers.LightProfileFor(model, persona: null) is not null;
    }
}
