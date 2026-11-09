import type { PointerEvent as ReactPointerEvent, ReactNode } from 'react';
export declare function IslandScaffold({ sidebarOpen, sidebar, sidebarWidth, sidebarDragging, onSidebarDrag, onSidebarCollapse, center, centerBare, centerContentWidth, left, right }: {
    sidebarOpen?: boolean;
    sidebar?: ReactNode;
    sidebarWidth?: number;
    sidebarDragging?: boolean;
    onSidebarDrag?: (e: ReactPointerEvent) => void;
    onSidebarCollapse?: () => void;
    center: ReactNode;
    centerBare?: boolean;
    centerContentWidth?: number;
    left?: ReactNode;
    right?: ReactNode;
}): import("react").JSX.Element;
