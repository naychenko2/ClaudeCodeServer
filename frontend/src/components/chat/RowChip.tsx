// Чип строки контекста хода (ADR-023): общий корпус для чипов ветки, объекта, «Чем», референсов и «Формата».
// Вынесен из ContextRowView, чтобы чип «Формат» (components/generation/format) не тянул строку циклом импорта.
import type { KeyboardEvent, ReactNode } from 'react';
import { C, FONT, FS, R, SP } from '../../lib/design';

export const CHIP_H = 22;

const act = (fn: () => void) => (e: KeyboardEvent) => {
  if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); fn(); }
};

export function RowChip({ kind, max, title, onClick, borderColor, dim, dashed, mono, warn, children }: {
  kind: string;
  max: number;
  title?: string;
  onClick?: (e: { currentTarget: HTMLElement }) => void;
  borderColor?: string;
  dim?: boolean;
  dashed?: boolean;
  mono?: boolean;
  // Предупреждение: жёлтая плашка (округлённый размер, сцена не в формате фильма)
  warn?: boolean;
  children: ReactNode;
}) {
  return (
    <span
      data-chip={kind}
      {...(dashed ? { 'data-gray': '' } : null)}
      title={title}
      role={onClick ? 'button' : undefined}
      tabIndex={onClick ? 0 : undefined}
      onClick={onClick ? e => onClick(e) : undefined}
      onKeyDown={onClick ? e => act(() => onClick({ currentTarget: e.currentTarget as HTMLElement }))(e) : undefined}
      style={{
        display: 'inline-flex', alignItems: 'center', gap: SP.xs + 1, flexShrink: 0, boxSizing: 'border-box',
        height: CHIP_H, maxWidth: max, padding: `0 ${SP.sm - 1}px`, borderRadius: R.md, minWidth: 0,
        border: `1px ${dashed ? 'dashed' : 'solid'} ${warn ? 'transparent' : borderColor ?? C.border}`, background: warn ? C.warningBg : C.bgCard,
        color: warn ? C.warningText : C.textSecondary, fontSize: FS.sm, fontFamily: mono ? FONT.mono : FONT.sans, whiteSpace: 'nowrap',
        cursor: onClick ? 'pointer' : 'default', opacity: dim ? 0.55 : 1,
      }}
    >
      {children}
    </span>
  );
}

export const ellipsis = { overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', minWidth: 0 } as const;

