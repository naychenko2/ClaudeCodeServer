import type { TrainingPhotoRejectReason } from '../api';
export declare const TRAIN_MIN = 20;
export declare const TRAIN_MAX = 50;
export declare const TRAIN_MIN_SIDE_PX = 512;
export declare const TRAIN_MAX_MB = 8;
export declare const TRAIN_UPLOAD_BATCH = 10;
export declare const TRAIN_GRID_CAP = 12;
export type PhotosState = 'empty' | 'uploading' | 'low' | 'enough' | 'limit';
export type StatusTone = 'muted' | 'warning' | 'success';
export declare function photosState(n: number, uploading: boolean, min?: number, max?: number): PhotosState;
export declare function photosCounter(n: number, min?: number, max?: number): string;
export declare function photosStatus(state: PhotosState, n: number, min?: number): {
    text: string;
    tone: StatusTone;
} | null;
export declare function rejectText(reason: TrainingPhotoRejectReason): string;
export declare const uploadProgressText: (done: number, total: number) => string;
export declare const uploadPercent: (done: number, total: number) => number;
export declare function chunk<T>(items: T[], size?: number): T[][];
export declare function splitByRoom<T extends {
    name: string;
}>(files: T[], count: number, max?: number): {
    accepted: T[];
    overflow: T[];
};
