import type { ReactNode } from 'react';
export interface TabItem<T extends string> {
    value: T;
    label: string;
    icon?: ReactNode;
    count?: number;
}
export declare function Tabs<T extends string>({ value, items, onChange, ariaLabel, transparent }: {
    value: T;
    items: TabItem<T>[];
    onChange: (v: T) => void;
    ariaLabel?: string;
    transparent?: boolean;
}): import("react").JSX.Element;
