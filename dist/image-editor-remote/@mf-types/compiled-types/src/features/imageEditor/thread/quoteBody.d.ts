import type { MediaFormatWish } from 'aihome_shell/kit';
import type { EditMode, ImageEditOp, ImageEditQuoteRequest } from '../api';
import { type Mark } from '../marks';
export declare const marksSent: (op: ImageEditOp, hasImage: boolean, marks: readonly Mark[]) => boolean;
export interface QuoteBodyInput {
    provider: string;
    model: string;
    mode: EditMode;
    op: ImageEditOp;
    count: number;
    hasImage: boolean;
    marks: readonly Mark[];
    withMask: boolean;
    removal: boolean;
    references: number;
    hasCharacter: boolean;
    size: {
        w: number;
        h: number;
    } | null;
    context?: {
        sessionId: string;
        contextRevision: number;
    } | null;
    format?: MediaFormatWish | null;
}
export declare function buildQuoteBody(i: QuoteBodyInput): ImageEditQuoteRequest;
