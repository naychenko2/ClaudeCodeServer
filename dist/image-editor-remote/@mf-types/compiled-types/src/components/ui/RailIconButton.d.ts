import { type HTMLAttributes, type ReactNode } from 'react';
import { type IconButtonVariant } from './IconButton';
import { type RailFlyoutAction } from './RailFlyout';
import type { BadgeTone } from './CountBadge';
export declare function RailIconButton({ side, label, hint, actions, active, disabled, variant, wrapper, hoverSuppressed, onClick, onHoverChange, children, }: {
    side: 'left' | 'right';
    variant?: IconButtonVariant;
    label: string;
    hint?: string | readonly {
        text: string;
        tone?: BadgeTone;
    }[];
    actions?: readonly RailFlyoutAction[];
    active?: boolean;
    disabled?: boolean;
    wrapper?: HTMLAttributes<HTMLElement>;
    hoverSuppressed?: boolean;
    onClick?: () => void;
    onHoverChange?: (hover: boolean) => void;
    children: ReactNode | ((hover: boolean) => ReactNode);
}): import("react").JSX.Element;
