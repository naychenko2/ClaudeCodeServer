import type { ReactNode } from 'react';
export declare function ImageLightbox({ src, alt, onClose, children }: {
    src: string;
    alt?: string;
    onClose: () => void;
    children?: ReactNode;
}): import("react").ReactPortal;
