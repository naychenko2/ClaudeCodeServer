using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Llm;

namespace ClaudeHomeServer.Services;

// Шов ILocalExecutorRoster (Core): локальные персоны-исполнители владельца и локальность хода
// конкретной задачи. Персона-исполнитель — тот, чья модель для её тира разворачивается в
// локального провайдера; ход задачи — тот, чья эффективная модель (ModelAssignmentResolver.
// ExecutorModel) разворачивается в локального провайдера. Разворот — только ModelAssignmentResolver
// (внутри него UserModelTierResolver.ModelFor — единственная точка склейки «тир → модель»),
// провайдер — IModelResolver.IsLocal; своей логики склейки здесь нет.
// Локальный провайдер выключен (Enabled=false) — персона не локальная: запуск на неё невозможен.
public sealed class LocalExecutorRoster(
    PersonaManager personas, ModelAssignmentResolver models, IModelResolver resolver, LlmProviderRegistry registry)
    : ILocalExecutorRoster
{
    public IReadOnlyList<LocalExecutor> For(string ownerId, string? projectId) =>
        personas.GetForContext(ownerId, projectId)
            .Where(PersonaConsultantToolset.IsExecutor)
            .Where(p => IsLocalPersona(p, ownerId))
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => new LocalExecutor(p.Id, p.Name))
            .ToList();

    public LocalTaskTurn? LocalTurnOf(TaskItem task, string ownerId)
    {
        // Эффективная модель задачи — единой точкой боевого запуска хода (тот же порядок:
        // тир задачи → модель/тир персоны → слот владельца). Не только исполнители: карточка
        // ревью/доработки с переопределённым тиром занимает тот же слот движка.
        var persona = task.PersonaId is { } pid ? personas.GetByIdInternal(pid) : null;
        var model = models.ExecutorModel(task, persona, ownerId);
        if (model is not { } m) return null;
        // Enabled не спрашиваем: ход уже идёт — слот занят независимо от выключателя провайдера
        // (у персоны ниже выключатель нужен: там решается «можно ли запустить»).
        if (resolver.ResolveProviderKeyByModel(m) is not { } key || !resolver.IsLocal(key)) return null;
        return new LocalTaskTurn(persona?.Name);
    }

    private bool IsLocalPersona(Persona persona, string ownerId)
    {
        var model = models.PersonaModel(persona, ownerId,
            LocalActionCatalog.DefaultTierOf(LocalActionCatalog.TasksExecutor));
        if (resolver.ResolveProviderKeyByModel(model) is not { } key) return false;
        return resolver.IsLocal(key) && registry.GetByKey(key)?.Enabled == true;
    }
}
