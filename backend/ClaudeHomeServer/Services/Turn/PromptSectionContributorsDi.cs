namespace ClaudeHomeServer.Services.Turn;

// Регистрация реестра IPromptSectionContributor (этап 2 плана «Шина событий хода»,
// ADR-013). Новый контрибьютор — одна строка AddPromptSectionContributor<T>() здесь,
// остальное подхватится само: секция регистрируется в SessionManager через
// PromptSectionContributorsRegistration.RegisterAll (фильтр prompt/assembling), а
// контейнер IEnumerable<IPromptSectionContributor> отдаёт уже отсортированным по Order
// (см. PromptSectionContributorsRegistration.cs).
public static class PromptSectionContributorsDi
{
    public static IServiceCollection AddPromptSectionContributors(this IServiceCollection services)
    {
        services.AddPromptSectionContributor<DossierTrailerContributor>();
        services.AddPromptSectionContributor<NotesRecallContributor>();
        services.AddPromptSectionContributor<PersonaRecallContributor>();
        services.AddPromptSectionContributor<PromptSectionsContributor>();
        services.AddPromptSectionContributor<PersonaBindingsContributor>();
        services.AddPromptSectionContributor<CodeGraphContributor>();
        services.AddPromptSectionContributor<PersonaLayerContributor>();
        return services;
    }

    // Контрибьютор регистрируется и по своему типу, и в набор IPromptSectionContributor —
    // как драйвер картинок в ImageGenerationRegistration: общий набор едет в шину,
    // конкретный тип — для прямого резолва в тестах и точечных обёртках.
    public static IServiceCollection AddPromptSectionContributor<T>(this IServiceCollection services)
        where T : class, IPromptSectionContributor
    {
        services.AddSingleton<T>();
        services.AddSingleton<IPromptSectionContributor>(sp => sp.GetRequiredService<T>());
        return services;
    }
}
