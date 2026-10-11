import type { ImageEditCharacter, ImageEditLoras, ImageEditorApi } from '../api';
import { type LoraInput } from './CharacterLoraSection';
type SetTrigger = (t: string | null) => void;
export declare function cancelTune(initialEdited: string | null, setTrigger: SetTrigger, onClose: () => void): void;
export declare function saveTune(o: {
    api: ImageEditorApi;
    projectId: string;
    character: ImageEditCharacter;
    lora: LoraInput | undefined;
    onChanged: (c: ImageEditCharacter) => void;
    onClose: () => void;
}): Promise<void>;
export declare function CharacterTuneSheet({ api, projectId, character, loraList, trigger, edited, setTrigger, onChanged, onClose }: {
    api: ImageEditorApi;
    projectId: string;
    character: ImageEditCharacter;
    loraList: ImageEditLoras | null;
    trigger: string;
    edited: string | null;
    setTrigger: SetTrigger;
    onChanged: (c: ImageEditCharacter) => void;
    onClose: () => void;
}): import("react").JSX.Element;
export {};
