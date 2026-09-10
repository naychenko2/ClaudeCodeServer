namespace ClaudeHomeServer.Services.Turn;

// Регистрация «чистых» контрибьюторов, оставшихся в Turn (Этап 5, шаг 6 — инверсия).
// Чужие уехали в свои подсистемы:
//
//   * CodeGraphContributor  → вертикаль CodeGraph (ClaudeHomeServer.CodeGraph)
//   * NotesRecallContributor → вертикаль Notes     (ClaudeHomeServer.Notes)
//
// Turn остаётся домом для контрибьюторов, которые тянут несколько вертикалей
// сразу (PersonaLayerContributor, PersonaRecallContributor) или живут на
// стыке root Services, который ещё не вынесен (PersonaBindingsContributor,
// PromptSectionsContributor — оба используют `PersonaBindingsService`,
// `PersonaPromptBuilder`, `FeatureFlagService` — Main root).
//
// Состав секций промпта собирается через `IEnumerable<IPromptSectionContributor>`
// из DI; шина применяет их в порядке `Order` (см. `TurnEventBus.ApplyAsync`).
public static class PromptSectionContributorsDi
{
    public static IServiceCollection AddPromptSectionContributors(this IServiceCollection services)
    {
        services.AddPromptSectionContributor<DossierTrailerContributor>();
        services.AddPromptSectionContributor<PromptSectionsContributor>();
        services.AddPromptSectionContributor<PersonaBindingsContributor>();
        // PersonaLayerContributor и PersonaRecallContributor живут в Turn: тянет по
        // 2–3 вертикали сразу (Skills/Team и Dossiers/Memory/Team соответственно),
        // разбиение размажет логику слоя персоны между файлами. См. отчёт задачи.
        services.AddPromptSectionContributor<PersonaRecallContributor>();
        services.AddPromptSectionContributor<PersonaLayerContributor>();
        return services;
    }
}