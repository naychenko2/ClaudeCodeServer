import type { Mode } from '../lib/modes';
export type { Mode };
export interface PermissionRule {
    pattern: string;
    action: 'allow' | 'deny';
}
export interface ProjectTag {
    name: string;
    order: number;
    color?: string;
}
export type ProjectBackgroundKind = 'pending' | 'generated' | 'standard' | 'failed';
export interface ProjectBackground {
    kind: ProjectBackgroundKind;
    tileVersion?: string | null;
    failReason?: string | null;
}
export interface BackgroundResult {
    kind: ProjectBackgroundKind;
    tileVersion?: string | null;
    suggestedColorKey?: string | null;
    colorApplied: boolean;
    failReason?: string | null;
}
export type ProjectCapabilityHost = 'server' | 'device' | 'off';
export interface ProjectCapabilityGroup {
    host: ProjectCapabilityHost;
    available: boolean;
    reason: string | null;
    features: string[];
}
export interface ProjectCapabilitiesView {
    host: 'server' | 'device';
    deviceId: string | null;
    files: ProjectCapabilityGroup;
    platform: ProjectCapabilityGroup;
    serverContent: ProjectCapabilityGroup;
    transcript?: ProjectCapabilityGroup;
    exec: {
        available: boolean;
        reason: string | null;
    };
}
export declare const ProjectFeature: {
    readonly Files: "files";
    readonly Diff: "diff";
    readonly Git: "git";
    readonly FileWatcher: "fileWatcher";
    readonly Terminal: "terminal";
    readonly DevServers: "devServers";
    readonly Skills: "skills";
    readonly Attachments: "attachments";
    readonly Chat: "chat";
    readonly History: "history";
    readonly Tasks: "tasks";
    readonly Memory: "memory";
    readonly Personas: "personas";
    readonly Notes: "notes";
    readonly Costs: "costs";
    readonly Tts: "tts";
    readonly Knowledge: "knowledge";
    readonly CodeGraph: "codeGraph";
    readonly Dossiers: "dossiers";
    readonly Docs: "docs";
    readonly MapHygiene: "mapHygiene";
    readonly LiveSubagents: "liveSubagents";
    readonly WorkflowView: "workflowView";
    readonly ChatBranch: "chatBranch";
    readonly Hands: "hands";
};
export type ProjectFeatureKey = (typeof ProjectFeature)[keyof typeof ProjectFeature];
export interface ProjectDeviceView {
    id: string;
    name: string;
    online: boolean;
    platform: string | null;
    agentVersion: string | null;
    harnessReady: boolean;
    harnessProblem: string | null;
}
export interface Project {
    id: string;
    name: string;
    rootPath: string;
    relativePath?: string;
    createdAt: string;
    updatedAt: string;
    ownerId?: string;
    groupId?: string;
    sessionCount?: number;
    difyDatasetId?: string;
    systemPrompt?: string;
    showHiddenFiles?: boolean;
    permissionRules?: PermissionRule[];
    boardColumns?: BoardColumn[];
    builtInSystemPrompt?: string;
    icon?: ProjectIcon;
    tagRegistry?: ProjectTag[];
    mcpServersOn?: string[] | null;
    defaultPersonaId?: string | null;
    onboardingSessionId?: string | null;
    presetKey?: string | null;
    background?: ProjectBackground | null;
    autoImportDossiers?: boolean;
    archiveAfterDays?: number | null;
    deviceId?: string | null;
    device?: ProjectDeviceView | null;
    capabilities?: ProjectCapabilitiesView | null;
    handsEnabled?: boolean;
    handsRefusal?: string | null;
    folderNotice?: string | null;
}
export interface ProjectIcon {
    kind: 'initials' | 'glyph' | 'image';
    color?: string;
    glyph?: {
        name?: string | null;
    } | null;
    imageFile?: string | null;
}
export interface BoardColumn {
    id: string;
    name: string;
    category: TaskStatus;
    color?: string;
}
export type CodeGraphNodeKind = 'Class' | 'Interface' | 'Struct' | 'Enum' | 'Component' | 'Hook' | 'UiPrimitive' | 'Util';
export type CodeGraphRelation = 'Calls' | 'Implements' | 'References';
export type CodeGraphConfidence = 'Extracted' | 'Inferred';
export interface CodeGraphNode {
    id: string;
    label: string;
    fullyQualifiedName: string;
    sourceFile: string;
    sourceLocation: string;
    kind: CodeGraphNodeKind;
}
export interface CodeGraphEdge {
    source: string;
    target: string;
    relation: CodeGraphRelation;
    confidence: CodeGraphConfidence;
}
export interface CodeGraphMetadata {
    builtAt?: string | null;
    nodeCount: number;
    edgeCount: number;
    fileCount: number;
    isStale: boolean;
}
export interface CodeGraph {
    nodes: CodeGraphNode[];
    edges: CodeGraphEdge[];
    godNodes: string[];
    metadata: CodeGraphMetadata;
}
export type DocLinkKind = 'doc' | 'repo' | 'external';
export interface DocLink {
    target: string;
    anchor: string | null;
    kind: DocLinkKind;
    text: string;
}
export interface DocHeading {
    level: number;
    text: string;
    slug: string;
}
export interface DocEntry {
    path: string;
    title: string;
    modified: string;
    size: number;
    headings: DocHeading[];
    binary?: boolean;
    sectionFolder?: string | null;
    type?: string | null;
    properties?: DocProperty[] | null;
}
export interface DocBacklink {
    path: string;
    title: string;
    anchor: string | null;
}
export interface DocDetail {
    path: string;
    title: string;
    content: string;
    links: DocLink[];
    backlinks: DocBacklink[];
    binary?: boolean;
    type?: string | null;
    properties?: DocProperty[] | null;
    propsRange?: {
        start: number;
        end: number;
    } | null;
}
export interface DocSearchHit {
    path: string;
    title: string;
    slug: string | null;
    snippet: string;
}
export interface DocFolderCandidate {
    path: string;
    count: number;
    exists: boolean;
}
export interface DocRootFileCandidate {
    name: string;
    exists: boolean;
}
export interface DocsScope {
    folders: string[];
    rootFiles: string[];
    types: string[];
    home?: string | null;
    excludeFolders?: string[] | null;
}
export interface DocOption {
    path: string;
    title: string;
}
export interface DocTypeGroup {
    key: string;
    title: string;
    extensions: string[];
    text: boolean;
}
export interface DocProperty {
    key: string;
    value: string;
    link?: string | null;
}
export type DocPropertyKind = 'choice' | 'date' | 'text' | 'docLink';
export type DocPropertyColor = 'gray' | 'accent' | 'success' | 'warning' | 'danger' | 'info' | 'plan';
export interface DocPropertyChoice {
    value: string;
    color: DocPropertyColor;
    title?: string | null;
}
export interface DocPropertyDef {
    key: string;
    kind: DocPropertyKind;
    title?: string | null;
    choices?: DocPropertyChoice[] | null;
    autoUpdate?: boolean;
    required?: boolean;
}
export interface DocTypeSchema {
    id: string;
    title: string;
    folders: string[];
    match?: string | null;
    badgeProperty?: string | null;
    properties: DocPropertyDef[];
}
export interface DocsScopeInfo {
    selected: DocsScope;
    folderCandidates: DocFolderCandidate[];
    rootFileCandidates: DocRootFileCandidate[];
    typeGroups: DocTypeGroup[];
    defaults: DocsScope;
    documents: DocOption[];
    home: string | null;
    scopeSource: 'file' | 'project';
    scopeFileError?: string | null;
    docTypes?: DocTypeSchema[] | null;
    docTypesError?: string | null;
    propertyColors?: DocPropertyColor[] | null;
}
export type DossierStatus = 'active' | 'degraded' | 'archived';
export type DossierOrigin = 'own' | 'imported';
export interface DossierEntry {
    id: string;
    commitSha: string;
    commitSubject: string;
    committedAt: string;
    sessionId: string | null;
    taskId: string | null;
    personaId: string | null;
    files: string[];
    symbols: string[];
    why: string;
    decisions: string[];
    rejected: string[];
    pitfalls: string[];
    invariants: string[];
    status: DossierStatus;
    summaryFailed: boolean;
    linksStale: boolean;
    origin: DossierOrigin;
    importedAuthor: string | null;
    capturedAt?: string | null;
}
export interface BoardItem {
    taskId: string;
    title: string;
    projectId?: string;
    sessionId?: string;
    column: 'queue' | 'working' | 'waiting' | 'done';
    sessionStatus: string;
    personaId?: string;
    currentToolName?: string;
    startedAt?: string;
    permissionPending: boolean;
}
export interface SystemPromptPart {
    kind: 'builtin' | 'user' | 'auto';
    content: string;
}
export interface ProjectGroup {
    id: string;
    name: string;
    color: string;
    order: number;
    icon?: string | null;
    charter?: string | null;
}
export type Sphere = ProjectGroup;
export interface SphereOverview {
    sphere: {
        id: string;
        name: string;
        color: string;
        icon: string | null;
        charter: string | null;
    };
    projects: {
        id: string;
        name: string;
        capabilities: ProjectCapabilitiesView;
    }[];
    team: {
        id: string;
        name: string;
        handle: string;
        role: string | null;
    }[];
    memory: {
        count: number;
    };
    openTasks: {
        id: string;
        title: string;
        projectId: string | null;
        status: 'todo' | 'inProgress' | 'done';
        priority: 'low' | 'medium' | 'high' | 'urgent';
        dueDate: string | null;
    }[];
}
export type SphereMemoryType = 'decision' | 'convention' | 'fact' | 'glossary';
export interface SphereMemoryPromotion {
    projectId: string;
    entryId: string;
    at: string;
}
export interface SphereMemoryEntry {
    id: string;
    scopeId: string;
    text: string;
    type: SphereMemoryType;
    salience: number;
    source: string;
    createdAt: string;
    promotedFrom: SphereMemoryPromotion | null;
}
export interface ProjectMemoryEntry {
    id: string;
    projectId: string;
    text: string;
    type: string;
    salience: number;
    source: string;
    createdAt: string;
}
export interface SphereMemoryProjectShelf {
    projectId: string;
    projectName: string;
    entries: ProjectMemoryEntry[];
}
export interface SphereMemoryResponse {
    sphere: SphereMemoryEntry[];
    projects: SphereMemoryProjectShelf[];
    maxEntries: number;
}
export interface DeleteSphereResponse {
    deletedMemory: number;
}
export type ModelTierValue = 'strong' | 'medium' | 'weak';
export type TaskStatus = 'todo' | 'inProgress' | 'done';
export type TaskPriority = 'urgent' | 'high' | 'medium' | 'low';
export type TaskAssignee = 'me' | 'claude';
export type TaskRecurrenceType = 'none' | 'daily' | 'weekly' | 'monthly' | 'yearly';
export interface TaskRecurrence {
    type: TaskRecurrenceType;
    interval: number;
    weekdays?: number[];
    until?: string;
}
export interface TaskSubtask {
    id: string;
    title: string;
    isDone: boolean;
}
export interface Task {
    id: string;
    projectId?: string;
    ownerId?: string;
    title: string;
    description: string;
    status: TaskStatus;
    columnId?: string;
    priority: TaskPriority;
    dueDate?: string;
    dueTime?: string;
    reminderMinutes?: number;
    reminderSentAt?: string;
    assignee?: TaskAssignee;
    recurrence?: TaskRecurrence;
    seriesId?: string;
    linkedSessionId?: string;
    personaId?: string;
    modelTier?: ModelTierValue;
    executionExpiresAfterMinutes?: number | null;
    claudeStartedAt?: string;
    claudeResult?: 'success' | 'error';
    resultMarkdown?: string;
    linkedFiles: string[];
    subtasks: TaskSubtask[];
    labels: string[];
    sourceNoteId?: string;
    sourceNoteLine?: number;
    order: number;
    createdAt: string;
    updatedAt: string;
    completedAt?: string;
    kind?: TaskKind;
    repro?: DefectRepro | null;
    verification?: TaskVerification | null;
    outcome?: DefectOutcome | null;
    executorStoppedAt?: string;
    executorStopReason?: string;
    /** Исполнитель ждёт устройство локального проекта (ADR-016): с какого момента и почему */
    deviceWaitSince?: string | null;
    deviceWaitReason?: string | null;
    droppedByHumanAt?: string;
    occurrenceOf?: string;
    virtual?: boolean;
}
export interface SearchHit {
    type: 'note' | 'task';
    id: string;
    title: string;
    context: string;
    snippet: string;
    score?: number | null;
    url: string;
}
export interface ExtractedTaskCandidate {
    title: string;
    due?: string | null;
    priority?: TaskPriority | null;
}
export interface ExtractTasksResponse {
    projectId?: string | null;
    tasks: ExtractedTaskCandidate[];
}
export interface NoteTask {
    line: number;
    text: string;
    done: boolean;
    due?: string | null;
    taskId?: string | null;
    taskStatus?: string | null;
}
export interface CreateTaskDto {
    id?: string;
    title: string;
    description?: string;
    status?: TaskStatus;
    columnId?: string;
    priority?: TaskPriority;
    dueDate?: string;
    dueTime?: string;
    reminderMinutes?: number;
    assignee?: TaskAssignee;
    recurrence?: TaskRecurrence;
    linkedSessionId?: string;
    personaId?: string;
    modelTier?: ModelTierValue;
    executionExpiresAfterMinutes?: number;
    resultMarkdown?: string;
    linkedFiles?: string[];
    subtasks?: {
        title: string;
    }[];
    labels?: string[];
    kind?: TaskKind;
    repro?: DefectRepro | null;
    verification?: TaskVerification | null;
}
export type TaskKind = 'task' | 'defect';
export interface DefectRepro {
    steps: string;
    expected?: string;
    actual?: string;
}
export interface TaskVerification {
    verifiedAt: string;
    personaId?: string | null;
    notes?: string | null;
}
export type DefectOutcome = 'closedWithoutCheck';
export interface UpdateTaskDto {
    title?: string;
    description?: string;
    status?: TaskStatus;
    priority?: TaskPriority;
    dueDate?: string;
    dueTime?: string;
    reminderMinutes?: number;
    assignee?: TaskAssignee;
    recurrence?: TaskRecurrence;
    linkedSessionId?: string;
    personaId?: string;
    modelTier?: ModelTierValue | '';
    executionExpiresAfterMinutes?: number;
    resultMarkdown?: string;
    linkedFiles?: string[];
    subtasks?: TaskSubtask[];
    labels?: string[];
    order?: number;
    columnId?: string;
    projectId?: string;
    kind?: TaskKind;
    repro?: DefectRepro | null;
    verification?: TaskVerification | null;
    outcome?: DefectOutcome | null;
}
export type ClaudeBilling = 'subscription' | 'api';
export interface AppSettings {
    defaultProjectsPath: string;
    claudeBilling?: ClaudeBilling;
    dailyBriefingEnabled?: boolean;
    modelTierStrong?: string | null;
    modelTierMedium?: string | null;
    modelTierWeak?: string | null;
}
export interface FeatureFlagDefinition {
    key: string;
    title: string;
    description: string;
    default: boolean;
    stage: 'dev' | 'beta' | 'stable';
}
export interface SessionImageChat {
    currentPath: string | null;
    draftFolder?: string | null;
    lineage: string[];
}
export interface ImageLaunchFields {
    by: string;
    prompt: string;
    provider: string;
    model: string;
    count: number;
    estimate?: {
        amount: number | null;
        unit: string;
        approx: boolean;
        source: string;
    } | null;
    jobId: string;
    timestamp?: number;
}
export interface ModuleRecordFields {
    module: string;
    recordType: string;
    data?: unknown;
    fallback: string;
    timestamp?: number;
}
export interface ImageSnapshotMark {
    revision: string;
    attached: boolean;
}
export interface Session {
    id: string;
    projectId?: string;
    personaId?: string;
    automationRuleId?: string | null;
    participants?: string[] | null;
    ownerId?: string;
    isPinned?: boolean;
    claudeSessionId?: string;
    imageChat?: SessionImageChat | null;
    mode: Mode;
    autoAllowTools?: string[];
    status: 'starting' | 'working' | 'active' | 'waiting' | 'orphaned' | 'finished' | 'error';
    lastMessage?: string;
    messageCount: number;
    createdAt: string;
    updatedAt: string;
    name?: string;
    topic?: string | null;
    model?: string;
    provider?: string;
    effort?: string;
    agentName?: string;
    expiresAfterMinutes?: number | null;
    expiryAnchor?: string | null;
    lastReadAt?: string | null;
    lastUserMessageAt?: string | null;
    notificationsMuted?: boolean;
    voiceMode?: boolean;
    voiceStyle?: string | null;
    workLoop?: {
        promise: string;
        iteration: number;
        maxIterations: number;
        phase: 'working' | 'waiting' | 'verifying';
    } | null;
    teamImplement?: SessionTeamImplement | null;
    worktreePath?: string | null;
    worktreeBranch?: string | null;
    taskExecution?: boolean;
    taskId?: string | null;
    parentSessionId?: string | null;
    origin: 'manual' | 'task' | 'automation';
    taskDone?: boolean;
    tags?: string[];
    excludeFromDossiers?: boolean;
    onboardingKind?: 'user' | 'project' | null;
    isArchived?: boolean;
    archivedAt?: string | null;
    archiveSummary?: string | null;
    archiveSummaryAt?: string | null;
    summaryNoteId?: string | null;
}
export type SessionContextType = 'file' | 'url' | 'task';
export interface SessionContextEntry {
    type: SessionContextType;
    id: string;
    title?: string | null;
    missing?: boolean;
}
export interface HomeSessionInfo {
    id: string;
    projectId?: string | null;
    projectName?: string | null;
    name?: string | null;
    status: Session['status'];
    lastMessage?: string | null;
    personaId?: string | null;
    taskId?: string | null;
    taskDone?: boolean;
    messageCount: number;
    updatedAt: string;
    origin: Session['origin'];
    isPinned?: boolean;
    tags?: string[];
    participants?: string[] | null;
    expiresAfterMinutes?: number | null;
    lastReadAt?: string | null;
}
export interface HomeSummaryResponse {
    active: HomeSessionInfo[];
    recent: HomeSessionInfo[];
}
export interface FileEntry {
    name: string;
    path: string;
    isDirectory: boolean;
    size?: number;
    modified: string;
    isModified: boolean;
    isNew?: boolean;
    synced?: 'direct' | 'inherited' | null;
}
export interface GitFileChange {
    path: string;
    status: string;
    oldPath?: string | null;
    added?: number | null;
    deleted?: number | null;
}
export interface ChangedBySession {
    sessionId: string;
    name: string;
    external: boolean;
}
export interface GitStatus {
    isRepo: boolean;
    branch: string | null;
    upstream: string | null;
    ahead: number;
    behind: number;
    detached: boolean;
    staged: GitFileChange[];
    unstaged: GitFileChange[];
    untracked: GitFileChange[];
    isWorktree: boolean;
}
export interface GitBranchInfo {
    name: string;
    current: boolean;
    upstream: string | null;
}
export interface GitCommitPromptInfo {
    global: string | null;
    projectOverride: string | null;
    useProject: boolean;
    effective: string;
    default: string;
}
export interface GitLogEntry {
    sha: string;
    shortSha: string;
    author: string;
    email: string;
    date: string;
    subject: string;
}
export interface GitCommitDetail {
    sha: string;
    shortSha: string;
    author: string;
    email: string;
    date: string;
    subject: string;
    body: string;
    files: GitFileChange[];
}
export interface GitStashEntry {
    index: number;
    message: string;
    date: string;
}
export interface GitBlameLine {
    line: number;
    sha: string;
    shortSha: string;
    author: string;
    date: string;
    content: string;
}
export interface GitRemoteInfo {
    serverEnabled: boolean;
    remoteUrl: string | null;
    originUrl: string | null;
    htmlUrl: string | null;
    autoCommit: boolean;
    autoPush: boolean;
}
export interface SyncMark {
    path: string;
    isDirectory: boolean;
}
export interface WorkflowAgentInfo {
    id: string;
    prompt: string;
    summary?: string;
    tools?: {
        name: string;
        count: number;
    }[];
    files?: string[];
    isDone?: boolean;
    agentType?: string;
}
export interface WorkflowAgentBlock {
    kind: 'text' | 'thinking' | 'tool_use' | 'structured';
    text?: string;
    toolName?: string;
    toolId?: string;
    toolInput?: unknown;
    toolResult?: string;
    isError?: boolean;
}
export interface StoredWorkflowProgress {
    kind: 'workflow_progress';
    toolUseId: string;
    isDone: boolean;
    aborted?: boolean;
    agents?: WorkflowAgentInfo[];
}
export type ServerMessage = {
    sessionId: string;
} & ({
    type: 'session_started';
    claudeSessionId: string;
    isResume: boolean;
    model: string;
    mode: string;
    cwd?: string;
    toolCount?: number;
    mcpServers?: {
        name: string;
        status: string;
    }[];
    turnWorktree?: {
        path: string;
        name: string;
    } | null;
} | {
    type: 'text_delta';
    text: string;
} | {
    type: 'user_message';
    text: string;
    attachedPaths?: string[];
    senderPersonaId?: string;
    auto?: boolean;
    senderOrigin?: string;
    senderChatName?: string;
    staffNote?: string;
    timestamp?: number;
    delegationTaskId?: string;
    imageSnapshot?: ImageSnapshotMark | null;
} | {
    type: 'guest_text';
    text: string;
    personaId: string;
    timestamp?: number;
    delegationTaskId?: string;
} | {
    type: 'thinking_delta';
    text: string;
} | {
    type: 'agent_text';
    parentToolUseId: string;
    text: string;
} | {
    type: 'agent_thinking';
    parentToolUseId: string;
    text: string;
} | {
    type: 'tool_use';
    id: string;
    name: string;
    input: unknown;
    parentToolUseId?: string;
    startedAt?: number;
} | {
    type: 'tool_input_delta';
    toolUseId: string;
    partialJson: string;
} | {
    type: 'tool_started';
    toolUseId: string;
    startedAt?: number;
} | ({
    type: 'tool_progress';
    toolUseId: string;
} & ToolProgress) | {
    type: 'tool_result';
    toolUseId: string;
    content: string;
    isError: boolean;
    finishedAt?: number;
} | {
    type: 'bg_agent_done';
    toolUseIds: string[];
    aborted?: boolean;
} | {
    type: 'bg_agents_presence';
    active: boolean;
    command: boolean;
} | {
    type: 'permission_request';
    requestId: string;
    toolName: string;
    toolInput: unknown;
} | {
    type: 'ask_question';
    toolUseId: string;
    input: unknown;
} | {
    type: 'plan_review';
    requestId: string;
    plan: string;
} | {
    type: 'interaction_resolved';
    kind: 'question' | 'permission' | 'plan';
    id: string;
    answers?: Record<string, string | string[]>;
    decision?: 'allowed' | 'denied' | 'always';
    approved?: boolean;
    feedback?: string;
} | {
    type: 'file_changed';
    path: string;
    added: number;
    removed: number;
    external?: boolean;
} | {
    type: 'result';
    subtype: string;
    durationMs: number;
    numTurns: number;
    usage?: UsageInfo;
    totalCostUsd?: number;
    apiErrorStatus?: string;
    permissionDenials?: string[];
    contextTokens?: number;
    usageModel?: string;
    durationApiMs?: number;
} | {
    type: 'fal_cost';
    requestId: string;
    endpointId?: string;
    costUsd: number;
    outputUnits?: number;
    unitPrice?: number;
} | {
    type: 'glif_cost';
    jobId: string;
    outputType?: string;
    mediaCount: number;
    credits?: number;
    model?: string;
} | {
    type: 'error';
    text: string;
    details?: string;
    action?: string | null;
} | {
    type: 'rate_limit';
    limitType: string;
    resetsAt?: string;
    status?: string;
    utilization?: number;
    isUsingOverage?: boolean;
    overageStatus?: string;
    overageResetsAt?: string;
} | {
    type: 'compact_boundary';
    trigger: string;
    preTokens?: number;
    postTokens?: number;
} | {
    type: 'context_pruned';
    kind: 'prune' | 'compact_cloud';
    eventId?: string;
    tokensBefore: number;
    tokensAfter: number;
    blocks: number;
    resultBlocks: number;
    inputBlocks: number;
    thinkingBlocks: number;
    prefillSeconds?: number;
    cacheReadTokens?: number;
    promptTokens?: number;
} | {
    type: 'compact_status';
    status?: string;
    compactResult?: string;
    compactError?: string;
} | ({
    type: 'image_launch';
} & ImageLaunchFields) | {
    type: 'image_file_moved';
    from: string;
    to: string;
    timestamp?: number;
} | ({
    type: 'module_record';
} & ModuleRecordFields) | {
    type: 'truncated';
} | {
    type: 'redacted_thinking';
} | {
    type: 'exited';
} | {
    type: 'status_changed';
    status: string;
    lastMessage?: string;
    messageCount?: number;
} | {
    type: 'chat_deleted';
} | {
    type: 'chat_archived';
    archived: boolean;
} | {
    type: 'chat_renamed';
    name: string;
    topic?: string | null;
} | {
    type: 'watchdogs_changed';
    sessions: string[];
    projects: string[];
} | {
    type: 'context_updated';
    entries: SessionContextEntry[];
} | {
    type: 'workflow_progress';
    toolUseId: string;
    agents: WorkflowAgentInfo[];
    isDone: boolean;
} | {
    type: 'task_changed';
    action: 'created' | 'updated' | 'deleted';
    task: Task;
} | {
    type: 'notes_changed';
    action: 'created' | 'updated' | 'deleted';
    noteId?: string;
} | {
    type: 'knowledge_changed';
    action: string;
    datasetId?: string;
} | {
    type: 'personas_changed';
    action: 'created' | 'updated' | 'deleted' | 'memory' | 'default';
    personaId?: string;
} | {
    type: 'image_backfilled';
    kind: 'project-icon' | 'persona-avatar';
    entityId: string;
} | {
    type: 'onboarding_completed';
    kind: 'user' | 'project';
    personaId: string;
    projectId?: string | null;
} | {
    type: 'team_memory_changed';
    action: 'added' | 'updated' | 'removed';
    projectId: string;
    entryId?: string;
} | {
    type: 'git_status_changed';
    projectId: string;
} | {
    type: 'git_turn_commit';
    sessionId: string;
    projectId: string;
    sha: string;
    subject: string;
} | {
    type: 'speaker_changed';
    personaId: string;
    label: string;
} | {
    type: 'provider_switched';
    provider: string;
    model?: string;
    label?: string;
    auto?: boolean;
    reason?: string;
    errorDetails?: string;
} | {
    type: 'provider_limit';
    resetsAt?: string;
    providers: ProviderFallbackOption[];
} | {
    type: 'work_loop';
    active: boolean;
    iteration: number;
    maxIterations: number;
    phase: string | null;
    waitingReason?: string | null;
    waitingTicks?: number;
} | {
    type: 'work_loop_stopped';
    reason: string;
    text: string;
} | {
    type: 'team_implement';
    active: boolean;
    stage: TeamImplementStage | null;
    waveNumber: number;
    autoWaves: boolean;
    coordinatorPersonaId: string | null;
    plannerPersonaId: string | null;
    executorPersonaIds: string[] | null;
    budget: TeamImplementBudget | null;
    planCardId: string | null;
    plannedWaves?: number;
    coordinatorNoCode?: boolean;
    stopped?: boolean;
    modeLocked?: boolean;
    planVersion?: number;
} | {
    type: 'team_plan';
    planId: string;
    plan: TeamPlan;
    resolved: boolean;
    approved: boolean | null;
    supersededBy?: number | null;
} | {
    type: 'team_escalation';
    escalationId: string;
    kind: TeamEscalationKind;
    title: string;
    details: string;
    actions: TeamEscalationAction[];
    taskId: string | null;
    taskTitle?: string | null;
    wave: number;
    resolved: boolean;
    chosenActionId: string | null;
    personaId?: string | null;
    resolutionNote?: string | null;
} | {
    type: 'team_planning';
    start: boolean;
    success: boolean;
    subtaskCount: number;
    waveCount: number;
    elapsedMs: number;
    route: string | null;
    failure: string | null;
    promptChars: number;
    responseChars: number;
    personaId?: string | null;
} | {
    type: 'team_wave_pulse';
    stage: 'wave' | 'checking';
    waveNumber: number;
    plannedWaves: number;
    tasksActive: number;
    tasksTotal: number;
    lastActivityAt: string;
    quietSeconds: number;
    liveness: TeamWaveLiveness;
} | {
    type: 'preview_status';
    status: string;
    port?: number;
    error?: string;
    serviceId?: string;
} | {
    type: 'preview_log';
    serviceId: string;
    data: string;
} | {
    type: 'notification';
    title: string;
    body: string;
    url?: string;
    kind: 'reminder' | 'claude' | 'info' | 'success' | 'meeting';
    notificationId?: string;
    notifType?: string;
    projectId?: string;
    sessionId?: string;
    taskId?: string;
    source?: string;
    tag?: string;
    personaId?: string;
    personaName?: string;
    personaRole?: string;
    personaColor?: string;
    personaHasAvatar?: boolean;
    projectName?: string;
    sticky?: boolean;
} | {
    type: 'recall_manifest';
    items: RecallItem[];
} | {
    type: 'pending_messages';
    items: {
        id: string;
        text: string;
        senderPersonaId?: string;
        senderOrigin?: string;
        enqueuedAt: string;
        senderChatName?: string;
        kind?: 'user' | 'agent';
        attachedPaths?: string[];
        mode?: string | null;
        waitingForDevice?: boolean;
    }[];
} | {
    type: 'composer_restore';
    text?: string | null;
    attachedPaths?: string[] | null;
    mode?: string | null;
} | {
    type: 'prompt_suggestion';
    text: string;
} | {
    type: 'hands_status';
    state: string;
    deviceName?: string | null;
    reason?: string | null;
} | {
    type: 'hands_notice';
    text: string;
} | {
    type: 'prompt_snapshot';
    snapshotId: string;
    applied: boolean;
    inheritedFromId?: string;
});
export interface PromptSection {
    key: string;
    title: string;
    text: string;
    kind: 'system' | 'turn' | 'cli-file' | 'truncated';
    size?: number | null;
    stable?: boolean;
    group?: string;
}
export interface CliSkill {
    name: string;
    description?: string | null;
    source: string;
}
export interface CliLayer {
    tools?: string[] | null;
    mcpServers?: {
        name: string;
        status: string;
    }[] | null;
    files?: PromptSection[] | null;
    skills?: CliSkill[] | null;
    transcriptBytes?: number | null;
    transcriptMessages?: number | null;
}
export interface PromptSnapshot {
    id: string;
    createdAt: number;
    applied: boolean;
    inheritedFromId?: string | null;
    sections: PromptSection[];
    cliArgs: string[];
    mcpServers: string[];
    model?: string | null;
    mode?: string | null;
    cliLayer?: CliLayer | null;
    truncatedSections?: PromptSection[] | null;
}
export interface UsageInfo {
    inputTokens: number;
    outputTokens: number;
    cacheReadTokens: number;
    cacheCreationTokens: number;
}
export interface ProjectService {
    id: string;
    name: string;
    source: string;
    command: string;
    args: string[];
    cwd: string | null;
    suggestedPort: number | null;
    autoPort: boolean;
    saved: boolean;
    status: string;
    runningPort: number | null;
    error: string | null;
    members?: string[] | null;
    origin?: {
        sessionId: string;
        label: string;
    } | null;
}
export interface ExternalPreviewLink {
    jti: string;
    projectId: string;
    serviceId: string;
    issuedAt: string;
    expiresAt: string;
    projectName?: string | null;
}
export interface ExternalLinkIssued {
    url: string;
    jti: string;
    expiresAt: string;
    evicted: {
        jti: string;
        projectId: string;
        serviceId: string;
    }[];
}
export interface LaunchConfigEntry {
    name?: string;
    runtimeExecutable?: string;
    runtimeArgs?: string[];
    program?: string;
    args?: string[];
    port?: number;
    autoPort?: boolean;
    cwd?: string;
    env?: Record<string, string>;
}
export interface ProviderFallbackOption {
    key: string;
    displayName: string;
    model: string;
    kind?: 'subscription' | 'provider';
    tierLabel?: string | null;
    utilization?: number | null;
}
export interface RateLimitInfo {
    limitType: string;
    utilization?: number;
    resetsAt?: string;
    status?: string;
    isUsingOverage?: boolean;
    overageStatus?: string;
    overageResetsAt?: string;
}
export type ProviderQuotaWindowUnit = 'percent' | 'count' | 'consumed' | 'alive';
export interface ProviderQuotaWindow {
    label: string;
    value: string;
    resetsAt: string | null;
    unit: ProviderQuotaWindowUnit;
}
export interface ProviderBalanceInfo {
    available: boolean;
    currency: string;
    totalBalance: string;
    asOf?: string;
    resetsAt?: string | null;
    windows?: ProviderQuotaWindow[] | null;
    note?: string | null;
    trackHistory?: boolean;
    grantedBalance?: number | null;
    planLabel?: string | null;
    keyLimit?: {
        remaining: number;
        total: number;
    } | null;
    spend?: {
        daily: number;
        weekly: number;
        monthly: number;
    } | null;
    health?: {
        requests24h?: number | null;
        successRate?: number | null;
        platformsAlive?: number | null;
        platformsTotal?: number | null;
    } | null;
}
export interface UsageSnapshot {
    timestamp: string;
    limitType: string;
    subscriptionKey?: string;
    utilization?: number;
    status?: string;
    isUsingOverage?: boolean;
    resetsAt?: string;
    overageStatus?: string;
    overageResetsAt?: string;
    source?: 'turn' | 'probe' | 'oauth' | null;
}
export interface PlanInfo {
    subscriptionType?: string;
    rateLimitTier?: string;
    label: string;
}
export interface UsageResponse {
    snapshots: UsageSnapshot[];
    plan?: PlanInfo;
    subscriptions?: Record<string, SubscriptionUsage>;
    rotationThreshold?: number;
    weeklyThreshold?: number;
    routingTarget?: string;
    providers?: Record<string, UsageSnapshot[]>;
    pollStatuses?: Record<string, string>;
    ollama?: OllamaUsageInfo;
}
export interface OllamaUsageInfo {
    enabled: boolean;
    model?: string | null;
    baseUrl?: string | null;
    provider?: string;
    actions: OllamaActionInfo[];
}
export interface PlacePresetRef {
    id: string;
    name: string | null;
    steps: string[];
}
export interface OllamaActionInfo {
    key: string;
    title: string;
    group: string;
    routedToOllama: boolean;
    source?: 'default' | 'config' | 'admin';
    route?: string;
    preset?: PlacePresetRef | null;
    requiresStrong?: boolean;
    agentic?: boolean;
}
export interface SubscriptionUsage {
    snapshots: UsageSnapshot[];
    name?: string;
    inRotation?: boolean;
    utilization?: number;
    exhausted?: boolean;
    tier?: string;
    loginCommand?: string | null;
    supportsOpus?: boolean | null;
    supports1M?: boolean | null;
    weeklyUtilization?: number;
    unavailableModels?: ModelUnavailableMark[];
}
export interface ModelUnavailableMark {
    model: string;
    reason: string;
    until: string;
}
export interface FalModelSpend {
    endpointId: string;
    cost: number;
}
export interface FalDaySpend {
    date: string;
    cost: number;
}
export interface FalUsageSummary {
    days: number;
    total: number;
    byModel: FalModelSpend[];
    series: FalDaySpend[];
}
export interface FalAccountResponse {
    enabled: boolean;
    balance?: number | null;
    currency?: string | null;
    usage?: FalUsageSummary | null;
}
export interface GlifSpend {
    last24h?: number;
    last7d?: number;
    last30d?: number;
}
export interface GlifAccountResponse {
    enabled: boolean;
    plan?: string | null;
    planStatus?: string | null;
    balance?: number | null;
    currency?: string | null;
    spend?: GlifSpend | null;
}
export interface YandexAccountResponse {
    enabled: boolean;
    error?: string | null;
    account?: {
        id: string;
        name?: string | null;
        balance?: string | null;
        currency?: string | null;
        active: boolean;
    } | null;
    asOf?: string | null;
    balanceHidden: boolean;
    days: number;
    spend?: {
        total: number;
        requests: number;
    } | null;
}
export interface ImageModelCaps {
    async?: boolean;
    maxCount?: number;
    aspectRatios?: string[];
}
export interface ImageModelInfo {
    id: string;
    displayName: string;
    description?: string | null;
    caps?: ImageModelCaps | null;
}
export interface ImageGeneratorInfo {
    key: string;
    displayName: string;
    enabled: boolean;
    models: ImageModelInfo[];
}
export interface ImagePlaceSettings {
    key: string;
    title: string;
    provider: string;
    activeProvider: string | null;
    enabled: boolean;
    model: string | null;
    models: Record<string, string | null>;
}
export interface ImageGenerationSettings {
    providers: ImageGeneratorInfo[];
    places: ImagePlaceSettings[];
}
export interface ImagePlacePatch {
    provider?: string;
    models?: Record<string, string | null>;
}
export interface ImageGenerationPatch {
    places: Record<string, ImagePlacePatch>;
}
export interface WorkLoopState {
    active: boolean;
    iteration: number;
    maxIterations: number;
    phase: 'working' | 'waiting' | 'verifying' | null;
    waitingReason?: string | null;
    waitingTicks?: number;
}
export type TeamImplementStage = 'interview' | 'planning' | 'confirming' | 'wave' | 'awaitingDecision' | 'checking' | 'idle';
export interface TeamImplementBudget {
    tasksUsed: number;
    wavesUsed: number;
    runsUsed: number;
    retriesUsed: number;
    wakeupsUsed: number;
    maxTasks: number;
    maxWaves: number;
    maxRuns: number;
    maxRetries: number;
    maxWakeups: number;
    maxWavesAfter: number;
    maxTasksAfter: number;
}
export interface SessionTeamImplement {
    stage: TeamImplementStage;
    waveNumber: number;
    plannedWaves: number;
    autoWaves: boolean;
    stopped: boolean;
    coordinatorPersonaId?: string | null;
    plannerPersonaId?: string | null;
    executorPersonaIds: string[];
    budget: TeamImplementBudget;
    coordinatorNoCode: boolean;
    planCardId?: string | null;
    modeLocked?: boolean;
    savedMode?: Mode | null;
    planVersion: number;
}
export interface TeamImplementState extends SessionTeamImplement {
    active: boolean;
}
export type TeamWaveLiveness = 'alive' | 'quiet' | 'stalled' | 'dead';
export interface TeamWavePulse {
    stage: 'wave' | 'checking';
    waveNumber: number;
    plannedWaves: number;
    tasksActive: number;
    tasksTotal: number;
    lastActivityAt: string;
    quietSeconds: number;
    liveness: TeamWaveLiveness;
}
export type TeamWaveTaskStatus = 'todo' | 'inProgress' | 'done';
export interface TeamWaveTask {
    id: string;
    title: string;
    executorPersonaId: string | null;
    status: TeamWaveTaskStatus;
    updatedAt: string;
    startedAt: string | null;
}
export interface TeamWaveSnapshot extends TeamWavePulse {
    tasks: TeamWaveTask[];
    thresholds: {
        quietMinutes: number;
        stalledMinutes: number;
    };
}
export type TeamTaskRestartOutcome = 'reissued' | 'escalated' | 'failed';
export interface TeamTaskRestartResponse {
    outcome: TeamTaskRestartOutcome;
    message: string;
}
export interface TeamWaveRestartResponse {
    requiresConfirm: boolean;
    liveTasks?: string[];
    reissued: number;
    escalated: number;
    failed: number;
    message: string;
}
export interface TeamTurnRestartResponse {
    outcome: 'restarted' | 'fresh';
    resumed: boolean;
    message: string;
}
export type TeamTurnRestartErrorCode = 'transcript_damaged';
export interface TeamPlanSubtask {
    id: string;
    title: string;
    goal: string;
    executorPersonaId?: string | null;
    executorRationale: string;
    files: string[];
    wave: number;
    doneCriteria: string;
}
export interface TeamPlan {
    id: string;
    request: string;
    summary: string;
    plannerPersonaId?: string | null;
    approved?: boolean | null;
    createdAt: string;
    waveCount: number;
    executorCount: number;
    subtasks: TeamPlanSubtask[];
    version: number;
    assumptions: string[];
    changes: string[];
    intent?: string;
    planFilePath?: string | null;
}
export type TeamPlanDecision = 'run' | 'reassign' | 'cancel' | 'edit';
export type TeamEscalationKind = 'blocker' | 'taskFailed' | 'planDeviation' | 'checkFailed' | 'productDecision' | 'budgetExhausted' | 'waveStalled' | 'waveGate' | 'stopped' | 'waveAdded' | 'needsClarification';
export interface TeamEscalationAction {
    id: string;
    label: string;
}
export interface TeamEscalation {
    id: string;
    kind: TeamEscalationKind;
    title: string;
    details: string;
    actions: TeamEscalationAction[];
    taskId?: string | null;
    taskTitle?: string | null;
    wave: number;
    createdAt?: string;
    resolved: boolean;
    chosenActionId?: string | null;
    personaId?: string | null;
    resolutionNote?: string | null;
}
export interface ToolProgress {
    stage?: string | null;
    label?: string | null;
    percent?: number | null;
    exact?: boolean | null;
    queuePosition?: number | null;
    etaSeconds?: number | null;
    lastTool?: string | null;
    lastToolKind?: string | null;
    toolUses?: number | null;
    durationMs?: number | null;
    stages?: ToolStage[] | null;
    totals?: ToolRunTotals | null;
}
export interface ToolStage {
    stage: string;
    label: string;
    startedAt: number;
    endedAt?: number | null;
    failed?: boolean | null;
}
export interface ToolRunTotals {
    passed: number;
    failed: number;
    total: number;
    failures?: ToolRunFailure[] | null;
}
export interface ToolRunFailure {
    name: string;
    message?: string | null;
}
export type ChatItem = {
    kind: 'user_message';
    text: string;
    attachedPaths?: string[];
    viaAgent?: boolean;
    senderPersonaId?: string;
    systemDirective?: boolean;
    auto?: boolean;
    senderOrigin?: string;
    senderChatName?: string;
    staffNote?: string;
    ts?: number;
    promptSnapshotId?: string;
    delegationTaskId?: string;
    imageSnapshot?: ImageSnapshotMark;
} | {
    kind: 'session_started';
    model: string;
    mode: string;
    cwd?: string;
    toolCount?: number;
    mcpServers?: {
        name: string;
        status: string;
    }[];
    turnWorktree?: {
        path: string;
        name: string;
    } | null;
} | {
    kind: 'text';
    text: string;
    personaId?: string;
    parentToolUseId?: string;
    model?: string;
    ts?: number;
    delegationTaskId?: string;
} | {
    kind: 'thinking';
    text: string;
    expanded: boolean;
    parentToolUseId?: string;
} | {
    kind: 'tool_use';
    id: string;
    name: string;
    input: unknown;
    result?: string;
    isError?: boolean;
    parentToolUseId?: string;
    streamingArg?: string;
    workflowAgents?: WorkflowAgentInfo[];
    workflowDone?: boolean;
    workflowAborted?: boolean;
    bgDone?: boolean;
    bgAborted?: boolean;
    startedAt?: number;
    finishedAt?: number;
    started?: boolean;
    appearedAt?: number;
    progress?: ToolProgress;
    progressAt?: number;
    stages?: ToolStage[];
    totals?: ToolRunTotals;
} | {
    kind: 'permission_request';
    requestId: string;
    toolName: string;
    toolInput: unknown;
    resolved: boolean;
    decision?: 'allowed' | 'denied' | 'always';
} | {
    kind: 'ask_question';
    toolUseId: string;
    input: unknown;
    resolved: boolean;
    answers?: Record<string, string | string[]>;
} | {
    kind: 'plan_review';
    requestId: string;
    plan: string;
    resolved: boolean;
    approved?: boolean;
    feedback?: string;
} | {
    kind: 'team_plan';
    planId: string;
    plan: TeamPlan;
    resolved: boolean;
    approved?: boolean | null;
    supersededBy?: number | null;
} | {
    kind: 'team_escalation';
    escalationId: string;
    escalation: TeamEscalation;
} | {
    kind: 'team_planning_done';
    subtaskCount: number;
    waveCount: number;
    elapsedMs: number;
} | {
    kind: 'file_changed';
    path: string;
    added: number;
    removed: number;
    external?: boolean;
} | {
    kind: 'result';
    subtype: string;
    durationMs: number;
    numTurns: number;
    usage?: UsageInfo;
    totalCostUsd?: number;
    apiErrorStatus?: string;
    permissionDenials?: string[];
    contextTokens?: number;
    durationApiMs?: number;
} | {
    kind: 'fal_cost';
    requestId: string;
    endpointId?: string;
    costUsd: number;
    outputUnits?: number;
    unitPrice?: number;
} | {
    kind: 'glif_cost';
    jobId: string;
    outputType?: string;
    mediaCount: number;
    credits?: number;
    model?: string;
} | {
    kind: 'rate_limit';
    limitType: string;
    resetsAt?: string;
    status?: string;
} | {
    kind: 'compact_boundary';
    trigger: string;
    preTokens?: number;
    postTokens?: number;
} | {
    kind: 'context_pruned';
    pruneKind: 'prune' | 'compact_cloud';
    eventId?: string;
    tokensBefore: number;
    tokensAfter: number;
    blocks: number;
    resultBlocks: number;
    inputBlocks: number;
    thinkingBlocks: number;
    prefillSeconds?: number;
    cacheReadTokens?: number;
    promptTokens?: number;
} | ({
    kind: 'image_launch';
} & ImageLaunchFields) | {
    kind: 'image_file_moved';
    from: string;
    to: string;
    timestamp?: number;
} | ({
    kind: 'module_record';
} & ModuleRecordFields) | {
    kind: 'truncated';
} | {
    kind: 'redacted_thinking';
} | {
    kind: 'interrupted';
    ts?: number;
} | {
    kind: 'resumed';
} | {
    kind: 'session_ended';
} | {
    kind: 'companion_switched';
    label: string;
    personaId?: string;
} | {
    kind: 'provider_switched';
    label: string;
} | {
    kind: 'model_switched';
    model: string;
    previousModel: string;
    reason?: string;
    rawLabel?: string;
    details?: string;
} | {
    kind: 'branched_from';
    sourceSessionId: string;
    sourceName: string;
    ts?: number;
} | {
    kind: 'provider_limit';
    resetsAt?: string;
    providers: ProviderFallbackOption[];
    resolved?: boolean;
} | {
    kind: 'git_turn_commit';
    projectId: string;
    sha: string;
    subject: string;
} | {
    kind: 'work_loop_stopped';
    reason: string;
    text: string;
} | {
    kind: 'hands_notice';
    text: string;
    tone: 'neutral' | 'warning';
} | {
    kind: 'error';
    text: string;
    canRetry?: boolean;
    details?: string;
    action?: string | null;
    ts?: number;
} | {
    kind: 'error_group';
    date: number;
    items: Extract<ChatItem, {
        kind: 'error';
    }>[];
};
export interface SkillInfo {
    name: string;
    description: string;
    argumentHint?: string;
    filePath: string;
}
export interface AgentInfo {
    name: string;
    description: string;
    color?: string;
    tools: string[];
    permissionMode?: string;
    fileName: string;
}
export interface SkillsData {
    skills: SkillInfo[];
    projectSkills: SkillInfo[];
    agents: AgentInfo[];
    workflows?: SkillInfo[];
    plugins?: SkillInfo[];
}
export interface RegistrySkill {
    source: string;
    skill: string;
    description: string | null;
    installs: number | null;
    url: string;
}
export interface SkillSuggestion {
    skill: RegistrySkill;
    reason: string;
}
export interface GeneratedSkill {
    name: string;
    description: string;
    body: string;
}
export interface Me {
    userId: string;
    username: string;
    displayName?: string | null;
    role: string;
    featureFlags?: Record<string, boolean>;
    subsystems?: string[];
    contextThresholds?: {
        warnPct: number;
        dangerPct: number;
    } | null;
    defaultPersonaId?: string | null;
    needsOnboarding?: boolean;
    onboardingSessionId?: string | null;
    executionEnvironment?: 'local' | 'container';
}
export interface AuthState {
    serverUrl: string;
    token: string;
    username: string;
    displayName?: string;
    role?: string;
    id?: string;
}
/** Как обращаться к пользователю: имя из профиля, иначе логин. */
export declare function displayNameOf(auth: {
    displayName?: string;
    username: string;
}): string;
export interface UserProfile {
    id: string;
    username: string;
    displayName?: string;
    role: 'admin' | 'user';
    createdAt: string;
    executionEnvironment?: 'local' | 'container';
}
export interface ChangelogItem {
    type: 'feature' | 'improvement' | 'fix' | 'other';
    area: string;
    emoji: string;
    title: string;
    benefit: string;
    score: number;
    scoreReason: string;
    authors: string[];
    projects: string[];
}
export interface ChangelogGeneration {
    durationMs: number;
    inputTokens: number;
    cacheCreationTokens: number;
    cacheReadTokens: number;
    outputTokens: number;
    costUsd: number | null;
    model: string | null;
    generatedAt: string;
}
export interface ChangelogDay {
    date: string;
    items: ChangelogItem[];
    degraded?: boolean;
    degradedReason?: string;
    generation?: ChangelogGeneration | null;
}
export interface DaySummaryStub {
    date: string;
    commitCount: number;
    cached: boolean;
}
export interface ChangelogStatus {
    configured: boolean;
    mode: string;
    detail: string | null;
}
export interface NoteAnnotationInfo {
    docScope: string;
    docPath: string;
    status: 'open' | 'resolved';
    blockId?: string | null;
    anchorQuote?: string | null;
    anchorHeading?: string | null;
    isReply?: boolean;
    docMissing?: boolean;
}
export interface NoteReply {
    noteId: string;
    title: string;
    excerpt: string;
    createdAt: string;
    tags: string[];
}
export interface DocAnnotation {
    noteId: string;
    title: string;
    status: 'open' | 'resolved';
    state: 'exact' | 'changed' | 'orphan';
    start: number;
    end: number;
    blockId?: string | null;
    anchorHeading?: string | null;
    quote: string;
    excerpt: string;
    tags: string[];
    updatedAt: string;
    replies: number;
}
export interface NoteSummary {
    id: string;
    title: string;
    source: string;
    sourceLabel: string;
    path: string;
    tags: string[];
    createdAt: string;
    updatedAt: string;
    expiresAt?: string;
    sourceSessionId?: string;
    annotation?: NoteAnnotationInfo | null;
    file?: string | null;
    fileMissing?: boolean;
}
export interface NoteLink {
    targetId: string;
    targetTitle: string;
    resolved: boolean;
}
export interface NoteBacklink {
    sourceId: string;
    sourceTitle: string;
    source: string;
    sourceLabel: string;
    snippet: string;
}
export interface NoteDetail {
    id: string;
    title: string;
    source: string;
    sourceLabel: string;
    path: string;
    content: string;
    tags: string[];
    links: NoteLink[];
    backlinks: NoteBacklink[];
    unlinkedMentions: NoteBacklink[];
    createdAt: string;
    updatedAt: string;
    expiresAt?: string;
    sourceSessionId?: string;
    annotation?: NoteAnnotationInfo | null;
    file?: string | null;
    fileMissing?: boolean;
}
export interface NoteGraphNode {
    id: string;
    title: string;
    source: string;
    sourceLabel: string;
    degree: number;
    ghost: boolean;
    tags?: string[];
    kind?: 'comment' | 'reply' | 'doc' | null;
    status?: 'open' | 'resolved' | null;
}
export interface NoteTemplate {
    id: string;
    title: string;
}
export interface NoteSemanticHit {
    id: string;
    title: string;
    source: string;
    sourceLabel: string;
    score: number;
    snippet: string;
}
export interface NoteGraphEdge {
    source: string;
    target: string;
}
export interface NoteGraph {
    nodes: NoteGraphNode[];
    edges: NoteGraphEdge[];
}
export interface NoteSource {
    key: string;
    label: string;
}
export interface NoteFolder {
    source: string;
    path: string;
}
export interface CreateNoteDto {
    title: string;
    content?: string;
    source?: string;
    templateId?: string;
    folder?: string;
    expiresAfterMinutes?: number | null;
    sourceSessionId?: string;
    file?: string;
}
export interface UpdateNoteDto {
    title?: string;
    content?: string;
}
export type KnowledgeVisibility = 'personal' | 'public';
export interface KnowledgeBaseSummary {
    id: string;
    title: string;
    type: string;
    visibility: KnowledgeVisibility;
    documentCount: number;
    createdAt: string | null;
    deletable: boolean;
    description?: string | null;
}
export interface KnowledgeDocument {
    id: string;
    name: string;
    indexingStatus: string;
    error?: string | null;
}
export interface KnowledgeBaseDetail extends KnowledgeBaseSummary {
    documents: KnowledgeDocument[];
}
export interface KnowledgeSearchHit {
    score: number;
    content: string;
    documentName: string;
}
export interface CreateKnowledgeBaseDto {
    title: string;
    description?: string;
    visibility: KnowledgeVisibility;
}
export interface KnowledgeListResponse {
    configured: boolean;
    items: KnowledgeBaseSummary[];
}
export interface KnowledgeSegment {
    position: number;
    content: string;
    wordCount: number;
}
export interface KnowledgeDocumentContent {
    id: string;
    segments: KnowledgeSegment[];
}
export type PersonaScope = 'global' | 'project' | 'sphere';
export type PersonaAccess = 'full' | 'readOnly' | 'custom';
export type PersonaSpecialty = 'none' | 'analyst' | 'planner' | 'reviewer' | 'executor' | 'secretary' | 'coordinator' | 'mentor' | 'designer' | 'consultant' | 'librarian' | 'tester' | 'backendExecutor' | 'frontendExecutor' | 'devopsExecutor';
export interface SpecialtyTemplate {
    access: PersonaAccess;
    tools: string[] | null;
    disallowedTools: string[] | null;
}
export interface SpecialtyCatalogEntry {
    key: PersonaSpecialty;
    label: string;
    description?: string;
    executorFamily: boolean;
    template: SpecialtyTemplate | null;
    icon?: string | null;
    color?: string | null;
}
export interface ModelRoutePreset {
    id: string;
    name: string;
    description?: string | null;
    steps: string[];
}
export interface ScopedPreset extends ModelRoutePreset {
    scope: 'owner' | 'global' | 'user';
}
export interface SpecialtyTemplateSettings {
    access: PersonaAccess;
    tools?: string[] | null;
    disallowedTools?: string[] | null;
    tierStrong?: string | null;
    tierMedium?: string | null;
    tierWeak?: string | null;
    defaultTier?: ModelTierValue | null;
    promptSections?: SpecialtyPromptSection[] | null;
    defaultBindings?: SpecialtyDefaultBinding[] | null;
}
export interface SpecialtyPromptSection {
    id: string;
    enabled: boolean;
    text?: string | null;
}
export interface SpecialtyDefaultBinding {
    type: PersonaBindingType;
    mode: PersonaBindingMode;
    condition: string;
    skillName?: string | null;
}
export interface SpecialtyPromptSectionsCatalog {
    textLimit: number;
    sections: SpecialtyPromptSectionMeta[];
    specialties: Record<string, SpecialtyPromptSectionsForSpecialty>;
}
export interface SpecialtyPromptSectionMeta {
    id: string;
    label: string;
    description: string;
}
export interface SpecialtyPromptSectionsForSpecialty {
    sections: {
        id: string;
        enabled: boolean;
        text: string;
    }[];
    defaultBindings: SpecialtyDefaultBinding[];
}
export interface ApplyDefaultBindingsResult {
    persona: Persona;
    applied: number;
}
export interface SpecialtySettingsLayer {
    specialties: Record<string, SpecialtyTemplateSettings>;
    defaultSpecialty?: SpecialtyTemplateSettings | null;
    presets: ModelRoutePreset[];
}
export interface SpecialtySettingsResponse {
    version: number;
    maxSubstitutions?: number;
    global: SpecialtySettingsLayer;
    presets: ScopedPreset[];
}
export interface ResetResult {
    specialties: number;
    shadowed: string[];
    personas: number;
    personaNames: string[];
}
export interface SubagentModelChip {
    kind: string;
    label: string;
    hint: string;
}
export interface ModelPreviewResponse {
    model: string | null;
    source: 'persona-model' | 'persona-cell' | 'specialty-cell' | 'owner-slot' | 'instance-slot' | 'place-assignment' | 'explicit' | null;
    tier: ModelTierValue | null;
    tierOrigin: 'task' | 'persona' | 'specialty' | 'place' | null;
    preset: {
        id: string;
        name: string | null;
        steps: string[];
        broken: boolean;
    } | null;
    chain: string[];
    subagentChip?: SubagentModelChip | null;
}
export interface PresetUsageEntry {
    kind: 'instance-slot' | 'owner-slot' | 'specialty-cell' | 'persona-model' | 'persona-cell' | 'place';
    label: string;
    ownerId?: string | null;
}
export interface PresetUsageResponse {
    presetId: string;
    count: number;
    usages: PresetUsageEntry[];
}
export interface AvatarCropStateDto {
    scale: number;
    offsetX: number;
    offsetY: number;
}
export interface PersonaAvatar {
    kind: 'initials' | 'image';
    color?: string;
    imageFile?: string;
    originalFile?: string | null;
    crop?: AvatarCropStateDto | null;
}
export interface PersonaContract {
    character?: string;
    tone?: string;
    mustDo?: string[];
    mustNot?: string[];
    outputFormat?: string;
    speechExamples?: string[];
    instructions?: string;
}
export interface TtsVoiceInfo {
    voice: string;
    label: string;
    gender: 'female' | 'male';
    roles: string[];
}
export interface TtsVoicesResponse {
    configured: boolean;
    voices: TtsVoiceInfo[];
}
export interface PersonaVoice {
    voice?: string;
    role?: string;
    speed?: number | null;
}
export interface Persona {
    id: string;
    ownerId: string;
    name: string;
    role?: string;
    handle: string;
    handleCustom?: boolean;
    description?: string;
    systemPrompt?: string;
    contract?: PersonaContract | null;
    model?: string;
    modelTier?: ModelTierValue;
    tierStrong?: string | null;
    tierMedium?: string | null;
    tierWeak?: string | null;
    effort?: string;
    scope: PersonaScope;
    projectId?: string;
    sphereId?: string;
    avatar: PersonaAvatar;
    voice?: PersonaVoice | null;
    greeting?: string;
    memoryEnabled: boolean;
    lightContext?: boolean | null;
    specialty?: PersonaSpecialty;
    tools?: string[] | null;
    access?: PersonaAccess;
    disallowedTools?: string[] | null;
    templateKey?: string | null;
    bindings?: PersonaBinding[] | null;
    allProjectsAccess?: boolean;
    automationRules?: PersonaAutomationRule[] | null;
    createdAt: string;
    updatedAt: string;
}
export interface PantheonTemplate {
    key: string;
    role: string;
    name: string;
    description: string;
    contract: PersonaContract;
    greeting: string;
    color: string;
    tools?: string[] | null;
    access?: PersonaAccess;
    model?: string;
    effort?: string;
    specialty?: PersonaSpecialty;
    connectedPersonaId?: string | null;
}
export type AutomationTriggerType = 'timer' | 'file' | 'note' | 'gitCommit' | 'taskStatus' | 'mention';
export type AutomationActionWeight = 'gate' | 'work';
export interface AutomationTrigger {
    type: AutomationTriggerType;
    args?: Record<string, unknown> | null;
}
export interface AutomationCondition {
    onlyIf?: string | null;
    quietFrom?: string | null;
    quietTo?: string | null;
    minIntervalMinutes?: number | null;
}
export interface AutomationAction {
    weight: AutomationActionWeight;
    instruction: string;
    rememberInHistory: boolean;
    expiresAfterMinutes?: number | null;
}
export interface PersonaAutomationRule {
    id: string;
    enabled: boolean;
    name: string;
    trigger: AutomationTrigger;
    condition?: AutomationCondition | null;
    action: AutomationAction;
    createdAt: string;
    updatedAt: string;
}
export interface AutomationRuleDto {
    enabled?: boolean;
    name?: string;
    triggerType?: AutomationTriggerType;
    triggerArgs?: Record<string, unknown> | null;
    conditionOnlyIf?: string | null;
    quietFrom?: string | null;
    quietTo?: string | null;
    minIntervalMinutes?: number | null;
    actionWeight?: AutomationActionWeight;
    actionInstruction?: string;
    rememberInHistory?: boolean;
    actionExpiresAfterMinutes?: number | null;
}
export type PersonaBindingType = 'project' | 'projectPath' | 'knowledge' | 'notes' | 'tool' | 'skill' | 'projectPersonas' | 'projectTasks';
export type PersonaBindingMode = 'auto' | 'always' | 'off';
export interface PersonaBinding {
    id: string;
    type: PersonaBindingType;
    target: string;
    path?: string | null;
    condition: string;
    mode: PersonaBindingMode;
    createdAt: string;
    updatedAt: string;
}
export interface PersonaBindingDto {
    type: PersonaBindingType;
    target: string;
    path?: string | null;
    condition?: string;
    mode?: PersonaBindingMode;
}
export interface BindingTarget {
    id: string;
    label: string;
    hint?: string | null;
    meta?: string | null;
    defaultEnabled?: boolean | null;
    defaultOrigin?: 'settings' | 'role' | null;
    status?: string | null;
}
export interface CreatePersonaDto {
    name: string;
    role?: string;
    description?: string;
    systemPrompt?: string;
    handle?: string;
    contract?: PersonaContract;
    model?: string;
    modelTier?: ModelTierValue | '';
    effort?: string;
    scope?: PersonaScope;
    projectId?: string;
    sphereId?: string;
    color?: string;
    greeting?: string;
    memoryEnabled?: boolean;
    lightContext?: boolean;
    tools?: string[];
    access?: PersonaAccess;
    disallowedTools?: string[];
    specialty?: PersonaSpecialty;
    allProjectsAccess?: boolean;
    autoAvatar?: boolean;
    avatarPrompt?: string;
}
export type UpdatePersonaDto = Partial<CreatePersonaDto>;
export type PersonaMemoryType = 'semantic' | 'episodic' | 'procedural';
export interface PersonaMemoryEntry {
    id: string;
    personaId: string;
    type: PersonaMemoryType;
    text: string;
    tags?: string[];
    salience: number;
    sourceSessionId?: string;
    pending?: boolean;
    createdAt: string;
    lastAccessedAt: string;
}
export interface PersonaWorkingFocus {
    what: string;
    status: string;
    nextStep?: string;
    sourceSessionId?: string;
    updatedAt: string;
}
export interface PersonaMemoryHit {
    id: string;
    type: PersonaMemoryType;
    text: string;
    tags: string[];
    score: number;
    createdAt: string;
}
export type TeamMemoryType = 'fact' | 'decision' | 'convention' | 'glossary';
export type TeamMemorySource = 'manual' | 'autoTurn' | 'autoMeeting';
export interface TeamMemoryEntry {
    id: string;
    ownerId: string;
    projectId: string;
    text: string;
    type: TeamMemoryType;
    tags?: string[];
    salience: number;
    source: TeamMemorySource;
    sourceSessionId?: string;
    createdAt: string;
}
export interface RecallItem {
    kind: string;
    ref?: string | null;
    title: string;
    snippet?: string | null;
}
export interface TeamMemberDraft {
    name?: string;
    role?: string;
    description?: string;
    character?: string;
    tone?: string;
    specialty?: string;
    color?: string;
    greeting?: string;
    avatarPrompt?: string;
}
export type NotificationKind = 'reminder' | 'claude' | 'info' | 'success' | 'meeting' | 'alert';
export interface NotificationItem {
    id: string;
    kind: NotificationKind;
    type: string;
    title: string;
    body: string;
    url?: string;
    projectId?: string;
    sessionId?: string;
    taskId?: string;
    source?: string;
    tag?: string;
    personaId?: string;
    personaName?: string;
    personaRole?: string;
    personaColor?: string;
    personaHasAvatar?: boolean;
    projectName?: string;
    sticky?: boolean;
    isRead: boolean;
    createdAt: string;
    readAt?: string;
}
export interface NotificationListResponse {
    items: NotificationItem[];
    totalCount: number;
    unreadCount: number;
}
export interface CreateNotificationRequest {
    kind: NotificationKind;
    type: string;
    title: string;
    body: string;
    url?: string;
    projectId?: string;
    sessionId?: string;
    taskId?: string;
    source?: string;
    tag?: string;
}
export interface SpendTokens {
    input: number;
    output: number;
    cacheRead: number;
    cacheCreation: number;
    total: number;
}
export interface SpendDayPoint {
    date: string;
    aggregated: boolean;
    total: number;
    bySource: Record<string, number>;
    falGenerations: number;
}
export interface SpendCardRow {
    key: string;
    name: string | null;
    meta: string | null;
    tokens: SpendTokens;
    turns: number;
    falGenerations: number;
}
export interface SpendOverviewResponse {
    from: string;
    to: string;
    detailDays: number;
    windowStart: string;
    allUsers: boolean;
    totals: SpendTokens;
    turns: number;
    falGenerations: number;
    costUsd?: number | null;
    rub?: {
        total: number;
        requests: number;
    } | null;
    byDay: SpendDayPoint[];
    cards: {
        users?: SpendCardRow[];
        projects: SpendCardRow[];
        models: SpendCardRow[];
        chats: SpendCardRow[];
        personas: SpendCardRow[];
        sources: SpendCardRow[];
        providers: SpendCardRow[];
    };
    topTurns: SpendTurnDto[];
}
export interface SpendPivotNode extends SpendCardRow {
    hasDetail: boolean;
}
export interface SpendPivotResponse {
    nodes: SpendPivotNode[];
}
export interface SpendTaskPromptRun {
    at: string;
    taskId: string;
    ownerId: string;
    projectId: string | null;
    sessionId: string | null;
    personaId: string | null;
    totalChars: number;
    totalTokensEst: number;
    task: number;
    expected: number;
    tools: number;
    rules: number;
    restrictions: number;
    delegation: number;
    omo: number;
    context: number;
    notes: number;
}
export interface SpendTaskPromptResponse {
    runs: SpendTaskPromptRun[];
}
export interface SpendTurnDto {
    id: string;
    timestamp: string;
    ownerId: string;
    userName: string | null;
    sessionId: string | null;
    chatName: string | null;
    projectId: string | null;
    projectName: string | null;
    taskId: string | null;
    taskTitle: string | null;
    personaId: string | null;
    personaName: string | null;
    provider: string | null;
    model: string | null;
    source: string;
    label: string | null;
    tokens: SpendTokens;
    generations: number;
    durationMs: number | null;
    own: boolean;
}
export interface SpendTurnsResponse {
    total: number;
    windowClamped: boolean;
    items: SpendTurnDto[];
}
export interface SpendTurnDetailResponse {
    turn: SpendTurnDto;
    neighbors: {
        id: string;
        timestamp: string;
        total: number;
    }[];
}
export interface SpendWidgetResponse {
    today: SpendTokens;
    week: SpendTokens;
    todayTurns: number;
    weekTurns: number;
    weekFalGenerations: number;
    byDay: SpendDayPoint[];
}
export interface SpendBadgeResponse {
    sessionId: string;
    total: SpendTokens;
    turns: number;
    lastTurn: SpendTurnDto | null;
}
export interface BackupSummary {
    chats: number;
    personas: number;
    tasks: number;
    notes: number;
    projects: number;
    users: number;
    totalBytes: number;
}
export interface BackupEntry {
    fileName: string;
    createdAt: string;
    size: number;
    summary: BackupSummary;
}
export interface BackupStatus {
    enabled: boolean;
    effectivePath: string;
    secretsPath: string;
    intervalHours: number;
    lastSuccessAt: string | null;
    lastError: string | null;
    lastAttemptAt: string | null;
    recent: BackupEntry[];
}
export type ReaderErrorCode = 'invalid-url' | 'local-address' | 'dns-failed' | 'unreachable' | 'tls-invalid' | 'timeout' | 'auth-required' | 'blocked-by-site' | 'not-found' | 'server-error' | 'too-many-redirects' | 'not-a-page' | 'pdf' | 'too-large' | 'not-readable';
export interface ReaderPage {
    title: string;
    siteName?: string | null;
    byline?: string | null;
    markdown: string;
}
export interface ReaderError {
    code: ReaderErrorCode;
    httpStatus?: number | null;
}
export interface McpValue {
    name: string;
    value?: string | null;
    hasValue: boolean;
    secret: boolean;
}
export interface McpAuth {
    kind: string;
    headerName?: string | null;
    hasSecret: boolean;
    authorizationServer?: string | null;
    clientId?: string | null;
    expiresAt?: string | null;
    hasTokens: boolean;
}
export interface McpServerStatus {
    status: string;
    observedAt: string;
    source: string;
    sessionId?: string | null;
    error?: string | null;
}
export interface McpServer {
    id: string;
    key: string;
    toolKey: string;
    label: string;
    description?: string | null;
    transport: string;
    command?: string | null;
    args: string[];
    env: McpValue[];
    url?: string | null;
    headers: McpValue[];
    auth: McpAuth;
    enabled: boolean;
    alwaysLoad: boolean;
    allowReadOnlyPersonas: boolean;
    allowOutsideProjects: boolean;
    source: string;
    authVersion: number;
    createdAt: string;
    updatedAt: string;
    status?: McpServerStatus | null;
    catalogRef?: McpCatalogRef | null;
    group?: McpBuiltinGroup | null;
}
export interface McpCatalogRef {
    name: string;
    version: string;
    publishedAt?: string | null;
}
export type McpBuiltinGroup = 'product' | 'integration' | 'persona-memory' | 'external';
export interface McpBuiltinServer {
    key: string;
    group: McpBuiltinGroup;
    status: McpServerStatus | null;
}
export interface McpValueInput {
    name: string;
    value?: string | null;
    secret?: boolean;
}
export interface McpServerUpsert {
    key?: string;
    label?: string;
    description?: string | null;
    transport?: string;
    command?: string | null;
    args?: string[];
    env?: McpValueInput[];
    url?: string | null;
    headers?: McpValueInput[];
    auth?: {
        kind?: string;
        headerName?: string | null;
        secret?: string | null;
        clientId?: string | null;
    };
    enabled?: boolean;
    alwaysLoad?: boolean;
    allowReadOnlyPersonas?: boolean;
    allowOutsideProjects?: boolean;
    catalogRef?: McpCatalogRef | null;
}
export interface McpProbeResult {
    ok: boolean;
    status: string;
    serverName?: string | null;
    toolCount?: number | null;
    toolNames?: string[] | null;
    error?: string | null;
}
export interface McpCatalogField {
    target: 'env' | 'header' | 'url' | 'args';
    name: string;
    description?: string | null;
    required: boolean;
    secret: boolean;
    default?: string | null;
}
export interface McpCatalogPrefill {
    key: string;
    label?: string | null;
    description?: string | null;
    transport: McpCatalogTransport;
    command?: string;
    args: string[];
    url?: string;
    fields: McpCatalogField[];
}
export type McpCatalogTransport = 'stdio' | 'http';
export interface McpCatalogServer {
    name: string;
    title?: string | null;
    description?: string | null;
    version?: string | null;
    repositoryUrl?: string | null;
    publishedAt?: string | null;
    status?: string | null;
    connectable: boolean;
    notice?: string | null;
    isLatest?: boolean;
    prefill?: McpCatalogPrefill | null;
}
export interface McpCatalogSearchResult {
    items: McpCatalogServer[];
    nextCursor?: string | null;
    error?: string | null;
}
export type McpCatalogFieldDraft = McpCatalogField;
export interface McpServerCatalogDraft {
    source: McpCatalogServer;
    catalogRef: McpCatalogRef;
    fieldsDraft: McpCatalogField[];
}
export interface McpCatalogRevision {
    name: string;
    status?: 'active' | 'deprecated' | 'deleted' | null;
    deprecated?: boolean | null;
    latestVersion?: string | null;
    checkFailed?: boolean | null;
    error?: string | null;
    hasNewerVersion?: boolean | null;
}
export interface McpCatalogRevisionResult {
    items: McpCatalogRevision[];
    checkFailed?: boolean | null;
    error?: string | null;
}
export interface McpOAuthStartResult {
    authorizeUrl: string;
    state: string;
    redirectUri: string;
}
export interface McpOAuthCompleteResult {
    ok: boolean;
    key: string;
}
export interface McpToolStat {
    tool: string;
    calls: number;
    failures: number;
    avgMs: number;
}
export interface McpCallFailure {
    at: string;
    tool: string;
    sessionId?: string | null;
    path: string;
    statusCode: number;
    elapsedMs: number;
}
export interface McpCallsResponse {
    tools: McpToolStat[];
    recentFailures: McpCallFailure[];
}
export type IncidentStatus = 'ok' | 'notConfigured' | 'unavailable';
export interface IncidentSummary {
    fingerprint: string;
    title: string;
    description?: string | null;
    severity?: string | null;
    environment?: string | null;
    startedAt?: string | null;
    resolvedAt?: string | null;
    isFiring: boolean;
    isMuted?: boolean;
}
export interface IncidentBreakdownRow {
    label: string;
    count: number;
}
export interface IncidentTurn {
    traceId: string;
    chatId?: string | null;
    at?: string | null;
    model?: string | null;
    provider?: string | null;
    errorType?: string | null;
    durationMs: number;
}
export interface IncidentLogLine {
    at?: string | null;
    severity: string;
    message: string;
}
export interface IncidentChat {
    chatId: string;
    projectId?: string | null;
    title?: string | null;
    failures: number;
    totalTokens: number;
    mcpFailures: string[];
    fromAlert?: boolean;
}
export interface IncidentDossier {
    incident: IncidentSummary;
    status: IncidentStatus;
    from: string;
    to: string;
    isForeignEnvironment: boolean;
    breakdownTag: string;
    breakdown: IncidentBreakdownRow[];
    turns: IncidentTurn[];
    turnsTotal: number;
    logs: IncidentLogLine[];
    logsTotal: number;
    chats: IncidentChat[];
    rulePath?: string | null;
}
export interface IncidentListResponse {
    status: IncidentStatus;
    items: IncidentSummary[];
}
export interface QuickPhrase {
    text: string;
    group?: string | null;
}
export type VideoProviderKind = 'live' | 'feed';
export type VideoError = 'not-configured' | 'needs-auth' | 'quota-exceeded' | 'unreachable';
export interface VideoProviderInfo {
    key: string;
    title: string;
    kind: VideoProviderKind;
    connected: boolean;
    needsAuth: boolean;
}
export interface VideoChannel {
    id: string;
    provider: string;
    title: string;
    embeddable: boolean;
    embedUrl: string | null;
    externalUrl: string | null;
    coverUrl: string | null;
    nowPlaying: string | null;
}
export interface VideoItem {
    id: string;
    provider: string;
    title: string;
    channelId: string;
    channelTitle: string;
    thumbnailUrl: string | null;
    publishedAt: string | null;
    embedUrl: string;
    externalUrl: string;
}
export interface VideoChannelsResponse {
    error: VideoError | null;
    channels: VideoChannel[];
}
export interface VideoFavoritesResponse {
    configured: boolean;
    keys: string[];
}
export interface VideoFeedResponse {
    error: VideoError | null;
    items: VideoItem[];
}
export interface DesktopDevice {
    id: string;
    name: string;
    fingerprint: string;
    clientVersion?: string | null;
    createdAt: string;
    lastSeenAt?: string | null;
    revoked: boolean;
    revokedAt?: string | null;
    tokenVersion: number;
    online?: boolean;
    platform?: string | null;
    agentVersion?: string | null;
    agentUpdate?: DeviceAgentUpdate | null;
    harnessReady?: boolean;
    harnessProblem?: string | null;
    capabilities?: {
        exec?: boolean;
        files?: boolean;
    } | null;
}
export interface DeviceAgentUpdate {
    state: 'idle' | 'downloading' | 'waiting-idle' | 'failed';
    targetVersion?: string | null;
    reason?: string | null;
}
export interface DesktopPairingCode {
    code: string;
    expiresAt: string;
    attemptsLeft: number;
    hostFingerprint?: string;
}
export interface LocalHandsChatStatus {
    state: string | null;
    reason: string | null;
    deviceName: string | null;
}
export type PlanMapGenre = 'feature' | 'fix' | 'choice' | 'audit' | 'framework' | 'operation';
export type PlanMapBlockType = 'step' | 'decision' | 'fork' | 'risk' | 'criterion' | 'boundary';
export type PlanMapBlockFlag = 'blocking' | 'needs-decision' | 'expands-scope' | 'has-cost' | 'review-fix';
export interface PlanMapNumber {
    value: string;
    label: string;
}
export interface PlanMapBlock {
    id: string;
    title: string;
    type: PlanMapBlockType;
    flags: PlanMapBlockFlag[];
    anchor: string;
    anchorIndex: number;
    dependsOn: string[];
}
export interface PlanMap {
    genre: PlanMapGenre;
    oneLine: string;
    numbers: PlanMapNumber[];
    blocks: PlanMapBlock[];
}
export type MapHygieneKind = 'dead-link' | 'dead-import' | 'root-relative' | 'long-section';
export type MapHygieneSeverity = 'high' | 'medium' | 'low';
export interface MapHygieneLinkFinding {
    path: string;
    section: string | null;
    line: number;
    target: string;
    reason: 'notFound' | 'rootRelativeOnly' | 'absolute' | 'outsideProject' | 'deadImport';
    candidates: string[];
    anchorText: string;
}
export interface MapHygieneSection {
    title: string;
    startLine: number;
    lines: number;
    docsRefs: number;
    firstLine: string;
}
export interface MapHygieneApplyPatch {
    before: string;
    after: string;
    anchorText: string;
}
export interface MapHygieneSuggestionAnchor {
    line: number;
    heading: string | null;
}
export interface MapHygieneSuggestion {
    id: string;
    kind: MapHygieneKind;
    severity: MapHygieneSeverity;
    fact: string;
    modelSays: string | null;
    anchor: MapHygieneSuggestionAnchor;
    savingLines: number;
    apply: MapHygieneApplyPatch | null;
}
export interface MapHygieneBudget {
    recommendedLines: number;
    overBudget: boolean;
}
export interface MapHygieneFileRef {
    path: string;
    lines: number;
    bytes: number;
}
export interface MapHygieneReport {
    path: string;
    exists: boolean;
    baseSha: string | null;
    lines: number;
    bytes: number;
    approxTokens: number;
    approxTokensNote: string;
    expandedLines: number;
    importCount: number;
    expansionTruncated: boolean;
    importDepthExceeded: boolean;
    budget: MapHygieneBudget;
    sectionCount: number;
    longSectionCount: number;
    sections: MapHygieneSection[];
    deadLinkCount: number;
    deadLinks: MapHygieneLinkFinding[];
    deadImportCount: number;
    deadImports: MapHygieneLinkFinding[];
    rootRelativeCount: number;
    rootRelativeLinks: MapHygieneLinkFinding[];
    skippedCount: number;
    skipped: MapHygieneLinkFinding[];
    unclosedFence: boolean;
    suggestions: MapHygieneSuggestion[];
    modelNote?: string | null;
    secondMap: MapHygieneFileRef | null;
    nestedMapCount: number;
    nestedMaps: MapHygieneFileRef[];
    localMap: MapHygieneFileRef | null;
    walkTruncated: boolean;
    truncated: boolean;
}
export type MapHygieneApplyFailureReason = 'anchorNotFound' | 'ambiguousAnchor' | 'unknownId' | 'notApplicable';
export interface MapHygieneApplyFailure {
    id: string;
    reason: MapHygieneApplyFailureReason;
}
export interface MapHygieneApplyResult {
    applied: string[];
    failed: MapHygieneApplyFailure[];
    newSha: string | null;
    scan: MapHygieneReport;
}
