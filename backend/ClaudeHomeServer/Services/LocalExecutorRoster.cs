using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Llm;

namespace ClaudeHomeServer.Services;

// Локальные персоны-исполнители владельца (шов ILocalExecutorRoster, Core): персона-исполнитель,
// чья модель для её тира разворачивается в локального провайдера. Разворот — только
// ModelAssignmentResolver (внутри него UserModelTierResolver.ModelFor — единственная точка склейки
// «тир → модель»), провайдер — IModelResolver.IsLocal; своей логики склейки здесь нет.
// Локальный провайдер выключен (Enabled=false) — персона не локальная: запуск на него невозможен.
public sealed class LocalExecutorRoster(
    PersonaManager personas, ModelAssignmentResolver models, IModelResolver resolver, LlmProviderRegistry registry)
    : ILocalExecutorRoster
{
    public IReadOnlyList<LocalExecutor> For(string ownerId, string? projectId) =>
        personas.GetForContext(ownerId, projectId)
            .Where(PersonaConsultantToolset.IsExecutor)
            .Where(p => IsLocal(p, ownerId))
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => new LocalExecutor(p.Id, p.Name))
            .ToList();

    private bool IsLocal(Persona persona, string ownerId)
    {
        var model = models.PersonaModel(persona, ownerId,
            LocalActionCatalog.DefaultTierOf(LocalActionCatalog.TasksExecutor));
        if (resolver.ResolveProviderKeyByModel(model) is not { } key) return false;
        return resolver.IsLocal(key) && registry.GetByKey(key)?.Enabled == true;
    }
}
