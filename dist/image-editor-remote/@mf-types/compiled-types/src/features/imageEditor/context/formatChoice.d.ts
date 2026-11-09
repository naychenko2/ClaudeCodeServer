import { type CropAnchor, type MediaFormatWish } from 'aihome_shell/kit';
export declare const wishForQuote: (scope: string) => MediaFormatWish | null;
export interface EditChoice {
    aspect: string | null;
    how: 'outpaint' | 'crop' | null;
    anchor: CropAnchor;
    cropping: boolean;
}
export declare const editOf: (threadId: string) => EditChoice;
export declare const editAspectOf: (threadId: string) => string | null;
export declare function setEdit(threadId: string, patch: Partial<EditChoice>): void;
export declare function resetEdit(threadId: string): void;
