namespace ClaudeHomeServer.Services.Composition;

// Канал устройства (ADR-016): агент локальных проектов на машине пользователя (S6-2).
// Реестр устройств, сопряжение, живой канал исполнения, билеты агента и его хаб-отправители
// — здесь, а не в сборке `ClaudeHomeServer.Devices`: блок смешанный — его половины
// смотрят в Main (`IChatFeed`, `IProjectFilesChangedNotifier`, `ILocalModelLiveSink`,
// `IDeviceOnlineHandler` на TaskExecutionService/PersonaAutomationService, диспетчер
// онлайн-событий), а перенос смешанного блока в вертикаль дал бы запрещённую связь
// вертикаль → Main (ADR-014 «Объём выноса меряется заглушками»). Рубильника-подсистемы
// у канала нет: ключ `desktop` остался только для совместимости с конфигами (S6-1).
//
// Порядок и состав — ровно те, что были в Program.cs: регистрация `IDeviceConnectionObserver`
// стоит до своего корня (DeviceOnlineDispatcher) — резолв ленивый, порядок здесь не
// влияет, но оставлен как был.
public static class DeviceChannelGlue
{
    public static IServiceCollection AddDeviceChannel(this IServiceCollection services, IConfiguration config)
    {
        // Реестр устройств и хеши их токенов — единственный стор канала; живые соединения хаба
        // живут только в памяти.
        services.AddSingleton<ClaudeHomeServer.Services.Devices.DeviceRegistry>();
        services.AddSingleton<ClaudeHomeServer.Services.Devices.DevicePairingService>();
        services.AddSingleton<ClaudeHomeServer.Services.Devices.DeviceConnectionRegistry>();
        // Статус рук локального проекта (ADR-016 §7) — в чат хода из донесений агента
        services.AddSingleton<ClaudeHomeServer.Services.Devices.ILocalHandsNotifier,
            ClaudeHomeServer.Services.Composition.LocalHandsNotifier>();
        // Живые события локальной модели (флаг local-model-stats): тиковый снимок генерации и
        // итог хода — в session-группу чата, только при наличии зрителя (LocalModelLiveSink).
        // Интерфейс (ILocalModelLiveSink) — в Llm; счётчик ходов (LocalModelTurnMeter) резолвит
        // его лениво, поэтому порядок регистрации не важен.
        services.AddSingleton<ClaudeHomeServer.Services.Llm.ILocalModelLiveSink,
            ClaudeHomeServer.Services.Composition.LocalModelLiveSink>();
        // Наблюдатель соединений — диспетчер выхода устройства в онлайн (ADR-016, план §5;
        // регистрация блоком ниже): форвард на тот же синглтон, не второй экземпляр.
        services.AddSingleton<ClaudeHomeServer.Services.Devices.IDeviceConnectionObserver>(
            sp => sp.GetRequiredService<ClaudeHomeServer.Services.Composition.DeviceOnlineDispatcher>());
        // Канал исполнения локальных проектов (ADR-016): шов IDeviceExecChannel (Core) — форвард
        // на тот же синглтон, который обслуживает WebSocket /api/devices/exec и Hello хаба.
        services.AddSingleton<ClaudeHomeServer.Services.Execution.IHostCliVersion, ClaudeHomeServer.Services.Execution.HostCliVersion>();
        services.AddSingleton<ClaudeHomeServer.Services.Devices.DeviceHarnessPolicy>();
        services.AddSingleton<ClaudeHomeServer.Services.Devices.IDeviceHelloRequestSender,
            ClaudeHomeServer.Services.Devices.DeviceHubHelloRequestSender>();
        services.AddHostedService<ClaudeHomeServer.Services.Devices.DeviceHelloRequestBroadcaster>();
        services.AddSingleton<ClaudeHomeServer.Services.Devices.IDeviceExecOpenSender,
            ClaudeHomeServer.Services.Devices.DeviceHubExecOpenSender>();
        // Каталог релизов агента устройства (agent-distribution Р3, Р5): его читают и ack хаба, и
        // анонимная раздача AgentDownloadsController. Регистрация под легасийным тумблером
        // `desktop` (Devices не оформлена подсистемой IAppSubsystem, ключ ради совместимости с
        // конфигами) — в Composition/DeviceAgentGlue.cs (S6-1): нет регистрации — каталог
        // null, раздача отвечает 503 с причиной, ack не называет версий.
        services.AddAgentReleaseCatalog(config);
        services.AddSingleton<ClaudeHomeServer.Services.Devices.DeviceExecChannel>();
        services.AddSingleton<ClaudeHomeServer.Services.Execution.IDeviceExecChannel>(
            sp => sp.GetRequiredService<ClaudeHomeServer.Services.Devices.DeviceExecChannel>());
        // Ретранслятор чтения для других устройств (ADR-016 §5) — тот же канал исполнения
        services.AddSingleton<ClaudeHomeServer.Services.Execution.IDeviceRelayChannel>(
            sp => sp.GetRequiredService<ClaudeHomeServer.Services.Devices.DeviceExecChannel>());
        // Выдача папки локального проекта агентом (решение владельца 2026-09-27) — он же
        services.AddSingleton<ClaudeHomeServer.Services.Execution.IDeviceFolderBindChannel>(
            sp => sp.GetRequiredService<ClaudeHomeServer.Services.Devices.DeviceExecChannel>());
        // Билеты браузера к localhost-API агента и доставка событий его ватчера в веб-морду
        // (ADR-016, задача 4.2): только память, рестарт бэкенда отзывает все билеты.
        services.AddSingleton<ClaudeHomeServer.Services.Devices.AgentTicketService>();
        services.AddSingleton<ClaudeHomeServer.Services.Composition.IProjectFilesChangedNotifier,
            ClaudeHomeServer.Services.Composition.ProjectFilesChangedNotifier>();

        // Фоновая работа при офлайн-устройстве (ADR-016, вариант А плана §5): гейт готовности
        // устройства проекта для пяти механизмов, диспетчер выхода устройства в онлайн (наблюдатель
        // реестра соединений + поминутный проход с потолком 24 ч) и его обработчики — исполнитель задач
        // (с под-задачами штаба), очередь чата, автоматизации персон. Сторожа догоняют своим тиком.
        services.AddSingleton<ClaudeHomeServer.Services.Execution.IProjectDeviceGate>(
            sp => new ClaudeHomeServer.Services.Execution.ProjectDeviceGate(
                () => sp.GetService<ClaudeHomeServer.Services.Execution.IDeviceExecChannel>()));
        services.AddSingleton<ClaudeHomeServer.Services.Composition.DeviceOnlineDispatcher>();
        services.AddGatedHostedFrom(config,
            sp => sp.GetRequiredService<ClaudeHomeServer.Services.Composition.DeviceOnlineDispatcher>());
        services.AddSingleton<ClaudeHomeServer.Services.Composition.ChatDeviceWaitHandler>();
        services.AddSingleton<ClaudeHomeServer.Services.Execution.IDeviceOnlineHandler>(
            sp => sp.GetRequiredService<ClaudeHomeServer.Services.Composition.ChatDeviceWaitHandler>());
        services.AddSingleton<ClaudeHomeServer.Services.Execution.IDeviceOnlineHandler>(
            sp => sp.GetRequiredService<TaskExecutionService>());
        services.AddSingleton<ClaudeHomeServer.Services.Execution.IDeviceOnlineHandler>(
            sp => sp.GetRequiredService<PersonaAutomationService>());
        return services;
    }
}
