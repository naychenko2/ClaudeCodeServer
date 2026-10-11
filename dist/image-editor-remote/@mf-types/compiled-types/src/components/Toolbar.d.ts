import type { CSSProperties, ReactNode, MouseEvent, RefCallback } from 'react';
export { PillSwitch } from './ui/PillSwitch';
export declare const tbBtnPrimary: CSSProperties;
export declare const tbBtnGhost: CSSProperties;
export declare function Toolbar({ isMobile, noBorder, bg, children, style, onContextMenu, rootRef }: {
    isMobile?: boolean;
    noBorder?: boolean;
    bg?: string;
    children: ReactNode;
    style?: CSSProperties;
    onContextMenu?: (e: React.MouseEvent) => void;
    rootRef?: RefCallback<HTMLDivElement>;
}): import("react").JSX.Element;
export declare function ToolbarIconButton({ onClick, title, ariaLabel, isMobile, color, disabled, active, style, className, children }: {
    onClick?: (e: MouseEvent) => void;
    title?: string;
    ariaLabel?: string;
    isMobile?: boolean;
    color?: string;
    disabled?: boolean;
    active?: boolean;
    style?: CSSProperties;
    className?: string;
    children: ReactNode;
}): import("react").JSX.Element;
