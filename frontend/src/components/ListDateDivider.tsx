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
 * align='left' + dense — вариант для плотных списков (панель «Документы»): подпись прижата
 * влево, слева от неё остаётся короткий отрезок черты, отступы вдвое меньше.
 */
export function ListDateDivider({ title, align = 'center', dense = false, flash = false }: {
  title: string;
  align?: 'center' | 'left';
  dense?: boolean;
  // Кратко подсветить и мигнуть — «вот сюда прокрутили»
  flash?: boolean;
}) {
  const lineColor = flash ? C.accent : C.divider;
  const line = { flex: 1, height: 1, background: lineColor };
  const stub = { width: 10, height: 1, background: lineColor, flexShrink: 0 };
  return (
    <div
      style={{
        display: 'flex', alignItems: 'center', gap: 8,
        padding: dense ? '5px 4px 3px' : '10px 4px 7px',
      }}
    >
      <div style={align === 'left' ? stub : line} />
      <span style={{
        fontSize: 11, fontWeight: 700, whiteSpace: 'nowrap',
        color: flash ? C.accent : C.textSecondary,
      }}>
        {title}
      </span>
      <div style={line} />
    </div>
  );
}
