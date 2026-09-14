namespace ClaudeHomeServer.Services.Turn;

// Регистрация одного контрибьютора секции промпта в DI.
//
// Контрибьютор регистрируется и по своему типу, и в набор IPromptSectionContributor —
// как драйвер картинок в ImageGenerationRegistration: общий набор едет в шину,
// конкретный тип — для прямого резолва в тестах и точечных обёртках.
//
// Этап 5, шаг 6 (инверсия контрибьюторов): extension переехал в Core из
// `Services/Turn/PromptSectionContributorsDi.cs`, потому что реализации
// `IPromptSectionContributor` теперь живут в чужих вертикалях (CodeGraph, Notes,
// …), а каждая вертикаль регистрирует свой контрибьютор в своём `*Subsystem.Register`.
// Turn больше не знает про конкретные классы контрибьюторов — только собирает
// `IEnumerable<IPromptSectionContributor>` из DI и подключает к шине.
public static class PromptSectionContributorRegistrationExtensions
{
    public static IServiceCollection AddPromptSectionContributor<T>(
        this IServiceCollection services)
        where T : class, IPromptSectionContributor
    {
        services.AddSingleton<T>();
        services.AddSingleton<IPromptSectionContributor>(sp => sp.GetRequiredService<T>());
        return services;
    }
}