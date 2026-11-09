export interface SelectGroup {
    label: string;
    disabled?: boolean;
}
export interface SelectOption<T extends string = string> {
    value: T;
    label: string;
    disabled?: boolean;
    group?: string | SelectGroup;
}
interface Props<T extends string> {
    value: T | '';
    onChange: (value: T | '') => void;
    options: SelectOption<T>[];
    placeholder?: string;
    disabled?: boolean;
    title?: string;
}
export declare function Select<T extends string = string>({ value, onChange, options, placeholder, disabled, title }: Props<T>): import("react").JSX.Element;
export {};
