// Кирпичи панели «Звук»: подпись секции, карточка-опция в две строки, иконка, подсказка

import type { ReactNode } from 'react';
import type { User } from 'lucide-react';
import { Button, C, FS, SP, ICON_SIZE, ICON_STROKE } from 'aihome_shell/kit';

export const ic = (I: typeof User, size: number = ICON_SIZE.xs) => <I size={size} strokeWidth={ICON_STROKE} />;

export function Label({ children, aside }: { children: ReactNode; aside?: ReactNode }) {
  return (
    <div style={{ display: 'flex', alignItems: 'baseline', gap: SP.sm, margin: `${SP.md}px 0 ${SP.xs}px` }}>
      <span style={{ flex: 1, fontSize: FS.xs, fontWeight: 600, color: C.textMuted, textTransform: 'uppercase', letterSpacing: 0.4 }}>
        {children}
      </span>
      {aside && <span style={{ fontSize: FS.xs, color: C.textMuted }}>{aside}</span>}
    </div>
  );
}

export function Hint({ children, warn }: { children: ReactNode; warn?: boolean }) {
  return <div style={{ fontSize: FS.xs, color: warn ? C.warningText : C.textMuted, marginTop: SP.xxs }}>{children}</div>;
}

// Опция в две строки. Недоступная — серая и пунктирная, причина — второй строкой и в подсказке;
// нажать её нельзя, но выбранной она остаётся (явный выбор не подменяем)
export function Opt({ on, name, hint, badges, disabled, title, onClick, dataKey }: {
  on: boolean; name: ReactNode; hint?: ReactNode; badges?: ReactNode; disabled?: boolean; title?: string;
  onClick: () => void; dataKey?: string;
}) {
  return (
    <span data-opt={dataKey} data-on={on ? 'true' : undefined} data-disabled={disabled ? 'true' : undefined} title={title}
      style={{ display: 'inline-flex', maxWidth: '100%' }}>
      <Button size="sm" variant={on ? 'ghostAccent' : 'secondary'} disabled={disabled} title={title} onClick={onClick}
        style={{
          height: 'auto', padding: `${SP.xs}px ${SP.md}px`, textAlign: 'left', maxWidth: '100%',
          border: `1px ${disabled ? 'dashed' : 'solid'} ${on ? C.accent : C.border}`,
        }}>
        <span style={{ display: 'flex', flexDirection: 'column', alignItems: 'flex-start', gap: 1, fontWeight: 400, minWidth: 0 }}>
          <span style={{ display: 'inline-flex', alignItems: 'center', gap: SP.xs, flexWrap: 'wrap' }}>{name}{badges}</span>
          {hint && <span style={{ fontSize: FS.xs, color: C.textMuted, whiteSpace: 'normal' }}>{hint}</span>}
        </span>
      </Button>
    </span>
  );
}

export function Row({ children }: { children: ReactNode }) {
  return <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.xs }}>{children}</div>;
}
