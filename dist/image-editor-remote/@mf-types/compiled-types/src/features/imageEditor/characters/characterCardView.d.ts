import type { LoraVersionDto } from '../api';
import { METER } from './characterTexts';
import { type TrainSectionView } from './loraTrainView';
export interface CardViewInput {
    photos: number;
    train: number | null;
    view: TrainSectionView;
    versions: LoraVersionDto[];
    own: string | null | undefined;
}
export type CardState = 2 | 3 | 4 | 5;
export type CardSay = {
    key: 'ready' | 'running' | 'queued' | 'trainedOwn' | 'failed' | 'failedLost' | 'failedAllRejected';
} | {
    key: 'needTrain';
    n: number;
} | {
    key: 'trained';
    title: string;
};
export interface CardBadge {
    text: 'done' | 'failed';
    tone: 'success' | 'danger';
}
export interface MeterStop {
    value: number;
    tone: 'success' | 'accent';
    estimate: boolean;
    titleKey: keyof typeof METER.stops | 'running';
    percent?: number | null;
    titleColor: string;
    hint: string;
}
export type CardAction = {
    kind: 'train';
    disabled: boolean;
    fixTrigger: boolean;
} | {
    kind: 'addTrainPhotos';
    count: number;
} | {
    kind: 'progress';
} | {
    kind: 'toContext';
} | {
    kind: 'retry';
} | {
    kind: 'replaceTrainPhotos';
} | {
    kind: 'notice';
    view: TrainSectionView;
};
export interface CardView {
    state: CardState;
    say: CardSay;
    badge: CardBadge | null;
    stops: [MeterStop, MeterStop, MeterStop];
    action: CardAction;
}
export declare function characterCardView(i: CardViewInput): CardView;
