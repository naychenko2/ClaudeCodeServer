// Кирпичи панели «Видео»: подпись секции, подсказка, иконка, опция-кнопка

import type { ReactNode } from 'react';
import type { User } from 'lucide-react';
import { C, FS, SP, ICON_SIZE, ICON_STROKE } from 'aihome_shell/kit';

export const ic = (I: typeof User, size: number = ICON_SIZE.xs) => <I size={size} strokeWidth={ICON_STROKE} />;

export function Label({ children, aside, by }: { children: ReactNode; aside?: ReactNode; by?: ReactNode }) {
  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, margin: `${SP.md}px 0 ${SP.xs}px` }}>
      <span style={{ fontSize: FS.xs, fontWeight: 600, color: C.textMuted, textTransform: 'uppercase', letterSpacing: 0.4 }}>
        {children}
      </span>
      {by}
      <span style={{ flex: 1 }} />
      {aside && <span style={{ fontSize: FS.xs, color: C.textMuted }}>{aside}</span>}
    </div>
  );
}

export function Hint({ children, warn }: { children: ReactNode; warn?: boolean }) {
  return <div style={{ fontSize: FS.xs, color: warn ? C.warningText : C.textMuted, marginTop: SP.xxs, overflowWrap: 'anywhere' }}>{children}</div>;
}
