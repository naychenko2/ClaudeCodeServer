import type { PointerEvent as ReactPointerEvent } from 'react';
export declare function IslandSidebarSplitter({ active, onMouseDown, onCollapse }: {
    active: boolean;
    onMouseDown: (e: ReactPointerEvent) => void;
    onCollapse: () => void;
}): import("react").JSX.Element;
