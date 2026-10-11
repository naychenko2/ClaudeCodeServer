import { type LoraTrainApi, type LoraTrainStateDto, type LoraVersionDto } from '../api';
import { type LoraFailure } from './loraErrors';
export declare const LORA_POLL_MS = 15000;
export type LoraTrainView = {
    kind: 'loading';
} | {
    kind: 'unavailable';
    reason: 'disabled' | 'no_seam' | 'unavailable';
} | {
    kind: 'idle';
} | {
    kind: 'queued';
    position: number | null;
} | {
    kind: 'running';
    step: number;
    totalSteps: number | null;
    etaSeconds: number | null;
} | {
    kind: 'done';
    version: LoraVersionDto;
} | {
    kind: 'failed';
    version: LoraVersionDto;
} | {
    kind: 'cancelled';
    version: LoraVersionDto;
};
export interface LoraTrainSnapshot {
    view: LoraTrainView;
    state: LoraTrainStateDto | null;
    error: LoraFailure | null;
    busy: boolean;
}
export interface LoraTrainController {
    getSnapshot(): LoraTrainSnapshot;
    subscribe(fn: () => void): () => void;
    refresh(): Promise<void>;
    start(trigger?: string): Promise<boolean>;
    cancel(): Promise<boolean>;
}
export declare function createLoraTrainController(projectId: string, slug: string, api?: LoraTrainApi, intervalMs?: number): LoraTrainController;
export declare function useLoraTraining(projectId: string | null, slug: string | null): {
    start: ((trigger?: string) => Promise<boolean>) | undefined;
    cancel: (() => Promise<boolean>) | undefined;
    refresh: (() => Promise<void>) | undefined;
    view: LoraTrainView;
    state: LoraTrainStateDto | null;
    error: LoraFailure | null;
    busy: boolean;
};
