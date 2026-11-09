import { type ReactNode } from 'react';
import { type SegmentOptionState } from './Segmented';
export interface IconSegmentedOption<T extends string> extends SegmentOptionState {
    value: T;
    label: string;
    icon: ReactNode;
}
export declare function IconSegmented<T extends string>({ value, options, onChange, style, quiet, persistKey, size }: {
    value: T;
    options: IconSegmentedOption<T>[];
    onChange: (v: T) => void;
    style?: React.CSSProperties;
    quiet?: boolean;
    persistKey?: string;
    size?: 'sm' | 'lg';
}): import("react").JSX.Element;
