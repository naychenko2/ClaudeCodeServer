import type { ButtonVariant } from './Button';
interface ModalActionsProps {
    confirmLabel: string;
    onConfirm: () => void;
    confirmVariant?: ButtonVariant;
    loading?: boolean;
    confirmDisabled?: boolean;
    cancelLabel?: string;
    onCancel: () => void;
}
export declare function ModalActions({ confirmLabel, onConfirm, confirmVariant, loading, confirmDisabled, cancelLabel, onCancel, }: ModalActionsProps): import("react").JSX.Element;
export {};
