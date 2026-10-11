import { type ReactNode } from 'react';
export declare function PanelHeaderSlot({ children, side, pinned }: {
    children: ReactNode;
    side?: 'left' | 'right';
    pinned?: boolean;
}): import("react").ReactPortal | null;
