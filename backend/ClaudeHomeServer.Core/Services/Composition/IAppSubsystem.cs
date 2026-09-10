namespace ClaudeHomeServer.Services.Composition;

// Внутренние границы продукта: каждая подсистема инкапсулирует свой раздел
// (video/backup/telemetry/...) и регистрирует нужные ей сервисы и hosted-сервисы.
//
// Не путать с внешними модулями YARP за `Services/Modules` (манифест `module.json`):
// внешний модуль — отдельный процесс за реверс-прокси, у него свой контракт и
// свой реестр (`ModuleRegistry`/`IModule`). Этот интерфейс — про РЕГИСТРАЦИЮ
// ВНУТРИ Microsoft DI, без выгрузки, без hot-plug.
public interface IAppSubsystem
{
    // Латиница, нижний регистр, уникально в рамках процесса. Используется как ключ
    // настроек (например, "Telemetry:Backends"), лог-префикс и якорь в логах/диагностике.
    // Сравнение `Key` на дубли — без учёта регистра (см. `AddSubsystems`).
    string Key { get; }

    // Человекочитаемое имя подсистемы (для логов и диагностических дампов).
    string Title { get; }

    // Одно предложение о разделе для админского экрана «Подсистемы» (`/api/admin/subsystems`).
    // Дефолт — пустая строка: вертикали, написанные до появления поля, автоматически
    // получают «голую» строку (имя без описания); этого хватает для стартового пилота.
    // Новые реализации должны переопределять свойство осмысленным описанием.
    string Description => "";

    // Зарегистрировать свои сервисы. Вызывается один раз при сборке контейнера,
    // в порядке, заданном вызовом `AddSubsystems`.
    void Register(IServiceCollection services, IConfiguration config);
}

// Опциональная фаза «после Build»: подсистемы, которым нужно выполнить действия
// на готовом `WebApplication` (регистрация провайдеров графа, миграция стора знаний
// из старого формата, пост-билд-хуки и т.п.), реализуют этот интерфейс помимо
// базового `IAppSubsystem`. `UseSubsystems` после `builder.Build()` зовёт
// `ConfigureApp` в порядке регистрации.
//
// Выделено в отдельный интерфейс, а не в default interface method `IAppSubsystem`:
// в проекте DIM раньше ломал Moq (см. комментарии в `ILlmSessionAdapter.cs`),
// и подсистемы пишутся без DI-фреймворков для моков, чтобы не плодить второй
// источник граблей.
public interface IAppPhaseSubsystem : IAppSubsystem
{
    void ConfigureApp(WebApplication app);
}

// DTO для REST-эндпоинтов `/api/auth/me` и `/api/admin/subsystems`.
//   Description     — одно предложение о разделе из реализации `IAppSubsystem.Description`.
//   Enabled         — что сейчас в конфиге (`Subsystems:{Key}:Enabled`, default true).
//   Active          — была ли подсистема реально зарегистрирована на старте процесса.
//   RestartRequired — `Enabled` и `Active` различаются: чтобы изменение подействовало,
//                     нужен рестарт (гейт читается один раз при сборке контейнера).
public sealed record SubsystemInfo(
    string Key, string Title, string Description,
    bool Enabled, bool Active, bool RestartRequired);

// Снимок состава подсистем, увиденного `AddSubsystems` на старте процесса:
// и активные (прошли гейт и зарегистрировались), и задизейбленные (выпали по
// `Subsystems:{Key}:Enabled=false`). Хранится в singleton-сторе, чтобы REST-гейт
// (`GET /api/admin/subsystems`) мог отдать обе группы сразу: ключ, имя, текущая
// настройка из конфига, факт применения и признак «нужен рестарт».
//
// Зачем и активные, и задизейбленные:
// 1. Активные показывают, что реально работает в текущем процессе (`Active=true`).
// 2. Задизейбленные показывают, что КОГДА-ТО было, и сейчас выключено одной
//    галочкой — это критично для админа: иначе он не отличит «забыли подключить»
//    от «выключено намеренно».
// Стор делает `AddSubsystems` через `services.AddSingleton` сам, до первого
// `Register`/`Use`-прохода; конкурентных потоков на старте нет, лок не нужен.
public sealed class SubsystemStateStore
{
    private readonly Dictionary<string, SubsystemSnapshot> _snapshots =
        new(StringComparer.OrdinalIgnoreCase);

    // Регистрация подсистемы, прошедшей гейт. `Register` уже вызван, подсистема
    // добавлена в `IEnumerable<IAppSubsystem>` — отмечаем её как активную.
    public void RecordActive(IAppSubsystem subsystem) =>
        _snapshots[subsystem.Key] = new SubsystemSnapshot(
            subsystem.Key, subsystem.Title, subsystem.Description, Active: true);

    // Регистрация попытки: подсистема передана в `AddSubsystems`, но гейт
    // `Subsystems:{Key}:Enabled=false` сказал пропустить — отмечаем как
    // неактивную. `Register` НЕ вызывается, `IEnumerable<IAppSubsystem>` её не
    // увидит. Имя берём из самого инстанса — Key уже проверили выше, без него
    // дошли бы до бросания исключения и сюда не попали.
    public void RecordDisabled(IAppSubsystem subsystem) =>
        _snapshots[subsystem.Key] = new SubsystemSnapshot(
            subsystem.Key, subsystem.Title, subsystem.Description, Active: false);

    // Снимок для REST: ключ/имя/описание/включённость/активность/нужен-ли-рестарт.
    // Конфиг читаем СВЕЖИЙ (на момент запроса), а не тот, что был при AddSubsystems,
    // — иначе `RestartRequired` был бы всегда false, а это самый ценный сигнал:
    // админ хочет знать, что после галочки в конфиге нужен рестарт.
    public IReadOnlyList<SubsystemInfo> Snapshot(IConfiguration config) =>
        _snapshots.Values
            .OrderBy(s => s.Key, StringComparer.OrdinalIgnoreCase)
            .Select(s =>
            {
                var enabled = SubsystemGate.IsEnabled(config, s.Key);
                return new SubsystemInfo(
                    Key: s.Key,
                    Title: s.Title,
                    Description: s.Description,
                    Enabled: enabled,
                    Active: s.Active,
                    RestartRequired: enabled != s.Active);
            })
            .ToList();

    // Список ключей активных подсистем — для фронта в `/api/auth/me`.
    // Порядок — как в словаре, по алфавиту (OrdinalIgnoreCase): список короткий,
    // детерминированный порядок важнее «как регистрировались».
    public IReadOnlyList<string> ActiveKeys() =>
        _snapshots.Values
            .Where(s => s.Active)
            .Select(s => s.Key)
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private sealed record SubsystemSnapshot(string Key, string Title, string Description, bool Active);
}

// Единая точка проверки гейта `Subsystems:{Key}:Enabled`. Дефолт `true` —
// выключение явное. Используют `SubsystemRegistration.AddSubsystems`,
// `SubsystemHostingExtensions` и любые точечные регистрации в Program.cs,
// которые надо закрыть гейтом (форвардеры швов, контрибьюторы, hosted).
public static class SubsystemGate
{
    public static bool IsEnabled(IConfiguration config, string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Ключ подсистемы пуст — гейт неприменим.", nameof(key));
        // GetValue<bool> с дефолтом true: отсутствующая секция = подсистема включена.
        // Это сохраняет обратную совместимость: ни одна подсистема до сих пор не
        // знала про гейт, и без явной настройки всё должно работать как раньше.
        return config.GetValue($"Subsystems:{key}:Enabled", true);
    }
}

// Регистрация подсистем: одна точка входа, через которую Program.cs подключает
// все внутренние разделы. Защита от дублей `Key` — контрактная гарантия, что
// `Telemetry:Backends` и `Telemetry:Alerts` не окажутся конкурентами за одну секцию.
public static class SubsystemRegistration
{
    public static IServiceCollection AddSubsystems(
        this IServiceCollection services, IConfiguration config, params IAppSubsystem[] subsystems)
    {
        if (subsystems is null) throw new ArgumentNullException(nameof(subsystems));
        if (config is null) throw new ArgumentNullException(nameof(config));

        // Стор состава подсистем — singleton в контейнере. Создаём его ДО цикла по
        // подсистемам, чтобы и активные, и задизейбленные попали в один и тот же
        // инстанс. `AddSingleton` тут идемпотентен только в пределах одной коллекции;
        // если кто-то зовёт AddSubsystems дважды (тест), последний стор перетирает
        // первый вместе с историей — это приемлемо для теста: реальный Production
        // стартует ровно один раз.
        var state = new SubsystemStateStore();
        services.AddSingleton(state);

        // Порядок важен: подсистемы более низкого слоя идут первыми, верхние — позже.
        // Дубликат `Key` — ошибка конфигурации: тихо проглатывать её нельзя, иначе
        // одинаковые секции настроек перетрут друг друга непредсказуемо.
        // Сравнение без учёта регистра: `Key` — ключ секции конфигурации (`Telemetry:Backends`),
        // а `IConfiguration` регистронезависим, и `Video` vs `video` для него одно и то же.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var subsystem in subsystems)
        {
            if (subsystem is null) throw new ArgumentException(
                "Подсистема в списке null — вероятно, пропущена регистрация.", nameof(subsystems));

            // Пустой или null Key пробрасываем явно: `HashSet.Add(null)` упадёт
            // с неинформативным NRE изнутри таблицы, а две подсистемы с пустым
            // ключом дадут тот же "дубликат Key=''", не отличая смысловой конфликт
            // от банальной недозаполненности.
            if (string.IsNullOrWhiteSpace(subsystem.Key))
                throw new ArgumentException(
                    $"IAppSubsystem '{subsystem.Title}' имеет пустой или null Key — регистрация отклонена.",
                    nameof(subsystems));

            if (!seen.Add(subsystem.Key))
                throw new ArgumentException(
                    $"Дубликат IAppSubsystem.Key='{subsystem.Key}' — ключ должен быть уникальным.",
                    nameof(subsystems));

            // Гейт `Subsystems:{Key}:Enabled` (дефолт true). Выключенная подсистема
            // НЕ зовёт Register и НЕ регистрируется как IAppSubsystem — иначе
            // UseSubsystems после Build попытался бы дёрнуть ConfigureApp у того,
            // чьи зависимости (NotesKnowledgeService, NoteTaskSyncService, ...) в
            // контейнер не попали, и свалился на резолве.
            if (!SubsystemGate.IsEnabled(config, subsystem.Key))
            {
                state.RecordDisabled(subsystem);
                continue;
            }

            subsystem.Register(services, config);

            // Регистрация инстанса под интерфейсом — чтобы `UseSubsystems` после
            // builder.Build() мог резолвить `IEnumerable<IAppSubsystem>` и звать
            // `ConfigureApp` у тех, кто реализует `IAppPhaseSubsystem`. Без этого
            // подсистема как объект после Build теряется (Register отработал и забыл).
            services.AddSingleton<IAppSubsystem>(subsystem);
            state.RecordActive(subsystem);
        }

        return services;
    }

    // Пост-билд шаг: для каждой подсистемы, реализующей `IAppPhaseSubsystem`,
    // зовём `ConfigureApp(app)` в порядке регистрации. Вызывать ОДИН РАЗ после
    // `builder.Build()` и до middleware-конвейера — иначе часть действий
    // (например, регистрация провайдеров графа) придёт позже реального
    // первого обращения и поведет себя непредсказуемо.
    public static WebApplication UseSubsystems(this WebApplication app)
    {
        var subsystems = app.Services.GetServices<IAppSubsystem>();
        foreach (var subsystem in subsystems)
        {
            if (subsystem is IAppPhaseSubsystem phase)
                phase.ConfigureApp(app);
        }
        return app;
    }
}