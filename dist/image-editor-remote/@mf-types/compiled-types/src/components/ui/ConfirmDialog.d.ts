import type { ReactNode } from 'react';
import type { ButtonVariant } from './Button';
interface ConfirmDialogProps {
    title: string;
    subtitle?: ReactNode;
    confirmLabel?: string;
    confirmVariant?: ButtonVariant;
    cancelLabel?: string;
    onConfirm: () => void | Promise<void>;
    onCancel: () => void;
}
export declare function ConfirmDialog({ title, subtitle, confirmLabel, confirmVariant, cancelLabel, onConfirm, onCancel, }: ConfirmDialogProps): import("react").JSX.Element;
export {};
