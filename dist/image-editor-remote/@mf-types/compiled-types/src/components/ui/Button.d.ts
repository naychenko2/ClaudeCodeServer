import type { CSSProperties, ReactNode, MouseEvent } from 'react';
export type ButtonVariant = 'primary' | 'secondary' | 'ghost' | 'ghostAccent' | 'ghostFilled' | 'danger' | 'dashed';
export type ButtonSize = 'xs' | 'sm' | 'md' | 'lg';
interface ButtonProps {
    variant?: ButtonVariant;
    size?: ButtonSize;
    fullWidth?: boolean;
    loading?: boolean;
    disabled?: boolean;
    glow?: boolean;
    pill?: boolean;
    leftIcon?: ReactNode;
    onClick?: (e: MouseEvent) => void;
    type?: 'button' | 'submit';
    title?: string;
    style?: CSSProperties;
    href?: string;
    children: ReactNode;
}
export declare function Button({ variant, size, fullWidth, loading, disabled, glow, pill, leftIcon, onClick, type, title, style, href, children, }: ButtonProps): import("react").JSX.Element;
export {};
