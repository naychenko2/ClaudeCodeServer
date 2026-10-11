interface ResetConfirmDialogProps {
    open: boolean;
    title: string;
    body: string;
    confirmLabel?: string;
    busy?: boolean;
    variant?: 'danger' | 'primary';
    onCancel: () => void;
    onConfirm: () => void;
}
export declare function ResetConfirmDialog({ open, title, body, confirmLabel, busy, variant, onCancel, onConfirm, }: ResetConfirmDialogProps): import("react").JSX.Element | null;
export {};
