import type { ReactNode } from 'react';
export type OverflowItem = {
    key: string;
    icon?: ReactNode;
    label: string;
    sublabel?: string;
    onClick?: () => void;
    toggle?: boolean;
    dot?: boolean;
    active?: boolean;
    danger?: boolean;
    disabled?: boolean;
    action?: {
        icon: ReactNode;
        title: string;
        onClick: () => void;
        disabled?: boolean;
    };
    keepOpen?: boolean;
    preview?: ReactNode;
    separator?: boolean;
};
type TriggerRenderer = (p: {
    open: boolean;
    toggle: () => void;
    ref: (el: HTMLElement | null) => void;
}) => ReactNode;
export declare function ToolbarOverflowMenu({ isMobile, items, children, title, triggerIcon, triggerLabel, triggerTitle, indicator, align, renderTrigger, openTrigger, triggerSize, }: {
    isMobile?: boolean;
    items?: OverflowItem[];
    children?: ReactNode;
    title?: string;
    triggerIcon?: ReactNode;
    triggerLabel?: string;
    triggerTitle?: string;
    indicator?: number | boolean | ReactNode;
    align?: 'left' | 'right';
    renderTrigger?: TriggerRenderer;
    openTrigger?: {
        counter: number;
        anchor?: DOMRect | null;
    };
    triggerSize?: number;
}): import("react").JSX.Element;
export {};
