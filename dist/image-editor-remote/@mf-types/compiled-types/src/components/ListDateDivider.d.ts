import { type ReactNode } from 'react';
export declare const LIST_FLASH_CLASS = "cc-list-flash";
export declare const LIST_FLASH_MS = 520;
/**
 * Разделитель групп в списках: тонкая черта с подписью.
 * В списках чатов заменяет дату на самих карточках — по разделителю видно, какие чаты
 * относятся к одному дню, и карточка не тратит на это место.
 *
 * dense — вариант для плотных списков (панели «Документация» и «Сервисы»): отступы
 * вдвое меньше, высота подписи совпадает со строкой файла.
 *
 * plain — вариант без черт: одна подпись у левого края, липнущая к верху при прокрутке.
 * Стоит там, где сами строки списка рамок не имеют (чаты): черта у заголовка была бы
 * единственной линией на экране и читалась бы как граница блока, а не как метка группы.
 *
 * С onClick разделитель становится переключателем группы (свернуть/развернуть) — корень
 * тогда кнопка, а не div: у сворачивания есть клавиатура и фокус, самодельный кликабельный
 * div их теряет. leading/trailing — место под шеврон и счётчик скрытых строк.
 */
export declare function ListDateDivider({ title, dense, plain, flash, onClick, leading, trailing, titleAttr, highlightOnHover, active, onLineClick, lineTitleAttr, onLineHover, beforeTitle, titleOverlay, }: {
    title: string;
    dense?: boolean;
    plain?: boolean;
    flash?: boolean;
    onClick?: () => void;
    leading?: ReactNode;
    trailing?: ReactNode;
    titleAttr?: string;
    highlightOnHover?: boolean;
    active?: boolean;
    onLineClick?: () => void;
    lineTitleAttr?: string;
    onLineHover?: (hovering: boolean) => void;
    beforeTitle?: ReactNode;
    titleOverlay?: ReactNode;
}): import("react").JSX.Element;
