import type { LoraVersionDto } from '../api';
export type VersionAction = 'connect' | 'rename' | 'remove';
export type VersionMarkTone = 'success' | 'neutral' | 'accent' | 'danger';
export declare function versionTitle(v: LoraVersionDto, all: LoraVersionDto[]): string;
export declare function versionMark(v: LoraVersionDto): {
    text: string;
    tone: VersionMarkTone;
};
export declare function versionActions(v: LoraVersionDto): VersionAction[];
export declare function needsRevertConfirm(target: LoraVersionDto, all: LoraVersionDto[]): boolean;
export declare function validateVersionTitle(raw: string): {
    ok: true;
    title: string;
} | {
    ok: false;
    error: string;
};
export declare const atVersionLimit: (used: number, limit: number) => boolean;
