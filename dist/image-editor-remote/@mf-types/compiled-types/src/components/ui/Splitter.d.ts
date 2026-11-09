import type { PointerEvent as ReactPointerEvent } from 'react';
export declare function Splitter({ orientation, active, onMouseDown }: {
    orientation?: 'v' | 'h';
    active: boolean;
    onMouseDown: (e: ReactPointerEvent) => void;
}): import("react").JSX.Element;
