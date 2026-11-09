export type ToastKind = 'reminder' | 'claude' | 'info' | 'success' | 'error';
export interface ToastAction {
    label: string;
    onClick: () => void;
}
export interface LocalToast {
    title: string;
    body: string;
    kind?: ToastKind;
    action?: ToastAction;
}
export declare function showToast(title: string, body: string, kind?: ToastKind, action?: ToastAction): void;
