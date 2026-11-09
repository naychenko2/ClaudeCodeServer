import { type ReactNode } from 'react';
export declare function SidebarSection({ title, count, hint, actions, collapsedActions, storageKey, defaultOpen, open: openProp, onToggle, children }: {
    title: string;
    count?: number;
    hint?: string;
    actions?: ReactNode;
    collapsedActions?: ReactNode;
    storageKey?: string;
    defaultOpen?: boolean;
    open?: boolean;
    onToggle?: () => void;
    children: ReactNode;
}): import("react").JSX.Element;
