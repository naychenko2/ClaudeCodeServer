import { type ImageEditCharacter, type LoraTrainApi, type LoraVersionsDto } from '../api';
import { type LoraFailure } from './loraErrors';
export interface LoraVersionsSnapshot {
    data: LoraVersionsDto | null;
    loading: boolean;
    error: LoraFailure | null;
    pending: string[];
}
export interface LoraVersionsController {
    getSnapshot(): LoraVersionsSnapshot;
    subscribe(fn: () => void): () => void;
    refresh(): Promise<void>;
    connect(runId: string): Promise<boolean>;
    rename(runId: string, title: string): Promise<boolean>;
    remove(runId: string): Promise<boolean>;
}
export declare function createLoraVersionsController(projectId: string, slug: string, api?: LoraTrainApi, onCharacterChanged?: (c: ImageEditCharacter | null) => void): LoraVersionsController;
export type LoraVersionsHandle = ReturnType<typeof useLoraVersions>;
export declare function useLoraVersions(projectId: string | null, slug: string | null, onCharacterChanged?: (c: ImageEditCharacter | null) => void): {
    ctl: LoraVersionsController | null;
    data: LoraVersionsDto | null;
    loading: boolean;
    error: LoraFailure | null;
    pending: string[];
};
