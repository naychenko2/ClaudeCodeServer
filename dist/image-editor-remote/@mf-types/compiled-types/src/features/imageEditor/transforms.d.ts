import type { ImageEncodeFormat, ImageEncodeSpec, ImageFractionRect, ImageTransformBase, ImageTransformOp, ImageTransformRequest, ImageTransformResponse } from './api';
export interface Dims {
    w: number;
    h: number;
}
export declare function chainTransform(base: Promise<ImageTransformBase>, ops: ImageTransformOp[], encode: ImageEncodeSpec | null, run: (req: ImageTransformRequest) => Promise<ImageTransformResponse>): {
    result: Promise<ImageTransformResponse & {
        stepId: string;
    }>;
    ready: Promise<ImageTransformBase>;
};
export declare function transformedSize(size: Dims, ops: ImageTransformOp[]): Dims;
export declare function opTitle(op: ImageTransformOp, encode?: ImageEncodeSpec | null): string;
export declare const CROP_RATIOS: readonly ["free", "1:1", "16:9", "9:16"];
export type CropRatio = typeof CROP_RATIOS[number];
export type CropCorner = 'nw' | 'ne' | 'sw' | 'se';
export declare function initialCrop(ratio: CropRatio, size: Dims): ImageFractionRect;
export declare function moveCrop(rect: ImageFractionRect, dx: number, dy: number): ImageFractionRect;
export declare function resizeCrop(rect: ImageFractionRect, corner: CropCorner, px: number, py: number, ratio: CropRatio, size: Dims): ImageFractionRect;
export declare function fitCropRatio(rect: ImageFractionRect, ratio: CropRatio, size: Dims): ImageFractionRect;
export declare const isFullCrop: (r: ImageFractionRect) => boolean;
export declare const SIZE_PRESETS: readonly [512, 1024, 1536, 2048];
export interface FormatPreset {
    key: string;
    label: string;
    w: number;
    h: number;
}
export declare const FORMAT_PRESETS: FormatPreset[];
export type BoxFit = 'contain' | 'pad' | 'cover';
export type PadBg = 'white' | 'transparent';
export declare const PAD_WHITE = "#FFFFFF";
export declare const padBgFor: (bg: PadBg | undefined, format: ImageEncodeFormat) => PadBg;
export declare function containDims(size: Dims, box: Dims): Dims;
export declare function sameAspect(size: Dims, box: Dims): boolean;
export declare function coverCrop(size: Dims, box: Dims): ImageFractionRect;
export declare const QUALITY_MIN = 40;
export declare const QUALITY_DEFAULT = 85;
export declare function presetDims(size: Dims, longSide: number): Dims;
export declare const lockedHeight: (size: Dims, w: number) => number;
export declare const lockedWidth: (size: Dims, h: number) => number;
export interface SizeForm {
    unit: 'px' | '%';
    w: number;
    h: number;
    percent: number;
    format: ImageEncodeFormat;
    quality: number;
    box?: (Dims & {
        fit: BoxFit;
    }) | null;
    padBg?: PadBg;
}
export declare function boxForm(size: Dims, box: Dims, fit: BoxFit): Pick<SizeForm, 'unit' | 'w' | 'h' | 'box'>;
export declare function sizeFormOps(size: Dims, f: SizeForm): {
    ops: ImageTransformOp[];
    encode: ImageEncodeSpec;
    target: Dims;
};
export declare function sizeFormChanged(size: Dims, f: SizeForm, sourceFormat: ImageEncodeFormat | null): boolean;
export declare function formatOf(mime: string): ImageEncodeFormat | null;
export declare function formatBytes(n: number): string;
export declare function debounceLatest<A extends unknown[]>(fn: (...args: A) => void, ms: number): {
    call: (...args: A) => void;
    cancel: () => void;
};
export declare const WEIGHT_DEBOUNCE_MS = 300;
export declare function weightEstimator(run: (base: ImageTransformBase, ops: ImageTransformOp[], encode: ImageEncodeSpec) => Promise<ImageTransformResponse>, onResult: (key: string, bytes: number | null, error: string | null) => void, ms?: number): {
    request: (base: Promise<ImageTransformBase>, key: string, ops: ImageTransformOp[], encode: ImageEncodeSpec) => void;
    cancel: () => void;
};
export declare function renderPreview(img: HTMLImageElement, op: ImageTransformOp): Promise<string | null>;
