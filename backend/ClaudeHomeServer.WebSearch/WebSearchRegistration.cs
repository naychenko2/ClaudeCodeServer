using ClaudeHomeServer.Services.Http;

namespace ClaudeHomeServer.Services.WebSearch;

// Регистрация вертикали WebSearch (S6-2): опции Perplexity, её HTTP-клиент и сам сервис
// собираются здесь, а не в Program.cs. Все три — собственность этой сборки: клиент нужен
// ровно PerplexitySearchService (его HttpClientName), опции — его конструктору; вне
// вертикали их никто не резолвит (потребитель — WebSearchToolset, он берёт сам сервис).
// Регистрация безусловна, как раньше: рубильника у вертикали нет, а «ключа нет» уже
// разбирает сам сервис (IsConfigured=false → тулсет хода не объявляется).
public static class WebSearchRegistration
{
    public static IServiceCollection AddWebSearch(this IServiceCollection services, IConfiguration config)
    {
        // Веб-поиск Perplexity Sonar (MCP-сервер websearch): сервис зарубежный — клиент ходит
        // ЧЕРЕЗ egress-прокси (WithoutEgressProxy тут НЕ звать, в отличие от локальных Dify/Forgejo).
        // Опциональная зависимость: пустой Perplexity:ApiKey выключает сервер целиком, а лежащий
        // сервис не должен сыпать в консоль стектрейсами на каждый запрос
        services.AddSingleton(PerplexityOptions.FromConfig(config));
        services.AddQuietHttpClient(
            PerplexitySearchService.HttpClientName,
            new QuietHttpClientProfile(
                Category: "ClaudeHomeServer.WebSearch.Perplexity",
                Subject: "сервисом веб-поиска Perplexity",
                Consequence: "Инструмент web_search вернёт модели отказ — сам ход это не ломает."));
        services.AddSingleton<PerplexitySearchService>();
        return services;
    }
}
