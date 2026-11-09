import type { ImageEditCharacter, ImageEditorApi } from '../api';
export declare function CharacterCard({ api, projectId, character, contextSessionId, onBack, onDeleted }: {
    api: ImageEditorApi;
    projectId: string;
    character: ImageEditCharacter;
    contextSessionId: string | null;
    onBack: () => void;
    onDeleted: (slug: string) => void;
}): import("react").JSX.Element;
