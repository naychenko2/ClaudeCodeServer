import { type MutableRefObject, type ReactNode } from 'react';
export type FacePhoto = {
    key: string;
    url: string;
    primary: boolean;
};
export declare function CharacterFacePhotos({ photos, onAdd, onRemove, uploading, disabled, isMobile, renderCaption, onSelectPhoto, selectedKey, labelsEditor, addRef }: {
    photos: FacePhoto[];
    onAdd: (files: File[]) => Promise<void>;
    onRemove: (key: string) => Promise<void>;
    uploading?: boolean;
    disabled?: boolean;
    isMobile?: boolean;
    renderCaption?: (photo: FacePhoto) => ReactNode;
    onSelectPhoto?: (photo: FacePhoto) => void;
    selectedKey?: string | null;
    labelsEditor?: ReactNode;
    addRef?: MutableRefObject<((files: File[]) => Promise<void>) | null>;
}): import("react").JSX.Element;
