import { type DragEvent, type HTMLAttributes, type ReactNode } from 'react';
import { type LucideIcon } from 'lucide-react';
import { type BadgeTone } from './CountBadge';
import { RAIL_W, RAIL_GAP } from './RailCapsule';
export { RAIL_W, RAIL_GAP };
export interface RailItem {
    key: string;
    title: string;
    Icon: LucideIcon;
    active: boolean;
    badge?: number | null;
    badgeTone?: BadgeTone;
    badgeSecondary?: number | null;
    badgeSecondaryTone?: BadgeTone;
    dot?: BadgeTone;
    hint?: string | readonly {
        text: string;
        tone?: BadgeTone;
    }[];
    onClick: () => void;
    dragProps?: HTMLAttributes<HTMLElement> & {
        draggable?: boolean;
    };
    onHoverStart?: () => void;
    onHoverEnd?: () => void;
    pinnable?: boolean;
    onTuck?: () => void;
    onFlip?: () => void;
}
interface Props {
    side: 'left' | 'right';
    hat?: string;
    hatTitle?: string;
    groups: RailItem[][];
    visible?: boolean;
    overflow?: {
        items: RailItem[];
        modeToggle?: {
            soloMode: boolean;
            onToggle: () => void;
        };
        badge?: number | null;
        dragActive?: boolean;
        onRestore?: (key: string) => void;
        drop?: {
            active: boolean;
            over: boolean;
            onDragOver: (e: DragEvent) => void;
            onDragLeave: () => void;
            onDrop: (e: DragEvent) => void;
        };
    };
    gapToCenter?: number;
    collapse?: {
        collapsed: boolean;
        disabled: boolean;
        onToggle: () => void;
    };
    peek?: {
        node: ReactNode;
        full: boolean;
        onMouseEnter: () => void;
        onMouseLeave: () => void;
    };
    footer?: ReactNode;
    drop?: {
        active: boolean;
        over: boolean;
        icon?: LucideIcon;
        key?: string;
        onInsert?: (pos: {
            before: string | null;
        } | null) => void;
        onDragOver: (e: DragEvent) => void;
        onDragLeave: () => void;
        onDrop: (e: DragEvent) => void;
    };
}
export declare function PanelRail({ side, hat, hatTitle, groups, visible, gapToCenter, overflow, collapse, peek, drop, footer }: Props): import("react").JSX.Element;
