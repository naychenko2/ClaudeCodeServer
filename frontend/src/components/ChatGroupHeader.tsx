import type { ReactNode } from 'react';
import { C, FS } from '../lib/design';
import { CountBadge } from './ui';

// Заголовок секции списка чатов в режимах «Теги» и «Темы»: [маркер] имя [метка] счётчик
// ··· [действия справа]. Один компонент на оба режима — второй заголовок, собранный
// руками, разъехался бы с первым на первой же правке размеров.
// sticky — липнет к верху скролла (у тем секции длинные: иначе не понять, к какой теме
// строки под курсором); фон bgWhite — тот же приём, что у ListDateDivider plain.
export function ChatGroupHeader({ title, count, marker, badge, muted, sticky, actions, isMobile }: {
  title: string;
  count: number;
  // Слева от имени (цветовая точка тега)
  marker?: ReactNode;
  // Сразу после имени (✦ у темы, предложенной AI)
  badge?: ReactNode;
  // Тихий тон имени — у хвостовых секций «Без тегов» / «Без темы»
  muted?: boolean;
  sticky?: boolean;
  // Справа: кнопки порядка, меню ⋮, «Разложить»
  actions?: ReactNode;
  // Тач: строка не ниже 40 (цель кнопки ⋮)
  isMobile?: boolean;
}) {
  return (
    <div style={{
      display: 'flex', alignItems: 'center', gap: 6, padding: '10px 4px 7px',
      minHeight: isMobile ? 40 : undefined, boxSizing: 'border-box',
      ...(sticky ? { position: 'sticky', top: 0, zIndex: 2, background: C.bgWhite } : null),
    }}>
      {marker}
      <span style={{
        fontSize: FS.xs, fontWeight: 700, color: muted ? C.textMuted : C.textSecondary,
        overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', minWidth: 0,
      }}>
        {title}
      </span>
      {badge}
      <CountBadge inline tone="muted" value={count} />
      <span style={{ flex: 1 }} />
      {actions}
    </div>
  );
}
