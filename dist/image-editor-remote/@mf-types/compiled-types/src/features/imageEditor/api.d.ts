import type { MediaFormatState, MediaFormatWish } from 'aihome_shell/kit';
export type ImageEditOp = 'generate' | 'edit' | 'inpaint' | 'outpaint' | 'removeBackground' | 'upscale' | 'enhanceFaces';
export type EditMode = 'auto' | 'fast' | 'precise' | 'photoreal';
export type MaskSupport = 'none' | 'native' | 'asReference';
export type ReferenceRole = 'character' | 'style' | 'object';
export type EditOutcome = 'ok' | 'failed' | 'insufficientCredits' | 'rejected' | 'cancelled' | 'unavailable';
export type EditStage = 'queued' | 'running' | 'downloading';
export type PriceUnit = 'usd' | 'credits' | 'free';
export type ImageEditJobStatus = 'queued' | 'running' | 'downloading' | 'completed' | 'failed' | 'cancelled' | 'interrupted';
export declare const AUTO_MODEL = "auto";
export interface ImageEditCaps {
    ops: ImageEditOp[];
    mask: MaskSupport;
    maxReferences: number;
    maxCount: number;
    faceByReferences: boolean;
    separateMaskPass?: boolean;
    maxInputSide?: number | null;
    maxInputMegapixels?: number | null;
    maxInputMb?: number | null;
}
export interface ImageEditPriceHint {
    amount: number;
    unit: string;
    per: string;
}
export interface ImageEditModel {
    id: string;
    label: string;
    modes?: EditMode[] | null;
    caps?: ImageEditCaps | null;
    priceHint?: ImageEditPriceHint | null;
}
export interface ImageEditProvider {
    key: string;
    label: string;
    priceUnit: string;
    models: ImageEditModel[];
    available?: boolean;
}
export interface ImageEditCatalog {
    default: {
        provider: string | null;
        model: string;
    };
    providers: ImageEditProvider[];
    limits: {
        maxFileMb: number;
        maxReferences: number;
        maxCount: number;
        heavyFileMb?: number;
    };
    reason: ImageEditCatalogReason | null;
}
export type ImageEditCatalogReason = 'no_provider_configured' | 'subsystem_disabled';
export interface ImageEditQuoteRequest {
    provider: string;
    model: string;
    mode: EditMode;
    op: ImageEditOp;
    count: number;
    hasMask: boolean;
    references: number;
    hasCharacter: boolean;
    width?: number | null;
    height?: number | null;
    hasAnnotations?: boolean;
    removal?: boolean;
    sessionId?: string | null;
    contextRevision?: number | null;
    format?: MediaFormatWish | null;
}
export interface ImageEditEstimate {
    amount: number | null;
    unit: string;
    approx: boolean;
    source: 'provider' | 'catalog' | 'history' | 'unknown';
    etaSeconds?: number | null;
    queueLength?: number | null;
}
export interface ImageEditQuote {
    quoteId: string;
    provider: string;
    model: string;
    estimate: ImageEditEstimate;
    expiresAt: string;
    expectedSeconds?: number | null;
}
export interface EditCost {
    amount: number;
    unit: string;
}
export interface ImageEditJob {
    jobId: string;
    projectId: string;
    status: ImageEditJobStatus;
    provider: string;
    model: string;
    variants: number[];
    cost?: EditCost | null;
    outcome?: EditOutcome | null;
    charged?: boolean | null;
    error?: string | null;
    queuePosition?: number | null;
    createdAt: string;
    chatSessionId?: string | null;
    initiator?: ImageEditInitiator;
    threadId?: string | null;
    estimate?: ImageEditEstimate | null;
    run?: number | null;
    runs?: number | null;
    etaSeconds?: number | null;
    runElapsedSeconds?: number | null;
}
export type ImageEditInitiator = 'human' | 'agent';
export interface ImageEditJobInput {
    quoteId: string;
    prompt: string;
    marks?: string;
    sourcePath?: string;
    source?: Blob;
    mask?: Blob;
    annotated?: Blob;
    characterSlug?: string;
    references?: ImageEditUploadedReference[];
    referencePaths?: ImageEditProjectReference[];
    aspectRatio?: string;
    matchSourceSize?: boolean;
    chatSessionId?: string;
    sessionId?: string;
    threadId?: string;
    baseStepId?: string;
    versionId?: string;
    contextRevision?: number;
}
export interface ImageEditUploadedReference {
    file: Blob;
    name: string;
    role: ReferenceRole;
}
export interface ImageEditProjectReference {
    path: string;
    role: ReferenceRole;
}
export declare function jobForm(input: ImageEditJobInput): FormData;
export interface ImageEditCharacterPhoto {
    file: string;
    primary?: boolean | null;
    angle?: string | null;
}
export interface ImageEditCharacter {
    slug: string;
    name: string;
    description?: string | null;
    path: string;
    photos: ImageEditCharacterPhoto[];
    local?: {
        lora: string;
        trigger: string;
        strength: number;
    } | null;
    createdAt: string;
}
export interface ImageEditLoras {
    available: boolean;
    items: string[];
    trained?: Record<string, string>;
}
export interface ImageEditCharacterInput {
    name: string;
    description?: string;
    removePhotos?: string[];
    photos: Blob[];
    lora?: {
        name: string;
        trigger: string;
        strength: number;
    } | 'remove';
}
export type ImageEditSaveMode = 'next-version' | 'as';
export interface ImageEditSaveRequest {
    jobId?: string | null;
    variant: number;
    sourcePath?: string;
    folder?: string;
    fileName?: string;
    mode?: ImageEditSaveMode | null;
    stepId?: string | null;
    chatSessionId?: string | null;
    sessionId?: string | null;
    threadId?: string | null;
    encode?: ImageEncodeSpec | null;
}
export interface SaveCheckRequest {
    folder?: string;
    name: string;
    format?: ImageEncodeFormat;
}
export interface SaveCheckResponse {
    path: string;
    taken: boolean;
    suggestion: string | null;
}
export type ImageEncodeFormat = 'png' | 'jpeg' | 'webp';
export interface ImageEncodeSpec {
    format: ImageEncodeFormat;
    quality?: number | null;
}
export interface ImageFractionRect {
    x: number;
    y: number;
    width: number;
    height: number;
}
export type ImageFlipAxis = 'horizontal' | 'vertical';
export type ImageTransformOp = {
    type: 'autoOrient';
} | {
    type: 'crop';
    rect: ImageFractionRect;
} | {
    type: 'rotate';
    degrees: 90 | 180 | 270;
} | {
    type: 'flip';
    axis: ImageFlipAxis;
} | {
    type: 'resize';
    width?: number | null;
    height?: number | null;
    percent?: number | null;
    lockAspect?: boolean;
} | {
    type: 'pad';
    width: number;
    height: number;
    color?: string | null;
    left?: number | null;
    top?: number | null;
};
export type ImageTransformBase = {
    path: string;
    stepId?: null;
    jobId?: null;
} | {
    stepId: string;
    path?: null;
    jobId?: null;
} | {
    jobId: string;
    variant: number;
    path?: null;
    stepId?: null;
};
export interface ImageTransformRequest {
    base: ImageTransformBase;
    ops: ImageTransformOp[];
    encode?: ImageEncodeSpec | null;
}
export interface ImageTransformResponse {
    stepId: string | null;
    width: number;
    height: number;
    bytes: number;
}
export interface ImageLaunchStoredMessage {
    kind: 'image_launch';
    by: ImageEditInitiator;
    prompt: string;
    provider: string;
    model: string;
    count: number;
    estimate?: ImageEditEstimate | null;
    jobId: string;
    timestamp?: number | null;
}
export interface ImageFileMovedStoredMessage {
    kind: 'image_file_moved';
    from: string;
    to: string;
    timestamp?: number | null;
}
export interface ImageSnapshot {
    revision: string;
    attached: boolean;
}
interface ImageEditEventOrigin {
    chatSessionId?: string | null;
    initiator?: ImageEditInitiator;
    threadId?: string | null;
}
export type ImageEditEvent = ImageEditEventOrigin & ({
    type: 'image_edit_progress';
    jobId: string;
    projectId: string;
    stage: EditStage;
    queuePosition?: number | null;
    run?: number | null;
    runs?: number | null;
    etaSeconds?: number | null;
    runElapsedSeconds?: number | null;
} | {
    type: 'image_edit_completed';
    jobId: string;
    projectId: string;
    variants: number[];
    cost?: EditCost | null;
} | {
    type: 'image_edit_failed';
    jobId: string;
    projectId: string;
    outcome: EditOutcome;
    charged?: boolean | null;
    error?: string | null;
    retryQuote?: ImageEditQuote | null;
});
export type ImageEditErrorCode = 'provider_unavailable' | 'invalid_request' | 'quote_not_found' | 'job_not_found' | 'character_not_found' | 'too_many_jobs' | 'image_editor_unavailable' | 'name_taken';
export declare function imageEditErrorCode(e: unknown): ImageEditErrorCode | null;
export declare function nameTakenSuggestion(e: unknown): string | null;
export interface ImageEditorApi {
    catalog(projectId: string): Promise<ImageEditCatalog>;
    quote(projectId: string, req: ImageEditQuoteRequest): Promise<ImageEditQuote>;
    format(projectId: string, q: {
        provider: string;
        model: string;
        op: ImageEditOp;
    }): Promise<MediaFormatState>;
    startJob(projectId: string, input: ImageEditJobInput): Promise<{
        jobId: string;
    }>;
    uploadSample(projectId: string, file: Blob, name: string): Promise<{
        uploadId: string;
    }>;
    getJob(projectId: string, jobId: string): Promise<ImageEditJob>;
    cancelJob(projectId: string, jobId: string): Promise<ImageEditJob>;
    variantUrl(projectId: string, jobId: string, n: number): string;
    save(projectId: string, req: ImageEditSaveRequest): Promise<{
        path: string;
    }>;
    subscribe(handler: (e: ImageEditEvent) => void): () => void;
    listCharacters(projectId: string): Promise<ImageEditCharacter[]>;
    createCharacter(projectId: string, input: ImageEditCharacterInput): Promise<ImageEditCharacter>;
    updateCharacter(projectId: string, slug: string, input: ImageEditCharacterInput): Promise<ImageEditCharacter>;
    deleteCharacter(projectId: string, slug: string): Promise<void>;
    loras(projectId: string): Promise<ImageEditLoras>;
    characterPhotoUrl(projectId: string, slug: string, file: string): string;
    saveCheck(projectId: string, req: SaveCheckRequest): Promise<SaveCheckResponse>;
    transform(projectId: string, req: ImageTransformRequest, opts?: {
        dryRun?: boolean;
    }): Promise<ImageTransformResponse>;
    stepUrl(projectId: string, stepId: string): string;
}
export type LoraRunStatus = 'training' | 'ready' | 'failed' | 'cancelled';
export type LoraTrainJobStatus = 'queued' | 'running';
export type TrainingPhotoRejectReason = 'format' | 'too_big' | 'too_small' | 'duplicate' | 'limit';
export interface TrainingPhoto {
    name: string;
    bytes: number;
    width: number;
    height: number;
}
export interface TrainingPhotosDto {
    items: TrainingPhoto[];
    count: number;
    min: number;
    max: number;
    photosHash: string;
}
export interface TrainingPhotosAddResultDto {
    photos: TrainingPhotosDto;
    rejected: {
        fileName: string;
        reason: TrainingPhotoRejectReason;
    }[];
}
export interface LoraTrainCurrent {
    runId: string;
    jobId: string;
    status: LoraTrainJobStatus;
    step?: number;
    totalSteps?: number;
    etaSeconds?: number;
    queuePosition?: number | null;
    startedAt?: string;
}
export interface LoraTrainStateDto {
    enabled: boolean;
    available: boolean;
    gpu: {
        busy: boolean;
        mine: boolean;
        busyUntil?: string;
    };
    current: LoraTrainCurrent | null;
    defaultTrigger: string;
    presets: {
        key: 'standard';
        etaSeconds: number;
    }[];
    photosChanged: boolean;
}
export interface LoraTrainStartRequest {
    preset: 'standard';
    trigger?: string;
    confirm: true;
}
export interface LoraTrainStartResponse {
    runId: string;
    jobId: string;
    queuePosition: number;
    etaSeconds: number;
}
export type LoraRejectReason = 'no_face' | 'many_faces' | 'small_face' | 'unreadable';
export interface LoraVersionDto {
    runId: string;
    status: LoraRunStatus;
    title?: string | null;
    createdAt?: string;
    trainedAt?: string;
    photosCount: number;
    photosKept?: number;
    trigger?: string;
    connected: boolean;
    rejected?: {
        name: string;
        reason: LoraRejectReason;
    }[];
    error?: {
        code: string;
        message: string;
    };
}
export interface LoraVersionsDto {
    items: LoraVersionDto[];
    own: {
        lora: string;
        trainedAt?: string | null;
    } | null;
    limit: number;
    used: number;
}
export interface LoraActiveDto {
    busy: boolean;
    mine: boolean;
    busyUntil?: string;
    jobId?: string;
}
export interface LoraTrainApi {
    photos(projectId: string, slug: string): Promise<TrainingPhotosDto>;
    addPhotos(projectId: string, slug: string, files: Blob[]): Promise<TrainingPhotosAddResultDto>;
    deletePhoto(projectId: string, slug: string, name: string): Promise<void>;
    photoUrl(projectId: string, slug: string, name: string): string;
    trainState(projectId: string, slug: string): Promise<LoraTrainStateDto>;
    startTrain(projectId: string, slug: string, req: LoraTrainStartRequest): Promise<LoraTrainStartResponse>;
    cancelTrain(projectId: string, slug: string): Promise<void>;
    active(): Promise<LoraActiveDto>;
    cancelActive(): Promise<void>;
    versions(projectId: string, slug: string): Promise<LoraVersionsDto>;
    connectVersion(projectId: string, slug: string, runId: string): Promise<ImageEditCharacter>;
    renameVersion(projectId: string, slug: string, runId: string, title: string): Promise<LoraVersionDto>;
    deleteVersion(projectId: string, slug: string, runId: string): Promise<{
        status: 'deleting';
        disconnected: boolean;
    }>;
}
export declare const loraTrainApi: LoraTrainApi;
export declare const MOCK_SOURCE_SIZE: {
    width: number;
    height: number;
};
export declare function mockEncodedBytes(width: number, height: number, encode?: ImageEncodeSpec | null): number;
export declare function mockApplyOps(size: {
    width: number;
    height: number;
}, ops: ImageTransformOp[]): {
    width: number;
    height: number;
};
export declare function createMockApi(mode: 'fal' | 'all'): ImageEditorApi;
export declare function characterSlug(name: string): string;
export declare function imageEditorApi(): ImageEditorApi;
export {};
