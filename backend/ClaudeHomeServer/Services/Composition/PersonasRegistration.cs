namespace ClaudeHomeServer.Services.Composition;

// Регистрация персон и их сервисов (S6-2): стор персон, миниатюры аватара, сборка
// промпта, привязки, черновик по промпту, провижн авто-ассистента, шаблоны прав
// специальности, файловые сабагенты-персоны и две разовые миграции персон.
//
// Почему здесь, а не в вертикали: сами эти типы живут в Main (они знают SessionManager,
// ProjectManager, Files и Llm напрямую), а вертикаль `ClaudeHomeServer.Personas` —
// тонкая сборка одного типа (PersonaDraftService), которого здесь один `AddSingleton`;
// выносить ради него отдельную точку регистрации нечем. Память персон/команды — в
// `MemorySubsystem`, паспорта изменений — в `DossiersSubsystem`, рубильник картинок —
// в `ImagesSubsystem`: их регистрации уже в своих подсистемах.
//
// Порядок и состав — ровно те, что были в Program.cs; форвардеры стоят рядом со своими
// корнями (IPersonaHandleResolver → PersonaManager, IPersonaServerToolGate /
// IPersonaPluginBindingPurger → PersonaBindingsService).
public static class PersonasRegistration
{
    public static IServiceCollection AddPersonas(this IServiceCollection services, IConfiguration config)
    {
        services.AddSingleton<PersonaManager>();
        // Миниатюры аватара (64/128/256): ленивая генерация через IImageRaster (подсистема Images,
        // ADR-018 §9) + сохранение рядом с оригиналом. Stateless — singleton.
        services.AddSingleton<PersonaAvatarThumbService>();
        // Форвардер, а не вторая регистрация: иначе второй стор персон и резолвер handle не видит
        // персон, созданных после старта (см. DuplicateSingletonRegistrationTests).
        services.AddSingleton<IPersonaHandleResolver>(sp => sp.GetRequiredService<PersonaManager>());
        services.AddSingleton<PersonaPromptBuilder>();
        // Память персон и команды (волна 3, шаг 4) — DI в подсистеме `MemorySubsystem`:
        // PersonaMemoryService и TeamMemoryService регистрируются там же.
        // Паспорта изменений (ADR-004) — DI в подсистеме `DossiersSubsystem`
        // (волна 3, шаг 2): InstanceSecretsProvider/DossierStore/DossierCaptureState/
        // DossierRecallService/DossierDiscussion{Store,Service}/DossierCaptureService (hosted)/
        // DossierAutoExporter (singleton + hosted через AddGatedHostedFrom — подписки в
        // StartAsync на тот же инстанс)/DossierAutoImporter (hosted). Форвардер
        // IKnowledgeSyncParticipant → DossierStore — участник реконсайлера Dify,
        // он в CoreSeamsGlue (кросс-вертикальный клей).
        services.AddSingleton<PersonaBindingsService>();
        // Этап 5, шаг 6: форвардер IPersonaServerToolGate → PersonaBindingsService — узкая часть
        // контракта ServerToolEnabled (deny-only по Tool-привязке), нужная контрибьюторам
        // секций промпта из чужих вертикалей (CodeGraph → codegraph). Без шва вертикаль
        // CodeGraph тянула бы root Services напрямую — запрет архитектуры.
        services.AddSingleton<IPersonaServerToolGate>(sp => sp.GetRequiredService<PersonaBindingsService>());
        // QA-фикс L3: форвардер IPersonaPluginBindingPurger → PersonaBindingsService — каскад
        // Plugins (PluginInstallCascade) снимает Skill-привязки plugin:{id} персон владельца, не
        // ссылаясь на Main напрямую (ADR-014). Регистрация неусловная: шов нужен, только когда
        // подсистема Plugins включена, но адаптер stateless и дёшево живёт всегда (как IPersonaServerToolGate).
        services.AddSingleton<ClaudeHomeServer.Services.Plugins.IPersonaPluginBindingPurger,
            ClaudeHomeServer.Services.Composition.PluginBindingPurgerAdapter>();
        // Черновик персоны по промпту (one-shot LLM → JSON): переиспользуется ai/quick-create
        // и страховкой онбординга «Применить итоги разговора». Stateless — singleton.
        // Тип живёт в сборке `ClaudeHomeServer.Personas`; регистрация здесь — рядом с остальными
        // сервисами персон, её вызывающие (quick-create, онбординг) — в Main.
        services.AddSingleton<ClaudeHomeServer.Services.Personas.PersonaDraftService>();
        // Провижн авто-ассистента (фича default-personas-onboarding): заготовка «Ассистент»
        // как дефолт при первом включении флага. Singleton — статический реестр семафоров.
        services.AddSingleton<DefaultAssistantProvisioner>();
        // Применение шаблонов прав специальности (тонкая прослойка над SpecialtySettingsStore,
        // который переехал в Services.Llm — DI там же, см. LlmSubsystem.cs)
        services.AddSingleton<SpecialtyTemplatesService>();
        // Файловые сабагенты-персоны: генерация + синк .md-агентов
        // Пул подписок с восстановлением пометок исчерпания из снапшотов usage после рестарта,
        // активность аккаунта, сторож setup-токена, прогрев/идл-пинг и OAuth-опрос утилизации —
        // переехали в Services.Llm (волна 4B, шаг 2). DI в подсистеме `LlmSubsystem`.
        services.AddSingleton<PersonaAgentFileGenerator>();
        services.AddSingleton<PersonaAgentFileSync>();
        // Генерация картинок (иконка проекта, аватар персоны, фон проекта) — подключается
        // подсистемой ImagesSubsystem вместе с прочими внутренними разделами ниже
        // (`AddSubsystems(...)`). FalImageService регистрируется внутри как драйвер —
        // отдельный AddSingleton дал бы второй экземпляр того же типа.
        // Память персон и команды (волна 3, шаг 4) — DI в подсистеме `MemorySubsystem`:
        // PersonaMemoryService/PersonaMemoryConsolidationService/PersonaMemoryAutolearnService/
        // TeamMemoryService/TeamMemoryConsolidationService/TeamMemoryAutolearnService.
        // Форвардеры `IKnowledgeSyncParticipant → {PersonaMemoryService, TeamMemoryService}`
        // — кросс-вертикальный клей реконсайлера error-документов Dify, не собственность
        // Memory: они в CoreSeamsGlue.
        // Разовый backfill дефолтных привязок существующим проектным персонам (файлы/заметки/знания)
        services.AddGatedHostedService<PersonaProjectBindingsMigration>(config);
        // Разовое решение «Облегчённого контекста» для персон из стора (по прежней логике провайдера)
        services.AddGatedHostedService<PersonaLightContextMigration>(config);
        return services;
    }

    // Проактивность персон (событийно-управляемый rules-движок): state store и сам
    // сервис-collaborator — Main-типы (знают SessionManager и персон); источники
    // триггеров — вертикаль TriggerSources (её регистрация там же); шов
    // IPersonaAutomationRunner нужен Tasks-вертикали, поэтому адаптер — здесь.
    public static IServiceCollection AddPersonaAutomation(this IServiceCollection services)
    {
        services.AddSingleton<AutomationStateStore>();
        services.AddSingleton<PersonaAutomationService>();
        // Шов IPersonaAutomationRunner (Core) → PersonaAutomationRunnerAdapter → PersonaAutomationService.
        // Регистрация адаптера вынесена в Main (Этап 5, вынос Tasks): вертикаль Tasks больше не
        // может регистрировать Main-типы. IPersonaAutomationRunner фабрика тоже здесь —
        // TasksSubsystem её больше не ставит.
        services.AddSingleton<PersonaAutomationRunnerAdapter>();
        services.AddSingleton<IPersonaAutomationRunner>(sp => sp.GetRequiredService<PersonaAutomationRunnerAdapter>());
        return services;
    }
}
