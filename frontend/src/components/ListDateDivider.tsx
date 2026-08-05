import type { ReactNode } from 'react';
import { C } from '../lib/design';

// Мигание при переходе к группе списка: одно короткое затухание по прозрачности — без
// подложки, чтобы не спорить с выделением строки. Класс экспортируется: вешать его можно и
// на всю секцию целиком (разделитель + её строки), а не только на подпись.
// Инжектим один раз, как focus-ring у IconButton.
export const LIST_FLASH_CLASS = 'cc-list-flash';
export const LIST_FLASH_MS = 320;   // один цикл 300 мс + запас на снятие класса

if (typeof document !== 'undefined' && !document.getElementById('cc-list-flash-style')) {
  const el = document.createElement('style');
  el.id = 'cc-list-flash-style';
  el.textContent =
    `@keyframes ccListFlash{0%,100%{opacity:1}50%{opacity:.2}}` +
    `.${LIST_FLASH_CLASS}{animation:ccListFlash .3s ease-in-out 1}`;
  document.head.appendChild(el);
}

/**
 * Разделитель групп в списках: тонкая черта с подписью.
 * В списках чатов заменяет дату на самих карточках — по разделителю видно, какие чаты
 * относятся к одному дню, и карточка не тратит на это место.
 *
 * align='left' + dense — вариант для плотных списков (панель «Документация»): подпись прижата
 * влево, слева от неё остаётся короткий отрезок черты, отступы вдвое меньше.
 *
 * С onClick разделитель становится переключателем группы (свернуть/развернуть) — корень
 * тогда кнопка, а не div: у сворачивания есть клавиатура и фокус, самодельный кликабельный
 * div их теряет. leading/trailing — место под шеврон и счётчик скрытых строк.
 */
export function ListDateDivider({
  title, subtitle, align = 'center', dense = false, flash = false, onClick, leading, trailing, titleAttr,
}: {
  title: string;
  // Приписка сразу после подписи, приглушённо: у групп документации это родительский
  // раздел. Название группы при этом остаётся коротким — путь целиком в него не влезает
  // и читается хуже, чем «где я» одним словом
  subtitle?: string;
  align?: 'center' | 'left';
  dense?: boolean;
  // Кратко подсветить и мигнуть — «вот сюда прокрутили»
  flash?: boolean;
  onClick?: () => void;
  leading?: ReactNode;
  trailing?: ReactNode;
  // Подсказка при наведении: у кликабельного разделителя объясняет, что будет по клику
  titleAttr?: string;
}) {
  const lineColor = flash ? C.accent : C.divider;
  const line = { flex: 1, height: 1, background: lineColor };
  const stub = { width: 10, height: 1, background: lineColor, flexShrink: 0 };
  const body = (
    <>
      {leading}
      <div style={align === 'left' ? stub : line} />
      <span style={{
        fontSize: 11, fontWeight: 700, whiteSpace: 'nowrap',
        color: flash ? C.accent : C.textSecondary,
      }}>
        {title}
      </span>
      {subtitle && (
        <span style={{
          fontSize: 10, fontWeight: 400, whiteSpace: 'nowrap',
          color: C.textMuted, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis',
        }}>
          {subtitle}
        </span>
      )}
      <div style={line} />
      {trailing}
    </>
  );
  const layout = {
    display: 'flex', alignItems: 'center', gap: 8,
    padding: dense ? '5px 4px 3px' : '10px 4px 7px',
  };
  if (!onClick) return <div style={layout}>{body}</div>;
  return (
    <button
      onClick={onClick}
      title={titleAttr}
      style={{
        ...layout,
        width: '100%', border: 'none', background: 'transparent',
        cursor: 'pointer', font: 'inherit', textAlign: 'left',
      }}
    >
      {body}
    </button>
  );
}
