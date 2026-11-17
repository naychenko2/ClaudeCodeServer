using ClaudeHomeServer.Services.Http;
using ClaudeHomeServer.Services.Mcp.Http;

namespace ClaudeHomeServer.Services.Composition;

// Регистрация продуктовых MCP-серверов поверх HTTP (ADR-012) (S6-2): личный реестр
// серверов владельца, его секреты/статусы/OAuth/пробы, тулсеты всех продуктовых серверов
// и реестр тулсетов с белым списком провайдера.
//
// Почему здесь, а не в вертикали: `Services.Mcp` — часть Main, а каждый тулсет — фасад
// над несколькими вертикалями сразу (tasks+notes+personas+chats у TasksToolset, память
// персон и команды у MemoryToolset, TestRuns у tests/dev). Регистрация тулсета в чужой
// подсистеме дала бы ей прямые ссылки на Main и на соседние вертикали; транспорт
// (McpTransportController) и состав tools/list держатся в композиции. Отдельные
// тулсеты уже ушли к своим вертикалям: NotesToolset — в NotesSubsystem, этот ряд —
// всё, что осталось.
//
// Порядок и состав — ровно те, что были в Program.cs, включая порядок последних
// `AddSingleton<IMcpToolset>` (последняя регистрация задаёт, что отдаёт
// GetRequiredService<IMcpToolset>) и пару `HiggsfieldToolset` + форвардер на
// IMcpToolset (задача eefcb96a).
public static class McpRegistration
{
    public static IServiceCollection AddProductiveMcpServers(this IServiceCollection services, IConfiguration config)
    {
        // Наблюдаемость вызовов продуктовых MCP-серверов (счётчики + последние сбои, только в памяти)
        services.AddSingleton<ClaudeHomeServer.Services.Mcp.McpCallLog>();
        // Личный реестр MCP-серверов владельца: записи без секретов (data/mcp-servers.json)
        // и значения ключей/токенов отдельным стором (data/mcp-secrets.json — не едет в облачный архив)
        services.AddSingleton<ClaudeHomeServer.Services.Mcp.McpSecretStore>();
        services.AddSingleton<ClaudeHomeServer.Services.Mcp.McpRegistry>();
        // Последний известный статус серверов (data/mcp-status.json — в архив не едет) и разовая
        // проба по кнопке: фонового поллинга нет, наблюдение приходит из system/init каждого хода
        services.AddSingleton<ClaudeHomeServer.Services.Mcp.McpStatusStore>();
        // Вход в чужой сервер по OAuth: discovery, регистрация клиента, обмен кода и обновление
        // токена перед ходом (pending-записи входа живут только в памяти — отсюда singleton)
        services.AddSingleton<ClaudeHomeServer.Services.Mcp.McpOAuthService>();
        services.AddSingleton<ClaudeHomeServer.Services.Mcp.McpProbeService>();
        // Инстансное подключение Higgsfield (фаза 1.1): единый OAuth-вход админа, шарится всеми
        services.AddSingleton<ClaudeHomeServer.Services.Mcp.HiggsfieldOAuthService>();
        // Шов для драйвера Higgsfield редактора картинок (ADR-017): токен без AdminOwnerId
        services.AddSingleton<ClaudeHomeServer.Services.Higgsfield.IHiggsfieldAccess,
            ClaudeHomeServer.Services.Mcp.HiggsfieldAccessAdapter>();
        // Запись модулей в ленту чата (ADR-019 §2): общий шов для подсистем, адаптер над SessionManager
        services.AddSingleton<ClaudeHomeServer.Services.Composition.IChatFeed, ChatFeed>();
        services.AddQuietHttpClient(
            ClaudeHomeServer.Services.Mcp.HiggsfieldOAuthService.HttpClientName,
            new QuietHttpClientProfile(
                Category: "ClaudeHomeServer.Mcp.Higgsfield",
                Subject: "инстансным подключением Higgsfield",
                Consequence: "Обновление токена не прошло — админу нужно переподключиться."));
        // Продуктовые MCP-серверы поверх HTTP (ADR-012): тулсет отдаёт схемы, общий контроллер
        // McpTransportController — транспорт. Новый сервер добавляется одной регистрацией здесь.
        services.AddSingleton<IMcpToolset, WidgetsToolset>();
        // Память персоны/команды (фаза 2, волна 1): один тулсет на все ключи memory и pmem_<handle> —
        // персона и проект едут хвостом маршрута /mcp/memory/{personaId}/{projectId}
        services.AddSingleton<IMcpToolset, MemoryToolset>();
        // Задачи и заметки (фаза 2, волна 2): сессия-вызыватель едет хвостом /mcp/{tasks,notes}/{sessionId},
        // по ней тулсет резолвит проект чата, персону и её привязки на каждый вызов
        services.AddSingleton<IMcpToolset, TasksToolset>();
        // NotesToolset — динамический модуль: регистрация переехала в NotesSubsystem.Register
        // Персоны (фаза 2, волна 2): тяжёлая оркестрация CRUD — в PersonasCrudService (общий с REST),
        // тулсет — тонкий JSON-фасад над ним и сервисами; хвост маршрута — та же сессия-вызыватель
        services.AddSingleton<ClaudeHomeServer.Services.PersonasCrudService>();
        // Чаты (фаза 2, волна 3): оркестрация chats_send/chats_report_up — общая для REST и wsp-тулсета
        services.AddSingleton<ClaudeHomeServer.Services.SessionMessagingService>();
        // Знания (фаза 2, волна 3): каталог баз Dify под пользователя — общий для REST и wsp-тулсета.
        // Регистрация `KnowledgeBaseCatalogService` переехала в `KnowledgeSubsystem` (волна 3,
        // шаг 3 — выделение подсистемы Knowledge).
        services.AddSingleton<IMcpToolset, PersonasToolset>();
        // Волна 3 (ADR-012): рабочее пространство, граф кода и уведомления
        services.AddSingleton<IMcpToolset, WorkspaceToolset>();
        services.AddSingleton<IMcpToolset, CodeGraphToolset>();
        services.AddSingleton<IMcpToolset, NotificationsToolset>();
        // Базы знаний Dify (фаза 2, волна 4 — последний сервер фазы): тулсет ходит во внешний
        // Dify напрямую через KnowledgeService, ключ не покидает бэкенд; хвост — сессия-вызыватель
        services.AddSingleton<IMcpToolset, DifyToolset>();
        // Сторожа чатов: хвост /mcp/watch/{sessionId} — сессия-вызыватель; право на чат
        // проверяется на каждый tools/list и вызов (WatchToolset.TryResolve)
        services.AddSingleton<IMcpToolset, WatchToolset>();
        // Репетитор детского режима (ADR-041, К3): хвост /mcp/tutor/{sessionId}; объявляется только
        // ребёнку (TurnLaunchContextFactory), а резолв ребёнка, чата и предмета — на каждый вызов.
        // KnowledgeService — необязательная зависимость (вертикаль «Знаний» может быть выключена)
        services.AddSingleton<IMcpToolset, TutorToolset>();
        // Веб-поиск (websearch: web_search + web_read): поиск идёт во внешний Perplexity Sonar
        // (ключ не покидает бэкенд), чтение страницы — существующим ридером панели «Чтение»
        // вместе с его SsrfGuard и квотой владельца. Пустой Perplexity:ApiKey = сервер ходу
        // не объявляется вовсе (SessionManager не строит контекст).
        services.AddSingleton<IMcpToolset, WebSearchToolset>();
        // Higgsfield (фаза 1.2): прокси к mcp.higgsfield.ai/mcp, 10 инструментов из белого списка.
        // Instance-level OAuth фазы 1.1 уже зарегистрирован выше (HiggsfieldOAuthService +
        // higgsfield-oauth-клиент); здесь — тихий клиент самого прокси и сам тулсет.
        services.AddQuietHttpClient(
            HiggsfieldToolset.HttpClientName,
            new QuietHttpClientProfile(
                Category: "ClaudeHomeServer.Mcp.Higgsfield",
                Subject: "прокси-тулсетом Higgsfield",
                Consequence: "Генерации картинок/видео/аудио недоступны — список инструментов устарел."));
        // Регистрация HiggsfieldToolset под двумя типами через форвардер: одна реализация
        // под собственным типом и под IMcpToolset, без дубля singleton. `AddSingleton<I, C>()`
        // дал бы ВТОРОЙ экземпляр рядом с `AddSingleton<C>()`, а каст `(HiggsfieldToolset)
        // sp.GetRequiredService<IMcpToolset>()` бросил бы InvalidCastException: последняя
        // AddSingleton<IMcpToolset> в AddSubsystems (NotesToolset) перекрыла бы Higgsfield —
        // приложение не поднялось бы. Форвардер ловит оба требования: один экземпляр +
        // каст не нужен (hosted берёт по собственному типу). Задача eefcb96a.
        services.AddSingleton<HiggsfieldToolset>();
        services.AddSingleton<IMcpToolset>(sp => sp.GetRequiredService<HiggsfieldToolset>());
        // Фоновый прогрев снимка tools/list для Higgsfield (шаг 4 задачи 6e309216):
        // тулсет остаётся тот же singleton, hosted берёт его из DI по тому же корню, что
        // и IMcpToolset — иначе у нас было бы ДВА HiggsfieldToolset в процессе, и обновление
        // кэша в одном не отражалось бы в ToolsFor у другого.
        services.AddGatedHostedFrom<HiggsfieldSnapshotWarmer>(config,
            sp => new HiggsfieldSnapshotWarmer(
                sp.GetRequiredService<HiggsfieldToolset>(),
                sp.GetRequiredService<ILogger<HiggsfieldSnapshotWarmer>>()));
        // Локальная генерация картинок и видео (local-media): ComfyUI на своей GPU. Движок живёт в
        // подсистеме images (тумблер LocalMedia:Enabled), тулсет — здесь; папку проекта и «файл
        // записан» вертикаль получает через шов ILocalMediaProjectAccess
        services.AddSingleton<ClaudeHomeServer.Services.Images.LocalMedia.ILocalMediaProjectAccess,
            ClaudeHomeServer.Services.Mcp.LocalMediaProjectAccess>();
        services.AddSingleton<IMcpToolset, LocalMediaToolset>();
        services.AddSingleton<IMcpToolset, TestsToolset>();
        // Сборка с прогрессом (dev: build) — движки той же вертикали TestRuns, гейты как у tests
        services.AddSingleton<IMcpToolset, DevToolset>();
        // Контекст чата для агента (ADR-023 §3.2): context_state / context_attach / context_detach; сессия — хвостом
        services.AddSingleton<IMcpToolset, TurnContextToolset>();
        // Плагины (ADR-031): тулсет — НЕусловно (Main), а не в PluginsSubsystem.Register: при
        // выключенной подсистеме сервер plugins обязан остаться в наборе (состав tools/list стабилен,
        // вызов — честный отказ). PluginDraftService — опциональная зависимость тулсета (null при
        // выключенной подсистеме); остальные зависимости (IMcpSessionAccessor/IProjectManager) — спина.
        services.AddSingleton<IMcpToolset, ClaudeHomeServer.Services.Plugins.Mcp.PluginsToolset>();
        services.AddSingleton<McpToolsetRegistry>();
        // Белый список инструментов профиля провайдера (KeepMcpTools): читает McpTransportController
        // на tools/list и tools/call, сами тулсеты о нём не знают
        services.AddSingleton<McpToolWhitelist>();
        return services;
    }
}
