using ClaudeHomeServer.Services.Knowledge;

namespace ClaudeHomeServer.Services.Composition;

// Клей узких швов (S6-2): регистрации Main-адаптеров, которыми вынесенные вертикали
// доходят до того, что живёт в спине и в других вертикалях. Это ровно те места, где
// известны обе стороны шва, — и единственное место, которому это позволено (ADR-014):
// в самой вертикали такой код был бы запрещённой ссылкой на Main или на соседнюю вертикаль,
// а в контроллере — дублированием знания о шве.
//
// Состав: участники реконсайлера error-документов Dify (пять владельцев локальных сторов
// — форварды на существующие синглтоны), адаптеры швов Knowledge, Spend, Deploy, Turn,
// контекста вызова MCP-тулсетов, мосты Notes↔Tasks (S6-1) и рассыльщик сессий.
//
// Порядок и состав — ровно те, что были в Program.cs. Регистрация `ISessionBroadcaster`
// остаётся рядом со своим корнем `SessionHubBroadcaster`: префиксы групп "user_"/"project_"
// собираются только там, потребители не видят SignalR напрямую.
public static class CoreSeamsGlue
{
    public static IServiceCollection AddCoreSeams(this IServiceCollection services, IConfiguration config)
    {
        // Секция DifyOptions + KnowledgeService + ProjectKnowledgeSyncService (singleton + hosted
        // мост ProjectKnowledgeTurnSync) + UserKnowledgeCascade + IKnowledgeAlertNotifier +
        // KnowledgeIndexReconciler (singleton + hosted) — DI в подсистеме `KnowledgeSubsystem`
        // (волна 3, шаг 3). Миграция `WorkspaceKnowledgeStore.MigrateFromProjects` — отдельный
        // блок стартового ряда после Build(): `Composition/StartupWarmup.cs` — см. ограничения
        // в шапке KnowledgeSubsystem.cs.
        // Участники реконсайлера error-документов Dify: пять владельцев локальных сторов
        // «запись → {DocId, Hash}» (форвард на существующие singleton'ы, не новые экземпляры).
        // Остаются в композиционном корне сознательно — кросс-вертикальный клей, не собственность
        // Knowledge: перенос в KnowledgeSubsystem дал бы ей прямые ссылки на DossierStore и
        // прочие чужие вертикали.
        // Первые два (Persona/TeamMemoryService) — фасады подсистемы Memory (волна 3, шаг 4);
        // `AddSingleton` ниже — форвардеры IKnowledgeSyncParticipant для них, а сами
        // регистрации этих сервисов переехали в `MemorySubsystem.Register`.
        services.AddSingleton<ClaudeHomeServer.Services.Knowledge.IKnowledgeSyncParticipant>(
            sp => sp.GetRequiredService<ClaudeHomeServer.Services.Memory.PersonaMemoryService>());
        services.AddSingleton<ClaudeHomeServer.Services.Knowledge.IKnowledgeSyncParticipant>(
            sp => sp.GetRequiredService<ClaudeHomeServer.Services.Memory.TeamMemoryService>());
        services.AddSingleton<ClaudeHomeServer.Services.Knowledge.IKnowledgeSyncParticipant>(
            sp => sp.GetRequiredService<ClaudeHomeServer.Services.Dossiers.DossierStore>());
        // Полка памяти сферы: без форвардера удаление пользователя оставляет sphere-memory.json
        // и датасеты {user}:sphere:*, а реконсайлер не чинит упавшие документы полки.
        services.AddSingleton<ClaudeHomeServer.Services.Knowledge.IKnowledgeSyncParticipant>(
            sp => sp.GetRequiredService<ClaudeHomeServer.Services.Memory.SphereMemoryService>());
        // Участие заметок в реконсайлере Dify (разрез Notes↔Knowledge): форвардер с гейтом
        // подсистемы и проверкой фактической загрузки динамического модуля — в
        // Composition/NotesKnowledgeGlue.cs (S6-1).
        services.AddNotesKnowledgeParticipant(config);
        services.AddSingleton<ClaudeHomeServer.Services.Knowledge.IKnowledgeSyncParticipant>(
            sp => sp.GetRequiredService<ProjectKnowledgeSyncService>());

        // Адаптеры Core-швов Knowledge (Этап 5, волна 5): живут в Main, регистрируются
        // здесь — Knowledge не должна знать про конкретные классы, только про швы.
        services.AddSingleton<ClaudeHomeServer.Services.IKnowledgeHubNotifier,
            ClaudeHomeServer.Services.Composition.KnowledgeHubNotifier>();
        services.AddSingleton<ClaudeHomeServer.Services.Composition.ISessionMessageObserver,
            ClaudeHomeServer.Services.Composition.SessionMessageObserver>();
        services.AddSingleton<ClaudeHomeServer.Services.Composition.IPersonaDirectory,
            ClaudeHomeServer.Services.Composition.PersonaDirectoryAdapter>();
        services.AddSingleton<ClaudeHomeServer.Services.Composition.IPersonaEvents,
            ClaudeHomeServer.Services.Composition.PersonaEventsImpl>();
        services.AddSingleton<ClaudeHomeServer.Services.Composition.IKnowledgeNotificationDispatcher,
            ClaudeHomeServer.Services.Composition.Notifications.KnowledgeNotificationDispatcher>();
        services.AddSingleton<ClaudeHomeServer.Core.Telemetry.IDifyMetrics,
            ClaudeHomeServer.Services.Composition.DifyMetricsAdapter>();

        // Этап 5, шаг 1: адаптеры узких швов для выноса Spend в отдельный .csproj.
        // Spend ходит в корень Services только через эти интерфейсы — иначе сторож
        // границ поймал бы прямую ссылку на SessionManager/PersonaManager/etc.
        services.AddSingleton<ClaudeHomeServer.Services.ISessionDirectory,
            ClaudeHomeServer.Services.Composition.SessionDirectoryAdapter>();
        services.AddSingleton<ClaudeHomeServer.Services.IPersonaLookup,
            ClaudeHomeServer.Services.Composition.PersonaLookupAdapter>();
        services.AddSingleton<ClaudeHomeServer.Services.IPersonaAvatarStore,
            ClaudeHomeServer.Services.Composition.PersonaAvatarStoreAdapter>();
        services.AddSingleton<ClaudeHomeServer.Services.IChatHistoryLoader,
            ClaudeHomeServer.Services.Composition.ChatHistoryLoaderAdapter>();
        services.AddSingleton<ClaudeHomeServer.Services.Composition.IChatTranscriptReader,
            ClaudeHomeServer.Services.Composition.ChatTranscriptReaderAdapter>();
        services.AddSingleton<ClaudeHomeServer.Services.ITaskLookup,
            ClaudeHomeServer.Services.Composition.TaskLookupAdapter>();

        // Этап 5, волна 2: адаптеры узких швов для выноса Deploy в отдельный .csproj.
        services.AddSingleton<ClaudeHomeServer.Services.Composition.INotificationSender,
            ClaudeHomeServer.Services.Composition.Notifications.DeployNotificationSenderAdapter>();
        services.AddSingleton<ClaudeHomeServer.Services.Composition.ISessionMessageSender,
            ClaudeHomeServer.Services.Composition.Sessions.SessionMessageSenderAdapter>();
        services.AddSingleton<ClaudeHomeServer.Services.Composition.IDeployAgentLock,
            ClaudeHomeServer.Services.Composition.Deploy.DeployAgentLockAdapter>();

        // Этап 5, узкие швы Turn: адаптеры, которыми контрибьюторы промпта заменили прямые
        // ссылки на вертикали. Регистрация в композиционном корне, а не в вертикали: адаптер
        // знает обе стороны шва, и это единственное место, которому это позволено.
        services.AddSingleton<ClaudeHomeServer.Services.Memory.IPersonaRecallSource,
            ClaudeHomeServer.Services.Composition.PersonaRecallSourceAdapter>();
        services.AddSingleton<ClaudeHomeServer.Services.Turn.IAgentPromptSource,
            ClaudeHomeServer.Services.Composition.AgentPromptSourceAdapter>();
        services.AddSingleton<ClaudeHomeServer.Services.Turn.ITeamMechanicsBlockSource,
            ClaudeHomeServer.Services.Composition.TeamMechanicsBlockAdapter>();
        services.AddSingleton<ClaudeHomeServer.Services.Skills.ICommandExpansion,
            ClaudeHomeServer.Services.Composition.CommandExpansionAdapter>();
        services.AddSingleton<ClaudeHomeServer.Services.Skills.ISkillSnapshotSource,
            ClaudeHomeServer.Services.Composition.SkillSnapshotSourceAdapter>();
        // Этап 5 (Turn): ещё 4 узких шва, чтобы Turn зависел только от Core. Те же
        // адаптеры 1:1 — IFeatureFlagGate/IFeatureFlagGate, IPersonaResolver,
        // IPersonaPromptAssembler, IPersonaBindingsSource. Состав — ровно те 12 мест,
        // что оставались после двух предыдущих волн (см. ADR-014 «Курс после пилота»).
        services.AddSingleton<ClaudeHomeServer.Services.Composition.IFeatureFlagGate,
            ClaudeHomeServer.Services.Composition.FeatureFlagGateAdapter>();
        services.AddSingleton<ClaudeHomeServer.Services.IPersonaResolver,
            ClaudeHomeServer.Services.Composition.PersonaResolverAdapter>();
        services.AddSingleton<ClaudeHomeServer.Services.Turn.IPersonaPromptAssembler,
            ClaudeHomeServer.Services.Composition.PersonaPromptAssemblerAdapter>();
        services.AddSingleton<ClaudeHomeServer.Services.Turn.IPersonaBindingsSource,
            ClaudeHomeServer.Services.Composition.PersonaBindingsSourceAdapter>();
        // Волна NotesToolset: швы контекста вызова MCP-over-HTTP-тулсетов
        // (SessionManager.GetOwned + PersonaBindingsService.Effective/SectionEnabled).
        services.AddSingleton<ClaudeHomeServer.Services.Mcp.Http.IMcpSessionAccessor,
            ClaudeHomeServer.Services.Composition.McpSessionAccessorAdapter>();
        services.AddSingleton<ClaudeHomeServer.Services.Mcp.Http.IMcpPersonaBindings,
            ClaudeHomeServer.Services.Composition.McpPersonaBindingsAdapter>();
        // Гейт делегированного хода для тулсетов из модулей (image-editor, ADR-018 §10.1)
        services.AddSingleton<ClaudeHomeServer.Services.Mcp.Http.IDelegatedTurnGate,
            ClaudeHomeServer.Services.Composition.DelegatedTurnGateAdapter>();

        // Этап 5, волна E: forwarder-регистрации двух Core-интерфейсов выноса Notes
        // (разрез Notes↔Tasks) с гейтом и проверкой загрузки модуля — в
        // Composition/NotesTasksGlue.cs (S6-1).
        services.AddNotesTaskBridge(config);

        // Этап 5, Ф4: шов ISessionBroadcaster (Core) → SessionHubBroadcaster (Main, поверх
        // IHubContext<SessionHub>). Префиксы групп "user_"/"project_" собираются только здесь —
        // потребители больше не видят SignalR напрямую.
        services.AddSingleton<ClaudeHomeServer.Services.Composition.SessionHubBroadcaster>();
        services.AddSingleton<ClaudeHomeServer.Services.Composition.ISessionBroadcaster>(
            sp => sp.GetRequiredService<ClaudeHomeServer.Services.Composition.SessionHubBroadcaster>());
        return services;
    }
}
