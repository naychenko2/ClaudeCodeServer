import type { HTMLAttributes, ReactNode } from 'react';
import type { LucideIcon } from 'lucide-react';
export type NoticeTone = 'warning' | 'danger' | 'info';
interface Props extends Omit<HTMLAttributes<HTMLDivElement>, 'title'> {
    tone?: NoticeTone;
    icon: LucideIcon;
    title?: ReactNode;
    children?: ReactNode;
    action?: ReactNode;
}
export declare function Notice({ tone, icon: Icon, title, children, action, style, ...rest }: Props): import("react").JSX.Element;
export {};
