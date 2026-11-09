import type { CSSProperties, ReactNode } from 'react';
export type BadgeTone = 'neutral' | 'accent' | 'success' | 'warning' | 'danger' | 'info' | 'plan';
export type BadgeSize = 'xs' | 'sm';
export declare const TONE_DOT: Record<BadgeTone, string>;
interface Props {
    tone?: BadgeTone;
    size?: BadgeSize;
    icon?: ReactNode;
    dot?: boolean;
    children: ReactNode;
    title?: string;
    onClick?: (e: React.MouseEvent<HTMLElement>) => void;
    active?: boolean;
    disabled?: boolean;
    style?: CSSProperties;
}
export declare function Badge({ tone, size, icon, dot, children, title, onClick, active, disabled, style, }: Props): import("react").JSX.Element;
export {};
