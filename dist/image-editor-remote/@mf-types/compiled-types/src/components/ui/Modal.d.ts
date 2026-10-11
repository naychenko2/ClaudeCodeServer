import type { ReactNode, CSSProperties } from 'react';
interface ModalProps {
    width?: number;
    title?: ReactNode;
    subtitle?: ReactNode;
    footer?: ReactNode;
    onClose: () => void;
    closeOnBackdrop?: boolean;
    hideCloseButton?: boolean;
    children?: ReactNode;
    cardStyle?: CSSProperties;
    size?: 'default' | 'fullscreen';
}
export declare function Modal({ width, title, subtitle, footer, onClose, closeOnBackdrop, hideCloseButton, children, cardStyle, size, }: ModalProps): import("react").ReactPortal;
export {};
