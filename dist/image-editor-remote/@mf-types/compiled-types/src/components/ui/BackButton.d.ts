import type { ReactNode, CSSProperties, MouseEvent, KeyboardEvent } from 'react';
export declare function BackButton({ onClick, title, children, iconColor, iconSize, style }: {
    onClick: (e: MouseEvent | KeyboardEvent) => void;
    title?: string;
    children?: ReactNode;
    iconColor?: string;
    iconSize?: number;
    style?: CSSProperties;
}): import("react").JSX.Element;
