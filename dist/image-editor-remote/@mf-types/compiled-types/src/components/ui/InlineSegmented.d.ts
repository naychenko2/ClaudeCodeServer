import type { ReactNode } from 'react';
import { type SegmentOptionState } from './Segmented';
export declare function InlineSegmented<T extends string>({ value, options, onChange, disabled, isMobile, touchWidth }: {
    value: T | null;
    options: ({
        value: T;
        label: string;
        tone?: {
            bg: string;
            fg: string;
        };
        icon?: ReactNode;
    } & SegmentOptionState)[];
    onChange: (v: T) => void;
    disabled?: boolean;
    isMobile?: boolean;
    touchWidth?: boolean;
}): import("react").JSX.Element;
