import type { ReactNode } from 'react';
export declare function PanelSlot({ weight, fill, slotRef, children }: {
    weight?: number;
    fill?: boolean;
    slotRef?: (el: HTMLDivElement | null) => void;
    children: ReactNode;
}): import("react").JSX.Element;
