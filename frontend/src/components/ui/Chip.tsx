import type { ReactNode } from 'react';
import { C, FONT, FS, R, SP } from '../../lib/design';

// Пилюля-чип: фильтр, действие, выбранный объект.
//  • variant="soft" (по умолчанию) — светлый чип; selected — акцентный фон и рамка.
//    dashed — пунктирная рамка («+ уровень», «Сбросить»).
//  • variant="toggle" — чип мультивыбора (фильтры чатов): selected заливается accent.
//    large — крупная тач-версия этого варианта.
// maxW — потолок ширины: подпись обрезается многоточием, крестик onRemove остаётся
// снаружи обрезки и достижим. touch — тач-цель: та же форма, но не ниже 32px.
// leading — миниатюра/аватар слева (18px), onRemove — крестик справа.
export function Chip({
  children, onClick, selected, dashed, title, maxW, touch, leading, onRemove,
  variant = 'soft', large,
}: {
  children: ReactNode;
  onClick?: () => void;
  selected?: boolean;
  dashed?: boolean;
  title?: string;
  maxW?: number | string;
  touch?: boolean;
  leading?: ReactNode;
  onRemove?: () => void;
  variant?: 'soft' | 'toggle';
  large?: boolean;
}) {
  const body = maxW !== undefined
    ? <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', minWidth: 0 }}>{children}</span>
    : children;
  const content = (
    <>
      {leading && <span style={{ display: 'inline-flex', width: 18, height: 18, flexShrink: 0, borderRadius: R.full, overflow: 'hidden' }}>{leading}</span>}
      {body}
      {onRemove && <ChipX onClick={onRemove} touch={touch} />}
    </>
  );

  if (variant === 'toggle') {
    return (
      <button
        onClick={onClick}
        title={title}
        style={{
          padding: large ? '7px 13px' : '4px 10px',
          borderRadius: R.pill,
          border: `1px solid ${selected ? C.accent : C.borderLight}`,
          background: selected ? C.accent : 'transparent',
          color: selected ? C.onAccent : C.textSecondary,
          fontSize: large ? FS.base : FS.sm,
          fontWeight: 600, fontFamily: FONT.sans, cursor: 'pointer',
          display: 'inline-flex', alignItems: 'center', gap: SP.xs,
          transition: 'background 0.12s, border-color 0.12s',
          ...(maxW !== undefined ? { maxWidth: maxW, overflow: 'hidden' } : null),
        }}
      >
        {content}
      </button>
    );
  }

  return (
    <span
      onClick={onClick}
      title={title}
      style={{
        display: 'inline-flex', alignItems: 'center', gap: 5, fontSize: FS.xs,
        padding: touch ? `${SP.sm}px 10px` : '3px 10px',
        borderRadius: R.max, whiteSpace: 'nowrap', fontFamily: FONT.sans, flexShrink: 0,
        border: `1px ${dashed ? 'dashed' : 'solid'} ${selected ? C.accentMuted : C.border}`,
        background: selected ? C.accentLight : C.bgCard,
        color: selected ? C.accent : C.textSecondary,
        fontWeight: selected ? 600 : 400,
        cursor: onClick ? 'pointer' : 'default',
        ...(maxW !== undefined ? { maxWidth: maxW, overflow: 'hidden' } : null),
      }}
    >
      {content}
    </span>
  );
}

// Крестик внутри чипа. touch — тач-площадь: без неё цель 8×14px,
// пальцем в неё не попасть (замер на планшете)
export function ChipX({ onClick, touch }: { onClick: () => void; touch?: boolean }) {
  return (
    <span
      onClick={e => { e.stopPropagation(); onClick(); }}
      style={touch
        ? {
            fontWeight: 700, opacity: 0.8, cursor: 'pointer', flexShrink: 0,
            display: 'inline-flex', alignItems: 'center', justifyContent: 'center',
            minWidth: SP.xxl, minHeight: SP.xxl, margin: `-${SP.sm}px -${SP.sm}px -${SP.sm}px 0`,
          }
        : { fontWeight: 700, opacity: 0.8, cursor: 'pointer', padding: '0 1px' }}
    >
      ×
    </span>
  );
}
