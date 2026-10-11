import type { ImageEditorApi, ImageEncodeFormat, ImageEncodeSpec, ImageTransformBase, ImageTransformOp } from './api';
import { type Dims } from './transforms';
export declare function AdjustPanel({ api, projectId, stepId, size, base, sourceFormat, beforeBytes, blockReason, cropping, onOp, onCrop, onApply }: {
    api: ImageEditorApi;
    projectId: string;
    stepId: string;
    size: Dims | null;
    base: Promise<ImageTransformBase> | null;
    sourceFormat: ImageEncodeFormat | null | undefined;
    beforeBytes: number | null;
    blockReason: string;
    cropping: boolean;
    onOp: (op: ImageTransformOp) => void;
    onCrop: () => void;
    onApply: (ops: ImageTransformOp[], encode: ImageEncodeSpec) => void;
}): import("react").JSX.Element;
