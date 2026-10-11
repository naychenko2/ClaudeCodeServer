import type { Me, Project, ProjectGroup, ProjectTag, Session, FileEntry, SyncMark, WorkflowAgentInfo, WorkflowAgentBlock, AppSettings, UserProfile, SkillsData, SkillInfo, RegistrySkill, SkillSuggestion, GeneratedSkill, PermissionRule, UsageResponse, ModelUnavailableMark, FalAccountResponse, GlifAccountResponse, YandexAccountResponse, ImageGenerationSettings, ImageGenerationPatch, ImagePlacePatch, ProviderBalanceInfo, FeatureFlagDefinition, SystemPromptPart, Task, CreateTaskDto, UpdateTaskDto, BoardColumn, BoardItem, HomeSummaryResponse, ChangelogDay, DaySummaryStub, ChangelogStatus, NoteSummary, NoteDetail, NoteBacklink, NoteGraph, DocAnnotation, NoteReply, NoteSource, NoteFolder, NoteTemplate, NoteSemanticHit, CreateNoteDto, UpdateNoteDto, NoteTask, ExtractTasksResponse, SearchHit, Persona, CreatePersonaDto, UpdatePersonaDto, PersonaScope, PersonaMemoryType, PersonaMemoryEntry, PersonaMemoryHit, PersonaContract, PersonaWorkingFocus, PantheonTemplate, PersonaBinding, PersonaBindingDto, PersonaVoice, TtsVoicesResponse, PersonaBindingType, BindingTarget, KnowledgeBaseDetail, KnowledgeSearchHit, CreateKnowledgeBaseDto, KnowledgeListResponse, KnowledgeDocumentContent, TeamMemoryEntry, TeamMemoryType, TeamMemberDraft, PersonaAutomationRule, AutomationRuleDto, ProjectService, LaunchConfigEntry, GitStatus, GitBranchInfo, GitLogEntry, GitCommitDetail, GitStashEntry, GitFileChange, GitBlameLine, GitRemoteInfo, GitCommitPromptInfo, SpendOverviewResponse, SpendPivotResponse, SpendTurnsResponse, SpendTurnDetailResponse, SpendWidgetResponse, SpendBadgeResponse, SpendTaskPromptResponse, BackupStatus, BackupSummary, CodeGraph, DocEntry, DocDetail, DocSearchHit, DocsScope, DocsScopeInfo, DocProperty, DocTypeSchema, PromptSnapshot, PromptSection, ReaderPage, ReaderErrorCode, SpecialtyCatalogEntry, SpecialtySettingsLayer, SpecialtySettingsResponse, SpecialtyPromptSectionsCatalog, ApplyDefaultBindingsResult, ResetResult, ModelPreviewResponse, PresetUsageResponse, PlacePresetRef, McpServer, McpBuiltinServer, McpServerUpsert, McpProbeResult, McpCallsResponse, McpOAuthStartResult, McpOAuthCompleteResult, McpCatalogSearchResult, McpCatalogRevisionResult, DossierEntry, DesktopDevice, DesktopPairingCode, LocalHandsChatStatus, BackgroundResult, ChangedBySession, IncidentListResponse, IncidentDossier, ExternalPreviewLink, ExternalLinkIssued, QuickPhrase, VideoProviderInfo, VideoChannelsResponse, VideoFeedResponse, PlanMap, VideoFavoritesResponse, SessionContextEntry, MapHygieneReport, MapHygieneApplyResult, SphereOverview, DeleteSphereResponse, SphereMemoryResponse, SphereMemoryEntry, SphereMemoryType } from '../types';
export interface ModelTiers {
    strong: string | null;
    medium: string | null;
    weak: string | null;
}
export interface GlyphCandidate {
    name?: string | null;
}
export interface ArchitectureGenerateResult {
    modelPath: string;
    graphBuiltAt: string | null;
    generatedAt: string;
    containers: number;
    components: number;
    added: number;
    matched: number;
    connectionsAdded: number;
    candidates?: number;
    markedMissing?: number;
    unmarked?: number;
    skippedDeleted?: number;
    missing?: string[] | null;
    candidatesSkipped?: string[] | null;
    agentTaskId?: string | null;
    agentPersonaId?: string | null;
    agentError?: string | null;
    warnings?: ArchitectureModelFinding[] | null;
}
export interface ArchitectureModelFinding {
    kind: 'dangling_parent' | 'dangling_connection' | 'dangling_flow_step' | 'unknown_handle' | 'duplicate_id';
    elementId: string;
    text: string;
}
export interface ArchitectureModelDto {
    exists: boolean;
    content: string | null;
    version: string | null;
    updatedAt: string | null;
    updatedBy: string | null;
    warnings?: ArchitectureModelFinding[];
}
export interface ArchitectureSaveResult {
    version: string;
    updatedAt: string | null;
    updatedBy: string | null;
    warnings?: ArchitectureModelFinding[];
}
export interface DeployStatusFile {
    startedAt: string | null;
    finishedAt: string | null;
    mode: string | null;
    branch: string | null;
    dirtyFiles: number;
    head: string | null;
    deployExitCode: number | null;
    result: string | null;
    productUp: boolean | null;
    note: string | null;
}
export interface DeployState {
    enabled: boolean;
    canLaunch: boolean;
    reason: string | null;
    status: DeployStatusFile | null;
    progress?: DeployProgressFile | null;
}
export interface DeployProgressFile {
    startedAt: string | null;
    stage: string | null;
    percent: number;
    step: string | null;
    line: string | null;
    updatedAt: string | null;
}
export interface PowerPending {
    action: PowerActionKind;
    runAt: string;
    secondsLeft: number;
    requestedBy: string;
}
export type PowerActionKind = 'shutdown' | 'restart' | 'sleep';
export interface PowerState {
    enabled: boolean;
    available: boolean;
    delaySeconds: number;
    pending: PowerPending | null;
}
export type RemoteCommandMode = 'oneshot' | 'daemon';
export type RemoteCommandState = 'running' | 'stopped' | 'unknown';
export interface RemoteCommandAction {
    key: string;
    title: string;
    mode: RemoteCommandMode;
    state: RemoteCommandState;
    busy: boolean;
    checkedAt: string | null;
    lastExitCode: number | null;
    url: string | null;
}
export interface RemoteCommandsState {
    enabled: boolean;
    actions: RemoteCommandAction[];
}
export interface RemoteCommandResult {
    ok: boolean;
    state: RemoteCommandState;
    busy?: boolean;
    detail?: string | null;
    checkedAt?: string | null;
    lastExitCode?: number | null;
}
export interface DeployJournalStep {
    name: string;
    status?: string | null;
    ms: number;
}
export interface DeployJournalResult {
    ok: boolean;
    status: string;
    message?: string | null;
    releaseId?: string | null;
    finishedAt?: string | null;
}
export interface DeployJournalRecord {
    id: string;
    kind?: string | null;
    phase: string;
    ref?: string | null;
    sha?: string | null;
    dirty?: boolean;
    dirtyFiles?: string[];
    initiatedBy?: {
        userId?: string | null;
        sessionId?: string | null;
    } | null;
    steps?: DeployJournalStep[];
    result?: DeployJournalResult | null;
    startedAt?: string | null;
}
export interface DeployJournalRelease {
    id: string;
    sha?: string | null;
    path?: string | null;
    createdAt?: string | null;
}
export interface DeployJournal {
    enabled: boolean;
    current: DeployJournalRecord | null;
    history: DeployJournalRecord[];
    releases: DeployJournalRelease[];
}
export type { WorkflowAgentInfo, WorkflowAgentBlock };
export interface ModuleInfo {
    id: string;
    displayName: string;
    description?: string | null;
    version: string;
    schemaVersion: string;
    apiBase: string;
    tab?: {
        label: string;
        icon?: string | null;
        order: number;
    } | null;
    remoteEntry?: string | null;
    exposedModule?: string | null;
}
export interface DifyDocument {
    id: string;
    name: string;
    indexingStatus: string;
    error?: string | null;
    tags?: string[];
}
export declare function setGitSessionContext(projectId: string, sessionId: string | null): void;
export declare function getGitSessionContext(): {
    projectId: string;
    sessionId: string;
} | null;
export declare function setStoredToken(token: string): void;
export declare const api: {
    auth: {
        login: (username: string, password: string) => Promise<{
            token: string;
            expiresAt: string;
            username: string;
            displayName?: string | null;
        }>;
        me: (opts?: {
            timeoutMs?: number;
        }) => Promise<Me>;
        changePassword: (currentPassword: string, newPassword: string) => Promise<{
            token: string;
            expiresAt: string;
        }>;
        setContextThresholds: (t: {
            warnPct?: number;
            dangerPct?: number;
        }) => Promise<{
            contextThresholds: {
                warnPct: number;
                dangerPct: number;
            } | null;
        }>;
        setTimeZone: (timeZone: string) => Promise<void>;
    };
    push: {
        vapidPublicKey: () => Promise<{
            publicKey: string;
        }>;
        subscribe: (sub: {
            endpoint: string;
            p256dh: string;
            auth: string;
        }) => Promise<void>;
        unsubscribe: (endpoint: string) => Promise<void>;
    };
    users: {
        list: () => Promise<UserProfile[]>;
        create: (data: {
            username: string;
            password: string;
            role: string;
            executionEnvironment?: string;
        }) => Promise<UserProfile>;
        update: (id: string, data: {
            username?: string;
            role?: string;
            executionEnvironment?: string;
        }) => Promise<UserProfile>;
        delete: (id: string) => Promise<void>;
        resetPassword: (id: string, newPassword: string) => Promise<void>;
    };
    settings: {
        get: () => Promise<AppSettings>;
        save: (s: Partial<AppSettings>) => Promise<AppSettings>;
    };
    meModelTiers: {
        get: () => Promise<ModelTiers>;
        save: (patch: Partial<ModelTiers>) => Promise<ModelTiers>;
    };
    adminUserModelTiers: {
        get: (userId: string) => Promise<ModelTiers>;
        save: (userId: string, patch: Partial<ModelTiers>) => Promise<ModelTiers>;
    };
    usage: {
        get: () => Promise<UsageResponse>;
        clearModelAvailability: (key: string, model: string) => Promise<{
            unavailableModels: ModelUnavailableMark[];
        }>;
    };
    spend: {
        overview: (query: string) => Promise<SpendOverviewResponse>;
        pivot: (query: string) => Promise<SpendPivotResponse>;
        turns: (query: string) => Promise<SpendTurnsResponse>;
        turn: (id: string) => Promise<SpendTurnDetailResponse>;
        taskPrompt: (taskId: string) => Promise<SpendTaskPromptResponse>;
        widget: () => Promise<SpendWidgetResponse>;
        badge: (sessionId: string) => Promise<SpendBadgeResponse>;
    };
    localActions: {
        setRoute: (key: string, route: string) => Promise<{
            key: string;
            route: string;
            source: string;
            preset?: PlacePresetRef | null;
        }>;
        reset: (key: string) => Promise<{
            key: string;
            route: string;
            source: string;
            preset?: PlacePresetRef | null;
        }>;
        applyPreset: (preset: "tiers" | "tiers-local") => Promise<{
            preset: string;
            count: number;
        }>;
    };
    backup: {
        get: () => Promise<BackupStatus>;
        run: () => Promise<{
            file: string;
            createdAt: string;
            summary: BackupSummary;
        }>;
    };
    fal: {
        account: (days?: number) => Promise<FalAccountResponse>;
    };
    glif: {
        account: () => Promise<GlifAccountResponse>;
    };
    yandex: {
        account: (days?: number) => Promise<YandexAccountResponse>;
    };
    imageGeneration: {
        get: () => Promise<ImageGenerationSettings>;
        save: (patch: ImageGenerationPatch) => Promise<ImageGenerationSettings>;
        savePlace: (place: string, patch: ImagePlacePatch) => Promise<ImageGenerationSettings>;
    };
    modules: {
        list: () => Promise<{
            items: ModuleInfo[];
        }>;
    };
    subsystemModules: {
        list: () => Promise<{
            items: {
                id: string;
                remoteUrl: string;
                exposedModule: string;
            }[];
        }>;
    };
    mcp: {
        list: () => Promise<McpServer[]>;
        get: (id: string) => Promise<McpServer>;
        builtin: () => Promise<McpBuiltinServer[]>;
        create: (data: McpServerUpsert) => Promise<McpServer>;
        update: (id: string, data: McpServerUpsert) => Promise<McpServer>;
        setEnabled: (id: string, enabled: boolean) => Promise<McpServer>;
        delete: (id: string) => Promise<void>;
        probe: (id: string, body?: {
            confirmed?: boolean;
        }) => Promise<McpProbeResult>;
        import: (fragment: unknown) => Promise<{
            created: McpServer[];
            skipped: {
                key: string;
                reason: string;
            }[];
        }>;
        catalogSearch: (q: string, cursor?: string | null) => Promise<McpCatalogSearchResult>;
        catalogRevisions: (names: string[]) => Promise<McpCatalogRevisionResult>;
        calls: (failures?: number) => Promise<McpCallsResponse>;
        oauthStart: (id: string, clientId?: string) => Promise<McpOAuthStartResult>;
        oauthComplete: (id: string, state: string, code: string) => Promise<McpOAuthCompleteResult>;
    };
    devices: {
        list: () => Promise<DesktopDevice[]>;
        startPairing: () => Promise<DesktopPairingCode>;
        pairingStatus: () => Promise<DesktopPairingCode>;
        cancelPairing: () => Promise<void>;
        rename: (id: string, name: string) => Promise<DesktopDevice>;
        revoke: (id: string) => Promise<void>;
    };
    providers: {
        balance: (key: string) => Promise<ProviderBalanceInfo>;
        usage: (key: string) => Promise<{
            balance: ProviderBalanceInfo | null;
            snapshots: {
                timestamp: string;
                balance: number;
                currency: string;
            }[];
        }>;
    };
    models: {
        list: () => Promise<{
            models: {
                value: string;
                displayName: string;
                description?: string | null;
                provider?: string | null;
                contextWindow?: number | null;
                isCurated?: boolean;
                resolvedVersion?: string | null;
            }[];
            providers?: Record<string, import("./models").ProviderCapabilities>;
            assignments?: Record<string, string | null>;
        }>;
        claudeCli: (refresh?: boolean) => Promise<{
            current: string | null;
            latest: string | null;
            updateAvailable: boolean | null;
            checkedAt: string | null;
            newModels?: {
                name: string;
                id: string;
                cliVersion: string;
                isFamilyDefault: boolean;
            }[];
            changes?: {
                version: string;
                items: string[];
                hidden: number;
            }[];
            hiddenCount?: number;
            truncated?: boolean;
        }>;
        preview: (q: {
            place?: string;
            personaId?: string;
            specialty?: string;
            tier?: string;
            sessionId?: string;
        }) => Promise<ModelPreviewResponse>;
        presetUsage: (id: string) => Promise<PresetUsageResponse>;
    };
    video: {
        providers: () => Promise<VideoProviderInfo[]>;
        channels: (provider: string, refresh?: boolean) => Promise<VideoChannelsResponse>;
        feed: (provider: string, channelId?: string, refresh?: boolean) => Promise<VideoFeedResponse>;
        favorites: () => Promise<VideoFavoritesResponse>;
        setFavorites: (keys: string[]) => Promise<VideoFavoritesResponse>;
        youtubeAuthUrl: () => Promise<{
            url: string;
        }>;
        youtubeDisconnect: () => Promise<{
            ok: boolean;
        }>;
    };
    power: {
        status: () => Promise<PowerState>;
        schedule: (action: PowerActionKind) => Promise<{
            action: PowerActionKind;
            runAt: string;
            secondsLeft: number;
        }>;
        cancel: () => Promise<{
            cancelled: boolean;
        }>;
    };
    remoteCommands: {
        status: () => Promise<RemoteCommandsState>;
        start: (key: string) => Promise<RemoteCommandResult>;
        stop: (key: string) => Promise<RemoteCommandResult>;
        refresh: (key: string) => Promise<RemoteCommandResult>;
        output: (key: string) => Promise<{
            text: string;
        }>;
    };
    deploy: {
        status: () => Promise<DeployState>;
        launch: () => Promise<{
            previousStartedAt: string | null;
        }>;
    };
    externalPreview: {
        list: () => Promise<{
            enabled: boolean;
            links: ExternalPreviewLink[];
        }>;
        revoke: (jti: string) => Promise<{
            revoked: number;
        }>;
        revokeAll: () => Promise<{
            revoked: number;
        }>;
    };
    deployJournal: {
        status: () => Promise<DeployJournal>;
    };
    featureFlags: {
        get: () => Promise<{
            definitions: FeatureFlagDefinition[];
            values: Record<string, boolean>;
        }>;
        set: (key: string, enabled: boolean) => Promise<{
            values: Record<string, boolean>;
        }>;
    };
    specialties: {
        list: () => Promise<SpecialtyCatalogEntry[]>;
        getSettings: () => Promise<SpecialtySettingsResponse>;
        saveGlobalLayer: (layer: SpecialtySettingsLayer) => Promise<{
            global: SpecialtySettingsLayer;
        }>;
        getUserLayer: (userId: string) => Promise<{
            user: SpecialtySettingsLayer;
            userId: string;
        }>;
        resetPreview: (scope: "owner" | "global", key?: string) => Promise<ResetResult>;
        reset: (scope: "owner" | "global", key?: string) => Promise<ResetResult>;
        setMaxSubstitutions: (scope: "global", value: number | null) => Promise<{
            maxSubstitutions: number;
        }>;
        promptSectionsCatalog: () => Promise<SpecialtyPromptSectionsCatalog>;
    };
    projects: {
        list: () => Promise<Project[]>;
        events: (id: string, opts?: {
            since?: string;
            type?: string;
            actor?: string;
            limit?: number;
        }) => Promise<unknown[]>;
        teamMemory: (id: string) => Promise<TeamMemoryEntry[]>;
        addTeamMemory: (id: string, text: string, type?: TeamMemoryType) => Promise<TeamMemoryEntry>;
        updateTeamMemory: (id: string, entryId: string, text: string) => Promise<TeamMemoryEntry>;
        removeTeamMemory: (id: string, entryId: string) => Promise<void>;
        create: (name: string, rootPath: string | null, createDirectory?: boolean, groupId?: string | null, git?: {
            enableGit?: boolean;
            gitAutoCommit?: boolean;
            gitAutoPush?: boolean;
        }, color?: string | null, local?: {
            deviceId: string;
            rootPath: string;
        }) => Promise<Project>;
        update: (id: string, data: {
            name?: string;
            rootPath?: string;
            systemPrompt?: string;
            showHiddenFiles?: boolean;
            permissionRules?: PermissionRule[];
            groupId?: string | null;
            color?: string | null;
            mcpServersOn?: string[];
            autoImportDossiers?: boolean;
            mcpCatalogConfirmed?: boolean;
        }) => Promise<Project>;
        setDevice: (id: string, body: {
            deviceId: string | null;
            rootPath?: string;
        }) => Promise<Project>;
        setHands: (id: string, enabled: boolean) => Promise<Project>;
        updateTags: (id: string, registry: ProjectTag[]) => Promise<Project>;
        delete: (id: string) => Promise<void>;
        suggestIcon: (id: string, opts?: {
            prompt?: string;
        }) => Promise<{
            candidates: GlyphCandidate[];
            failReason?: string | null;
        }>;
        suggestIconPreview: (opts?: {
            name?: string;
            prompt?: string;
        }) => Promise<{
            candidates: GlyphCandidate[];
            failReason?: string | null;
        }>;
        selectIcon: (id: string, candidate: {
            name?: string | null;
        }) => Promise<Project>;
        setIconMode: (id: string, kind: "initials" | "glyph" | "image") => Promise<Project>;
        uploadIconImage: (id: string, file: File) => Promise<Project>;
        iconImageUrl: (project: Project) => string | null;
        getBuiltinPrompt: () => Promise<{
            content: string;
        }>;
        mapHygiene: {
            scan: (id: string) => Promise<MapHygieneReport>;
            review: (id: string, baseSha: string) => Promise<MapHygieneReport>;
            apply: (id: string, baseSha: string, ids: string[]) => Promise<MapHygieneApplyResult>;
        };
        generateBackground: (id: string) => Promise<BackgroundResult>;
        resetBackground: (id: string) => Promise<BackgroundResult>;
        backgroundTileUrl: (project: Project) => string | null;
        getEffectivePrompt: (id: string) => Promise<{
            parts: SystemPromptPart[];
        }>;
        updateBoardColumns: (id: string, columns: BoardColumn[]) => Promise<Project>;
        applyPreset: (id: string, presetKey: string) => Promise<{
            created: string[];
            skipped: {
                path: string;
                reason: string;
            }[];
        }>;
        codeGraph: (id: string) => Promise<CodeGraph>;
        codeGraphBuild: (id: string) => Promise<void>;
        architectureGenerate: (id: string, withAgent?: boolean) => Promise<ArchitectureGenerateResult>;
        architectureModel: (id: string) => Promise<ArchitectureModelDto>;
        architectureSaveModel: (id: string, content: string, baseVersion: string | null) => Promise<ArchitectureSaveResult>;
        services: (id: string) => Promise<{
            services: ProjectService[];
            activeServiceId: string | null;
        }>;
        previewStart: (id: string, svc: {
            serviceId: string;
            name: string;
            command: string;
            args: string[];
            cwd?: string;
            port?: number;
            autoPort?: boolean;
            env?: Record<string, string>;
        }) => Promise<{
            status: string;
            port?: number;
            error?: string;
            serviceId: string;
        }>;
        previewStop: (id: string, serviceId: string) => Promise<{
            status: string;
        }>;
        previewStatus: (id: string) => Promise<{
            running: {
                serviceId: string;
                name: string;
                port: number | null;
                status: string;
                error: string | null;
            }[];
            activeServiceId: string | null;
        }>;
        previewActive: (id: string, serviceId: string) => Promise<{
            activeServiceId: string;
        }>;
        previewActiveExternal: (id: string, serviceId: string) => Promise<{
            activeServiceId: string;
            port: number;
        }>;
        previewStopExternal: (id: string, serviceId: string, confirm?: boolean) => Promise<{
            status: string;
            pid: number;
        }>;
        previewExternalLink: (id: string, serviceId: string) => Promise<ExternalLinkIssued>;
        getLaunchConfig: (id: string) => Promise<{
            configurations: LaunchConfigEntry[];
        }>;
        putLaunchConfig: (id: string, configurations: LaunchConfigEntry[]) => Promise<{
            configurations: LaunchConfigEntry[];
        }>;
    };
    home: {
        summary: (recent?: number) => Promise<HomeSummaryResponse>;
    };
    board: {
        agents: () => Promise<{
            items: BoardItem[];
        }>;
        interrupt: (sessionId: string) => Promise<void>;
        allowPermission: (sessionId: string, requestId: string) => Promise<void>;
        denyPermission: (sessionId: string, requestId: string) => Promise<void>;
    };
    projectGroups: {
        list: () => Promise<ProjectGroup[]>;
        create: (name: string, color: string, extra?: {
            icon?: string;
            charter?: string;
        }) => Promise<ProjectGroup>;
        update: (id: string, data: {
            name?: string;
            color?: string;
            icon?: string;
            charter?: string;
        }) => Promise<ProjectGroup>;
        reorder: (orderedIds: string[]) => Promise<ProjectGroup[]>;
        delete: (id: string) => Promise<DeleteSphereResponse>;
    };
    spheres: {
        overview: (id: string) => Promise<SphereOverview>;
        memory: (id: string) => Promise<SphereMemoryResponse>;
        addMemory: (id: string, text: string, type?: SphereMemoryType) => Promise<SphereMemoryEntry>;
        removeMemory: (id: string, entryId: string) => Promise<void>;
        adoptMemory: (id: string, projectId: string, entryId: string) => Promise<SphereMemoryEntry>;
    };
    tasks: {
        listAll: (from?: string, to?: string) => Promise<Task[]>;
        listByProject: (projectId: string) => Promise<Task[]>;
        listByPersona: (personaId: string) => Promise<Task[]>;
        create: (projectId: string | null, dto: CreateTaskDto) => Promise<Task>;
        get: (taskId: string) => Promise<Task>;
        update: (taskId: string, dto: UpdateTaskDto) => Promise<Task>;
        delete: (taskId: string) => Promise<void>;
        execute: (taskId: string) => Promise<Task>;
        aiDescription: (title: string, projectId?: string | null) => Promise<{
            description: string;
        }>;
        aiSubtasks: (title: string, description: string, projectId?: string | null) => Promise<{
            subtasks: string[];
        }>;
        aiClassify: (title: string, description?: string | null, projectId?: string | null) => Promise<{
            priority: string | null;
            labels: string[];
        }>;
        aiNormalizeTitle: (title: string) => Promise<{
            title: string;
            dueHint: string | null;
        }>;
        aiFindDuplicate: (title: string, description?: string | null, projectId?: string | null) => Promise<{
            duplicateId: string | null;
            reason: string | null;
        }>;
    };
    notes: {
        list: (source?: string, q?: string) => Promise<NoteSummary[]>;
        sources: () => Promise<NoteSource[]>;
        graph: (annotations?: boolean) => Promise<NoteGraph>;
        templates: () => Promise<NoteTemplate[]>;
        resolve: (name: string, anchor?: string) => Promise<{
            note: NoteDetail;
            fragment: string | null;
        }>;
        daily: (date: string) => Promise<NoteDetail>;
        caps: () => Promise<{
            semantic: boolean;
        }>;
        annotate: (dto: {
            doc: {
                scope: string;
                path: string;
            };
            selection: {
                start: number;
                end: number;
                text: string;
            };
            comment?: string;
            tags?: string[];
            title?: string;
        }) => Promise<NoteDetail>;
        annotations: (scope: string, path: string) => Promise<DocAnnotation[]>;
        setStatus: (id: string, status: "open" | "resolved") => Promise<NoteDetail>;
        repin: (id: string, selection: {
            start: number;
            end: number;
            text: string;
        }) => Promise<NoteDetail>;
        reply: (id: string, comment: string, tags?: string[]) => Promise<NoteDetail>;
        replies: (id: string) => Promise<NoteReply[]>;
        semantic: (q: string, topK?: number) => Promise<{
            available: boolean;
            results: NoteSemanticHit[];
        }>;
        reindex: () => Promise<{
            changed: number;
        }>;
        moveFolder: (source: string, path: string, newPath: string) => Promise<{
            notes: {
                oldId: string;
                newId: string;
            }[];
        }>;
        folders: () => Promise<NoteFolder[]>;
        createFolder: (source: string, path: string) => Promise<NoteFolder>;
        deleteFolder: (source: string, path: string) => Promise<{
            removed: number;
        }>;
        move: (id: string, folder: string | null, targetSource?: string) => Promise<NoteDetail>;
        linkMention: (id: string, targetTitle: string) => Promise<NoteDetail>;
        suggestLinks: (id: string) => Promise<{
            title: string;
            why: string;
        }[]>;
        suggestTags: (id: string) => Promise<string[]>;
        suggestTitle: (id: string) => Promise<{
            title: string;
        }>;
        dailySummary: (date: string) => Promise<NoteDetail>;
        toc: (id: string) => Promise<NoteDetail>;
        translate: (id: string) => Promise<NoteDetail>;
        get: (id: string) => Promise<NoteDetail>;
        backlinks: (id: string) => Promise<NoteBacklink[]>;
        create: (dto: CreateNoteDto) => Promise<NoteDetail>;
        update: (id: string, dto: UpdateNoteDto) => Promise<NoteDetail>;
        delete: (id: string) => Promise<void>;
        tasks: (id: string) => Promise<NoteTask[]>;
        promoteTask: (id: string, line: number) => Promise<Task>;
        toggleTask: (id: string, line: number, done: boolean) => Promise<NoteDetail>;
        setNoteTaskDue: (id: string, line: number, due: string | null) => Promise<NoteDetail>;
    };
    personas: {
        list: (opts?: {
            scope?: string;
            projectId?: string;
        }) => Promise<Persona[]>;
        get: (id: string) => Promise<Persona>;
        create: (dto: CreatePersonaDto) => Promise<Persona>;
        update: (id: string, dto: UpdatePersonaDto) => Promise<Persona>;
        remove: (id: string, successorId?: string) => Promise<void>;
        makeDefault: (id: string) => Promise<Persona>;
        chats: (id: string) => Promise<Session[]>;
        createChat: (id: string, body: {
            mode?: string;
            resumeSessionId?: string;
            name?: string;
            projectId?: string;
        }) => Promise<Session>;
        match: (task: string, projectId?: string | null, requiredTool?: string) => Promise<{
            personaId: string | null;
        }>;
        pantheon: () => Promise<{
            templates: PantheonTemplate[];
        }>;
        connectPantheon: (keys?: string[]) => Promise<Persona[]>;
        assignPersonaToChat: (chatId: string, personaId: string | null, agentName?: string | null) => Promise<Session>;
        assignPersonaToSession: (projectId: string, sessionId: string, personaId: string | null, agentName?: string | null) => Promise<Session>;
        memory: (id: string, type?: PersonaMemoryType) => Promise<PersonaMemoryEntry[]>;
        memorySearch: (id: string, q: string, topK?: number) => Promise<PersonaMemoryHit[]>;
        remember: (id: string, body: {
            type: PersonaMemoryType;
            text: string;
            tags?: string[];
        }) => Promise<PersonaMemoryEntry>;
        updateMemory: (id: string, entryId: string, text: string) => Promise<PersonaMemoryEntry>;
        memoryToNote: (id: string, entryId: string) => Promise<{
            noteId: string;
            noteTitle: string;
        }>;
        noteToMemory: (id: string, noteId: string) => Promise<void>;
        forget: (id: string, entryId: string) => Promise<void>;
        confirmMemory: (id: string, entryId: string) => Promise<void>;
        focus: (id: string) => Promise<PersonaWorkingFocus | null>;
        clearFocus: (id: string) => Promise<void>;
        avatarCaps: () => Promise<{
            generate: boolean;
        }>;
        generateAvatar: (id: string, opts?: {
            prompt?: string;
            count?: number;
        }) => Promise<{
            candidates: string[];
        }>;
        selectAvatar: (id: string, file: string) => Promise<Persona>;
        avatarUrl: (persona: Persona) => string | null;
        uploadAvatar: (id: string, original: File, cropped: Blob, crop: {
            scale: number;
            offsetX: number;
            offsetY: number;
        }) => Promise<Persona>;
        recropAvatar: (id: string, cropped: Blob, crop: {
            scale: number;
            offsetX: number;
            offsetY: number;
        }) => Promise<Persona>;
        avatarOriginalUrl: (persona: Persona) => string | null;
        avatarCandidateUrl: (id: string, file: string) => string;
        quickCreate: (body: {
            prompt: string;
            scope?: PersonaScope;
            projectId?: string;
            sphereId?: string;
        }) => Promise<Persona>;
        aiTeam: (projectId: string, prompt: string) => Promise<{
            members: TeamMemberDraft[];
        }>;
        aiCharacter: (body: {
            name?: string;
            role?: string;
            description?: string;
            current?: string;
            instruction?: string;
        }) => Promise<{
            contract: PersonaContract;
        }>;
        bindings: (id: string) => Promise<PersonaBinding[]>;
        addBinding: (id: string, dto: PersonaBindingDto) => Promise<PersonaBinding>;
        updateBinding: (id: string, bindingId: string, dto: PersonaBindingDto) => Promise<PersonaBinding>;
        removeBinding: (id: string, bindingId: string) => Promise<void>;
        suggestVoice: (req: {
            name?: string;
            role?: string;
            description?: string;
            character?: string;
            tone?: string;
        }) => Promise<{
            voice: string | null;
            role: string | null;
        }>;
        setVoice: (id: string, voice: PersonaVoice | null) => Promise<Persona>;
        setBindings: (id: string, bindings: PersonaBindingDto[]) => Promise<PersonaBinding[]>;
        bindingTargets: (type: string, source?: string, personaId?: string) => Promise<BindingTarget[]>;
        aiBindingCondition: (body: {
            type: PersonaBindingType;
            target: string;
            path?: string | null;
        }) => Promise<{
            condition: string;
        }>;
        suggestBindings: (id: string) => Promise<{
            candidates: PersonaBinding[];
        }>;
        generateBindings: (id: string, prompt: string) => Promise<{
            candidates: PersonaBinding[];
        }>;
        applyDefaultBindings: (id: string) => Promise<ApplyDefaultBindingsResult>;
        automation: (id: string) => Promise<PersonaAutomationRule[]>;
        addAutomation: (id: string, dto: AutomationRuleDto) => Promise<PersonaAutomationRule>;
        updateAutomation: (id: string, ruleId: string, dto: AutomationRuleDto) => Promise<PersonaAutomationRule>;
        removeAutomation: (id: string, ruleId: string) => Promise<void>;
        testAutomation: (id: string, ruleId: string) => Promise<void>;
        suggestAutomation: (id: string) => Promise<{
            candidates: PersonaAutomationRule[];
        }>;
        generateAutomation: (id: string, prompt: string) => Promise<{
            candidates: PersonaAutomationRule[];
        }>;
    };
    briefing: {
        today: (date?: string) => Promise<NoteDetail>;
    };
    search: (q: string, topK?: number) => Promise<SearchHit[]>;
    onboarding: {
        startUser: () => Promise<Session>;
        startProject: (projectId: string) => Promise<Session>;
        applyTranscript: () => Promise<Persona>;
    };
    sessions: {
        list: (projectId: string) => Promise<Session[]>;
        handsStatus: (sessionId: string) => Promise<LocalHandsChatStatus>;
        iconBatch: (projectId: string) => Promise<{
            processed: number;
            skipped: number;
        }>;
        create: (projectId: string, mode?: string, resumeSessionId?: string, name?: string, model?: string, agentName?: string, effort?: string) => Promise<Session>;
        update: (projectId: string, sessionId: string, data: {
            name?: string | null;
            model?: string | null;
            effort?: string | null;
            expiresAfterMinutes?: number | null;
            tags?: string[];
            excludeFromDossiers?: boolean | null;
            notificationsMuted?: boolean;
            voiceMode?: boolean;
            voiceStyle?: string;
        }) => Promise<Session>;
        delete: (projectId: string, sessionId: string) => Promise<void>;
        getContext: (projectId: string, sessionId: string) => Promise<SessionContextEntry[]>;
        putContext: (projectId: string, sessionId: string, entries: Pick<SessionContextEntry, "type" | "id" | "title">[]) => Promise<SessionContextEntry[]>;
        getHistory: (projectId: string, sessionId: string) => Promise<unknown[]>;
        summary: (sessionId: string) => Promise<NoteDetail>;
        extractTasks: (sessionId: string) => Promise<ExtractTasksResponse>;
        revokeAutoAllow: (sessionId: string, tool: string) => Promise<Session>;
        cancelPending: (sessionId: string, messageId: string) => Promise<void>;
        preemptForPending: (sessionId: string) => Promise<void>;
        promptSnapshot: (sessionId: string, snapshotId: string) => Promise<PromptSnapshot>;
        promptSnapshotFile: (sessionId: string, snapshotId: string, key: string) => Promise<PromptSection>;
        analyzePrompt: (sessionId: string, snapshotId: string, includeText: boolean) => Promise<{
            analysis: string;
        }>;
    };
    wall: {
        get: () => Promise<{
            chats: Session[];
        }>;
        put: (chatIds: string[]) => Promise<{
            chats: Session[];
        }>;
        candidates: () => Promise<Session[]>;
    };
    quickPhrases: {
        get: () => Promise<{
            phrases: QuickPhrase[];
        }>;
        put: (phrases: QuickPhrase[]) => Promise<{
            phrases: QuickPhrase[];
        }>;
    };
    chats: {
        list: () => Promise<Session[]>;
        agentsPresence: () => Promise<{
            agents: string[];
            commands: string[];
        }>;
        get: (id: string) => Promise<Session>;
        create: (mode?: string, resumeSessionId?: string, name?: string, model?: string, effort?: string) => Promise<Session>;
        update: (id: string, data: {
            name?: string | null;
            model?: string | null;
            effort?: string | null;
            pinned?: boolean;
            expiresAfterMinutes?: number | null;
            notificationsMuted?: boolean;
            voiceMode?: boolean;
            voiceStyle?: string;
        }) => Promise<Session>;
        setParent: (id: string, parentId: string | null) => Promise<Session>;
        markRead: (id: string) => Promise<void>;
        retitle: (id: string) => Promise<Session>;
        createGroup: (personaIds: string[], mode?: string, name?: string) => Promise<Session>;
        setParticipants: (id: string, personaIds: string[]) => Promise<Session>;
        setWorkLoop: (id: string, enabled: boolean) => Promise<Session>;
        setTeamImplement: (id: string, enabled: boolean, opts?: {
            autoWaves?: boolean;
            coordinatorPersonaId?: string;
            plannerPersonaId?: string;
            executorPersonaIds?: string[];
        }) => Promise<Session>;
        setTeamImplementAuto: (id: string, autoWaves: boolean) => Promise<Session>;
        stopTeamImplement: (id: string) => Promise<Session>;
        getTeamWaveSnapshot: (id: string) => Promise<import("../types").TeamWaveSnapshot>;
        restartTeamWaveTask: (id: string, taskId: string) => Promise<import("../types").TeamTaskRestartResponse>;
        restartTeamWave: (id: string, confirm: boolean) => Promise<import("../types").TeamWaveRestartResponse>;
        restartTeamWaveTurn: (id: string, startFresh?: boolean) => Promise<import("../types").TeamTurnRestartResponse>;
        setWorktree: (id: string, enabled: boolean, branch?: string, force?: boolean) => Promise<Session>;
        migrateProvider: (id: string, model: string, subscriptionKey?: string) => Promise<Session>;
        branch: (id: string, body: {
            userMessageIndex: number;
            anchorText: string;
            include: "turn" | "beforePrompt";
            name?: string;
        }) => Promise<{
            chatId: string;
            projectId: string | null;
            claudeSessionId: string;
            draft: string | null;
        }>;
        setMode: (id: string, mode: string) => Promise<Session>;
        dropWindow1M: (id: string) => Promise<Session>;
        delete: (id: string) => Promise<void>;
        getHistory: (id: string) => Promise<unknown[]>;
        uploadFile: (id: string, file: File, projectId?: string) => Promise<{
            path: string;
        }>;
    };
    watchdogs: {
        snapshot: () => Promise<{
            sessions: string[];
            projects: string[];
        }>;
    };
    history: {
        days: (sinceDays?: number) => Promise<DaySummaryStub[]>;
        day: (date: string) => Promise<ChangelogDay>;
        newCount: (sinceIso: string) => Promise<{
            count: number;
        }>;
        status: () => Promise<ChangelogStatus>;
        invalidateDay: (date: string) => Promise<void>;
        clear: () => Promise<void>;
    };
    docs: {
        index: (projectId: string) => Promise<DocEntry[]>;
        doc: (projectId: string, path: string) => Promise<DocDetail>;
        search: (projectId: string, q: string) => Promise<DocSearchHit[]>;
        scope: (projectId: string) => Promise<DocsScopeInfo>;
        setScope: (projectId: string, scope: Partial<DocsScope>) => Promise<DocsScopeInfo>;
        saveScopeFile: (projectId: string) => Promise<DocsScopeInfo>;
        create: (projectId: string, folder: string, name: string, kind: "doc" | "section") => Promise<{
            path: string;
            index: DocEntry[];
        }>;
        rename: (projectId: string, path: string, newName: string, updateLinks: boolean) => Promise<{
            path: string;
            updatedDocs: number;
            brokenLinks: number;
            moved: Record<string, string>;
            index: DocEntry[];
        }>;
        remove: (projectId: string, path: string) => Promise<{
            removed: string[];
            brokenLinks: number;
            removedFiles: number;
            index: DocEntry[];
        }>;
        move: (projectId: string, path: string, targetFolder: string, updateLinks: boolean) => Promise<{
            path: string;
            updatedDocs: number;
            brokenLinks: number;
            moved: Record<string, string>;
            index: DocEntry[];
        }>;
        setOrder: (projectId: string, folder: string, items: string[]) => Promise<DocEntry[]>;
        setProperty: (projectId: string, path: string, key: string, value: string | null) => Promise<{
            properties: DocProperty[];
            touched: string[];
            index: DocEntry[];
        }>;
        setDocTypes: (projectId: string, types: DocTypeSchema[]) => Promise<{
            scope: DocsScopeInfo;
            index: DocEntry[];
        }>;
    };
    dossiers: {
        list: (projectId: string, filter?: {
            file?: string;
            symbol?: string;
            commit?: string;
        }) => Promise<{
            entries: DossierEntry[];
            coverage?: {
                periodDays: number;
                commits: number;
                dossiers: number;
            } | null;
        }>;
        exportStatus: (projectId: string) => Promise<{
            isGitRepo: boolean;
            sharedFolder: boolean;
            hasDossierBranch: boolean;
            autoExport: "active" | "foreignTip" | "originOnly" | "sharedFolder" | null;
        }>;
        exportRun: (projectId: string, push: boolean) => Promise<{
            status: "exported" | "nothingToExport";
            count: number;
        }>;
        importRun: (projectId: string) => Promise<{
            status: "imported" | "nothingToImport" | "noBranch";
            added: number;
            skipped: number;
        }>;
    };
    telemetry: {
        status: () => Promise<{
            configured: boolean;
            reachable: boolean;
            proxyPath: string;
            discussProjectId?: string | null;
        }>;
        incidents: () => Promise<IncidentListResponse>;
        incident: (fingerprint: string) => Promise<IncidentDossier>;
        incidentText: (fingerprint: string) => Promise<{
            text: string;
        }>;
        incidentExplain: (fingerprint: string) => Promise<{
            text: string;
        }>;
        muteIncident: (fingerprint: string, muted: boolean) => Promise<void>;
    };
    files: {
        fileUrl: (projectId: string, path: string) => string;
        list: (projectId: string, path?: string) => Promise<FileEntry[]>;
        tree: (projectId: string, path?: string, showHidden?: boolean) => Promise<FileEntry[]>;
        search: (projectId: string, q: string) => Promise<FileEntry[]>;
        getContent: (projectId: string, path: string) => Promise<{
            content: string | null;
            isBinary: boolean;
            isImage: boolean;
            isDocument?: boolean;
            docKind?: string;
            mimeType?: string;
            base64?: string;
            fileSize?: number;
        }>;
        saveContent: (projectId: string, path: string, content: string) => Promise<void>;
        documentConvert: (projectId: string, path: string) => Promise<{
            markdown: string;
        }>;
        documentSummary: (projectId: string, path: string) => Promise<{
            summary: string;
        }>;
        documentExtract: (projectId: string, path: string) => Promise<{
            decisions: string[];
            dates: string[];
            people: string[];
            actionItems: string[];
        }>;
        documentTags: (projectId: string, path: string) => Promise<{
            tags: string[];
        }>;
        toMarkdown: (projectId: string, path: string, targetDir?: string | null, enhance?: boolean) => Promise<{
            savedPath: string;
            markdown: string;
        }>;
        getDiff: (projectId: string, path: string) => Promise<{
            diff: string | null;
        }>;
        changedBy: (projectId: string, paths: string[]) => Promise<{
            files: Record<string, ChangedBySession[]>;
        }>;
        revert: (projectId: string, path: string) => Promise<void>;
        createFile: (projectId: string, path: string, content?: string) => Promise<void>;
        mkdir: (projectId: string, path: string) => Promise<void>;
        rename: (projectId: string, oldPath: string, newPath: string) => Promise<void>;
        delete: (projectId: string, path: string) => Promise<void>;
        saveFromUrl: (projectId: string, url: string, path: string) => Promise<{
            path: string;
        }>;
        officeDiscard: (projectId: string, path: string) => Promise<void>;
        getOfficeVersion: (projectId: string, path: string) => Promise<{
            ms: number;
        }>;
        officeForceSave: (projectId: string, path: string) => Promise<{
            ok: boolean;
            reason?: string;
        }>;
        upload: (projectId: string, file: File, targetPath?: string) => Promise<void>;
    };
    hostFiles: {
        getContent: (path: string) => Promise<{
            content: string | null;
            isBinary: boolean;
            isImage: boolean;
            isVideo?: boolean;
            isAudio?: boolean;
            isDocument?: boolean;
            docKind?: string;
            mimeType?: string;
            base64?: string;
            fileSize?: number;
        }>;
    };
    git: {
        status: (projectId: string) => Promise<GitStatus>;
        diff: (projectId: string, path: string, staged?: boolean) => Promise<{
            diff: string | null;
        }>;
        log: (projectId: string, limit?: number, branch?: string) => Promise<GitLogEntry[]>;
        unpushed: (projectId: string, limit?: number) => Promise<GitLogEntry[]>;
        getCommitPrompt: (projectId: string) => Promise<GitCommitPromptInfo>;
        setCommitPrompt: (projectId: string, global: string, project: string, useProject: boolean) => Promise<GitCommitPromptInfo>;
        detectCommitStyle: (projectId: string) => Promise<{
            prompt: string;
        }>;
        branches: (projectId: string) => Promise<GitBranchInfo[]>;
        commitDetail: (projectId: string, sha: string) => Promise<GitCommitDetail>;
        commitFileDiff: (projectId: string, sha: string, path: string) => Promise<{
            diff: string | null;
        }>;
        stage: (projectId: string, path: string) => Promise<GitStatus>;
        unstage: (projectId: string, path: string) => Promise<GitStatus>;
        stageAll: (projectId: string) => Promise<GitStatus>;
        discard: (projectId: string, path: string) => Promise<GitStatus>;
        discardAll: (projectId: string) => Promise<GitStatus>;
        commit: (projectId: string, message: string, amend?: boolean) => Promise<{
            sha: string;
        }>;
        checkout: (projectId: string, branch: string) => Promise<GitStatus>;
        createBranch: (projectId: string, name: string, from?: string) => Promise<GitStatus>;
        fetch: (projectId: string) => Promise<GitStatus>;
        pull: (projectId: string) => Promise<GitStatus>;
        push: (projectId: string) => Promise<GitStatus>;
        sync: (projectId: string) => Promise<GitStatus>;
        stageHunk: (projectId: string, patch: string) => Promise<GitStatus>;
        unstageHunk: (projectId: string, patch: string) => Promise<GitStatus>;
        stashList: (projectId: string) => Promise<GitStashEntry[]>;
        stashShow: (projectId: string, index: number) => Promise<{
            files: GitFileChange[];
        }>;
        stashPush: (projectId: string, message?: string) => Promise<GitStatus>;
        stashPop: (projectId: string, index: number) => Promise<GitStatus>;
        stashDrop: (projectId: string, index: number) => Promise<GitStatus>;
        revertCommit: (projectId: string, sha: string) => Promise<GitStatus>;
        blame: (projectId: string, path: string) => Promise<GitBlameLine[]>;
        forgejoCredentials: (projectId: string) => Promise<{
            login: string;
            password: string | null;
        }>;
        resetForgejoPassword: (projectId: string) => Promise<{
            login: string;
            password: string;
        }>;
        fileLog: (projectId: string, path: string, limit?: number) => Promise<GitLogEntry[]>;
        fileAtCommit: (projectId: string, sha: string, path: string) => Promise<{
            content: string | null;
        }>;
        restoreFile: (projectId: string, sha: string, path: string) => Promise<GitStatus>;
        saveNow: (projectId: string) => Promise<{
            committed: boolean;
            sha?: string;
        }>;
        aiCommitMessage: (projectId: string) => Promise<{
            summary: string;
            description: string;
        }>;
        aiStashName: (projectId: string) => Promise<{
            name: string;
        }>;
        init: (projectId: string) => Promise<{
            status: GitStatus;
            htmlUrl: string | null;
        }>;
        remote: (projectId: string) => Promise<GitRemoteInfo>;
        setRemote: (projectId: string, url: string) => Promise<GitRemoteInfo>;
        createServerRepo: (projectId: string) => Promise<GitRemoteInfo>;
        setAutoCommit: (projectId: string, enabled: boolean, push: boolean) => Promise<{
            autoCommit: boolean;
            autoPush: boolean;
        }>;
    };
    knowledge: {
        getStatus: (projectId: string) => Promise<{
            datasetId: string | null;
            documents: DifyDocument[];
            total: number;
        }>;
        indexFile: (projectId: string, relativePath: string) => Promise<{
            datasetId: string;
            document: DifyDocument;
        }>;
        indexFolder: (projectId: string, relativePath: string) => Promise<{
            indexed: number;
            skipped: number;
            documents: DifyDocument[];
        }>;
        deleteDocument: (projectId: string, documentId: string) => Promise<void>;
        deleteDataset: (projectId: string) => Promise<void>;
        setDocumentTags: (projectId: string, documentName: string, documentId: string, tags: string[]) => Promise<void>;
    };
    knowledgeBases: {
        list: () => Promise<KnowledgeListResponse>;
        get: (id: string) => Promise<KnowledgeBaseDetail>;
        describe: (id: string) => Promise<{
            description: string;
        }>;
        create: (dto: CreateKnowledgeBaseDto) => Promise<{
            id: string;
            title: string;
            visibility: string;
        }>;
        remove: (id: string) => Promise<void>;
        addDocumentText: (id: string, name: string, text: string) => Promise<{
            id: string;
            name: string;
            indexingStatus: string;
        }>;
        addDocumentFile: (id: string, file: File, name?: string) => Promise<{
            id: string;
            name: string;
            indexingStatus: string;
        }>;
        removeDocument: (id: string, docId: string) => Promise<void>;
        getDocument: (id: string, docId: string) => Promise<KnowledgeDocumentContent>;
        search: (id: string, q: string, method: "semantic" | "fulltext", topK?: number) => Promise<{
            items: KnowledgeSearchHit[];
        }>;
    };
    skills: {
        list: (projectId: string) => Promise<SkillsData>;
        listGlobal: () => Promise<SkillInfo[]>;
        getSkill: (skillName: string) => Promise<{
            content: string;
        }>;
        saveSkill: (skillName: string, content: string) => Promise<void>;
        createSkill: (name: string, content: string) => Promise<{
            name: string;
        }>;
        getAgent: (projectId: string, agentName: string) => Promise<{
            content: string;
        }>;
        saveAgent: (projectId: string, agentName: string, content: string) => Promise<void>;
        createAgent: (projectId: string, name: string, content: string) => Promise<{
            name: string;
        }>;
        find: (q: string, owner?: string) => Promise<{
            query: string;
            translatedQuery: string | null;
            results: RegistrySkill[];
        }>;
        install: (source: string, skill: string, scope: "project" | "global", projectId?: string) => Promise<{
            installed: string;
            scope: string;
        }>;
        uninstall: (skill: string, scope: "project" | "global", projectId?: string) => Promise<void>;
        suggest: (ctx: {
            personaId?: string;
            projectId?: string;
            query?: string;
        }) => Promise<{
            candidates: SkillSuggestion[];
        }>;
        generate: (prompt: string) => Promise<GeneratedSkill>;
        installForPersona: (personaId: string, source: string, skill: string) => Promise<{
            installed: string;
            bound: boolean;
            warning?: string;
        }>;
    };
    workflow: {
        getAgents: (transcriptDir: string) => Promise<{
            agents: WorkflowAgentInfo[];
        }>;
        getTimeline: (transcriptDir: string, agentId: string) => Promise<{
            blocks: WorkflowAgentBlock[];
        }>;
    };
    sync: {
        list: (projectId: string) => Promise<SyncMark[]>;
        add: (projectId: string, path: string, isDirectory: boolean) => Promise<void>;
        remove: (projectId: string, path: string) => Promise<void>;
    };
    tts: {
        voices: () => Promise<TtsVoicesResponse>;
    };
    plans: {
        buildMap: (plan: string) => Promise<PlanMap | null>;
    };
    reader: {
        read: (url: string) => Promise<ReaderReadResult>;
        embedCheck: (url: string) => Promise<{
            embeddable: boolean;
        }>;
    };
    higgsfield: {
        status: () => Promise<HiggsfieldStatus>;
        adminStatus: () => Promise<HiggsfieldAdminStatus>;
        connect: () => Promise<{
            authorizeUrl: string;
        }>;
        disconnect: () => Promise<{
            ok: true;
        }>;
    };
};
export type HiggsfieldStatus = {
    connected: boolean;
    health?: string | null;
    error?: string | null;
};
export type HiggsfieldAdminStatus = HiggsfieldStatus & {
    expiresAt?: string | null;
};
export type ReaderReadResult = {
    ok: true;
    page: ReaderPage;
} | {
    ok: false;
    error: {
        code: ReaderErrorCode;
        httpStatus?: number | null;
    };
};
