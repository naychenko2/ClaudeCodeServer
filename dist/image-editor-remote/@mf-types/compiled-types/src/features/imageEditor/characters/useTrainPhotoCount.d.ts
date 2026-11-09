import { type LoraTrainApi } from '../api';
export interface TrainPhotoCountController {
    getSnapshot(): number | null;
    subscribe(fn: () => void): () => void;
    set(n: number): void;
}
export declare function createTrainPhotoCountController(projectId: string, slug: string, api?: Pick<LoraTrainApi, 'photos'>): TrainPhotoCountController;
export declare function useTrainPhotoCount(projectId: string | null, slug: string | null): {
    count: number | null;
    set: (n: number) => void | undefined;
};
