import type { ImageEditCharacter, ImageEditorApi } from '../api';
export declare const NAME_SHEET_TITLE = "\u0418\u043C\u044F \u0438 \u043E\u043F\u0438\u0441\u0430\u043D\u0438\u0435";
export declare const canSaveName: (name: string) => boolean;
export declare function saveNameDescription(o: {
    api: ImageEditorApi;
    projectId: string;
    character: ImageEditCharacter;
    name: string;
    description: string;
    onChanged: (c: ImageEditCharacter) => void;
    onClose: () => void;
}): Promise<void>;
type SaveArgs = Parameters<typeof saveNameDescription>[0];
export declare function submitNameSheet(o: SaveArgs, ui: {
    setBusy: (b: boolean) => void;
    setError: (e: string | null) => void;
}): Promise<void>;
export declare function CharacterNameSheet({ api, projectId, character, onChanged, onClose }: {
    api: ImageEditorApi;
    projectId: string;
    character: ImageEditCharacter;
    onChanged: (c: ImageEditCharacter) => void;
    onClose: () => void;
}): import("react").JSX.Element;
export {};
