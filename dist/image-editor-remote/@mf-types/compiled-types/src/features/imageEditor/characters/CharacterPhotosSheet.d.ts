import type { ImageEditCharacter, ImageEditorApi } from '../api';
export declare function CharacterPhotosSheet({ api, projectId, character, onChanged, onCount, onClose, focus }: {
    api: ImageEditorApi;
    projectId: string;
    character: ImageEditCharacter;
    onChanged: (c: ImageEditCharacter) => void;
    onCount?: (n: number) => void;
    onClose: () => void;
    focus?: 'face' | 'train';
}): import("react").JSX.Element;
