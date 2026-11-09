import type { ImageEditCharacter, ImageEditLoras } from '../api';
type Lora = NonNullable<ImageEditCharacter['local']>;
export type LoraInput = {
    name: string;
    trigger: string;
    strength: number;
} | 'remove';
export declare function CharacterLoraSection({ list, current, slug, disabled, onChange }: {
    list: ImageEditLoras | null;
    current: Lora | null | undefined;
    slug?: string;
    disabled?: boolean;
    onChange: (lora: LoraInput | undefined) => void;
}): import("react").JSX.Element;
export {};
