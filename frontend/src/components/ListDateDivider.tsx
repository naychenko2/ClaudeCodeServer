import { C } from '../lib/design';

/**
 * Разделитель групп в списках чатов: тонкая черта с датой по центру.
 * Заменяет дату на самих карточках — по разделителю видно, какие чаты
 * относятся к одному дню, и карточка не тратит на это место.
 */
export function ListDateDivider({ title }: { title: string }) {
  const line = { flex: 1, height: 1, background: C.divider };
  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: 8, padding: '10px 4px 7px' }}>
      <div style={line} />
      <span style={{ fontSize: 11, fontWeight: 700, color: C.textSecondary, whiteSpace: 'nowrap' }}>
        {title}
      </span>
      <div style={line} />
    </div>
  );
}
