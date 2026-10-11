import type { HTMLAttributes, MouseEvent, ReactNode } from 'react';
export declare function handleMenuEscape(e: {
    key: string;
    defaultPrevented: boolean;
    preventDefault: () => void;
}, onClose: () => void, restoreFocus: () => void): boolean;
export declare function Menu({ onClose, align, top, bottom, minWidth, maxWidth, anchor, maxHeight, gap, anchorSide, anchorAlign, preferUp, inertBackdrop, fullWidth, innerScroll, children }: {
    onClose: () => void;
    align?: 'left' | 'right';
    top?: number;
    bottom?: number;
    minWidth?: number;
    maxWidth?: number;
    anchor?: DOMRect;
    maxHeight?: number;
    gap?: number;
    anchorSide?: 'left' | 'right';
    anchorAlign?: 'start' | 'end';
    preferUp?: boolean;
    inertBackdrop?: boolean;
    fullWidth?: boolean;
    innerScroll?: boolean;
    children: ReactNode;
}): import("react").JSX.Element;
export declare function MenuSep(): import("react").JSX.Element;
export interface MenuItemAction {
    icon: ReactNode;
    title: string;
    onClick: () => void;
    disabled?: boolean;
}
export declare function MenuItem({ icon, iconSize, iconTile, label, hint, hintWrap, onClick, danger, disabled, wrapper, action, actions, isMobile }: {
    icon?: ReactNode;
    iconSize?: number;
    iconTile?: boolean;
    label: ReactNode;
    hint?: ReactNode;
    hintWrap?: boolean;
    onClick?: (e: MouseEvent) => void;
    danger?: boolean;
    disabled?: boolean;
    wrapper?: HTMLAttributes<HTMLElement>;
    action?: MenuItemAction;
    actions?: readonly MenuItemAction[];
    isMobile?: boolean;
}): import("react").JSX.Element;
