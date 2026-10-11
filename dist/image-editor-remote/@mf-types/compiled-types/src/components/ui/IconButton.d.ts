import type { CSSProperties, MouseEvent, ReactNode } from 'react';
export type IconButtonSize = 'xs' | 'sm' | 'md' | 'lg';
export type IconButtonTone = 'muted' | 'accent' | 'danger';
export type IconButtonVariant = 'ghost' | 'soft' | 'media';
interface Props {
    onClick?: (e: MouseEvent) => void;
    title?: string;
    ariaLabel?: string;
    disabled?: boolean;
    active?: boolean;
    size?: IconButtonSize;
    tone?: IconButtonTone;
    variant?: IconButtonVariant;
    color?: string;
    style?: CSSProperties;
    className?: string;
    children: ReactNode;
}
export declare function IconButton({ onClick, title, ariaLabel, disabled, active, size, tone, variant, color, style, className, children, }: Props): import("react").JSX.Element;
export {};
