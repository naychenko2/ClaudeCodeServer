import type { ImageEditLoras } from '../api';
import type { LoraVersionsHandle } from './useLoraVersions';
export declare const versionsCounter: (used: number, limit: number | undefined) => string;
export declare function CharacterVersionsSheet({ loraList, versions, onRestart, onClose }: {
    loraList: ImageEditLoras | null;
    versions: LoraVersionsHandle;
    onRestart: () => void;
    onClose: () => void;
}): import("react").JSX.Element;
