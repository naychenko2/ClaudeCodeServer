import type { LoraTrainStateDto, LoraVersionDto } from '../api';
import { type LoraFailure } from './loraErrors';
import type { LoraTrainView } from './useLoraTraining';
export declare const TRIGGER_RE: RegExp;
export declare const isValidTrigger: (t: string) => boolean;
export declare function formatDuration(seconds: number): string;
export declare function formatTrainedDate(iso: string | undefined): string;
export interface TrainViewInput {
    view: LoraTrainView;
    state: LoraTrainStateDto | null;
    error: LoraFailure | null;
    photoCount: number | null;
    minPhotos?: number;
    trigger: string;
    isAdmin: boolean;
    dismissedRunId: string | null;
    now?: number;
}
export type TrainSectionView = {
    kind: 'loading';
} | {
    kind: 'off';
} | {
    kind: 'noLocal';
} | {
    kind: 'busyOther';
    text: string;
    canStopForeign: boolean;
} | {
    kind: 'busyMine';
} | {
    kind: 'queued';
} | {
    kind: 'running';
    text: string;
    percent: number | null;
    etaText: string | null;
} | {
    kind: 'done';
    version: LoraVersionDto;
    first: boolean;
    meta: string;
    photosChanged: boolean;
} | {
    kind: 'failed';
    version: LoraVersionDto;
    allRejected: boolean;
    lost: boolean;
} | {
    kind: 'form';
    label: string;
    disabled: boolean;
    reason: 'photos' | 'trigger' | null;
    stopped: boolean;
    photosChanged: boolean;
};
export declare function isLost(v: LoraVersionDto): boolean;
export declare function versionFailureText(v: LoraVersionDto): string;
export declare function isAllRejected(v: LoraVersionDto): boolean;
export declare function trainSectionView(i: TrainViewInput): TrainSectionView;
export declare function failureText(f: LoraFailure, min?: number): string | null;
export declare function confirmParts(hasConnected: boolean): {
    before: "Видеокарта будет занята примерно ";
    bold: "2 часа";
    after: ". Всё это время новые картинки на локальной модели будут стоять в очереди и ждать. Отменить обучение можно в любой момент.";
    note: "Новая версия не включится сама — вы выберете, подключать ли её, когда обучение закончится." | null;
};
export declare function badgeFor(v: TrainSectionView): {
    text: string;
    tone: 'neutral' | 'accent' | 'success' | 'danger';
} | null;
