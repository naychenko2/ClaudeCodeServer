import type { ImageEditCharacter, ImageEditorApi } from '../api';
export declare function CharacterCreate({ api, projectId, onCreated, onClose }: {
    api: ImageEditorApi;
    projectId: string;
    onCreated: (c: ImageEditCharacter) => void;
    onClose: () => void;
}): import("react").JSX.Element;
