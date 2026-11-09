import { type CSSProperties, type HTMLAttributes, type ReactNode } from 'react';
import { type LucideIcon } from 'lucide-react';
interface PanelShellProps {
    icon?: ReactNode;
    title: string;
    badge?: string | null;
    headerActions?: ReactNode;
    toolbar?: ReactNode;
    children: ReactNode;
    noScroll?: boolean;
    contentStyle?: CSSProperties;
    draggable?: boolean;
    dragged?: boolean;
    dropTarget?: boolean;
    flash?: boolean;
    highlighted?: boolean;
    rootProps?: HTMLAttributes<HTMLDivElement>;
    rootRef?: (el: HTMLDivElement | null) => void;
    headerProps?: HTMLAttributes<HTMLDivElement> & {
        draggable?: boolean;
    };
    bare?: boolean;
    onClose?: () => void;
    closeMode?: 'button' | 'icon';
    iconAction?: {
        Icon: LucideIcon;
        title: string;
        onClick: () => void;
    };
    fill?: boolean;
    hideIfEmpty?: boolean;
    style?: CSSProperties;
    slideDirection?: 'up' | 'left';
    animate?: boolean;
}
export declare function PanelShell({ icon, title, badge, headerActions, toolbar, children, noScroll, contentStyle, draggable, dragged, dropTarget, flash, highlighted, rootProps, rootRef, headerProps, bare, onClose, closeMode, iconAction, fill, hideIfEmpty, style, slideDirection, animate, }: PanelShellProps): import("react").JSX.Element | null;
export {};
