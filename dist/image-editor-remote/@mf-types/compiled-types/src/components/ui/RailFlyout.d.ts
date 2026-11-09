import { type CSSProperties, type ReactNode } from 'react';
import type { LucideIcon } from 'lucide-react';
import type { BadgeTone } from './CountBadge';
export interface RailFlyoutAction {
    Icon: LucideIcon;
    title: string;
    onClick: () => void;
}
export declare function RailFlyout({ side, label, hint, open, actions, railWidth, hostStyle, standalone, onDismiss, children }: {
    side: 'left' | 'right';
    label: string;
    hint?: string | readonly {
        text: string;
        tone?: BadgeTone;
    }[];
    open: boolean;
    actions?: readonly RailFlyoutAction[];
    railWidth: number;
    hostStyle?: CSSProperties;
    standalone?: boolean;
    onDismiss?: () => void;
    children: ReactNode;
}): import("react").JSX.Element;
