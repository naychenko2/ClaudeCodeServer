import type { ImageEditLoras } from '../api';
import type { LoraVersionsHandle } from './useLoraVersions';
export declare function CharacterLoraVersions({ loraList, versions, disabled, hideTitle }: {
    loraList: ImageEditLoras | null;
    versions: LoraVersionsHandle;
    disabled?: boolean;
    hideTitle?: boolean;
}): import("react").JSX.Element;
export declare function OwnLabel({ label, split }: {
    label: string;
    split: boolean;
}): import("react").JSX.Element;
