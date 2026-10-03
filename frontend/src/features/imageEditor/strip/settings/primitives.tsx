// Общие кирпичи секций настроек картинок: подпись секции, выбор в две строки, иконка.
// Секции собирают и карточка над полосой «Картинки», и боковая панель генерации (ADR-021 §3).

import type { CSSProperties, ReactNode } from 'react';
import type { User } from 'lucide-react';
import { Button, C, FS, SP, ICON_SIZE, ICON_STROKE } from 'aihome_shell/kit';
import type { useThreadLaunch } from '../../thread/useThreadLaunch';

export type Launch = ReturnType<typeof useThreadLaunch>;

export const ic = (I: typeof User, size: number = ICON_SIZE.xs) => <I size={size} strokeWidth={ICON_STROKE} />;

// Обёртка чипа: на телефоне тач-цель не ниже 40 — чип touch сам по себе 36
export const touchWrap = (touch?: boolean): CSSProperties =>
  (touch ? { display: 'inline-flex', alignItems: 'center', minHeight: 40 } : { display: 'inline-flex' });

export function Label({ children }: { children: ReactNode }) {
  return (
    <div style={{ fontSize: FS.xs, fontWeight: 600, color: C.textMuted, textTransform: 'uppercase', letterSpacing: 0.4, margin: `${SP.md}px 0 ${SP.xs}px` }}>
      {children}
    </div>
  );
}

// Выбор в карточке настроек: имя и подсказка в две строки
export function Opt({ on, name, hint, disabled, title, onClick }: {
  on: boolean; name: string; hint?: ReactNode; disabled?: boolean; title?: string; onClick: () => void;
}) {
  return (
    <Button size="sm" variant={on ? 'ghostAccent' : 'secondary'} disabled={disabled} title={title} onClick={onClick}
      style={{ height: 'auto', padding: `${SP.xs}px ${SP.md}px`, border: `1px solid ${on ? C.accent : C.border}`, textAlign: 'left' }}>
      <span style={{ display: 'flex', flexDirection: 'column', alignItems: 'flex-start', gap: 1, fontWeight: 400 }}>
        <span>{name}</span>
        {hint && <span style={{ fontSize: FS.xs, color: C.textMuted }}>{hint}</span>}
      </span>
    </Button>
  );
}
