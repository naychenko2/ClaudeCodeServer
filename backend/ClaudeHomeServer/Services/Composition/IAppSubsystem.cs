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

// Регистрация подсистем: одна точка входа, через которую Program.cs подключает
// все внутренние разделы. Защита от дублей `Key` — контрактная гарантия, что
// `Telemetry:Backends` и `Telemetry:Alerts` не окажутся конкурентами за одну секцию.
public static class SubsystemRegistration
{
    public static IServiceCollection AddSubsystems(
        this IServiceCollection services, IConfiguration config, params IAppSubsystem[] subsystems)
    {
        if (subsystems is null) throw new ArgumentNullException(nameof(subsystems));

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

            subsystem.Register(services, config);

            // Регистрация инстанса под интерфейсом — чтобы `UseSubsystems` после
            // builder.Build() мог резолвить `IEnumerable<IAppSubsystem>` и звать
            // `ConfigureApp` у тех, кто реализует `IAppPhaseSubsystem`. Без этого
            // подсистема как объект после Build теряется (Register отработал и забыл).
            services.AddSingleton<IAppSubsystem>(subsystem);
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
