import { type ImageEditCatalog, type ImageEditJobInput, type ImageEditOp, type ImageEditPriceHint, type ImageEditProjectReference, type ImageEditUploadedReference, type ImageTransformBase, type ReferenceRole } from './api';
import type { MediaFormatWish } from 'aihome_shell/kit';
export type Sample = {
    id: string;
    source: 'upload';
    name: string;
    role: ReferenceRole;
    file: Blob;
    url: string;
} | {
    id: string;
    source: 'project';
    name: string;
    role: ReferenceRole;
    path: string;
    url: string;
};
export declare const SAMPLE_ROLES: [ReferenceRole, string, string][];
export declare function maxSamples(serverLimit: number, modelLimit: number | null | undefined): number;
export declare function samplesToJobInput(samples: Sample[]): {
    references: ImageEditUploadedReference[];
    referencePaths: ImageEditProjectReference[];
};
export declare function applyAgentReferences(list: Sample[], refs: ImageEditProjectReference[], make: (ref: ImageEditProjectReference) => Sample): Sample[];
export type QuickAction = 'removeBackground' | 'upscale' | 'removeMarked' | 'outpaint' | 'enhanceFaces';
export declare const QUICK_ACTIONS: QuickAction[];
export declare const OUTPAINT_RATIOS: readonly ["1:1", "16:9", "9:16"];
export type OutpaintRatio = typeof OUTPAINT_RATIOS[number];
export declare const QUICK_LABEL: Record<QuickAction, string>;
export type LaunchAction = {
    kind: 'prompt';
    prompt: string;
} | {
    kind: QuickAction;
    ratio?: OutpaintRatio;
};
export interface LaunchPlan {
    op: ImageEditOp;
    prompt: string;
    useMask: boolean;
    removal: boolean;
    aspectRatio?: OutpaintRatio;
}
export declare function quickPlan(action: QuickAction, ratio: OutpaintRatio): LaunchPlan;
export declare function outpaintFormatWish(fromContext: boolean, plan: LaunchPlan, now?: Date): MediaFormatWish | null;
export declare const quickUsesOwnModel: (action: QuickAction) => action is "enhanceFaces";
export interface QuickRoute {
    provider: string;
    providerLabel: string;
    model: string;
    count: number;
    maxReferences: number | null;
    priceHint: ImageEditPriceHint | null;
}
export interface QuickAvailability {
    route: QuickRoute | null;
    reason: string;
    fallback: QuickRoute | null;
}
export declare function quickAvailability(action: QuickAction, catalog: ImageEditCatalog | null, providerKey: string | null, modelId: string, count: number): QuickAvailability;
export declare function fallbackLabel(r: QuickRoute): string;
export declare const quickOffered: (action: QuickAction, catalog: ImageEditCatalog | null) => boolean;
export declare function actionTitle(a: LaunchAction): string;
export interface HistoryStep {
    id: string;
    original: boolean;
    title: string;
    src: string;
    ready?: Promise<ImageTransformBase> | null;
    base?: ImageTransformBase | null;
    pending?: boolean;
    w?: number;
    h?: number;
    bytes?: number;
}
export type SaveSource = {
    jobId: string;
    variant: number;
} | {
    stepId: string;
};
export declare function stepSaveSource(s: HistoryStep | undefined): SaveSource | null;
export interface History {
    steps: HistoryStep[];
    cur: number;
}
export declare const EMPTY_HISTORY: History;
export declare function pushStep(h: History, step: HistoryStep): History;
export declare function dropStepsFrom(h: History, id: string): History;
export declare const patchStep: (h: History, id: string, patch: Partial<HistoryStep>) => History;
export declare const goToStep: (h: History, i: number) => History;
export declare function stepLabel(h: History, i: number): string;
export declare const currentSrc: (h: History) => string | null;
export declare function panelJobInput(samples: Sample[], plan: LaunchPlan): Pick<ImageEditJobInput, 'references' | 'referencePaths' | 'aspectRatio'>;
