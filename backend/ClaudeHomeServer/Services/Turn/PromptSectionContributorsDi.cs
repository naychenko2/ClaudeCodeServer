namespace ClaudeHomeServer.Services.Turn;

// Регистрация реестра IPromptSectionContributor (этап 2 плана «Шина событий хода»,
// ADR-013). Новый контрибьютор — одна строка AddPromptSectionContributor<T>() здесь,
// остальное подхватится само: секция регистрируется в SessionManager через
// PromptSectionContributorsRegistration.RegisterAll (фильтр prompt/assembling), а
// контейнер IEnumerable<IPromptSectionContributor> отдаёт уже отсортированным по Order
// (см. PromptSectionContributorsRegistration.cs).
public static class PromptSectionContributorsDi
{
    // isEnabled — точечный гейт по типу контрибьютора (напр. NotesRecallContributor зависит
    // от NotesKnowledgeService и не должен регистрироваться при выключенной подсистеме Notes).
    // null — все контрибьюторы включены (прежнее поведение, используется тестами и боевым
    // путём для остальных подсистем). Гейт применяется НА РЕГИСТРАЦИИ, а не пост-хок удалением
    // дескрипторов из IServiceCollection: удаление ловило только конкретный тип — интерфейсный
    // форвардер `IPromptSectionContributor → sp.GetRequiredService<T>()` регистрируется через
    // ImplementationFactory (не ImplementationType), и предикат по ImplementationType его не
    // находил, оставляя сироту, которая валила IEnumerable<IPromptSectionContributor> на первом
    // же резолве (найдено при написании теста гейта Subsystems:Notes:Enabled=false).
    public static IServiceCollection AddPromptSectionContributors(
        this IServiceCollection services, Func<Type, bool>? isEnabled = null)
    {
        void Add<T>() where T : class, IPromptSectionContributor
        {
            if (isEnabled is null || isEnabled(typeof(T)))
                services.AddPromptSectionContributor<T>();
        }
        Add<DossierTrailerContributor>();
        Add<NotesRecallContributor>();
        Add<PersonaRecallContributor>();
        Add<PromptSectionsContributor>();
        Add<PersonaBindingsContributor>();
        Add<CodeGraphContributor>();
        Add<PersonaLayerContributor>();
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
