// Тонкая полоса над строкой контекста: иконка + текст + (средний элемент) + ссылка-действие + необязательный ✕.
// Общий корпус для «Выбор снят · Вернуть» (UndoNotice) и «Контекст N · продолжить в новом чате» (ContinueHint):
// без общего примитива рамка и отступы двух соседних полос разъедутся (история Notice.tsx, дизайн-ревью 4.7, S2).
// Геометрия — по спецификации docs/design/mockups/continue-hint-variants.md: border-box, height 28/32,
// C.bgPanel, C.border, ссылка C.accent, ✕ C.textMuted.
import { useState, type CSSProperties, type ReactNode } from 'react';
import { C, FS, R, SP, SHADOW } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';
import { X } from 'lucide-react';
import { ellipsis } from './RowChip';

// Мышь есть (не тач-устройство): hover-состояния только для неё, на таче — :active
const CAN_HOVER = typeof window !== 'undefined' && !window.matchMedia('(hover: none)').matches;

// Кольцо фокуса с клавиатуры — инжектим один раз, как IconButton и Chip
const FOCUS_CLASS = 'cc-rownotice';
if (typeof document !== 'undefined' && !document.getElementById('cc-rownotice-style')) {
  const el = document.createElement('style');
  el.id = 'cc-rownotice-style';
  el.textContent = `.${FOCUS_CLASS}:focus-visible{outline:none;box-shadow:${SHADOW.focus};}`;
  document.head.appendChild(el);
}

// Действие-ссылка справа: подпись + иконка до/после неё. data — data-{data} (например «undo»),
// testId — data-testid; иконка до (UndoNotice: RotateCcw) или после (ContinueHint: ArrowRight)
export interface RowNoticeAction {
  label: ReactNode;
  onClick: () => void;
  testId?: string;
  data?: string;
  iconBefore?: ReactNode;
  iconAfter?: ReactNode;
}

// ✕ «Не сейчас»: закрывает полосу до следующего роста контекста.
// title — полный текст тултипа, ariaLabel — короткий (скринридер), по умолчанию = title
export interface RowNoticeSnooze {
  onClick: () => void;
  title: string;
  ariaLabel?: string;
  testId?: string;
}

export function RowNotice({ icon, text, middle, action, snooze, isMobile, testId, dataAttr }: {
  icon: ReactNode;
  text: ReactNode;
  // Необязательный элемент между текстом и действием (отсчёт «N с» у UndoNotice)
  middle?: ReactNode;
  action?: RowNoticeAction;
  snooze?: RowNoticeSnooze;
  isMobile?: boolean;
  testId?: string;
  // Дополнительный data-атрибут корня (UndoNotice держит data-context-notice для тестов)
  dataAttr?: string;
}) {
  const hoverable = CAN_HOVER && !isMobile;
  const [hoverA, setHoverA] = useState(false);
  const [pressA, setPressA] = useState(false);
  const [hoverX, setHoverX] = useState(false);
  const [pressX, setPressX] = useState(false);

  // Правый отступ: SP.xs (4 px) — заточен под ✕ (snooze); без ✕ (UndoNotice) — SP.sm (8 px),
  // как было у UndoNotice. На мобиле без ✕ отступ не должен становиться 0.
  const rightPad = snooze ? SP.xs : SP.sm;
  const bar: CSSProperties = {
    boxSizing: 'border-box', display: 'flex', alignItems: 'center', gap: SP.sm,
    height: isMobile ? 32 : 28,
    margin: `0 0 ${SP.xs}px`,
    padding: `0 ${rightPad}px 0 ${SP.sm}px`,
    border: `1px solid ${C.border}`, borderRadius: R.lg,
    background: C.bgPanel, fontSize: FS.sm, color: C.textSecondary,
  };

  const actionStyle: CSSProperties = {
    display: 'inline-flex', alignItems: 'center', gap: SP.xs, flexShrink: 0,
    border: 'none', background: pressA ? C.bgSelected : 'transparent', padding: 0,
    color: C.accent, fontSize: FS.sm, fontWeight: 600, fontFamily: 'inherit', cursor: 'pointer',
    borderRadius: R.sm,
    textDecoration: hoverA ? 'underline' : 'none', textUnderlineOffset: 2,
  };
  const snoozeStyle: CSSProperties = {
    display: 'inline-grid', placeItems: 'center', flexShrink: 0,
    width: isMobile ? 32 : 20, height: isMobile ? 30 : 20,
    border: 'none', background: (hoverX || pressX) ? C.bgSelected : 'transparent', padding: 0,
    color: hoverX ? C.textSecondary : C.textMuted, cursor: 'pointer', borderRadius: R.sm,
  };

  return (
    <div data-row-notice="" {...(dataAttr ? { [`data-${dataAttr}`]: '' } : null)} role="status" {...(testId ? { 'data-testid': testId } : null)} style={bar}>
      <span style={{ display: 'inline-flex', alignItems: 'center', flexShrink: 0 }}>{icon}</span>
      <span style={{ flex: 1, ...ellipsis }}>{text}</span>
      {middle}
      {action && (
        <button
          type="button" className={FOCUS_CLASS}
          {...(action.data ? { [`data-${action.data}`]: '' } : null)}
          data-testid={action.testId}
          onClick={action.onClick}
          onMouseEnter={hoverable ? () => setHoverA(true) : undefined}
          onMouseDown={isMobile ? () => setPressA(true) : undefined}
          onMouseUp={isMobile ? () => setPressA(false) : undefined}
          onMouseLeave={() => { if (hoverable) setHoverA(false); if (isMobile) setPressA(false); }}
          style={actionStyle}
        >
          {action.iconBefore}
          {action.label}
          {action.iconAfter}
        </button>
      )}
      {snooze && (
        <button
          type="button" className={FOCUS_CLASS}
          data-testid={snooze.testId}
          title={snooze.title} aria-label={snooze.ariaLabel ?? snooze.title}
          onClick={snooze.onClick}
          onMouseEnter={hoverable ? () => setHoverX(true) : undefined}
          onMouseDown={isMobile ? () => setPressX(true) : undefined}
          onMouseUp={isMobile ? () => setPressX(false) : undefined}
          onMouseLeave={() => { if (hoverable) setHoverX(false); if (isMobile) setPressX(false); }}
          style={snoozeStyle}
        >
          <X size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
        </button>
      )}
    </div>
  );
}
