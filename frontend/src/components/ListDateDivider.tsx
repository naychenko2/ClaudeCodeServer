import { C } from '../lib/design';

/**
 * Разделитель групп в списках: тонкая черта с подписью.
 * В списках чатов заменяет дату на самих карточках — по разделителю видно, какие чаты
 * относятся к одному дню, и карточка не тратит на это место.
 *
 * align='left' + dense — вариант для плотных списков (панель «Документы»): подпись прижата
 * влево, слева от неё остаётся короткий отрезок черты, отступы вдвое меньше.
 */
export function ListDateDivider({ title, align = 'center', dense = false }: {
  title: string;
  align?: 'center' | 'left';
  dense?: boolean;
}) {
  const line = { flex: 1, height: 1, background: C.divider };
  const stub = { width: 10, height: 1, background: C.divider, flexShrink: 0 };
  return (
    <div style={{
      display: 'flex', alignItems: 'center', gap: 8,
      padding: dense ? '5px 4px 3px' : '10px 4px 7px',
    }}>
      <div style={align === 'left' ? stub : line} />
      <span style={{ fontSize: 11, fontWeight: 700, color: C.textSecondary, whiteSpace: 'nowrap' }}>
        {title}
      </span>
      <div style={line} />
    </div>
  );
}
