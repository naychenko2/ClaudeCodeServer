import type { PointerEvent as ReactPointerEvent } from 'react';
export declare function IslandSplitter({ orientation, active, onMouseDown, gap, length }: {
    orientation?: 'v' | 'h';
    active: boolean;
    onMouseDown: (e: ReactPointerEvent) => void;
    gap?: number;
    length?: number;
}): import("react").JSX.Element;
