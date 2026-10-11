import type { ReactNode, CSSProperties, MouseEvent, KeyboardEvent, AriaAttributes } from 'react';
type PressKey = Pick<KeyboardEvent, 'key' | 'target' | 'currentTarget' | 'preventDefault'>;
export declare function handlePressableKey<E extends PressKey>(e: E, onPress: (e: E) => void): boolean;
export declare function PressableArea({ onPress, title, children, feedback, className, style, 'aria-label': ariaLabel, 'aria-haspopup': ariaHaspopup, }: {
    onPress: (e: MouseEvent | KeyboardEvent) => void;
    title?: string;
    children?: ReactNode;
    feedback?: boolean;
    className?: string;
    style?: CSSProperties;
    'aria-label'?: string;
    'aria-haspopup'?: AriaAttributes['aria-haspopup'];
}): import("react").JSX.Element;
export {};
