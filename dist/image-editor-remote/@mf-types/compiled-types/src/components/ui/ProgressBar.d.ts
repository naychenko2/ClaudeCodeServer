import { type CSSProperties } from 'react';
export type ProgressTone = 'accent' | 'muted' | 'success' | 'warning' | 'danger';
export declare const PROGRESS_H: {
    readonly default: 4;
    readonly thin: 3;
};
declare const HEIGHT: {
    readonly default: 4;
    readonly thin: 3;
};
export declare function ProgressBar({ value, tone, estimate, transition, indeterminate, size, label, style }: {
    value: number;
    tone?: ProgressTone;
    estimate?: boolean;
    transition?: string;
    indeterminate?: boolean;
    size?: keyof typeof HEIGHT;
    label?: string;
    style?: CSSProperties;
}): import("react").JSX.Element;
export {};
