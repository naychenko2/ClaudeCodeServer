import { C } from '../lib/design';

// Мигание разделителя (переход к группе из списка): три коротких цикла по прозрачности —
// без подложки, чтобы не спорить с выделением строки. Инжектим один раз, как focus-ring
// у IconButton.
const FLASH_CLASS = 'cc-divider-flash';
if (typeof document !== 'undefined' && !document.getElementById('cc-divider-flash-style')) {
  const el = document.createElement('style');
  el.id = 'cc-divider-flash-style';
  el.textContent =
    `@keyframes ccDividerFlash{0%,100%{opacity:1}50%{opacity:.2}}` +
    `.${FLASH_CLASS}{animation:ccDividerFlash .3s ease-in-out 3}`;
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
      className={flash ? FLASH_CLASS : undefined}
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
