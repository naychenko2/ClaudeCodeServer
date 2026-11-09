using ClaudeHomeServer.Services.Http;

namespace ClaudeHomeServer.Services.Composition;

// Общие HTTP-клиенты бэкенда (S6-2): именованные клиенты внешних и локальных сервисов,
// через которые ходят несколько вертикалей сразу (fal — генерация и биллинг, glif —
// генерация, OnlyOffice Command API — файлы, MCP probe/OAuth/catalog — реестр владельца,
// llm-provider/alibaba-console/anthropic-oauth — модельный слой).
//
// Почему в композиции, а не в вертикалях: клиент общий, а `IHttpClientFactory` отдаёт его
// по имени тому, кто спросил; регистрация внутри одной вертикали сделала бы его зависимым
// от её рубильника и скрыла бы от остальных потребителей. Клиенты локальных сервисов
// (dify, forgejo) уже у своих вертикалей — там обязателен `WithoutEgressProxy`.
//
// Порядок и состав — ровно те, что были в Program.cs. `AddHttpForwarder()` (YARP) и
// конфигурация ReverseProxy остаются в Program.cs: это конфигурация хоста.
public static class HttpClientRegistration
{
    public static IServiceCollection AddSharedHttpClients(this IServiceCollection services, IConfiguration config)
    {
        // Dify и fal — опциональные зависимости: локальный Dify поднят не всегда, fal живёт за DPI,
        // и оба вызывающих ловят отказ сами (KnowledgeService деградирует, FalImageService возвращает
        // пустой список). Тихий клиент вместо дефолтного — иначе каждый запрос печатает Error
        // со стектрейсом; см. Services/Http/QuietHttpLogger.
        // HTTP-клиент `dify` зарегистрирован внутри KnowledgeSubsystem (волна 3, шаг 3): Dify —
        // локальный сервис, и `WithoutEgressProxy` обязателен (инвариант, как у Forgejo).
        // Forgejo — клиент локального Git-сервера: зарегистрирован внутри GitSubsystem
        // (Forgejo — локальный сервис, `WithoutEgressProxy` обязателен).
        services.AddQuietHttpClient("fal", new QuietHttpClientProfile(
            Category: "ClaudeHomeServer.Media.Fal",
            Subject: "сервисом fal.ai",
            Consequence: "Генерация изображений и учёт расхода недоступны."))
            // Свой таймаут клиента (хвосты ревью R-S-SPEND-4, п.2; S-SPEND-TAIL-2): без него — дефолт
            // HttpClient 100 с, а слот гейта троттлинга в FalCostService держится на всё время запроса:
            // один зависший опрос биллинга стопорил бы весь бэкфилл цен на 100 с. 15 с — конвенция
            // проекта для внешних API (ObservabilityExtensions, VideoSubsystem); опрос billing-events —
            // короткий JSON GET. Длинные вызовы под этот номер не попадают: FalImageService/
            // FalVideoEngine/FalAudioEngine/FalImageEditor берут этот же именованный клиент и ставят
            // свой больший таймаут на полученном экземпляре до первого запроса (180 с / 120 с / 60 с).
            // Опрос цен это не задевает: фабрика отдаёт новый экземпляр на каждый CreateClient, и
            // перезапись одного потребителя живёт только на его экземпляре — опрос биллинга и короткие
            // запросы аккаунта (FalAccountService) остаются на 15 с (сторож: FalHttpClientRegistrationTests).
            .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(15));
        // Озвучка голосового режима чата переехала в TtsSubsystem (волна 1.4).
        // Контракт фолбэка на голос браузера живёт в TtsController: 503 not_configured / 502 upstream.
        services.AddQuietHttpClient(
            ClaudeHomeServer.Controllers.FilesController.OnlyOfficeCommandClient,
            new QuietHttpClientProfile(
                Category: "ClaudeHomeServer.Files.OnlyOffice",
                Subject: "Command API OnlyOffice",
                Consequence: "Принудительное сохранение документа ждёт таймаут."))
            .WithoutEgressProxy();
        services.AddHttpClient("glif");
        // Проба MCP-сервера из личного реестра: чужой сервер лежит штатно (не поднят, сменил адрес),
        // и человек видит причину в ответе — консоли не нужны стектрейсы на каждый клик
        services.AddQuietHttpClient(
            ClaudeHomeServer.Services.Mcp.McpProbeService.HttpClientName,
            new QuietHttpClientProfile(
                Category: "ClaudeHomeServer.Mcp.Probe",
                Subject: "внешним MCP-сервером",
                Consequence: "Проверка сервера показала отказ — сам ход это не ломает."));
        // Authorization server чужого MCP-сервера: недоступен ровно так же штатно (нет DCR, лежит
        // well-known, отозван клиент) — человек видит причину в ответе, консоли стектрейсы не нужны
        services.AddQuietHttpClient(
            ClaudeHomeServer.Services.Mcp.McpOAuthService.HttpClientName,
            new QuietHttpClientProfile(
                Category: "ClaudeHomeServer.Mcp.OAuth",
                Subject: "сервером авторизации MCP",
                Consequence: "Вход в сервер не выполнен — инструменты этого сервера в ход не поедут."));
        // Официальный реестр MCP (каталог MCP-серверов, волна 1): сервис зарубежный — клиент ходит
        // ЧЕРЕЗ egress-прокси (WithoutEgressProxy тут НЕ звать). Опциональная зависимость: пустой
        // Mcp:Catalog:BaseUrl выключает каталог целиком, раздел продолжает работать вручную.
        services.AddSingleton(ClaudeHomeServer.Services.Mcp.Catalog.McpCatalogOptions.FromConfig(config));
        services.AddQuietHttpClient(
            ClaudeHomeServer.Services.Mcp.Catalog.McpCatalogClient.HttpClientName,
            new QuietHttpClientProfile(
                Category: "ClaudeHomeServer.Mcp.Catalog",
                Subject: "реестром MCP registry.modelcontextprotocol.io",
                Consequence: "Поиск по каталогу MCP-серверов недоступен — добавить сервер можно вручную."))
            .ConfigureHttpClient(c =>
            {
                // Реестр публичный, но из РФ ходим через egress-прокси — общий таймаут включает
                // DNS+коннект+тело, и 5 с не хватает в дневной нагрузке. 15 с — запас с одной
                // попытки; повтор — отдельный шаг (rate-limit на поиск + кэш страницы).
                c.Timeout = TimeSpan.FromSeconds(15);
                // Реестр публичный и маленький, но доверять его размеру не обязаны: бьём oversized-ответ
                c.MaxResponseContentBufferSize = 2 * 1024 * 1024;
            });
        services.AddSingleton<ClaudeHomeServer.Services.Mcp.Catalog.McpCatalogClient>();
        // Сторонний провайдер — опциональная зависимость: баланс уходит в протухший кэш, каталог
        // моделей — в дефолтный список, фоновое действие — к другой модели. Мёртвый провайдер
        // не должен засыпать консоль стектрейсами (см. QuietHttpLogger)
        services.AddQuietHttpClient("llm-provider", new QuietHttpClientProfile(
            Category: "ClaudeHomeServer.Llm.Provider",
            Subject: "API стороннего провайдера моделей",
            Consequence: "Баланс и каталог моделей не обновятся, фоновое действие уйдёт другой модели."));
        // Шлюз квоты Alibaba Coding Plan: авторизация по cookie консоли, а не по ApiKey (публичного
        // API квоты у Token Plan нет). Неавторизованную (протухшую) сессию шлюз редиректит 302 на
        // err.taobao.com — редиректы отключаем, иначе получим HTML вместо JSON. Логирование протухания
        // (одна строка Warning, троттлинг) — в ProviderBalanceService.LogAlibabaExpiry, без QuietHttpLogger,
        // чтобы не дублировать жалобы: протухание видно и на уровне тела (code != SUCCESS / NotAuthorised)
        services.AddHttpClient("alibaba-console")
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddHttpClient("anthropic-oauth");
        return services;
    }
}
