import type { ImageEditLoras, ImageEditorApi } from '../api';
export declare function useLoraList(api: ImageEditorApi, projectId: string): {
    list: ImageEditLoras | null;
    reload: () => void;
};
