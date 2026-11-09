export type Mark = {
    type: 'mask';
    points: [number, number][];
    width: number;
} | {
    type: 'rect';
    x: number;
    y: number;
    w: number;
    h: number;
} | {
    type: 'arrow';
    x1: number;
    y1: number;
    x2: number;
    y2: number;
} | {
    type: 'text';
    x: number;
    y: number;
    text: string;
};
export type Tool = 'hand' | 'mask' | 'arrow' | 'rect' | 'text' | 'eraser';
export declare const strokeScale: (w: number, h: number) => number;
export declare function MarksLayer({ marks, k, onPick }: {
    marks: Mark[];
    k: number;
    onPick?: (i: number) => void;
}): import("react").JSX.Element;
export declare const hasMaskMark: (marks: Mark[]) => boolean;
export declare const hasAnnotationMark: (marks: Mark[]) => boolean;
export declare function marksToJson(marks: Mark[], w: number, h: number): string;
export declare const MASK_MAX = 2048;
export declare function maskExportSize(w: number, h: number): {
    w: number;
    h: number;
    k: number;
};
export declare function exportMask(marks: Mark[], w: number, h: number): Promise<Blob | null>;
export declare function exportAnnotated(img: HTMLImageElement, marks: Mark[], w: number, h: number): Promise<Blob | null>;
export declare function exportChatSnapshot(img: HTMLImageElement, marks: Mark[], w: number, h: number, maxSide?: number): Promise<Blob | null>;
