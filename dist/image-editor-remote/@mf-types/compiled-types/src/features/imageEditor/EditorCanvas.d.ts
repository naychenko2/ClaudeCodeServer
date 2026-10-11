import { type ReactNode } from 'react';
import { type Mark, type Tool } from './marks';
import type { ImageFractionRect } from './api';
import type { CropRatio } from './transforms';
export declare function EditorCanvas({ src, size, marks, onMarksChange, tool, onTextAt, onImageLoad, crop, onCropChange, overlay, hint }: {
    src: string;
    size: {
        w: number;
        h: number;
    } | null;
    marks: Mark[];
    onMarksChange: (marks: Mark[]) => void;
    tool: Tool;
    onTextAt: (x: number, y: number) => void;
    onImageLoad: (img: HTMLImageElement) => void;
    crop?: {
        rect: ImageFractionRect;
        ratio: CropRatio;
    } | null;
    onCropChange?: (rect: ImageFractionRect) => void;
    overlay?: ReactNode;
    hint?: string;
}): import("react").JSX.Element;
