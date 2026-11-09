// Чип строки контекста хода (ADR-023): общий корпус для чипов ветки, объекта, «Чем», референсов и «Формата».
// Вынесен из ContextRowView, чтобы чип «Формат» (components/generation/format) не тянул строку циклом импорта.
import { createContext, useContext, type KeyboardEvent, type ReactNode } from 'react';
import { C, FONT, FS, R, SP } from '../../lib/design';

export const CHIP_H = 22;

// Чипы внутри губы плоские: рамку держит сама губа, вторая рамка у каждого чипа — «рамка в рамке».
// Остаются только рамки-сигналы: пунктир серого референса и цвет borderColor
export const FlatChips = createContext(false);
// Высота чипа: на телефоне крупнее — это тач-цель под палец
export const ChipHeight = createContext(CHIP_H);
// Телефон, строки «Где» и «С чем»: чип ужимается по ширине строки (имя — многоточием), а не уезжает за край
export const ShrinkChips = createContext(false);

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
  const flat = useContext(FlatChips);
  const chipH = useContext(ChipHeight);
  const shrink = useContext(ShrinkChips);
  const framed = !flat || dashed || borderColor != null || warn;
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
        display: 'inline-flex', alignItems: 'center', gap: SP.xs + 1, flexShrink: shrink ? 1 : 0, boxSizing: 'border-box',
        height: chipH, maxWidth: shrink ? `min(${max}px, 100%)` : max, padding: `0 ${SP.sm - 1}px`, borderRadius: R.md, minWidth: 0,
        border: framed ? `1px ${dashed ? 'dashed' : 'solid'} ${warn ? 'transparent' : borderColor ?? C.border}` : '1px solid transparent',
        background: warn ? C.warningBg : (flat ? 'transparent' : C.bgCard),
        color: warn ? C.warningText : C.textSecondary, fontSize: FS.sm, fontFamily: mono ? FONT.mono : FONT.sans, whiteSpace: 'nowrap',
        cursor: onClick ? 'pointer' : 'default', opacity: dim ? 0.55 : 1,
      }}
    >
      {children}
    </span>
  );
}

export const ellipsis = { overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', minWidth: 0 } as const;
