import { type FormatModel, type MediaFormatWish, type ContextKindCtx } from 'aihome_shell/kit';
export declare function setWish(scope: string, wish: Omit<MediaFormatWish, 'by' | 'at'>): void;
export declare function aspectLabel(w: number, h: number): string;
export declare function outpaintSize(w: number, h: number, aspect: string): {
    w: number;
    h: number;
} | null;
export declare function imageFormat(ctx: ContextKindCtx, actionId: string | null): FormatModel | null;
