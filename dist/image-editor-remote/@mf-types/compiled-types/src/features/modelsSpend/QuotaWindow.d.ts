import type { ProviderQuotaWindow } from '../../types';
export declare const barTone: (used: number) => "var(--c-success)" | "var(--c-warning)" | "var(--c-danger)";
export declare const barTextTone: (used: number) => "var(--c-text-heading)" | "var(--c-warning-text)" | "var(--c-danger-text)";
export interface QuotaWindowView {
    label: string;
    kind: 'percent' | 'count' | 'consumed' | 'alive';
    usedPct: number | null;
    usedCount: number | null;
    totalCount: number | null;
    valueText: string;
    resetsAt: string | null;
    exhausted: boolean;
}
export declare function parseQuotaWindow(w: ProviderQuotaWindow): QuotaWindowView;
export declare function CountSegments({ used, total, label }: {
    used: number;
    total: number;
    label?: string;
}): import("react").JSX.Element;
export declare function QuotaWindow({ w, dim, labelWidth }: {
    w: QuotaWindowView;
    dim?: boolean;
    labelWidth?: number;
}): import("react").JSX.Element;
