import type { ReactNode } from 'react';
export declare function Chip({ children, onClick, selected, dashed, title, maxW, touch, leading, onRemove, variant, large, dense, removeLabel, keyboard, }: {
    children: ReactNode;
    onClick?: () => void;
    selected?: boolean;
    dashed?: boolean;
    title?: string;
    maxW?: number | string;
    touch?: boolean;
    leading?: ReactNode;
    onRemove?: () => void;
    removeLabel?: string;
    variant?: 'soft' | 'toggle';
    large?: boolean;
    dense?: boolean;
    keyboard?: boolean;
}): import("react").JSX.Element;
export declare const CHIP_FOCUS_CLASS = "cc-chip-kb";
export declare function chipKeyboardProps(onClick: () => void): {
    role: "button";
    tabIndex: number;
    className: string;
    onKeyDown: (e: {
        key: string;
        preventDefault: () => void;
    }) => void;
};
export declare function ChipX({ onClick, touch, label }: {
    onClick: () => void;
    touch?: boolean;
    label?: string;
}): import("react").JSX.Element;
