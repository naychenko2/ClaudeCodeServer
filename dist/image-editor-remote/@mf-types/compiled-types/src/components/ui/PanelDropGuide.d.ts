import type { CSSProperties, DragEvent, HTMLAttributes } from 'react';
import type { LucideIcon } from 'lucide-react';
export declare const SEP_HIT = 22;
export declare const sepShift: (base: number) => number;
export declare function PanelDropSpot({ over, icon: Icon, boxProps, style }: {
    over?: boolean;
    icon?: LucideIcon;
    boxProps?: Pick<HTMLAttributes<HTMLDivElement>, 'onDragOver' | 'onDragLeave' | 'onDrop'>;
    style?: CSSProperties;
}): import("react").JSX.Element;
export declare function PanelDropLine({ axis, over, accent, shift, inset }: {
    axis: 'x' | 'y';
    over?: boolean;
    accent?: boolean;
    shift?: number;
    inset?: number;
}): import("react").JSX.Element;
export declare function PanelDropGuide({ axis, dndActive, over, base, edge, fill, icon: Icon, onDragOver, onDragLeave, onDrop }: {
    axis: 'x' | 'y';
    dndActive: boolean;
    over: boolean;
    base?: number;
    edge?: 'start' | 'end';
    fill?: boolean;
    icon?: LucideIcon;
    onDragOver: (e: DragEvent) => void;
    onDragLeave: () => void;
    onDrop: (e: DragEvent) => void;
}): import("react").JSX.Element;
