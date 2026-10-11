import type { ReactNode } from 'react';
export declare function PillSwitch<T extends string>({ value, options, onChange, fill, isMobile, draggable, persistKey, compact, autoCompact, iconsOnly, variant, renderOption }: {
    value: T;
    options: {
        value: T;
        label: string;
        icon?: ReactNode;
        title?: string;
    }[];
    onChange: (v: T) => void;
    fill?: boolean;
    isMobile?: boolean;
    draggable?: boolean;
    persistKey?: string;
    compact?: boolean;
    /** Авто-компакт: включает compact при переполнении трека */
    autoCompact?: boolean;
    /** Только иконки у всех сегментов (узкие тулбары); подпись уезжает в title/aria-label */
    iconsOnly?: boolean;
    variant?: 'default' | 'hub';
    /** Кастомный рендер конкретного сегмента (вернуть null — обычная кнопка). Активный
     *  кастомный сегмент рисует свой фон и подавляет скользящую пилюлю под собой. */
    renderOption?: (opt: {
        value: T;
        label: string;
        icon?: ReactNode;
    }, active: boolean) => ReactNode;
}): import("react").JSX.Element;
