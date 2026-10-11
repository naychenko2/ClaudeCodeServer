import type { ReactNode, CSSProperties, KeyboardEvent, Ref } from 'react';
export declare function FieldLabel({ children }: {
    children: ReactNode;
}): import("react").JSX.Element;
export declare function Field({ label, hint, error, children }: {
    label?: ReactNode;
    hint?: ReactNode;
    error?: ReactNode;
    children: ReactNode;
}): import("react").JSX.Element;
interface TextFieldProps {
    value: string;
    onChange: (v: string) => void;
    placeholder?: string;
    type?: string;
    mono?: boolean;
    autoFocus?: boolean;
    disabled?: boolean;
    letterSpacing?: string;
    onEnter?: () => void;
    onFocus?: () => void;
    onBlur?: () => void;
    onEscape?: () => void;
    autoComplete?: string;
    title?: string;
    invalid?: boolean;
    inputMode?: 'numeric' | 'decimal' | 'text' | 'search';
    style?: CSSProperties;
}
export declare function TextField({ value, onChange, placeholder, type, mono, autoFocus, disabled, letterSpacing, onEnter, onFocus, onBlur, onEscape, title, invalid, inputMode, autoComplete, style, voice, isMobile }: TextFieldProps & {
    voice?: boolean;
    isMobile?: boolean;
}): import("react").JSX.Element;
interface TextAreaProps {
    value: string;
    onChange: (v: string) => void;
    placeholder?: string;
    autoGrow?: boolean;
    minHeight?: number;
    maxHeight?: number;
    disabled?: boolean;
    readOnly?: boolean;
    autoComplete?: string;
    autoFocus?: boolean;
    onKeyDown?: (e: KeyboardEvent<HTMLTextAreaElement>) => void;
    style?: CSSProperties;
}
export declare function TextArea({ value, onChange, placeholder, autoGrow, minHeight, maxHeight, disabled, readOnly, autoFocus, onKeyDown, autoComplete, style, voice, isMobile }: TextAreaProps & {
    voice?: boolean;
    isMobile?: boolean;
}): import("react").JSX.Element;
interface IconFieldProps {
    icon?: ReactNode;
    value: string;
    onChange: (v: string) => void;
    placeholder?: string;
    type?: string;
    mono?: boolean;
    disabled?: boolean;
    letterSpacing?: string;
    height?: number;
    radius?: number;
    fontSize?: number;
    style?: CSSProperties;
    autoComplete?: string;
    autoFocus?: boolean;
    onEnter?: () => void;
    inputRef?: Ref<HTMLInputElement>;
}
export declare function IconField({ icon, value, onChange, placeholder, type, mono, disabled, letterSpacing, height, radius, fontSize, style, autoFocus, onEnter, inputRef, autoComplete, voice, isMobile, }: IconFieldProps & {
    voice?: boolean;
    isMobile?: boolean;
}): import("react").JSX.Element;
export {};
