import type { ReactNode } from 'react';
interface EmptyStateProps {
    icon: ReactNode;
    title: string;
    subtitle?: ReactNode;
    action?: ReactNode;
    compact?: boolean;
    inline?: boolean;
}
export declare function EmptyState({ icon, title, subtitle, action, compact, inline }: EmptyStateProps): import("react").JSX.Element;
export {};
