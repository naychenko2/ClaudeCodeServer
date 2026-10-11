interface ToggleProps {
    checked: boolean;
    onChange: (v: boolean) => void;
    disabled?: boolean;
    width?: number;
    height?: number;
    /** Делает тумблер фокусируемым с клавиатуры (Tab) и включает управление стрелками ←/→. */
    focusable?: boolean;
    /** Вызывается при нажатии Enter, когда тумблер в фокусе (например, отправка формы). */
    onEnter?: () => void;
    /** Доступное имя переключателя: без него скринридер читает безымянный «switch». */
    ariaLabel?: string;
}
export declare const Toggle: import("react").ForwardRefExoticComponent<ToggleProps & import("react").RefAttributes<HTMLDivElement>>;
export {};
