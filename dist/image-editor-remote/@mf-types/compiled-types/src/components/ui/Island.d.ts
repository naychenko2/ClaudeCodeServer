import type { CSSProperties, HTMLAttributes, Ref, ReactNode } from 'react';
export declare function Island({ bg, borderColor, shadow, style, rootProps, rootRef, children }: {
    bg?: string;
    borderColor?: string;
    shadow?: string;
    style?: CSSProperties;
    rootProps?: HTMLAttributes<HTMLDivElement>;
    rootRef?: (el: HTMLDivElement | null) => void;
    children: ReactNode;
}): import("react").JSX.Element;
export declare function IslandHeader({ icon, title, badge, actions, headerProps, children, leading }: {
    icon?: ReactNode;
    title: string;
    badge?: string | null;
    leading?: ReactNode;
    actions?: ReactNode;
    headerProps?: HTMLAttributes<HTMLDivElement> & {
        draggable?: boolean;
        ref?: Ref<HTMLDivElement>;
    };
    children?: ReactNode;
}): import("react").JSX.Element;
