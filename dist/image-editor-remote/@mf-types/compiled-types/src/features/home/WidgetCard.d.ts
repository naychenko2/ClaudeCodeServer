import { type ReactNode } from 'react';
export declare function WidgetCard({ icon, title, action, fill, onCreate, createTitle, children }: {
    icon?: ReactNode;
    title: string;
    action?: ReactNode;
    fill?: boolean;
    onCreate?: () => void;
    createTitle?: string;
    children: ReactNode;
}): import("react").JSX.Element;
export declare function MiniSegment<T extends string>({ value, options, onChange }: {
    value: T;
    options: {
        value: T;
        label: string;
        title?: string;
        dot?: string;
    }[];
    onChange: (v: T) => void;
}): import("react").JSX.Element;
export declare function WidgetAction({ label, onClick }: {
    label: string;
    onClick: () => void;
}): import("react").JSX.Element;
export declare function WidgetEmpty({ text }: {
    text: string;
}): import("react").JSX.Element;
export declare function relTime(iso: string): string;
