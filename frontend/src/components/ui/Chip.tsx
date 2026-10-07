import type { ReactNode } from 'react';
import { C, FONT, FS, R, SHADOW, SP } from '../../lib/design';

// Пилюля-чип: фильтр, действие, выбранный объект.
//  • variant="soft" (по умолчанию) — светлый чип; selected — акцентный фон и рамка.
//    dashed — пунктирная рамка («+ уровень», «Сбросить»).
//  • variant="toggle" — чип мультивыбора (фильтры чатов): selected заливается accent.
//    large — крупная тач-версия этого варианта.
// maxW — потолок ширины: подпись обрезается многоточием, крестик onRemove остаётся
// снаружи обрезки и достижим. touch — тач-цель: та же форма, но не ниже 32px.
// leading — миниатюра/аватар слева (18px), onRemove — крестик справа.
// dense — узкие поля по бокам (6 вместо 10): тесный ряд, где каждый пиксель отдан подписи.
export function Chip({
  children, onClick, selected, dashed, title, maxW, touch, leading, onRemove,
  variant = 'soft', large, dense, removeLabel,
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
  removeLabel?: string;  // подпись крестика для скринридера и тултипа
  variant?: 'soft' | 'toggle';
  large?: boolean;
  dense?: boolean;
}) {
  const body = maxW !== undefined
    ? <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', minWidth: 0 }}>{children}</span>
    : children;
  const content = (
    <>
      {leading && <span style={{ display: 'inline-flex', width: 18, height: 18, flexShrink: 0, borderRadius: R.full, overflow: 'hidden' }}>{leading}</span>}
      {body}
      {onRemove && <ChipX onClick={onRemove} touch={touch} label={removeLabel} />}
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
        padding: `${touch ? SP.sm : 3}px ${dense ? 6 : 10}px`,
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

// Кольцо фокуса крестика с клавиатуры — инжектим один раз, как у IconButton
const X_FOCUS_CLASS = 'cc-chipx';
if (typeof document !== 'undefined' && !document.getElementById('cc-chipx-style')) {
  const el = document.createElement('style');
  el.id = 'cc-chipx-style';
  el.textContent = `.${X_FOCUS_CLASS}:focus-visible{outline:none;box-shadow:${SHADOW.focus};}`;
  document.head.appendChild(el);
}

// Крестик внутри чипа. Настоящая кнопка, а не span: иначе убрать чип нельзя ни с
// клавиатуры, ни скринридером. label — что именно убираем («Убрать вложение x.ts»).
// touch — тач-площадь: без неё цель 8×14px, пальцем в неё не попасть (замер на планшете).
// В toggle-чип (он сам button) крестик не кладём: кнопка в кнопке — невалидная разметка
export function ChipX({ onClick, touch, label = 'Убрать' }: { onClick: () => void; touch?: boolean; label?: string }) {
  return (
    <button
      type="button"
      className={X_FOCUS_CLASS}
      onClick={e => { e.stopPropagation(); onClick(); }}
      title={label}
      aria-label={label}
      style={{
        // Сброс нативной кнопки: крестик наследует цвет и шрифт чипа
        background: 'none', border: 'none', color: 'inherit', font: 'inherit',
        fontWeight: 700, opacity: 0.8, cursor: 'pointer', flexShrink: 0, lineHeight: 1,
        borderRadius: R.max,
        ...(touch
          ? {
              display: 'inline-flex', alignItems: 'center', justifyContent: 'center', padding: 0,
              minWidth: SP.xxl, minHeight: SP.xxl, margin: `-${SP.sm}px -${SP.sm}px -${SP.sm}px 0`,
            }
          : { padding: '0 1px' }),
      }}
    >
      ×
    </button>
  );
}
