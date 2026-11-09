import { type ContextAction } from 'aihome_shell/kit';
import type { ImageEditOp } from '../api';
export type ImageState = 'draft' | 'file' | 'marks';
export interface ImageActionInput {
    hasFile: boolean;
    marks: number;
    hasMask: boolean;
    offered: {
        removeBackground: boolean;
        upscale: boolean;
        outpaint: boolean;
    };
    openBrush: () => void;
    format: {
        outpaintAspect: string | null;
        pendingAspect: boolean;
    };
}
export declare const imageState: (i: Pick<ImageActionInput, "hasFile" | "marks">) => ImageState;
export declare const EDIT_OP: (hasMask: boolean) => ImageEditOp;
export declare const buildImageActions: (i: ImageActionInput) => readonly ContextAction[];
