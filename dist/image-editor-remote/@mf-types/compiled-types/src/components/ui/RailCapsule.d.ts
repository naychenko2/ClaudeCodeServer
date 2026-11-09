import type { CSSProperties, HTMLAttributes, ReactNode } from 'react';
export declare const RAIL_W = 40;
export declare const RAIL_GAP = 8;
export declare const RAIL_ITEM_GAP = 6;
interface Props extends HTMLAttributes<HTMLDivElement> {
    side: 'left' | 'right';
    visible?: boolean;
    gapToCenter?: number;
    border?: string;
    background?: string;
    style?: CSSProperties;
    children: ReactNode;
}
export declare function RailCapsule({ side, visible, gapToCenter, border, background, style, children, ...rest }: Props): import("react").JSX.Element;
export {};
