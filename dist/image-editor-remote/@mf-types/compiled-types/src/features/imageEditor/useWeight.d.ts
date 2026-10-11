import type { ImageEditorApi, ImageEncodeSpec, ImageTransformBase, ImageTransformOp } from './api';
export declare function useWeight(api: ImageEditorApi, projectId: string, base: Promise<ImageTransformBase> | null, ops: ImageTransformOp[], encode: ImageEncodeSpec | null): {
    bytes: number | null;
    error: string | null;
    loading: boolean;
};
