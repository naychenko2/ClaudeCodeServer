import { type BadgeTone } from '../ui/Badge';
export type ExecutorGroup = 'auto' | 'local' | 'cloud';
export declare const EXECUTOR_GROUP_LABEL: Record<Exclude<ExecutorGroup, 'auto'>, string>;
export type ExecutorBadgeTone = BadgeTone | 'good' | 'warn';
export interface ExecutorBadge {
    label: string;
    tone?: ExecutorBadgeTone;
}
export declare const badgeTone: (t: ExecutorBadgeTone | undefined) => BadgeTone;
export type ExecutorUnit = 'free' | 'usd' | 'credits' | 'rub';
export interface ExecutorRow {
    id: string;
    group: ExecutorGroup;
    name: string;
    sub?: string;
    now?: string;
    price: string;
    free?: boolean;
    amount?: number | null;
    unit?: ExecutorUnit | null;
    etaSeconds?: number | null;
    badges?: readonly ExecutorBadge[];
    disabled?: boolean;
    locked?: boolean;
    reason?: string;
}
export declare function rowPriceShort(r: Pick<ExecutorRow, 'price' | 'free' | 'amount' | 'unit'>): string;
export declare function groupExecutorRows(rows: readonly ExecutorRow[]): {
    group: ExecutorGroup;
    rows: ExecutorRow[];
}[];
export declare function ExecutorSummaryRow({ label, name, parts, price, open, onToggle, isMobile }: {
    label?: string;
    name: string;
    parts?: readonly string[];
    price?: ExecutorBadge;
    open: boolean;
    onToggle: () => void;
    isMobile?: boolean;
}): import("react").JSX.Element;
export declare function ExecutorList({ rows, value, onChange, ariaLabel, isMobile, bare }: {
    rows: readonly ExecutorRow[];
    value: string;
    onChange: (id: string) => void;
    ariaLabel?: string;
    isMobile?: boolean;
    bare?: boolean;
}): import("react").JSX.Element;
