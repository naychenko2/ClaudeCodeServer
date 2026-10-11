export interface CheckboxProps {
    checked: boolean;
    onChange: (v: boolean) => void;
    disabled?: boolean;
    /** Доступное имя: без него скринридер читает безымянный «checkbox». */
    ariaLabel?: string;
}
export declare const Checkbox: import("react").ForwardRefExoticComponent<CheckboxProps & import("react").RefAttributes<HTMLButtonElement>>;
