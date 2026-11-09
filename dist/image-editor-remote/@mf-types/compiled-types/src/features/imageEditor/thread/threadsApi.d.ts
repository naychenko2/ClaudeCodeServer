export interface ImageThreadStack {
    stackId: string;
    steps: string[];
    forkedFromStepId: string | null;
    old: boolean;
}
export interface ImageThreadVersion {
    id: string;
    number: number;
    jobId: string | null;
    variant: number | null;
    baseVersionId: string | null;
    baseStepId: string | null;
    steps: string[];
    currentStepId: string | null;
    createdAt: string;
    similarity?: number | null;
    mostSimilar?: boolean;
}
export type ImageThreadLaunchStatus = 'running' | 'done' | 'failed' | 'cancelled' | 'interrupted';
export interface ImageThreadLaunch {
    jobId: string;
    baseVersionId: string | null;
    baseStepId: string | null;
    at: string;
    status: ImageThreadLaunchStatus;
    initiator: 'human' | 'agent';
    prompt: string | null;
}
export interface ImageThreadSettings {
    provider: string | null;
    model: string | null;
    count: number;
    matchSourceSize: boolean;
}
export interface ImageThread {
    id: string;
    file: string | null;
    lineage: string[];
    draftFolder: string | null;
    stacks: ImageThreadStack[];
    currentStackId: string | null;
    currentStepId: string | null;
    settings: ImageThreadSettings | null;
    pendingJobId: string | null;
    interruptedJobId?: string | null;
    createdAt: string;
    versions?: ImageThreadVersion[];
    currentVersionId?: string | null;
    launches?: ImageThreadLaunch[];
}
export interface ImageThreadEvent {
    at: string;
    kind: string;
    text: string;
    threadId?: string | null;
    jobId?: string | null;
}
export interface ImageThreadsState {
    focus: string | null;
    revision: number;
    threads: ImageThread[];
    events?: ImageThreadEvent[];
}
export declare const EMPTY_THREADS: ImageThreadsState;
export interface ImageThreadChangedEvent {
    type: 'image_thread_changed';
    sessionId: string;
    projectId: string;
    revision: number;
    state: ImageThreadsState;
}
export type ImageThreadTake = {
    jobId: string;
    variant: number;
} | {
    stepId: string;
};
export interface ThreadCropResponse {
    width: number;
    height: number;
    aspect: string;
    versionId: string | null;
    state: ImageThreadsState;
}
export declare const threadsApi: {
    crop: (projectId: string, sessionId: string, threadId: string, body: {
        aspect: string;
        anchor: string;
        versionId?: string | null;
        dryRun?: boolean;
    }) => Promise<ThreadCropResponse>;
    get: (projectId: string, sessionId: string) => Promise<ImageThreadsState>;
    create: (projectId: string, sessionId: string, body: {
        file?: string;
        draftFolder?: string;
        revision: number;
    }) => Promise<ImageThreadsState>;
    remove: (projectId: string, sessionId: string, threadId: string, revision: number) => Promise<ImageThreadsState>;
    focus: (projectId: string, sessionId: string, threadId: string | null, revision: number) => Promise<ImageThreadsState>;
    take: (projectId: string, sessionId: string, threadId: string, what: ImageThreadTake, revision: number) => Promise<ImageThreadsState>;
    dismiss: (projectId: string, sessionId: string, threadId: string, jobId: string, revision: number) => Promise<ImageThreadsState>;
    rollback: (projectId: string, sessionId: string, threadId: string, stepId: string | null, revision: number) => Promise<ImageThreadsState>;
    current: (projectId: string, sessionId: string, threadId: string, versionId: string, revision: number) => Promise<ImageThreadsState>;
    addStep: (projectId: string, sessionId: string, threadId: string, stepId: string, revision: number) => Promise<ImageThreadsState>;
    settings: (projectId: string, sessionId: string, threadId: string, settings: ImageThreadSettings, revision: number) => Promise<ImageThreadsState>;
    subscribe: (handler: (e: ImageThreadChangedEvent) => void) => () => void;
};
export declare function conflictState(e: unknown): ImageThreadsState | null;
