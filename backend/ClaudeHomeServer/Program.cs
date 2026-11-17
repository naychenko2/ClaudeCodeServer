using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using ClaudeHomeServer.Services.ChatContext;
using ClaudeHomeServer.Services.ChildMode;
using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Auth;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Composition.Llm;
using ClaudeHomeServer.Services.Composition.Notifications;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Services.Devices;
using ClaudeHomeServer.Services.Docs;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Git;
using ClaudeHomeServer.Services.Llm.Claude;
using ClaudeHomeServer.Services.Team;
using ClaudeHomeServer.Services.Http;
using ClaudeHomeServer.Services.Mcp;
using ClaudeHomeServer.Services.Mcp.Http;
using ClaudeHomeServer.Services.ProjectServices;
using ClaudeHomeServer.Services.Spend;
using ClaudeHomeServer.Services.Terminal;
using ClaudeHomeServer.Services.Tasks;
using ClaudeHomeServer.Services.TriggerSources;
using ClaudeHomeServer.Services.Modules;
using ClaudeHomeServer.Services.Turn;
using ClaudeHomeServer.Services.Video;
using ClaudeHomeServer.Services.WebSearch;
using ClaudeHomeServer.Telemetry;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Yarp.ReverseProxy.Forwarder;

JwtSecurityTokenHandler.DefaultMapInboundClaims = false;

// Кириллица в stdout: без явной кодировки .NET на Windows пишет в OEM (866), и потребители
// вывода (раннер-трей, docker logs) получают кашу. Единый UTF-8 — при любом способе запуска.
try { Console.OutputEncoding = System.Text.Encoding.UTF8; }
catch { /* нет консоли/права — не критично, останется дефолт */ }

// Режимы обслуживания (--backup / --restore / --inspect) отрабатывают и завершаются,
// не поднимая веб-приложение. Обязательно ДО ProcessRegistry.Initialize ниже: тот бьёт
// «сирот» по pid-файлу, а он общий с работающим сервером.
if (ClaudeHomeServer.Services.Backup.BackupCli.TryHandle(args)) return;

// ADR-015 §8 этап 2: dryRun-прогон синка профилей. Работает без поднятого веб-приложения:
// обходит подпапки _profilesDir, делает усыновление (если манифеста нет) и считает
// "удалил бы" по mirror-зонам. Удалений не производит — только печатает сводку.
// Рубильник Claude:ProfileSync:Mirror (off | dryRun | on) управляет поведением;
// в dryRun запускать через env-переменную Claude__ProfileSync__Mirror=dryRun.
if (ClaudeHomeServer.Services.Llm.ProfileSyncCli.TryHandle(args)) return;

var builder = WebApplication.CreateBuilder(args);

// Локальные машинно-специфичные переопределения (пути, URL, секреты).
// Файл вне git (.gitignore), у каждого свой. Грузится последним — переопределяет
// appsettings.json и appsettings.{Environment}.json. Необязателен: нет файла — берутся
// дефолты из git (важно, чтобы у брата ничего не отъехало).
// В Testing (TestWebApplicationFactory) файл НЕ подключаем: иначе боевые токены
// подписок, Dify:ApiKey и ключи LlmProviders разработчика протекают в тестовые хосты,
// а прогрев подписок запускает настоящие claude.exe с боевым OAuth-токеном.
if (!builder.Environment.IsEnvironment("Testing"))
    builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

// Инспекционная копия: её параметры обязаны победить Local.json, который только что лёг
// поверх командной строки. Иначе копия открыла бы боевой DataPath и боевой Dify.
var inspectionOverrides = ClaudeHomeServer.Services.Backup.BackupCli.InspectionOverrides(args);
if (inspectionOverrides is not null) builder.Configuration.AddInMemoryCollection(inspectionOverrides);
var inspectionMode = builder.Configuration.GetValue<bool>("InspectionMode");

// Панель «Компоненты»: data/subsystems.json — машинный файл тумблеров, который пишет сам
// сервер (PUT /api/admin/subsystems/{key}, S2c-1). Грузится ПОСЛЕ Local.json: значение,
// записанное панелью, перекрывает ручную настройку в Local.json (обратного нет — этот файл
// переписывает JSON-парсер, и комментарии Local.json не пережили бы его). optional: нет
// файла — ничего не выключено, всё как раньше. reloadOnChange: чтобы GET /api/admin/subsystems
// показывал свежий Enabled без рестарта процесса (проверено тестом
// Put_ЗатемGET_ТотЖеХост_ПоказываетНовыйEnabled, SubsystemsWriteTests); сам гейт применения
// остаётся одноразовым (ADR-014).
//
// Путь резолвится по финальному DataPath — уже после инспекционного оверрайда, чтобы копия
// читала свой каталог, а не боевой. Связка «путь источника == путь записи»: контроллер пишет
// SubsystemOverridesFile.PathFor(хост-конфигурации) — тот же резолв от того же DataPath,
// поэтому записанное доезжает до следующего старта (сторож связки — тест
// Put_ПослеЗаписи_НовыйХостНаТомЖеКаталоге_НеАктивизируетПодсистему). Она держится ровно
// пока: (1) ни один источник конфигурации не добавлен ПОСЛЕ этой строки и (2) DataPath здесь
// финальный. Любой AddJsonFile/AddInMemory ниже или правка DataPath ниже молча разорвут
// связку — файл начнёт читаться не там, где пишется. Правило: ниже этого места источники
// конфигурации не добавляются (сейчас их действительно нет ни одного — проверено по файлу).
var subsystemsPath = SubsystemOverridesFile.PathFor(builder.Configuration);
// Битый файл как обычный источник конфигурации ронял весь старт процесса: AddSource грузит
// источник сразу, и InvalidDataException прилетает из этого же AddJsonFile — инстанс не
// поднимался бы вовсе (проверено тестом Host_БитыйФайлНаСтарте... на коде без пропуска),
// при том что ручка на живом процессе на том же файле отвечает 503 с причиной. Старт дороже:
// такой источник пропускаем (состав подсистем — по умолчанию, как без файла) и говорим
// причину в stderr: человек чинит файл руками, панель на нём до починки отвечает 503.
var subsystemsLoadError = SubsystemOverridesFile.DescribeConfigurationLoadError(subsystemsPath);
// Провайдер файлов этого источника (PhysicalFileProvider с FileSystemWatcher под reloadOnChange)
// AddJsonFile создаёт сам для абсолютного пути, и освобождать его некому — освобождаем на
// остановке хоста. Поток наблюдателя несёт ExecutionContext старта хоста, а с ним — весь граф
// приложения: в тестах каждый утилизированный хост оставался в памяти (замер 16.11.2026:
// testhost монолита рос до 17 ГиБ, 232 живых потока «.NET File Watcher»)
IDisposable? subsystemsFiles = null;
if (subsystemsLoadError is null)
{
    builder.Configuration.AddJsonFile(subsystemsPath, optional: true, reloadOnChange: true);
    subsystemsFiles = (builder.Configuration.Sources[^1] as FileConfigurationSource)?.FileProvider as IDisposable;
}
else
    Console.Error.WriteLine(
        $"{subsystemsPath} не подключён источником конфигурации: {subsystemsLoadError} " +
        "Тумблеры панелей на этот запуск не применены — состав подсистем по умолчанию.");

// `llm` — неотключаемая подсистема: ядро сессий зависит от LlmProviderRegistry обязательно
// (ADR-014, «Неотключаемые подсистемы»). Вызов остаётся здесь, до регистраций и до побочных
// эффектов старта (чанка процессов-сирот, загрузка модулей): иначе старт падал бы глубже и
// с чужим текстом — «No service for type 'LlmProviderRegistry' has been registered», по нему
// причину приходится раскапывать заново. Тело проверки — в NonDisablableSubsystems.EnsureLlmEnabled:
// туда оно вынесено ради юнит-теста, который может вызвать саму проверку (сюда — нет).
NonDisablableSubsystems.EnsureLlmEnabled(builder.Configuration);

// Таймстемп в консольном логе (I11): обёртка Console.Out/Error добавляет UTC-время в начало
// каждой строки — и для ILogger, и для голых Console.WriteLine. Здесь, чтобы Local.json тоже
// мог переопределить формат. Ключ Diagnostics:ConsoleTimestampFormat — НЕ под Logging:Console:
// то имя (Logging:Console:TimestampFormat) биндится во фреймворчный ConsoleLoggerOptions.
// TimestampFormat, и форматтер ILogger печатал второй таймстемп (с липовым Z — локальное время).
// Пусто — без обёртки.
TimestampedConsoleWriter.Enable(builder.Configuration["Diagnostics:ConsoleTimestampFormat"]);

// Файловый лог инстанса (data/logs/server-YYYYMMDD.log, stderr-зеркало, дневная ротация):
// вне Development/Testing включён по умолчанию — боевой инстанс обязан оставлять след
// обрывов ходов и смертей процессов CLI независимо от способа запуска (трей/Runner/консоль).
// Выключается Logging:File:Enabled=false (см. Services/Diagnostics/FileLog.cs).
ClaudeHomeServer.Services.Diagnostics.FileLog.Attach(builder.Configuration, builder.Environment);

// Зачистка процессов-сирот от предыдущего запуска сервера (краш/форс-килл):
// на Windows дочерние node-процессы MCP-серверов не умирают при смерти родителя —
// без этого они копятся и съедают гигабайты памяти. Должно быть ДО первого Process.Start.
// В инспекционной копии пропускаем: pid-файл лежит рядом с exe (а не в DataPath), то есть
// принадлежит БОЕВОМУ серверу — чистка убила бы его MCP-серверы и идущие ходы.
//
// Execution:ProcessRegistry:Enabled (дефолт true) — вторая, та же по смыслу оговорка:
// реестр процессов ПРОЦЕСС-ГЛОБАЛЕН (статика), а хост в процессе бывает не один. В бою
// хост ровно один и владеет всеми порождёнными процессами машины; тестовый хост
// (appsettings.Testing.json) — не владеет: он поднимается и гасится десятки раз за прогон
// рядом с чужими процессами того же процесса ОС, и его ApplicationStopping вычищал реестр
// и убивал процессы параллельно идущих тестов (доказано: подъём и Dispose тестового хоста
// снимал с учёта и убивал заранее зарегистрированный живой процесс).
var ownsProcessRegistry = !inspectionMode
    && builder.Configuration.GetValue("Execution:ProcessRegistry:Enabled", true);
if (ownsProcessRegistry) ProcessRegistry.Initialize();

// Изоляция процессов local-среды по памяти (инцидент 2026-09-19: systemd-oomd дважды
// убил прод ccs.service целиком, потому что сборки агентских CLI живут в cgroup прода).
// Опции — секция Execution:Isolation (Enabled/Slice/MemoryMax). Дефолт выключено: на
// Windows и в dev-контейнере user-шины нет, и там обёртка fail-open.
// Выставить в appsettings.Local.json:
//   "Execution": { "Isolation": { "Enabled": true, "Slice": "ccs-agents.slice",
//     "MemoryMax": "16G" } }
// MemoryHigh в примере нет намеренно: под systemd-oomd дроссель сам становится источником
// PSI-давления и убивает наш же scope (разбор 2026-09-22). Заполненный ключ ругается в
// stderr на старте — см. IsolationOptions.FromConfig.
ClaudeHomeServer.Services.Execution.IsolationOptions.Instance =
    ClaudeHomeServer.Services.Execution.IsolationOptions.FromConfig(builder.Configuration);

// Потолок одновременных ТЯЖЁЛЫХ запусков (сборки/тесты): пределы scope заданы per-scope, а
// число одновременных scope не ограничивало ничто — инцидент oomd 2026-09-21, 18,1 GB в
// четырёх параллельных прогонах. Ключ Execution:Isolation:MaxConcurrentBuilds, дефолт 2,
// явный 0 — без ограничения (откат без пересборки).
ClaudeHomeServer.Services.Execution.BuildConcurrencyGate.Configure(
    ClaudeHomeServer.Services.Execution.BuildConcurrencyGate.LimitFromConfig(builder.Configuration));

// Гашение scope висит на событии Exited и умирает вместе с процессом бэкенда: упал бэкенд —
// scope с узлами сборки остался в slice, и увидеть его можно только отсюда, со следующего
// старта. Как сирота отличается от scope живого соседнего инстанса — в ScopeOrphanSweeper.
// Фоном: старт не должен ждать systemctl, а свои scope этого запуска сторож не тронет
// (их владелец — мы, и мы живы).
_ = Task.Run(() => ClaudeHomeServer.Services.Execution.ScopeOrphanSweeper.Sweep(
    ClaudeHomeServer.Services.Execution.IsolationOptions.Instance));

// Признак «сервер работает на этом каталоге data»: держится весь uptime и проверяется
// восстановлением. Живой сервер во время restore продолжил бы писать в перемещённый
// каталог и пересоздал бы data под собой.
var instanceLock = ClaudeHomeServer.Services.Backup.InstanceLock.TryAcquireInstance(
    Path.GetDirectoryName(Path.GetFullPath(builder.Configuration["DataPath"]
        ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json")))!);

// Токен подписки claude CLI (`claude setup-token`) можно держать в appsettings.Local.json —
// удобнее, чем переменная окружения: IDE наследует окружение от родителя (explorer/Toolbox),
// и свежий setx там не виден, пока не перезайдёшь в систему. Кладём его в env процесса, откуда
// его унаследуют все запуски claude.exe (ClaudeSession, OneShotClaudeRunner, ModelCatalogService).
// Явная переменная окружения имеет приоритет — конфиг её не перетирает (важно для docker).
const string OAuthTokenVar = "CLAUDE_CODE_OAUTH_TOKEN";
if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(OAuthTokenVar))
    && builder.Configuration["Claude:OAuthToken"] is { Length: > 0 } oauthToken
    && !string.IsNullOrWhiteSpace(oauthToken))
{
    Environment.SetEnvironmentVariable(OAuthTokenVar, oauthToken);
    // Значение — секрет, печатаем только факт (иначе токен утечёт в логи IDE/CI)
    Console.WriteLine($"[Claude] Токен подписки взят из конфига Claude:OAuthToken ({oauthToken.Length} симв.)");
}

// Последний рубеж пайплайна: исключение, не пойманное по дороге, логируется структурно
// (маршрут, тип, точка броска) и превращается в 500 ProblemDetails — см.
// Services/Http/UnhandledExceptionHandler.cs. AddProblemDetails() нужен самому middleware:
// без зарегистрированного IProblemDetailsService оно отказывается стартовать. На готовые
// ответы контроллеров это не влияет — тело problem+json пишут только StatusCodePages и
// обработчик исключений, а StatusCodePages здесь не подключён.
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ClaudeHomeServer.Services.Http.UnhandledExceptionHandler>();

// Держим IMvcBuilder в переменной: ниже, после загрузки динамических модулей (ModuleLoader),
// на том же builder'е подключаем их контроллеры (AddApplicationPart-эквивалент —
// ConfigureApplicationPartManager, единственный доступный на IServiceCollection-уровне путь).
// Отказ локального проекта из глубины сервиса (ProjectCapabilityGuard.ServerRoot) — 409 с
// кодом local_project, а не 500 (ADR-016 §4, G1)
var mvcBuilder = builder.Services.AddControllers(o => o.Filters.Add(new LocalProjectExceptionFilter()))
    .AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.Converters.Add(
            new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase));
        // Чат автора голоса фичи живёт в сторе, но наружу не отдаётся — одним конвертером типа
        // на все точки отдачи TaskItem (см. Services/FeatureNoteJsonConverter.cs). Тот же
        // конвертер ниже у SignalR и в выходных опциях задач (FeatureJsonOptions, TasksToolset).
        o.JsonSerializerOptions.Converters.Add(new ClaudeHomeServer.Services.FeatureNoteJsonConverter());
    });
// Вычисленные «связи» сессии (parentSessionId/taskDone) дописываются на границе
// сериализации — одним конвертером типа Session на все точки отдачи (см.
// Services/SessionJsonConverter.cs). Только в опциях MVC: стор sessions.json пишется
// своими опциями SessionManager, и вычисленные поля в файл не попадают.
builder.Services.AddSingleton<Microsoft.Extensions.Options.IConfigureOptions<Microsoft.AspNetCore.Mvc.JsonOptions>,
    ClaudeHomeServer.Services.SessionJsonOptionsSetup>();
// Сжатие ответов REST (подключается в конвейере только для /api, см. UseWhen ниже):
// список чатов проекта — сотни КБ JSON, и через внешний домен он ехал несжатым.
// EnableForHttps — осознанно: секрет в теле отдаёт только /api/auth, он из сжатия исключён.
builder.Services.AddResponseCompression(o =>
{
    o.EnableForHttps = true;
    o.Providers.Add<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProvider>();
    o.Providers.Add<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProvider>();
});
// Сборки вертикалей, собранные под `Microsoft.NET.Sdk.Web`, несут атрибут
    // `[assembly: ApplicationPart("...")]` — MSBuild дописывает его в сгенерированный
    // `obj/*/ClaudeHomeServer.MvcApplicationPartsAssemblyInfo.cs` ссылочного проекта
    // Main, и `ApplicationPartManager.PopulateDefaultParts` добавляет их к составу
    // MVC. Из-за этого гейт сам по себе маршрутов не снимает: у обычной ProjectReference-
    // вертикали часть остаётся подключённой, и отказ даёт сам контроллер — честный 503
    // с причиной (например PluginsController). В пилоте (Notes, до fa922fe7c) контроллер
    // гейт не проверял: активация падала на отсутствующих зависимостях → ProblemDetails
    // 500 на каждый запрос к `/api/notes/*` (задача 4defaacc, QA-прогон 2026-09-10).
    //
    // Notes — теперь динамический модуль (сценарий Б): ApplicationPart добавляется
    // ModuleLoader'ом по пути из DynamicModules-конфига (см. ниже), а не автоматически
    // через ProjectReference. Старый gate по имени сборки ("ClaudeHomeServer.Notes") удалён.
    // Гейт «выключить Notes» — любой из двух замков: `DynamicModules.notes.Enabled=false`
    // (ModuleLoader не загрузит dll) или `Subsystems:Notes:Enabled=false` — ModuleLoader
    // проверяет его ДО Register: без регистрации сервисов, без ApplicationPart (маршрутов
    // нет) и без RecordActive; Main-side-форвардеры читают тот же гейт.

// Динамические модули (сценарий Б): отдельные сборки, грузятся по пути из секции "DynamicModules"
// конфига при старте (не через ProjectReference). Load-once, выгрузки нет (DI сам не выгружает).
// Загрузчик зовёт Register каждого Enabled-модуля (регистрация сервисов) и возвращает
// загруженные сборки; контроллеры каждой подключаем AssemblyPart'ом на том же mvcBuilder'е.
//
// Честный ILogger<ModuleLoader> (M10): на этом этапе builder.Build() ещё не вызван, а
// WebApplicationBuilder в .NET 10 не отдаёт готовый ILoggerFactory/ServiceProvider (builder.Logging
// — ILoggingBuilder, CreateLogger<T> на нём не резолвится). Поэтому заводим ОДНОРАЗОВУЮ
// консольную фабрику с той же секцией Logging, что у хоста: «модуль не загрузился» уходит
// в консоль, а не теряется в no-op-фабрике. Временный BuildServiceProvider здесь не годится
// (ASP0000: второй контейнер синглтонов). Логирование идёт сразу, в LoadAll, внутри using.
// Стор подсистем: регистрируем ДО LoadAll, чтобы ModuleLoader записал в него
// динамические модули (RecordActive/RecordDisabled) и AddSubsystems переиспользовал
// тот же инстанс.
var dynamicModuleStore = new ClaudeHomeServer.Services.Composition.SubsystemStateStore();
builder.Services.AddSingleton(dynamicModuleStore);
// Реестр — вне using: ниже по нему же идёт раздача MF-remote загруженных модулей.
var dynamicModuleRegistry = new ClaudeHomeServer.Services.DynamicModules.ModuleRegistry(builder.Configuration);
using (var dynamicModuleLogFactory = LoggerFactory.Create(logging => logging
    .AddConfiguration(builder.Configuration.GetSection("Logging"))
    .AddConsole()))
{
    var dynamicModuleLoader = new ClaudeHomeServer.Services.DynamicModules.ModuleLoader(
        dynamicModuleRegistry,
        builder.Configuration,
        dynamicModuleLogFactory.CreateLogger<ClaudeHomeServer.Services.DynamicModules.ModuleLoader>());
    foreach (var dynamicModuleAssembly in dynamicModuleLoader.LoadAll(builder.Services))
    {
        // MVC-контроллеры загруженной сборки подключаем как отдельный ApplicationPart
        // на том же IMvcBuilder (ConfigureApplicationPartManager отрабатывает на построении
        // менеджера частей — единственный путь на уровне сервиса).
        mvcBuilder.ConfigureApplicationPartManager(parts =>
            parts.ApplicationParts.Add(new Microsoft.AspNetCore.Mvc.ApplicationParts.AssemblyPart(dynamicModuleAssembly)));
        // Записываем подсистему в стор: AddSubsystems ниже переиспользует этот инстанс.
        var implType = dynamicModuleAssembly.GetTypes()
            .FirstOrDefault(t => t is not null && !t.IsAbstract && !t.IsGenericTypeDefinition
                && typeof(ClaudeHomeServer.Services.Composition.IAppSubsystem).IsAssignableFrom(t));
        if (implType is not null)
            dynamicModuleStore.RecordActive((ClaudeHomeServer.Services.Composition.IAppSubsystem)Activator.CreateInstance(implType)!);
    }
    // Задизейбленные модули (Enabled=false): RecordDisabled, чтобы админский
    // экран показывал «выключено намеренно», а не «забыли подключить». Имя и описание строки
    // берутся из класса модуля (ModuleLoader.DescribeDisabled — метаданные без Register):
    // строка выключенного модуля обязана называться так же, как строка рабочего, а человеческое
    // имя раздела знает только класс. Из дескриптора реестра (DisabledModuleStub) — только когда
    // класса нет в развертке.
    foreach (var desc in dynamicModuleRegistry.All.Where(m => !m.Enabled))
    {
        dynamicModuleStore.RecordDisabled(dynamicModuleLoader.DescribeDisabled(desc) ?? new DisabledModuleStub(desc));
    }
    // Третий сценарий отключения: `DynamicModules:N:Enabled=true` (ModuleLoader грузит dll)
    // + `Subsystems:{key}:Enabled=false` (ModuleLoader возвращает null до загрузки — рубильник
    // бьёт первым в `ModuleLoader.TryLoadOne`). Цикл выше отбирает только `!m.Enabled` и
    // обходит такой модуль, `RecordActive` ему тоже не достаётся — модуль выпадает из снимка
    // ВООБЩЕ, и админ не отличает «выключено намеренно» от «не подключали». Снимок должен
    // показывать обе группы (Активные/Задизейбленные — контракт SubsystemStateStore),
    // поэтому добавляем запись здесь; отбор таких модулей — `ModuleRegistry.SubsystemGatedOff`
    // (реестр — единственный владелец дескрипторов и их `Enabled`). Дубля с предыдущим
    // циклом нет: тот берёт `!m.Enabled`, этот — включённые записи с выключенным рубильником
    // подсистемы. Пересечение пусто.
    foreach (var desc in dynamicModuleRegistry.SubsystemGatedOff(builder.Configuration))
    {
        dynamicModuleStore.RecordDisabled(dynamicModuleLoader.DescribeDisabled(desc) ?? new DisabledModuleStub(desc));
    }
}

// Статические вертикали: их `[assembly: ApplicationPart("...")]` MSBuild дописывает
// автоматически (см. блок выше). Гейт `Subsystems:{key}:Enabled` снимает DI-регистрацию
// (Register не вызывается), но ApplicationPart остаётся в MVC-частях: маршруты на месте,
// и отказ даёт сам контроллер, держущий гейт, — честный 503 с причиной
// (сейчас — PluginsController; 500 на активации без зависимостей был состоянием
// до nullable-волны, см. блок выше). У вертикали без контроллера HTTP-маршрутов нет вовсе.
// Notes и Spend — динамические модули (сценарий Б): ApplicationPart подключает
// ModuleLoader (выше), а не MSBuild-атрибут — M1-костыль (ручное снятие части
// ClaudeHomeServer.Spend) снят после перехода на ReferenceOutputAssembly="false".

// Hosted-сервисы: в Testing-среде (TestWebApplicationFactory) НЕ регистрируются без
// явного флага Testing:EnableHostedServices=true — 17 фоновых циклов на каждый из
// ~27 бутов тестовых хостов только жгли время прогона и порождали фоновую возню
// (диагностика 2026-07-30). Singleton-регистрации остаются — лениво достаются из DI.
// Хелперы живут в Services/Composition/SubsystemHostingExtensions.cs — те же правила
// гейта, чтобы подсистемы и main-Program регистрировали hosted одинаково.

builder.Services.AddSignalR(o =>
    {
        // Смягчаем разрывы у клиентов с дрожащим каналом (мобильные, засыпающие вкладки):
        // сервер закрывает соединение, если не слышал клиента дольше ClientTimeoutInterval.
        // Дефолт 30 с рвал соединение при коротком замолкании — поднимаем до 60 с
        // (должен быть ≥ 2× клиентского KeepAlive). KeepAlive 15 с — пинги сервера клиенту.
        o.ClientTimeoutInterval = TimeSpan.FromSeconds(60);
        o.KeepAliveInterval = TimeSpan.FromSeconds(15);
        // Медленное рукопожатие на плохом канале не должно ронять подключение
        o.HandshakeTimeout = TimeSpan.FromSeconds(30);
        // Отказ файловой группы для локального проекта (ADR-016 §4, G1) на методах хабов
        Microsoft.AspNetCore.SignalR.HubOptionsExtensions.AddFilter(o, new ProjectCapabilityHubFilter());
    })
    .AddJsonProtocol(o =>
    {
        o.PayloadSerializerOptions.Converters.Add(
            new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase));
        // task_changed несёт полный TaskItem: чат автора голоса фичи фильтруется тем же
        // конвертером, что и REST-ответы (см. Services/FeatureNoteJsonConverter.cs).
        o.PayloadSerializerOptions.Converters.Add(new ClaudeHomeServer.Services.FeatureNoteJsonConverter());
    });

// Реестр живых соединений пользователя (S1-4): держится рядом с SignalR — его читают
// только хаб (OnConnected/OnDisconnected) и точки отзыва членства (ProjectMembershipService).
// Состояние — память процесса, синглтон; после рестарта заполняется заново подключениями.
builder.Services.AddSingleton<ClaudeHomeServer.Hubs.UserConnectionRegistry>();

// Observability: OTel SDK (traces + metrics) с two-mode конфигурацией.
// Конфиг через секцию Telemetry в appsettings*.json. См. docs/observability/overview.md.
builder.Services.AddObservability(builder.Configuration);

// Ядро композиции (S6-2): вход и пользователи, среда исполнения процессов, реестр
// проектов, сферы и журнал событий проекта, сервисы расхода и медиа-журнала, швы рядом
// со своими корнями, а также ядро сессий и файлов чата (до S3) — в
// `Composition/CoreRegistration.cs`. Относительный порядок ядра и ядра сессий — тот же,
// что был здесь.
builder.Services.AddCore(builder.Configuration);
// Персона: стор, миниатюры аватара, промпт, привязки, черновик, провижн авто-ассистента,
// шаблоны прав специальности, файловые сабагенты и миграции персон — в
// `Composition/PersonasRegistration.cs` (S6-2). Память персон — в `MemorySubsystem`,
// паспорта — в `DossiersSubsystem`.
builder.Services.AddPersonas(builder.Configuration);
// Файловые сабагенты-персоны, провижн и миграции персон — в PersonasRegistration выше;
// планирование режима «Командная реализация» (Э2) — в TasksGlue ниже: оно работает с
// TaskManager и исполнителем задач, а не с персонами.
// Генерация картинок (иконка проекта, аватар персоны, фон проекта) — подключается
// подсистемой ImagesSubsystem вместе с прочими внутренними разделами ниже
// (`AddSubsystems(...)`). FalImageService регистрируется внутри как драйвер —
// отдельный AddSingleton дал бы второй экземпляр того же типа.
// Разовая переадресация закреплённых моделей GLM на действующий каталог (алиасы z.ai) —
// gated hosted: в Testing не стартует, повторный проход отсекается marker-файлом в data.
// Живёт в спине рядом с прочими миграциями сторов, а не в вертикали Llm (см. шапку файла).
builder.Services.AddGatedHostedService<GlmModelAliasMigration>(builder.Configuration);
// Разовое сведение моделей родного Claude к семействам (opus/fable/sonnet/haiku) во всех сторах —
// gated hosted, повторный проход отсекается маркером model-families-migration-v1.done в data.
builder.Services.AddGatedHostedService<ClaudeModelFamilyMigration>(builder.Configuration);
// Чаты картинки v2 уходят в архив (ADR-019, решение 2): идемпотентно по маркеру
// SessionImageChat.MigratedAt, поэтому и восстановленный старый бэкап мигрирует при старте
builder.Services.AddGatedHostedService<ImageChatV3Migration>(builder.Configuration);
// Ядро сессий и файлов чата (ChatDigestService, FileService/ProjectFiles, SessionContextResolver,
// ProjectPresetService, UnifiedSearchService, SyncService/FileWatcherService, ITurnEventBus,
// MediaEventHub, SessionManager, ProjectFileSessionsIndex, PrefixFingerprintCache,
// ModelCatalogService) — в `Composition/CoreRegistration.cs` (S6-2; до S3, см. его шапку).
// TaskManager/TaskAiService/BoardService/DailyBriefingService/TaskSchedulerService
// — DI в подсистеме `TasksSubsystem` (волна 4C, шаг 1).
// Документация проекта (README + docs/) для панели «Доки»: индекс, связи, поиск, макеты,
// карта проекта и документо-ИИ — все регистрации этой вертикали живут в самой сборке
// Docs (`DocsRegistration.AddDocs`, S6-2): ни один из её типов не нужен вне панели.
builder.Services.AddDocs();
// ProjectPresetService и UnifiedSearchService — в `Composition/CoreRegistration.cs`
// (ядро сессий, указатель выше).
// Подсистема Git: GitService/GitServerService/GitAiService регистрируются в
// `ClaudeHomeServer.Git/Services/Git/GitSubsystem.cs` — вертикаль вынесена в отдельную
// сборку (Этап 3, 2026-09-08), сюда её приносит ProjectReference Main→Git. Реакторы
// GitAutoCommitService/CommitAttributionService — общая композиция спины, см. блок
// ниже (Этап 3, уборка Git).
// Аналитика расхода токенов (Spend Analytics v2) — DI в подсистеме `SpendSubsystem`.
// Модельный слой (OneShotClaudeRunner / OllamaClient / LlamaServerClient / CloudCheapClient /
// LocalActionRouter / ModelAssignmentResolver / CheapTextRunner / LocalActionOverridesStore /
// LocalActionPresetService / FallbackSettingsStore / OllamaActionRankService и тихие HTTP-клиенты
// Ollama/LlamaServer) — DI в подсистеме `LlmSubsystem` (шаг 0 волны 4, см. LlmSubsystem.cs).
// Фон рабочего пространства проекта (ADR-008) и значок проекта (ADR-009) переехали
// в подсистемы `BackgroundsSubsystem` / `ProjectIconsSubsystem` (волна 2 внутренних
// подсистем): регистрации подключаются через `AddSubsystems(...)` ниже. Здесь остаются
// только общие клиенты и конфигурация, нужные вне подсистем (HTTP-клиенты fal/glif —
// общие с биллинг-сервисами, см. шапку `ImagesSubsystem`).
// Общий LLM-резолвер записи памяти (Mem0 ADD/UPDATE/DELETE/NOOP) — DI в подсистеме
// `MemorySubsystem` (S6-2): тип и все его потребители — одна сборка `Services.Memory`,
// за её пределами его никто не резолвит.
// One-shot ответы персон от их лица (persona_ask из MCP персон)
builder.Services.AddSingleton<PersonaAskService>();
// SyncService и FileWatcherService — в `Composition/CoreRegistration.cs` (ядро сессий,
// указатель выше).
// Реактор авто-commit (Project.GitAutoCommit после каждого хода Claude). Живёт в
// корне Services, не в Git-вертикали — это «реакция на ход» по прецеденту
// PersonaMemoryAutolearnService. Здесь, а не в GitSubsystem, потому что регистрации
// реакторов — общая композиция спины, а не домена Git.
builder.Services.AddGatedHostedService<GitAutoCommitService>(builder.Configuration);
// Журнал правок человека в общем проекте (S3-3): дебаунс-коммит ТОЛЬКО записанных путей с
// автором-человеком. Один экземпляр: шов IHumanEditJournal и hosted-сервис — тот же объект.
builder.Services.AddSingleton<HumanEditCommitter>();
builder.Services.AddSingleton<IHumanEditJournal>(sp => sp.GetRequiredService<HumanEditCommitter>());
builder.Services.AddGatedHostedFrom(builder.Configuration, sp => sp.GetRequiredService<HumanEditCommitter>());
// Детект коммита по сдвигу HEAD: помечает чатам зафиксированные пути
// (Session.CommittedFilePaths), чтобы атрибуция файлов чатам не врала после коммита.
// Аналогично GitAutoCommitService — реактор на горячем пути статуса, не домен Git.
builder.Services.AddSingleton<CommitAttributionService>();
builder.Services.AddSingleton<ConnectionDiagnostics>();
builder.Services.AddSingleton<ChatHistoryService>();
// Диагностика промпта панели сообщений: снимок промпта хода пишет SessionManager по шине
// (PromptAssembled, см. его подписку на шину), читает — панель сообщений
// (SessionMessagesController); разбор — on-demand вызов дешёвого исполнителя через
// ICheapTextRunner (Llm — через Core-шов; приватность разбора — в шапке файла). Не вертикаль:
// своей области и интерфейсов нет, потребители — панель сообщений и сам ход. Остаются здесь,
// рядом с другими сервисами панели сообщений.
builder.Services.AddSingleton<PromptSnapshotStore>();
builder.Services.AddSingleton<PromptAuditService>();
// WorkspaceKnowledgeStore — DI в подсистеме `KnowledgeSubsystem`
// (волна 3, шаг 3): миграция из старых Project-записей — отдельный блок стартового
// ряда после Build(): `Composition/StartupWarmup.cs` (см. ограничения в шапке KnowledgeSubsystem.cs).
builder.Services.AddSingleton<FalCostService>();
// Шов фактической цены fal (дефект 22068e9d, шаг S-SPEND-1): IFalRequestCost указывает на
// ТОТ ЖЕ экземпляр FalCostService (тот же приём, что ISpendCollector → SpendStore и
// IHumanEditJournal → HumanEditCommitter), иначе опрос и потребители разъедутся по разным
// клиентам и разным дедупам.
builder.Services.AddSingleton<IFalRequestCost>(sp => sp.GetRequiredService<FalCostService>());
builder.Services.AddSingleton<FalAccountService>();
builder.Services.AddSingleton<GlifAccountService>();
// UsageService — DI в подсистеме `LlmSubsystem` (волна 4B, шаг 2: переезд подписок и
// настроек специальностей к модели).
// Модельный слой (LlmProviderRegistry / ProviderBalanceService / ProviderHealthRegistry /
// ContextCapacityRegistry / IProviderBalanceService / FileChangeAttributor /
// ILlmSessionAdapterFactory / SubagentRunLog / TurnRunLog / EgressProbe) —
// DI в подсистеме `LlmSubsystem` (шаг 0 волны 4, см. LlmSubsystem.cs).
// Продуктовые MCP-серверы поверх HTTP (ADR-012): реестр владельца, его OAuth/статусы/пробы
// и все тулсеты — в `Composition/McpRegistration.cs` (S6-2). Каждый тулсет — фасад над
// несколькими вертикалями, поэтому он держится в композиции, а не в чужой подсистеме.
builder.Services.AddProductiveMcpServers(builder.Configuration);
// BoardService — DI в подсистеме `TasksSubsystem` (волна 4C, шаг 1).
// Шина событий хода (ITurnEventBus) и хаб редакторов (IMediaEvents/MediaEventHub) —
// в `Composition/CoreRegistration.cs` (ядро сессий, указатель выше).
// Этап 2: контрибьюторы секций системного промпта (этап 2 плана «Шина событий хода»).
// Каждый контрибьютор несёт Order/Key/Group и два метода: IsEnabled (гейт по
// per-session условиям — без него регрессия golden-фикстуры 4) и BuildAsync (исключения
// гасятся внутри). Подключаются к шине фильтром prompt/assembling через
// PromptSectionContributorsRegistration.RegisterAll в SessionManager.
// Новый контрибьютор — одна строка в PromptSectionContributorsDi.AddPromptSectionContributors().
// Здесь регистрируются только «чистые» контрибьюторы Turn; вертикальные (NotesRecallContributor,
// CodeGraphContributor) регистрирует своя подсистема в `Register` (Этап 5, шаг 6 — инверсия).
// Гейт отключаемой подсистемы получается СТРУКТУРНЫМ: при `Subsystems:Notes:Enabled=false`
// `AddSubsystems` не зовёт `NotesSubsystem.Register`, и контрибьютор не регистрируется вовсе —
// ни предикат на регистрации, ни пост-хок удаление дескрипторов здесь больше не нужны.
builder.Services.AddPromptSectionContributors();
// SessionManager — в `Composition/CoreRegistration.cs` (ядро сессий, указатель выше).
// Серверные сторожа чатов: стор + цикл опроса. Запуск poll-команд — через
// ILauncherFactory (среда владельца); цикл — hosted, в Testing-среде не поднимается
// (см. AddHosted), юниты гоняют TickAsync напрямую
builder.Services.AddSingleton<ClaudeHomeServer.Services.Watchdog.WatchdogStore>();
builder.Services.AddSingleton<ClaudeHomeServer.Services.Watchdog.IWatchdogEnvironment,
    ClaudeHomeServer.Services.Watchdog.WatchdogEnvironment>();
builder.Services.AddSingleton<ClaudeHomeServer.Services.Watchdog.IWatchdogCommandRunner,
    ClaudeHomeServer.Services.Watchdog.WatchdogCommandRunner>();
builder.Services.AddSingleton<ClaudeHomeServer.Services.Watchdog.IWatchdogAlarm,
    ClaudeHomeServer.Services.Watchdog.WatchdogAlarm>();
builder.Services.AddGatedHostedService<ClaudeHomeServer.Services.Watchdog.WatchdogService>(builder.Configuration);
// Снапшот сторожей + событие watchdogs_changed (визуализация для фронта): подписка на
// Changed стора живёт в конструкторе — слушатель обязан существовать к первым постановкам;
// прогрев ниже делает подписку независимой от hosted-цикла
builder.Services.AddSingleton<ClaudeHomeServer.Services.Watchdog.WatchdogNotifier>();
// ProjectFileSessionsIndex, PrefixFingerprintCache, ModelCatalogService —
// в `Composition/CoreRegistration.cs` (ядро сессий, указатель выше).
// Уведомления и push-доставка — в `Composition/NotificationsRegistration.cs` (S6-2):
// их пишут задачи, знания, бэкап и выкатка, а доставка одна на инстанс.
builder.Services.AddNotifications();
// Исполнитель задач и его швы, очередь фич, предгейт, слоты ревью, локатор деревьев,
// волны «Командной реализации» и утренний брифинг — в `Composition/TasksGlue.cs` (S6-2):
// сама вертикаль задач — в `TasksSubsystem`, здесь то, чего она видеть не может.
builder.Services.AddTaskExecutorGlue(builder.Configuration);
builder.Services.AddSingleton<SessionSummaryService>();
// Сводка карточки архива (место chat-digest, шаг 5 плана «Архив чатов») и
// карта плана (место plan-map, «Визуальный разворот плана» часть B) —
// DI в подсистеме `LlmSubsystem` (шаг 0 волны 4, см. LlmSubsystem.cs).
builder.Services.AddSingleton<ChatTaskExtractionService>();
// Утренний брифинг (кросс-вертикальный фасад Tasks+Notes+Llm) — в TasksGlue выше.
// Проактивность персон: источники триггеров — вертикаль TriggerSources (её сборка),
// state store, сам движок и шов раннера — `PersonasRegistration.AddPersonaAutomation`.
// Резолвер корня и пять источников триггеров — регистрации вертикали TriggerSources
// (их сборка, `TriggerSourcesRegistration.AddTriggerSources`, S6-2).
builder.Services.AddTriggerSources();
builder.Services.AddPersonaAutomation();
// Бэкапы: singleton + hosted-обёртка — снапшот дёргают и таймер, и админский API
builder.Services.AddSingleton<ClaudeHomeServer.Services.Backup.BackupService>();
builder.Services.AddGatedHostedFrom(builder.Configuration, sp =>
    sp.GetRequiredService<ClaudeHomeServer.Services.Backup.BackupService>());
// Ночная уборка worktree и /tmp, тревога о нехватке места (WorktreeCleanupService)
builder.Services.AddSingleton<ClaudeHomeServer.Services.WorktreeCleanupService>();
builder.Services.AddGatedHostedFrom(builder.Configuration, sp =>
    sp.GetRequiredService<ClaudeHomeServer.Services.WorktreeCleanupService>());

// === Канал устройства (ADR-016): агент локальных проектов на машине пользователя ===
// Реестр устройств, сопряжение, живой канал исполнения, билеты агента и его хаб-отправители,
// диспетчер онлайна и его обработчики — в `Composition/DeviceChannelGlue.cs` (S6-2):
// блок смешанный (половины смотрят в Main), перенос в сборку Devices дал бы запрещённую
// связь вертикаль → Main.
builder.Services.AddDeviceChannel(builder.Configuration);
// TaskSchedulerService — DI в подсистеме `TasksSubsystem` (волна 4C, шаг 1).
builder.Services.AddGatedHostedService<ChatExpiryService>(builder.Configuration);
// Автоправило архивации чатов (флаг chat-auto-archive) — singleton + hosted: кнопка
// «Применить сейчас» (POST /api/chats/archive-run) дёргает RunNowAsync того же инстанса
builder.Services.AddSingleton<ChatArchiveService>();
builder.Services.AddGatedHostedFrom(builder.Configuration, sp => sp.GetRequiredService<ChatArchiveService>());
// Темы чатов проекта — singleton + hosted: кнопки «Разложить сейчас/
// заново» (POST /api/projects/{id}/subjects/run|reset-ai) работают с тем же инстансом и его замком
builder.Services.AddSingleton<ChatSubjectSweeper>();
builder.Services.AddGatedHostedFrom(builder.Configuration, sp => sp.GetRequiredService<ChatSubjectSweeper>());
builder.Services.AddGatedHostedService<ChatTurnLoggerService>(builder.Configuration);
// NoteExpiryService — DI в подсистеме `NotesSubsystem` (волна 4C, шаг 2).
// Фоновый прогрев сводок «Что нового» — в `Services.Changelog.ChangelogSubsystem`.
// Терминал (PTY) — единственная регистрация в корне Services, под гейтом workspace-destructive.
// Подсистема не заведена сознательно: единственная регистрация и два резолва при
// shutdownTerminals (см. блок var app = builder.Build() ниже) — прецедент вертикали
// без IAppSubsystem, как у Services.Watchdog.
// Шов `ITerminalHubNotifier` (Этап 5, волна C, шаг 2): реализация лежит
// рядом с TerminalHub (Hubs/), вертикаль Terminal зависит только от Core.
builder.Services.AddSingleton<ClaudeHomeServer.Services.Composition.ITerminalHubNotifier,
    ClaudeHomeServer.Hubs.TerminalHubNotifier>();
builder.Services.AddSingleton<TerminalService>();
// Раздел «Сервисы проекта» (Preview/DevServer/ExternalPreview/...) — пилотная подсистема
// волны 4A. Сам `ProjectServicesSubsystem.Register` подключает ВСЕ регистрации этой
// группы: подсистема самодостаточна, точка регистрации HTTP-клиента `proxy` тоже там.
// Внешние CDN медиа (/api/proxy): прямой канал к ним душится DPI —
// эти запросы ОБЯЗАНЫ идти через системный egress-прокси.
builder.Services.AddHttpClient("media-proxy");
// Загрузка произвольных пользовательских URL (save-from-url): без авто-редиректов,
// чтобы редирект на приватный хост не обошёл SSRF-проверку (см. SsrfGuard).
builder.Services.AddHttpClient("safe-download")
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
// Раздел «Видео» — пилот подсистемы (см. `ClaudeHomeServer.Video/VideoSubsystem.cs`).
// Сам `VideoSubsystem.Register` подключает и платформенный `IMemoryCache` для своих
// провайдеров: подсистема самодостаточна, точку регистрации кеша в `Program.cs`
// больше не держим.
builder.Services.AddChatContext();
// Личное состояние чата участника общего проекта (S1-7): оверлей поверх полей Session. Читает его
// конвертер Session на границе сериализации, поэтому нужен доступ к текущему запросу.
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton(sp => new ClaudeHomeServer.Services.ChatUserState.ChatUserStateStore(
    ClaudeHomeServer.Services.ChatUserState.ChatUserStateStore.RootFromConfig(sp.GetRequiredService<IConfiguration>())));
builder.Services.AddSingleton<ClaudeHomeServer.Services.ChatUserStateService>();
// Журнал присутствия ребёнка по пространствам (KM-B5b, разрез §3): минуты по дням в
// data/child-usage/. Пишет его ручка heartbeat (KM-B5e), читает — статус «остаток минут» (KM-B5c).
builder.Services.AddSingleton(sp => new ChildUsageStore(
    ChildUsageStore.RootFromConfig(sp.GetRequiredService<IConfiguration>()),
    sp.GetRequiredService<ILogger<ChildUsageStore>>()));
// Провижн персоны-тьютора ребёнка (KM-B1d, ADR-041): «Помощник» с ключом в
// ChildProfile.TutorPersonaId. Вызывается только точками записи детского профиля (KM-B1e).
builder.Services.AddSingleton<ChildTutorProvisioner>();
builder.Services.AddSubsystems(builder.Configuration,
    // `git` идёт ПЕРВЫМ: это нижний слой вертикалей — на него смотрят будущие
    // Dossiers (захват коммитов), Knowledge (синк файлов), Deploy (publish/rollback),
    // плюс hosted-сервисы SessionManager/ProjectManager. Раньше — сломает их DI-порядок.
    new ClaudeHomeServer.Services.Git.GitSubsystem(),
    // CodeGraph — после Git, потому что это первая подсистема с пост-билд фазой
    // (`IAppPhaseSubsystem.ConfigureApp` регистрирует языковые провайдеры `.cs`/`.ts`/`.tsx`).
    new ClaudeHomeServer.Services.CodeGraph.CodeGraphSubsystem(),
    // Dossiers — после CodeGraph: вертикаль захвата паспортов зависит от швов
    // `Services.Git` (GitService — захват коммитов) и `Services.CodeGraph`
    // (CodeGraphService — обогащение паспортов графом кода). Нижние слои должны
    // регистрироваться раньше: Microsoft DI порядок регистраций при резолве не
    // учитывает; порядок здесь — очередь старта IHostedService и читаемость
    // (нижние слои раньше).
    new ClaudeHomeServer.Services.Dossiers.DossiersSubsystem(),
    // Llm — после Dossiers: нижний слой, от которого зависят Memory (ICheapTextRunner
    // для консолидации/autolearn), Spend (LlmProviderRegistry для прайса),
    // Backgrounds/ProjectIcons (ICheapTextRunner для подбора фона/значка).
    // Шаг 0 волны 4: адрес переездов Workflow / Subscription / Specialty на
    // следующих шагах — здесь живут LlmProviderRegistry/ModelAssignmentResolver/
    // UserModelTierResolver/OneShotClaudeRunner/CheapTextRunner и т.д.
    new ClaudeHomeServer.Services.Llm.LlmSubsystem(),
    // Memory — после Llm: фасады памяти персон/команды используют ICheapTextRunner
    // (консолидация LLM-merge и autolearn). Общий слой `Services.Memory`
    // (MemoryWriteResolver/MemoryDify) при этом независим.
    new ClaudeHomeServer.Services.Memory.MemorySubsystem(),
    // Knowledge — после Dossiers: вертикаль Dify RAG (Knowledge.md + ADR-013).
    // Форвардеры `IKnowledgeSyncParticipant → {DossierStore, ...}` — в
    // Services/Composition/CoreSeamsGlue.cs:36-51 (кросс-вертикальный клей), поэтому
    // KnowledgeSubsystem не зависит
    // от Dossiers/Knowledge напрямую; миграция WorkspaceKnowledgeStore из Project —
    // отдельный пост-билд блок ниже, чтобы не словить construct до PostRestoreHook.
    new ClaudeHomeServer.Services.Knowledge.KnowledgeSubsystem(),
    // Spend — динамический модуль (сценарий Б): грузится ModuleLoader'ом по пути из
    // секции DynamicModules, НЕ через ProjectReference. Швы (ISpendAnalytics,
    // ITaskPromptMetricsStore, ISpendDetailReader, ISpendCollector) — в Core.
    new VideoSubsystem(),
    new ClaudeHomeServer.Services.Yandex.YandexSubsystem(),
    new ClaudeHomeServer.Services.Reader.ReaderSubsystem(),
    new ClaudeHomeServer.Services.Images.ImagesSubsystem(),
    new ClaudeHomeServer.Services.Tts.TtsSubsystem(),
    new ClaudeHomeServer.Services.Deploy.DeploySubsystem(),
    new ClaudeHomeServer.Services.Backgrounds.BackgroundsSubsystem(),
    new ClaudeHomeServer.Services.ProjectIcons.ProjectIconsSubsystem(),
    // Architecture — динамический модуль (Viaduct 10.2, сценарий Б, как Notes): грузится
    // ModuleLoader'ом по пути из секции DynamicModules, НЕ через ProjectReference.
    // ProjectServices — раздел «Сервисы проекта» (Preview/DevServer). Регистрация ниже
    // всех: вертикаль листовая, ни от кого не зависит; наоборот, на неё ссылаются
    // PreviewController и SessionHub (через Program.cs).
    new ClaudeHomeServer.Services.ProjectServices.ProjectServicesSubsystem(),
    // TestRuns — движок run_tests (листовая: Core и только Core). Тулсет `tests` в Main
    // зависит от него опционально — выключенная подсистема даёт честный отказ инструмента.
    new ClaudeHomeServer.Services.TestRuns.TestRunsSubsystem(),
    // Changelog — «Что нового»: листовая подсистема, ни от кого не зависит, читает
    // git-вывод и `data/changelog/product.json` через `FileService`.
    new ClaudeHomeServer.Services.Changelog.ChangelogSubsystem(),
    // Tasks — вертикаль задач с доской агентов (BoardService). Утренний брифинг
    // (DailyBriefingService) остался в корне Services как кросс-вертикальный фасад
    // (Tasks+Notes+Llm) и регистрируется выше по тексту Program.cs.
    // Регистрируется после Changelog потому что зависит от `Services.Hubs`
    // (IHubContext<SessionHub>) и `Services.Llm` (ICheapTextRunner через TaskAiService).
    // TaskSchedulerService — gated hosted, его тип `AddGatedHostedService` активируется
    // флагом (см. appsettings). Шов Tasks → Models.Session (три статических
    // резолвера) описан в `Services/Tasks/TasksSubsystem.cs` (см. комментарий 6).
    new ClaudeHomeServer.Services.Tasks.TasksSubsystem(),
    // Notes — динамический модуль (сценарий Б): грузится ModuleLoader'ом по пути из
    // секции DynamicModules, НЕ через ProjectReference. Шов `Notes → Tasks`
    // через `INoteTaskBridge` (Core) + `TaskBridge` (Main, ниже).
    // Skills — вертикаль навыков (реестр skills.sh, LLM-подбор/генерация, обёртка
    // CLI «npx skills»). Регистрируется после Notes: порядок с Task/Notes не связан
    // (Skills — листовая, единственный шов `SessionManager → SkillsService` остаётся
    // до этапа 4 — расщепление SessionManager).
    new ClaudeHomeServer.Services.Skills.SkillsSubsystem(),
    // Plugins (ADR-031, шаг 1.2) — вертикаль плагинов владельца: хранилище
    // (PluginStore) и IPluginDirSource (каталоги для --plugin-dir). Листовая:
    // потребители (ClaudeSession, мост панели) берут IPluginDirSource/IPluginCapability
    // опционально через Core-швы, порядок с остальными не связан.
    new ClaudeHomeServer.Services.Plugins.PluginsSubsystem());

// Строки ядра состава (S2b-1): функциональность продукта, которая не `IAppSubsystem` —
// сессии и чат, проекты, вход, файлы. Они всегда в процессе, гейта не имеют и в панели
// «Компоненты» показываются отдельной группой без тумблеров (canDisable=false,
// docs/mockups/components-admin-v1-proposal.md). Ключ с префиксом `core:` — чтобы не
// столкнулся с ключом вертикали; в ActiveKeys (`/api/auth/me`) они не попадают.
var subsystemsState = builder.Services.SubsystemsState();
subsystemsState.RecordCore("core:sessions", "Сессии и чат",
    "Чаты, сессии и ходы: ядро рантайма, всегда включено.");
subsystemsState.RecordCore("core:projects", "Проекты",
    "Реестр проектов и их настройки: ядро, всегда включено.");
subsystemsState.RecordCore("core:auth", "Вход",
    "Вход, пользователи и токены: ядро входа, всегда включено.");
subsystemsState.RecordCore("core:files", "Файлы",
    "Файлы проектов и наблюдение за диском: ядро работы с диском, всегда включено.");

// Учёт обращений к подсистемам: карта «сборка → ключ» и стор строятся из АКТИВНЫХ подсистем
// после AddSubsystems; сброс на диск — hosted-сервис (в инспекционной копии не пишет)
builder.Services.AddSingleton(sp => new ClaudeHomeServer.Services.Composition.SubsystemUsageAttribution(
    sp.GetRequiredService<ClaudeHomeServer.Services.Composition.SubsystemStateStore>(),
    sp.GetServices<ClaudeHomeServer.Services.Mcp.Http.IMcpToolset>()));
builder.Services.AddSingleton(sp => new ClaudeHomeServer.Services.Composition.SubsystemUsageStore(
    sp.GetRequiredService<ClaudeHomeServer.Services.Composition.SubsystemUsageAttribution>().ObservableKeys));
if (!inspectionMode)
    builder.Services.AddHostedService<ClaudeHomeServer.Services.Composition.SubsystemUsageFlushService>();
// Общие HTTP-клиенты бэкенда (fal, OnlyOffice, glif, MCP probe/OAuth/catalog,
// llm-provider, alibaba-console, anthropic-oauth) — в `Composition/HttpClientRegistration.cs`
// (S6-2): они нужны нескольким вертикалям сразу, а не одной. Клиенты локальных сервисов
// (dify, forgejo) — у своих вертикалей.
builder.Services.AddSharedHttpClients(builder.Configuration);
// Веб-поиск Perplexity Sonar (MCP-сервер websearch): опции, их HTTP-клиент и сам сервис —
// регистрация в самой вертикали WebSearch (`WebSearchRegistration.AddWebSearch`, S6-2):
// клиент нужен ровно PerplexitySearchService, за пределами сборки их никто не резолвит.
builder.Services.AddWebSearch(builder.Configuration);
// Клиенты модельного слоя (llm-provider, alibaba-console, anthropic-oauth) — там же,
// в HttpClientRegistration (S6-2).
builder.Services.AddHttpForwarder();
// Раздел «Телеметрия»: опции проброса SigNoz UI (Telemetry:Ui) + короткий HTTP-клиент
// для health-пинга статуса. Опции регистрируем ВСЕГДА (выключенные тоже) — их читают
// и контроллер статуса, и middleware проброса /telemetry-proxy/** ниже.
builder.Services.AddSingleton(
    ClaudeHomeServer.Telemetry.TelemetryUiOptions.FromConfig(builder.Configuration));
builder.Services.AddHttpClient("telemetry-ui", c => c.Timeout = TimeSpan.FromSeconds(3))
    .WithoutEgressProxy();
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"))
    .ConfigureHttpClient((_, handler) =>
    {
        // Таймаут установки TCP-соединения с модулем. Без него при мёртвом модуле
        // connect() висит до HttpClient.Timeout (100 с). 2 с — достаточно для любого
        // живого бэкенда и не блокирует шлюз надолго при недоступности.
        handler.ConnectTimeout = TimeSpan.FromSeconds(2);
    });
// Платформа внешних модулей (docs/modules/integration-contract.md): реестр манифестов,
// RS256-токены с JWKS и ДОБАВОЧНЫЙ провайдер YARP-конфига из реестра (LoadFromConfig выше
// не заменяется — YARP объединяет несколько IProxyConfigProvider, существующие маршруты
// OnlyOffice/drawio/forgejo работают как раньше).
builder.Services.AddSingleton<ClaudeHomeServer.Services.Modules.ModuleRegistry>();
builder.Services.AddSingleton<ClaudeHomeServer.Services.DynamicModules.RemoteStaticFiles>();
builder.Services.AddSingleton<ClaudeHomeServer.Services.Modules.ModuleTokenService>();
builder.Services.AddSingleton<Yarp.ReverseProxy.Configuration.IProxyConfigProvider,
    ClaudeHomeServer.Services.Modules.ModuleProxyConfigProvider>();
// LLM-канал модулей (контракт §10): лимит конкурентности per-модуль + учёт вызовов R13
builder.Services.AddSingleton<ClaudeHomeServer.Services.Modules.HostLlmConcurrencyLimiter>();
builder.Services.AddSingleton<ClaudeHomeServer.Services.Modules.ModuleLlmUsageStore>();
// Питание машины из веб-морды (выключить/перезагрузить/усыпить). Как и выкатка, по умолчанию
// выключено — см. PowerControlOptions. Сервисы живут в хосте (модуля Power нет), поэтому
// регистрации — здесь, а не в подсистеме.
builder.Services.Configure<ClaudeHomeServer.Models.PowerControlOptions>(builder.Configuration.GetSection(ClaudeHomeServer.Models.PowerControlOptions.Section));
builder.Services.AddSingleton<ClaudeHomeServer.Services.Power.IPowerActions,
    ClaudeHomeServer.Services.Power.WindowsPowerActions>();
builder.Services.AddSingleton<ClaudeHomeServer.Services.Power.PowerControlService>();
// Пульт удалённых команд (запуск/остановка объявленных в конфиге действий на машине сервера):
// тот же приём, что у питания — по умолчанию выключено, реестр действий только из
// appsettings.Local.json. Hosted-регистрация ФОРВАРДИТСЯ на тот же синглтон, а не
// AddHostedService<T>(): тот создал бы второй экземпляр, и StopAsync гасил бы daemon-детей
// у пустышки, оставляя настоящих сиротами.
builder.Services.Configure<ClaudeHomeServer.Models.RemoteCommandsOptions>(builder.Configuration.GetSection(ClaudeHomeServer.Models.RemoteCommandsOptions.Section));
builder.Services.AddSingleton<ClaudeHomeServer.Services.RemoteCommands.IShellCommandRunner,
    ClaudeHomeServer.Services.RemoteCommands.LocalShellCommandRunner>();
builder.Services.AddSingleton<ClaudeHomeServer.Services.RemoteCommands.RemoteCommandsService>();
builder.Services.AddSingleton<IHostedService>(sp =>
    sp.GetRequiredService<ClaudeHomeServer.Services.RemoteCommands.RemoteCommandsService>());
// Авто-возобновление ходов после рестарта (ADR-033): параметры из секции TurnAutoResume.
builder.Services.Configure<ClaudeHomeServer.Models.TurnAutoResumeOptions>(builder.Configuration.GetSection(ClaudeHomeServer.Models.TurnAutoResumeOptions.Section));
// Фоновый оркестратор: шов-хост (адаптер над SessionManager) + hosted-сервис (в Testing не стартует)
builder.Services.AddSingleton<ClaudeHomeServer.Services.Composition.ITurnAutoResumeHost, ClaudeHomeServer.Services.SessionManagerTurnResumeHost>();
builder.Services.AddGatedHostedService<ClaudeHomeServer.Services.TurnAutoResume.TurnAutoResumeService>(builder.Configuration);
// Клей узких швов — участники реконсайлера Dify, адаптеры швов Knowledge/Spend/Deploy/Turn,
// контекст вызова MCP-тулсетов, мосты Notes↔Tasks и рассыльщик сессий: в
// `Composition/CoreSeamsGlue.cs` (S6-2). Это единственное место, где известны обе стороны шва.
builder.Services.AddCoreSeams(builder.Configuration);

// JWT для REST/SignalR; Negotiate (NTLM/Kerberos) для WebDAV (Microsoft Office).
// Плюс именованная схема токена устройства: дефолтная JwtBearer к каналу устройства не
// допускается вовсе — сервисный JWT владельца лежит в env каждого его хода. Схема
// именованная: эндпоинты канала называют её явным [Authorize(AuthenticationSchemes = ...)],
// и ни один эндпоинт не открывается «заодно».
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer()
    // Неудачный Type3 (на Linux gss-ntlmssp кидает исключение → 500) → 401 с одним Basic
    .AddNegotiate(o => o.Events = new Microsoft.AspNetCore.Authentication.Negotiate.NegotiateEvents
    {
        OnAuthenticationFailed = ClaudeHomeServer.WebDav.NegotiateFailure.HandleAsync,
    })
    // токен устройства: 256 бит, на сервере только хеш в data/devices.json
    .AddDeviceAuth();
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<JwtService>((opts, jwt) =>
    {
        opts.TokenValidationParameters = jwt.ValidationParameters;
        opts.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                var token = ctx.Request.Query["access_token"].ToString();
                if (!string.IsNullOrWhiteSpace(token)) ctx.Token = token;
                return Task.CompletedTask;
            },
            // Отзыв токенов сменой пароля: подпись и срок ещё ничего не гарантируют.
            // Здесь проверка накрывает весь [Authorize]-периметр — REST и handshake SignalR
            OnTokenValidated = ctx =>
            {
                var svc = ctx.HttpContext.RequestServices.GetRequiredService<JwtService>();
                if (!svc.IsSessionCurrent(ctx.Principal)) ctx.Fail("Токен отозван сменой пароля");
                return Task.CompletedTask;
            }
        };
    });
builder.Services.AddAuthorization(options =>
    // Политика для ручек, которые обязаны быть доступны из ХОДА админа (MCP-инструменты
    // deploy_*): роль в сервисном токене всегда "user", а проверять надо владельца по стору —
    // см. AdminByStoreRequirement
    options.AddPolicy(AdminByStoreRequirement.PolicyName, policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.Requirements.Add(new AdminByStoreRequirement());
    }));
builder.Services.AddSingleton<IAuthorizationHandler, AdminByStoreHandler>();

// За reverse-proxy (Caddy/туннель) берём реальный IP клиента из X-Forwarded-For,
// иначе rate-limit считал бы все запросы с адреса прокси как один
builder.Services.Configure<ForwardedHeadersOptions>(o =>
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);

// Лимитеры RateLimitingMiddleware утилизирует контейнер: иначе их таймер держит хост
// после остановки (см. RateLimiterLifetime)
builder.Services.AddSingleton<RateLimiterOwner>();
// Защита /api/auth/login от перебора паролей — фиксированное окно на IP.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // §10.2 v1.5: превышение лимита → 429 + Retry-After. FixedWindowRateLimiter кладёт
    // остаток окна в метаданные лизы при отказе, но заголовок сам не проставляет — читаем
    // и выставляем явно, для всех политик разом (тело ответа не трогаем).
    options.OnRejected = (ctx, _) =>
    {
        if (ctx.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            ctx.HttpContext.Response.Headers.RetryAfter =
                ((int)retryAfter.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return ValueTask.CompletedTask;
    };
    options.AddPolicy("auth-login", ctx =>
    {
        var limit = ctx.RequestServices.GetRequiredService<IConfiguration>()
            .GetValue("Auth:LoginRateLimit", 10);
        return RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = limit,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            });
    });
    // Выпуск host-токенов модулей (§10.2 v1.5): RSA-подпись на каждый выпуск — защищаем от
    // молотьбы. Партиция — {moduleId}:{sub} из aud/sub токена (без проверки подписи, она и
    // не нужна: подделка aud/sub лишь уводит запрос в другую партицию, сам запрос всё равно
    // упрётся в Validate следом). Иначе все модули идут с внутренних адресов (127.0.0.1,
    // host.docker.internal) и делят один общий счётчик — зациклившийся модуль бьёт по
    // всем соседям. Нераспознанный токен — фолбэк на IP, чтобы не образовать общую корзину.
    options.AddPolicy("host-token", ctx =>
    {
        var limit = ctx.RequestServices.GetRequiredService<IConfiguration>()
            .GetValue("Modules:HostTokenRateLimit", 30);
        var registry = ctx.RequestServices.GetRequiredService<ModuleRegistry>();
        var token = HostChannelMiddleware.ExtractBearerToken(ctx.Request);
        var partitionKey = HostChannelMiddleware.TryReadPartitionKey(token, registry)
            ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = limit,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            });
    });
    // Анонимная раздача агента устройства (agent-distribution Р11): скрипты, указатель и
    // архивы по 70–100 МБ. Партиция — IP: учётных данных у установщика нет по построению
    options.AddPolicy(ClaudeHomeServer.Controllers.AgentDownloadsController.RateLimitPolicy, ctx =>
    {
        var limit = ctx.RequestServices.GetRequiredService<IConfiguration>()
            .GetValue("DeviceAgent:DownloadRateLimit", 30);
        return RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = limit,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            });
    });
    // Поиск по каталогу MCP-серверов: потолок запросов обязан стоять на бэке —
    // дебаунс фронта не защита. Партиция — владелец: реестр внешний, и молотить его
    // от одного аккаунта нельзя; без sub (запрос до авторизации) — фолбэк на IP
    options.AddPolicy("mcp-catalog", ctx =>
    {
        var limit = ctx.RequestServices.GetRequiredService<IConfiguration>()
            .GetValue("Mcp:Catalog:RateLimit", 30);
        var partitionKey = ctx.User.FindFirst("sub")?.Value
            ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = limit,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            });
    });
});

// CORS: только белый список origin'ов из конфига (Cors:AllowedOrigins).
// Фронт раздаётся same-origin из wwwroot, поэтому пустой список ничего не ломает.
var corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins(corsOrigins)
     .AllowAnyHeader()
     .AllowAnyMethod()
     .AllowCredentials()));

// Инспекционная копия не запускает НИ ОДИН свой фоновый сервис: планировщик задач взялся
// бы выполнять просроченные задачи Claude-исполнителем (реальные деньги и правки файлов),
// синхронизация знаний — «поправлять» боевые датасеты Dify под отставшее состояние,
// автоочистки — удалять чаты и заметки. Снимаем регистрации разом, а не по списку:
// перечень пришлось бы дописывать при каждом новом сервисе, и однажды его забудут.
// Хостинговые сервисы самого ASP.NET Core (Kestrel и прочие) не трогаем — только свои.
//
// Фильтр — по имени сборки `ClaudeHomeServer` и `ClaudeHomeServer.*`, минус `*.Tests`:
// ровно та же логика, что у сторожей границ (SubsystemBoundaryTests), иначе вынесенная
// вертикаль (Llm, Images, Tasks, Notes…) продолжит ехать в инспекции молча — фильтр
// по одной сборке Main её не видит.
static bool IsOwnHostedServiceDescriptor(ServiceDescriptor d)
{
    var implAsm = d.ImplementationType?.Assembly
        ?? d.ImplementationFactory?.Method.DeclaringType?.Assembly;
    var n = implAsm?.GetName().Name;
    if (n is null) return false;
    if (n.EndsWith(".Tests", StringComparison.Ordinal)) return false;
    return n == "ClaudeHomeServer" || n.StartsWith("ClaudeHomeServer.", StringComparison.Ordinal);
}

// Чтение `inspectionMode` здесь повторное — первое (Program.cs:74) смотрит на конфиг ДО
// `ConfigureAppConfiguration` (TestWebApplicationFactory.ExtraConfig), второй раз —
// уже после всех источников. Раннее чтение остаётся для ProcessRegistry/InstanceLock,
// позднее — для гейта фоновых сервисов: без этого фильтр не снимал бы хосты
// телеметрии и вынесенных вертикалей в тестовой среде.
if (builder.Configuration.GetValue<bool>("InspectionMode"))
{
    var background = builder.Services
        .Where(d => d.ServiceType == typeof(IHostedService))
        .Where(IsOwnHostedServiceDescriptor)
        .ToList();
    foreach (var descriptor in background) builder.Services.Remove(descriptor);
    Console.WriteLine($"[Inspection] фоновые сервисы отключены ({background.Count})");
}

var app = builder.Build();
if (subsystemsFiles is not null) app.Lifetime.ApplicationStopped.Register(subsystemsFiles.Dispose);

// Пост-билд шаг подсистем: для каждой подсистемы, реализующей IAppPhaseSubsystem,
// зовём ConfigureApp(app) в порядке регистрации. Вызываем ДО любых других
// пост-билд инициализаций и до middleware-конвейера, чтобы зависящие от него
// шаги (например, MigrateFromProjects в Knowledge) уже видели результат.
app.UseSubsystems();

// Стартовый ряд после Build(): статические логгеры и корни транскриптов, доводка после
// восстановления, прогрев обязательных сервисов, разовые миграции сторов — в
// `Composition/StartupWarmup.cs` (S6-2). Порядок внутри ряда сохранён строка в строку:
// PostRestoreHook обязан идти до MigrateFromProjects, ModuleRegistry — до слоя Llm.
await app.RunStartupWarmup(inspectionMode);

app.UseForwardedHeaders();

// Ставится сразу за ForwardedHeaders — раньше любого перехватчика, чтобы падение в них
// тоже попало в структурный лог, а не в безымянное сообщение Kestrel.
app.UseExceptionHandler();

// Принудительный HTTPS только для публичного домена naychenko.me;
// доступ из локальной сети по IP остаётся по HTTP (сертификат на IP не выдан)
if (!app.Environment.IsDevelopment())
    app.Use(async (ctx, next) =>
    {
        if (!ctx.Request.IsHttps &&
            ctx.Request.Host.Host.EndsWith("naychenko.me", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Response.Redirect(
                $"https://{ctx.Request.Host.Host}{ctx.Request.PathBase}{ctx.Request.Path}{ctx.Request.QueryString}",
                permanent: false);
            return;
        }
        await next();
    });
// Внешний доступ к дев-серверу проекта по отдельному поддомену (ExternalPreviewOptions).
//
// Почему сайт нельзя показать под путём /preview/{id}/ и понадобился свой хост: дев-сервер
// отдаёт абсолютные ссылки от корня («/assets/app.js», «/@vite/client», чанки Module
// Federation), и под префиксом они уходят в корень ПРОДУКТА, а не к сайту. Здесь префикса нет.
//
// Стоит ДО UseRouting не из-за раздачи фронта (она сильно ниже), а из-за перехватчиков между:
// WebDAV на /projects/*, ModuleGateway и прокси OnlyOffice, который ловит ЛЮБОЙ путь, у
// которого второй символ — цифра. Ассеты проксируемого сайта попадали бы к ним.
//
// Правило перехвата ИНВЕРСНОЕ: хост из конфигурации наш всегда, и всё, что мы не обслужили
// сами, отбивается здесь же. Иначе выключенная фича отдавала бы со второго имени весь
// продукт целиком — и /api, и фронт.
{
    var extInvoker = new HttpMessageInvoker(new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false,
    });

    app.Use(async (ctx, next) =>
    {
        var router = ctx.RequestServices.GetRequiredService<ExternalPreviewRouter>();
        if (!router.IsOwnHost(ctx.Request.Host.Host)) { await next(); return; }

        // Продление сертификата по HTTP-01 обязано проходить мимо перехвата. Сейчас выпуск
        // идёт через DNS-01, но смену механизма наш 404 сломал бы молча и через месяцы —
        // причину искали бы где угодно, только не здесь.
        if (ctx.Request.Path.StartsWithSegments("/.well-known/acme-challenge"))
        {
            await next();
            return;
        }

        // Обмен токена на куку. Токен обязан исчезнуть из адресной строки: в URL он осел бы
        // в истории браузера, в закладках и в логах любого промежуточного узла.
        if (ctx.Request.Path.Equals(ExternalPreviewRouter.AuthPath, StringComparison.OrdinalIgnoreCase))
        {
            var handoff = ctx.Request.Query["t"].ToString();
            var (authTarget, authDenial) = await router.ResolveAsync(handoff);
            if (authTarget is null)
            {
                await ExternalPreviewResponses.WriteDenialAsync(ctx, authDenial);
                return;
            }

            ctx.Response.Cookies.Append(ExternalPreviewRouter.CookieName, handoff, new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Lax,
                Path = "/",
                // Срок куки = остатку срока ссылки. Сессионная умерла бы вместе с вкладкой
                // телефона, и это выглядело бы как «ссылка сломалась» на ровном месте.
                MaxAge = authTarget.Link.ExpiresAt - DateTimeOffset.UtcNow,
            });
            ctx.Response.Redirect("/");
            return;
        }

        var (target, denial) = await router.ResolveAsync(ctx.Request.Cookies[ExternalPreviewRouter.CookieName]);
        if (target is null)
        {
            await ExternalPreviewResponses.WriteDenialAsync(ctx, denial);
            return;
        }

        // Префикс не срезаем и не добавляем: сайт живёт в корне — ровно ради этого здесь
        // отдельный хост, а не путь.
        var extForwarder = ctx.RequestServices.GetRequiredService<IHttpForwarder>();
        var extError = await extForwarder.SendAsync(ctx, target.BaseUrl, extInvoker, ForwarderRequestConfig.Empty,
            new ExternalPreviewTransformer(target.Port, ctx.Request.Host, ctx.Request.IsHttps));
        // До назначения не достучались — забываем и порт, и выбранную семью адресов:
        // сервис мог смениться. Отмены клиентом о живости назначения не говорят ничего.
        if (extError is ForwarderError.Request or ForwarderError.RequestTimedOut)
            router.ForgetPort(target.Link.Jti, target.Port);
    });
}

// Сжатие — только REST: статика, превью-прокси, хабы и MCP живут по своим правилам.
// /api/auth исключён: токен в теле ответа рядом со сжатием — материал для BREACH
app.UseWhen(
    ctx => ctx.Request.Path.StartsWithSegments("/api") && !ctx.Request.Path.StartsWithSegments("/api/auth"),
    branch => branch.UseResponseCompression());

app.UseRouting();
app.UseCors();
// UseRateLimiter — после UseRouting, иначе эндпоинт-политика [EnableRateLimiting] не видна
app.UseRateLimiterOwnedByHost();
// Инспекционная копия — только чтение. Гейт один на весь пайплайн, а не перечень
// контроллеров: перечень устаревает с каждым новым эндпоинтом. Стоит ДО аутентификации
// (иначе запись отбивал бы 401 раньше нас, и гейт работал бы только для залогиненных),
// ДО WebDAV и YARP-прокси (их PUT/MKCOL/MOVE/DELETE пишут в реальные папки проектов).
//
// Следствие: POST /hubs/*/negotiate тоже отбивается, поэтому SignalR в копии не
// поднимается и UI показывает ошибку подключения. Это защита, а не дефект: живой хаб
// означал бы ходы чатов и терминалы в боевых рабочих папках.
if (inspectionMode)
{
    app.Use(async (ctx, next) =>
    {
        var method = ctx.Request.Method;
        var readOnly = HttpMethods.IsGet(method) || HttpMethods.IsHead(method)
            || HttpMethods.IsOptions(method);
        var isAuth = ctx.Request.Path.StartsWithSegments("/api/auth");

        if (!readOnly && !isAuth)
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            await ctx.Response.WriteAsJsonAsync(new
            {
                error = "inspection_read_only",
                message = "Инспекционная копия работает только на чтение",
            });
            return;
        }
        await next();
    });
}

// Хаб устройств и канал исполнения — только HTTPS или петля, как сопряжение (ADR-008)
ClaudeHomeServer.Services.Devices.DeviceChannelGuard.UseDeviceChannelGuard(app);
app.UseAuthentication();
app.UseAuthorization();

// Наблюдаемость продуктовых MCP-серверов: их вызовы к бэкенду видно по заголовку
// X-Caller-Session-Id. Ставим ПОСЛЕ авторизации: иначе 401 попадал бы в статистику как
// отказ инструмента, хотя до контроллера запрос и не дошёл
app.UseMcpCallLog();

// Учёт обращений по ключу подсистемы (HTTP + MCP): после авторизации — 401/403 в счёт не идут
app.UseSubsystemUsage();

// Gateway внешних модулей (контракт §5.2): срезка клиентских identity-заголовков,
// валидация cc_token и инъекция модульного токена — ДО YARP-прокси
app.UseModuleGateway();

// Host-канал модулей (контракт §10.2): аутентификация /api/host/** модульным токеном RS256.
// Поверхность вне HMAC-схемы ядра — контроллеры под префиксом БЕЗ [Authorize], вход
// охраняется только здесь. Rate-limit обмена токена отрабатывает раньше (UseRateLimiter выше).
app.UseHostChannel();

// WebDAV — middleware перехватывает /projects/* до роутинга.
// Собственный Basic Auth внутри хендлера, вне JWT pipeline.
// Также отвечает на OPTIONS / (Windows WebClient зондирует корень перед монтированием).
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path.Value ?? "";
    if (ctx.Request.Method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase) && path == "/")
    {
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentLength = 0;
        ctx.Response.Headers["DAV"] = "1, 2";
        ctx.Response.Headers["MS-Author-Via"] = "DAV";
        ctx.Response.Headers["Allow"] = "OPTIONS, GET, HEAD, PUT, DELETE, PROPFIND, PROPPATCH, MKCOL, COPY, MOVE, LOCK, UNLOCK";
        return;
    }
    if (path == "/projects" || path.StartsWith("/projects/", StringComparison.OrdinalIgnoreCase))
    {
        await ClaudeHomeServer.WebDav.WebDavHandler.HandleAsync(ctx);
        return;
    }
    await next(ctx);
});

// OnlyOffice DS добавляет версионный префикс к URL ресурсов И Socket.IO WebSocket:
// /9.4.0-hash/web-apps/... и /9.4.0-hash/doc/.../c/?transport=websocket
// IHttpForwarder поддерживает WebSocket upgrade нативно — в отличие от HttpClient.
{
    var dsBase = builder.Configuration
        .GetSection("ReverseProxy:Clusters:onlyoffice:Destinations:default")
        .GetValue<string>("Address") ?? "http://localhost:8090";
    var ooInvoker = new HttpMessageInvoker(new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false,
    });

    // no-op SW: заменяем проблемный OO Service Worker нейтральным,
    // который очищает все кеши и не перехватывает запросы.
    // Иначе после первого визита SW кешируется в браузере и начинает
    // перехватывать Analytics.js с ошибкой net::ERR_FAILED.
    const string noOpSw =
        "self.addEventListener('install',e=>e.waitUntil(self.skipWaiting()));" +
        "self.addEventListener('activate',e=>e.waitUntil(" +
        "caches.keys().then(ks=>Promise.all(ks.map(k=>caches.delete(k)))).then(()=>self.clients.claim())" +
        "));";

    app.Use(async (ctx, next) =>
    {
        var path = ctx.Request.Path.Value ?? "";
        if (path.Length > 1 && char.IsDigit(path[1]))
        {
            // Service Worker OO заменяем no-op-ом — избегаем cacheFirst-ошибок в браузере
            if (path.EndsWith("/document_editor_service_worker.js", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Response.ContentType = "application/javascript; charset=utf-8";
                ctx.Response.Headers["Service-Worker-Allowed"] = "/";
                ctx.Response.Headers["Cache-Control"] = "no-store";
                await ctx.Response.WriteAsync(noOpSw);
                return;
            }

            var forwarder = ctx.RequestServices.GetRequiredService<IHttpForwarder>();
            await forwarder.SendAsync(ctx, dsBase, ooInvoker, ForwarderRequestConfig.Empty, HttpTransformer.Default);
            return;
        }
        await next();
    });
}

// Dev-server preview proxy: /preview/{projectId}/{**path} → http://127.0.0.1:{port}
// Сам проброс — DevServerPreviewForwarder вертикали (им же пользуется агент устройства),
// здесь только вход: кто вправе смотреть превью проекта.
{
    var previewInvoker = DevServerPreviewForwarder.CreateInvoker();

    app.Use(async (ctx, next) =>
    {
        if (DevServerPreviewForwarder.TryParse(ctx.Request.Path.Value, out var projectId, out var restPath))
        {
            // Аутентификация: middleware выполняется ДО endpoint routing, поэтому [Authorize]
            // тут не действует и ctx.User для iframe-запроса пуст. Токен берём из cookie
            // cc_preview (её ставит фронт перед загрузкой iframe — уходит и с сабресурсами),
            // либо из access_token / Bearer (прямое открытие в новой вкладке). Затем сверяем
            // владельца проекта — иначе любой мог бы проксироваться на чужой dev-сервер.
            var jwtSvc = ctx.RequestServices.GetRequiredService<JwtService>();
            // Куку принимаем только со СВОЕГО адреса. SameSite=Strict тут не защита: поддомен
            // внешнего доступа — тот же site, и браузер приложил бы куку к запросу со страницы
            // проксируемого дев-сайта (см. SecFetchSiteGuard). Отвергнутая кука роняет запрос
            // на access_token/Bearer ниже — их автоматически никто не подставляет.
            var previewToken = SecFetchSiteGuard.CookieAuthAllowed(ctx.Request)
                ? ctx.Request.Cookies["cc_preview"]
                : null;
            if (string.IsNullOrEmpty(previewToken))
            {
                var q = ctx.Request.Query["access_token"].ToString();
                if (!string.IsNullOrEmpty(q)) previewToken = q;
                else
                {
                    var auth = ctx.Request.Headers.Authorization.ToString();
                    if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                        previewToken = auth["Bearer ".Length..].Trim();
                }
            }
            var previewUserId = jwtSvc.ValidateUserToken(previewToken);
            if (previewUserId is null)
            {
                ctx.Response.StatusCode = 401;
                await ctx.Response.WriteAsync("{\"error\":\"Требуется авторизация\"}");
                return;
            }
            var previewProject = ctx.RequestServices.GetRequiredService<ProjectManager>().GetById(projectId);
            if (previewProject is null || previewProject.OwnerId != previewUserId)
            {
                ctx.Response.StatusCode = 403;
                await ctx.Response.WriteAsync("{\"error\":\"Доступ запрещён\"}");
                return;
            }
            // Превью локального проекта отдаёт агент устройства (ADR-016, G1), не сервер
            if (ProjectCapabilityGuard.Refusal(previewProject, ProjectCapabilityArea.FileBound) is { } refusal)
            {
                ctx.Response.StatusCode = StatusCodes.Status409Conflict;
                await ctx.Response.WriteAsJsonAsync(new { error = refusal, code = ProjectCapabilityGuard.Code });
                return;
            }

            await DevServerPreviewForwarder.ForwardAsync(ctx,
                ctx.RequestServices.GetRequiredService<DevServerService>(),
                ctx.RequestServices.GetRequiredService<IHttpForwarder>(),
                previewInvoker, projectId, restPath);
            return;
        }
        await next();
    });
}

// Telemetry proxy: /telemetry-proxy/** → SigNoz UI (раздел «Телеметрия», admin-only).
// Same-origin проброс для iframe: браузер грузит /telemetry-proxy/ с нашего origin, мы
// форвардим на локальный SigNoz. Ключевое отличие от preview выше — префикс НЕ срезаем:
// SigNoz с env SIGNOZ_GLOBAL_EXTERNAL__URL=.../telemetry-proxy сам живёт под этим base-path
// (вставляет <base href> в SPA, ассеты и API резолвятся под префиксом — подтверждено спайком),
// поэтому HttpTransformer.Default отдаёт путь как есть. Стоит ДО раздачи фронта/SPA-fallback,
// чтобы /telemetry-proxy/* не ушёл в index.html.
{
    var telemetryUi = app.Services.GetRequiredService<ClaudeHomeServer.Telemetry.TelemetryUiOptions>();
    var telemetryInvoker = new HttpMessageInvoker(new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false,
    });

    app.Use(async (ctx, next) =>
    {
        var path = ctx.Request.Path.Value ?? "";
        if (path.Equals("/telemetry-proxy", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/telemetry-proxy/", StringComparison.OrdinalIgnoreCase))
        {
            // Выключено в конфиге — 503, фронт покажет заглушку «настрой, администратор».
            if (!telemetryUi.Enabled)
            {
                ctx.Response.StatusCode = 503;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                await ctx.Response.WriteAsync("{\"error\":\"Телеметрия не настроена\"}");
                return;
            }

            // Аутентификация как у preview: iframe не носит Bearer, поэтому токен берём из
            // cookie cc_telemetry (её ставит фронт перед загрузкой iframe — уходит и с
            // сабресурсами SigNoz), либо из access_token / Bearer (прямое открытие в новой вкладке).
            var jwtSvc = ctx.RequestServices.GetRequiredService<JwtService>();
            // Тот же гейт, что у preview: кука действует только со своего адреса
            var token = SecFetchSiteGuard.CookieAuthAllowed(ctx.Request)
                ? ctx.Request.Cookies["cc_telemetry"]
                : null;
            if (string.IsNullOrEmpty(token))
            {
                var q = ctx.Request.Query["access_token"].ToString();
                if (!string.IsNullOrEmpty(q)) token = q;
                else
                {
                    var auth = ctx.Request.Headers.Authorization.ToString();
                    if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                        token = auth["Bearer ".Length..].Trim();
                }
            }
            var userId = jwtSvc.ValidateUserToken(token);
            if (userId is null)
            {
                ctx.Response.StatusCode = 401;
                await ctx.Response.WriteAsync("{\"error\":\"Требуется авторизация\"}");
                return;
            }
            // Телеметрия — админская. Роль берём из стора (source-of-truth: ловит отзыв роли,
            // в отличие от роли из уже выданного JWT). Не админ — 403, чтобы валидный
            // cc_telemetry не-админа не пускал к SigNoz.
            var user = ctx.RequestServices.GetRequiredService<UserStore>().GetById(userId);
            if (user is null || user.Role != "admin")
            {
                ctx.Response.StatusCode = 403;
                await ctx.Response.WriteAsync("{\"error\":\"Доступ запрещён\"}");
                return;
            }

            var forwarder = ctx.RequestServices.GetRequiredService<IHttpForwarder>();
            await forwarder.SendAsync(ctx, telemetryUi.InternalUrl, telemetryInvoker,
                ForwarderRequestConfig.Empty, HttpTransformer.Default);
            return;
        }
        await next();
    });
}

// Статические ветки подсистем (сейчас — собранный Viaduct раздела «Архитектура»):
// ставятся здесь, после защитных middleware и до SPA-фолбэка, а не в UseSubsystems —
// там ветка ушла бы из-под HTTPS-редиректа и перехватчика превью-хоста
foreach (var contributor in app.Services.GetServices<IStaticBranchContributor>())
    contributor.Configure(app);

// Раздача фронтенда: wwwroot/ рядом с exe (prod) или ../../frontend/dist (dev)
var wwwrootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot");
var devDistPath = Path.GetFullPath(Path.Combine(
    Directory.GetCurrentDirectory(), "..", "..", "frontend", "dist"));
var distPath = Directory.Exists(wwwrootPath) ? wwwrootPath : devDistPath;
if (Directory.Exists(distPath))
{
    // Откуда реально раздаётся фронт — главный вопрос при «на стенде старый дизайн»;
    // wwwroot в репо не живет (артефакт сборки), деву нужен свежий frontend/dist
    app.Logger.LogInformation("Фронтенд раздаётся из {Path}", distPath);
    var fp = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(distPath);

    // index.html, SW-файлы и remoteEntry.js любых MF-remote — no-store: браузер всегда
    // берёт свежую точку входа. /assets/** и /{имя}-remote/assets/** — immutable: хэши
    // в именах гарантируют уникальность, кэшируем «вечно».
    Action<StaticFileResponseContext> setCacheHeaders = ctx =>
        ClaudeHomeServer.Services.DynamicModules.RemoteStaticFiles.ApplyCacheHeaders(
            ctx.Context.Request.Path, ctx.File.Name, ctx.Context.Response.Headers);

    // .onnx (модель Silero барж-ина, wwwroot/vad) в стандартной карте MIME отсутствует —
    // без явной записи StaticFiles отвечает 404, SPA-fallback отдаёт вместо модели
    // index.html, и VAD падает на инициализации («No graph was found in the protobuf»).
    // В dev не воспроизводится: статику там раздаёт Vite, он отдаёт octet-stream сам
    var contentTypes = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
    contentTypes.Mappings[".onnx"] = "application/octet-stream";

    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = fp });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = fp, OnPrepareResponse = setCacheHeaders, ContentTypeProvider = contentTypes });

    // MF-remote подсистем (N2): для каждого DynamicModules-модуля с Frontend.RemoteUrl
    // раздаём статику из ФИЗИЧЕСКОГО wwwroot/{имя-папки}. Отдельно от distPath (выше fp может
    // указывать на dev-dist, не на wwwroot), чтобы в проде запрос remoteEntry.js всегда
    // резолвился в файл, а не SPA-fallback → index.html (loadRemote упал бы).
    // Middleware стоит РАНЬШЕ MapFallbackToFile, поэтому перехватывает /*-remote/* до SPA-фолбэка.
    // Список раздачи — только загруженные модули (выключенный гейтом remote не отдаём).
    foreach (var servedModule in dynamicModuleRegistry.ServedRemotes(dynamicModuleStore))
    {
        var remoteUrl = servedModule.Frontend!.RemoteUrl;
        if (string.IsNullOrEmpty(remoteUrl)) continue;
        // L3 (2026-09-15): абсолютный URL (https://…) не начинается с '/' — TrimStart+Split
        // дал бы папку "https:" → Directory.Exists=false → тихий пропуск без лога.
        if (!remoteUrl.StartsWith('/'))
        {
            app.Logger.LogWarning(
                "[DynamicModules] Frontend:RemoteUrl модуля {Key} не является относительным путём ({Url}) — раздача MF-remote пропущена",
                servedModule.Key, remoteUrl);
            continue;
        }
        // Из RemoteUrl = "/{имя}-remote/remoteEntry.js" папка на диске = первый слаг URL.
        var folder = remoteUrl.TrimStart('/').Split('/')[0];
        if (string.IsNullOrEmpty(folder)) continue;
        var remotePath = Path.Combine(
            ClaudeHomeServer.Services.DynamicModules.RemoteStaticFiles.PrimaryRoot(app.Configuration), folder);
        if (Directory.Exists(remotePath))
        {
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(remotePath),
                RequestPath = "/" + folder,
                OnPrepareResponse = setCacheHeaders,
                ContentTypeProvider = contentTypes
            });
        }
    }

    // /_api/* — Office/SharePoint-запросы; возвращаем 404 вместо SPA, иначе Word показывает «Нет доступа»
    app.Map("/_api", api => api.Run(ctx => { ctx.Response.StatusCode = 404; return Task.CompletedTask; }));
    // /api/* — REST API: любой несопоставленный маршрут (выключенная подсистема, опечатка,
    // устаревший путь) обязан отдавать 404, а не 200+index.html из SPA-фолбэка — иначе клиент
    // не отличит «выключенный Spend» (контроллер не зарегистрирован, action нет) от
    // «живой чат SPA» (HTML-страница с JS): фронт получит 200 и попытается распарсить
    // HTML как JSON. Тот же дефект на живом хосте 2026-09-16: выключенный
    // /api/spend/overview отдавал 200 text/html.
    //
    // Раньше это была `app.Map("/api", api => api.Run(...404...))` — терминальная ветка
    // middleware, выполнявшаяся В ПАЙПЛАЙНЕ до исполнения выбранного эндпоинта и рубившая
    // ВСЕ /api/* как 404, ВКЛЮЧАЯ живые контроллеры (Александр, ревью c66e4127). Сейчас —
    // fallback-эндпоинт с шаблоном `/api/{**rest}`: более специфичный, чем
    // `MapFallbackToFile` ниже, поэтому выигрывает у SPA-фолбэка, но ПРОИГРЫВАЕТ реальным
    // контроллерам (MVC-роутинг специфичнее fallback'а). `/hubs/*`, `/mcp/*` и
    // `/api/modules` (ModuleGateway, регистрируется раньше строкой 1512) не задеты.
    app.MapFallback("/api/{**rest}", ctx => { ctx.Response.StatusCode = 404; return Task.CompletedTask; });
    app.MapFallbackToFile("index.html", new StaticFileOptions { FileProvider = fp, OnPrepareResponse = setCacheHeaders });
}
else
{
    app.Logger.LogWarning(
        "Фронтенд не найден: нет ни {Wwwroot} (рядом с exe), ни {Dist} (дев). " +
        "Сервер поднимется без статики — собери фронт (cd frontend; npm run build) или выложи wwwroot",
        wwwrootPath, devDistPath);
}

// JWKS модульных токенов (контракт §5.3) — публичный well-known, модули валидируют
// подписи RS256 по нему; аутентификации не требует по определению
app.MapGet("/.well-known/aihome-modules/jwks.json",
    (ModuleTokenService tokens) => Results.Json(tokens.BuildJwks()));

// Кастомный proxy-pipeline = дефолтный YARP + оформление ошибок модулей (§3.2):
// gateway обязан отдавать зарезервированные формы module_unavailable/module_timeout.
// Маршруты из конфига (OnlyOffice/drawio/forgejo) в ветку ошибок модулей не попадают.
app.MapReverseProxy(proxyPipeline =>
{
    proxyPipeline.Use(async (ctx, next) =>
    {
        await next();

        var routeId = ctx.GetReverseProxyFeature().Route.Config.RouteId;
        if (!routeId.StartsWith("module-", StringComparison.Ordinal) || ctx.Response.HasStarted)
            return;
        var moduleId = routeId["module-".Length..];

        var error = ctx.Features.Get<IForwarderErrorFeature>()?.Error;
        if (error == ForwarderError.RequestTimedOut)
        {
            // §3.1/§3.2: activity timeout 300 с бездействия → форма module_timeout
            ctx.Response.Clear();
            ctx.Response.StatusCode = StatusCodes.Status504GatewayTimeout;
            await ctx.Response.WriteAsJsonAsync(new { error = "module_timeout", moduleId });
        }
        else if (error is not null and not ForwarderError.RequestCanceled
                 || ctx.Response.StatusCode is StatusCodes.Status502BadGateway or StatusCodes.Status503ServiceUnavailable)
        {
            // Модуль погашен/unhealthy (нет доступных destinations или ошибка соединения)
            ctx.Response.Clear();
            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            ctx.Response.Headers.RetryAfter = "15";
            await ctx.Response.WriteAsJsonAsync(new { error = "module_unavailable", moduleId, retryAfterSeconds = 15 });
        }
    });
    proxyPipeline.UseSessionAffinity();
    proxyPipeline.UseLoadBalancing();
    proxyPipeline.UsePassiveHealthChecks();
});
app.MapControllers();
app.MapHub<SessionHub>("/hubs/session");
app.MapHub<TerminalHub>("/hubs/terminal");
// Канал десктопного агента (ADR-008): исходящее соединение клиента с машины пользователя,
// push команды в конкретное соединение. Схема авторизации — токен устройства, а не общий JWT
app.MapHub<ClaudeHomeServer.Services.Devices.DeviceHub>("/hubs/devices");
// Шлюз LLM локальных проектов (ADR-016): авторизация — токен хода, а не JWT; выключен
// тумблером LlmGateway:Enabled (404). llm — неотключаемая строка ядра (CanDisable=false в
// LlmSubsystem): панель её не трогает (PUT отдаёт 400 core_subsystem), а ручной
// Subsystems:llm:Enabled=false в конфиге роняет старт явным отказом (EnsureLlmEnabled,
// ADR-014 §5). Проверка наличия сервисов — страховка на случай нештатной сборки хоста.
if (app.Services.GetService<ClaudeHomeServer.Services.Llm.Gateway.UpstreamSelector>() is not null)
    ClaudeHomeServer.Services.Llm.Gateway.LlmGatewayEndpoints.MapLlmGateway(app);

// Шлюз MCP для хода на устройстве (ADR-016): вход по токену хода, а не по JWT.
// llm — неотключаемая строка ядра (CanDisable=false): конфиг её не выключает (старт падает
// с явным отказом, ADR-014 §5), панель тумблер не пишет; GetService — лишь страховка.
if (app.Services.GetService<ClaudeHomeServer.Services.Llm.Gateway.TurnTokenService>() is not null)
{
    ClaudeHomeServer.Services.Llm.Gateway.McpGatewayEndpoints.MapMcpGateway(app);
    // OCR страницы локальной Qwen (фича «руки без UIA», этап A, шаг A2)
    ClaudeHomeServer.Services.Llm.Gateway.OcrGatewayEndpoints.MapOcrGateway(app);
    // Туннель выхода собственного трафика CLI устройства: тумблер LlmGateway:Egress:Enabled
    ClaudeHomeServer.Services.Llm.Gateway.EgressGatewayEndpoints.MapEgressGateway(app);
}

// Потоковый канал исполнения устройства (ADR-016): WebSocket, та же схема токена устройства
app.MapDeviceExecChannel();

// Graceful shutdown: гасим все живые процессы claude, терминалы и dev-серверы.
//
// Ссылки берём СЕЙЧАС, а не в колбэке: если хост не смог подняться (занятый порт —
// обычное дело, когда рядом уже крутится второй инстанс), провайдер успевает
// освободиться раньше, чем сработает ApplicationStopping, и резолв падал с
// ObjectDisposedException — гасить процессы было уже нечем. Все три сервиса —
// синглтоны, так что заранее взятая ссылка та же самая.
var shutdownSessions = app.Services.GetRequiredService<SessionManager>();
// Токен хода (ADR-016): резолв здесь создаёт экземпляр, а с ним — подписку на
// turn/completed; удаление чата отзывает его токены явно (ход мог и не начаться).
// GetService — страховка: llm неотключаема (CanDisable=false, панель тумблер не пишет, а
// ручной Subsystems:llm:Enabled=false в конфиге роняет старт явным отказом, ADR-014 §5).
if (app.Services.GetService<ClaudeHomeServer.Services.Llm.Gateway.TurnTokenService>() is { } turnTokens)
    shutdownSessions.OnSessionDeleted += s => turnTokens.RevokeSession(s.Id);
var shutdownTerminals = app.Services.GetRequiredService<TerminalService>();
// GetService, а не GetRequiredService: подсистема project-services отключаемая
// (Subsystems:project-services) — на выключенной вертикали гасить dev-серверы нечем,
// и этот резолв не должен ронять сборку хоста (S2a-2, ADR-014).
var shutdownDevServers = app.Services.GetService<DevServerService>();
// Стор задач пишется с дебаунсом (TaskManager.ScheduleSave) — несохранённое досбрасываем
// руками. Dispose контейнера сделал бы то же самое, но он случается ПОСЛЕ остановки хоста,
// а терять правки при штатной остановке нельзя даже в окне между двумя фазами.
// GetService, а не GetRequiredService: подсистема задач отключаемая (Subsystems:tasks).
var shutdownTasks = app.Services.GetService<ClaudeHomeServer.Services.Tasks.TaskManager>();

app.Lifetime.ApplicationStopping.Register(() =>
{
    // Каждый шаг отдельно: упавшая уборка одного не должна оставить процессы других
    static void Safe(Action step, string what)
    {
        try { step(); }
        catch (Exception ex) { Console.Error.WriteLine($"Shutdown: {what} — {ex.Message}"); }
    }

    if (shutdownTasks is not null) Safe(shutdownTasks.Flush, "стор задач");
    Safe(shutdownSessions.KillAllProcesses, "процессы claude");
    Safe(shutdownTerminals.Dispose, "терминалы");
    if (shutdownDevServers is not null) Safe(shutdownDevServers.Dispose, "dev-серверы");
    // Тот же pid-файл принадлежит боевому серверу — копия его не трогает; не владеющий
    // реестром хост (тестовый) тем более: KillAll бьёт по ВСЕМ процессам процесса ОС
    if (ownsProcessRegistry) Safe(ProcessRegistry.KillAll, "реестр процессов");
});

app.Run();

// Отпустить признак «сервер работает» — после этого восстановление разрешено
if (instanceLock is not null)
{
    try { instanceLock.ReleaseMutex(); } catch { /* мьютекс мог быть заброшен */ }
    instanceLock.Dispose();
}

public partial class Program { }

// Фолбэк снимка для выключенного динамического модуля, когда его класс недоступен в развертке
// (нет dll): несёт Key/Title из дескриптора реестра без загрузки сборки, чтобы SubsystemStateStore
// показывал «выключено намеренно». Когда класс на месте, имя читается из него
// (ModuleLoader.DescribeDisabled) — человеческое имя и описание раздела знает только класс.
sealed class DisabledModuleStub(ClaudeHomeServer.Services.Composition.ModuleDescriptor desc)
    : ClaudeHomeServer.Services.Composition.IAppSubsystem
{
    public string Key => desc.Key;
    public string Title => desc.Title ?? desc.Key;
    public string Description => "";
    public void Register(IServiceCollection services, IConfiguration config) { }
}
