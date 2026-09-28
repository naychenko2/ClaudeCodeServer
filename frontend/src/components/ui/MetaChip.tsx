import type { CSSProperties, ReactNode } from 'react';
import { C, FS, R, SP } from '../../lib/design';

// Чип метаданных в шапке документа центра: «N типов», «собрано 27.07 14:02»,
// «обновил Гриша · 5 мин назад». Спокойный, без акцента — это подпись, а не действие.
// Вынесен из CodeGraphDocument, когда понадобился второй раз (документ «Архитектура»).
export function MetaChip({ children, title, style }: { children: ReactNode; title?: string; style?: CSSProperties }) {
  return (
    <span title={title} style={{
      fontSize: FS.xs, color: C.textSecondary, background: C.bgCard, border: `1px solid ${C.borderLight}`,
      borderRadius: R.max, padding: `${SP.xs}px ${SP.sm}px`, whiteSpace: 'nowrap', display: 'inline-flex', gap: 4, alignItems: 'center',
      flexShrink: 0, ...style,
    }}>{children}</span>
  );
}
