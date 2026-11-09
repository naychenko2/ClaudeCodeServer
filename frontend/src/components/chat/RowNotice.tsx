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

// Кольцо фокуса с клавиатуры — инжектим один раз, как IconButton и Chip.
// Общий для полосы (RowNotice) и ушка (LipEar): обе рисуют кнопки через эти же примитивы.
const FOCUS_CLASS = 'cc-rownotice';
if (typeof document !== 'undefined' && !document.getElementById('cc-rownotice-style')) {
  const el = document.createElement('style');
  el.id = 'cc-rownotice-style';
  el.textContent = `.${FOCUS_CLASS}:focus-visible{outline:none;box-shadow:${SHADOW.focus};}`;
  document.head.appendChild(el);
}

// Действие-ссылка: подпись + иконка до/после неё. Состояние hover/tap/focus — внутреннее.
// data — data-{data} (например «undo»), testId — data-testid; iconBefore (UndoNotice: RotateCcw)
// или iconAfter (ContinueHint: ArrowRight). Общий с ушком (LipEar) — не дублируем стили.
export function RowNoticeAction({ label, onClick, testId, data, iconBefore, iconAfter, isMobile }: {
  label: ReactNode;
  onClick: () => void;
  testId?: string;
  data?: string;
  iconBefore?: ReactNode;
  iconAfter?: ReactNode;
  isMobile?: boolean;
}) {
  const hoverable = CAN_HOVER && !isMobile;
  const [hover, setHover] = useState(false);
  const [press, setPress] = useState(false);
  const style: CSSProperties = {
    display: 'inline-flex', alignItems: 'center', gap: SP.xs, flexShrink: 0,
    border: 'none', background: press ? C.bgSelected : 'transparent', padding: 0,
    color: C.accent, fontSize: FS.sm, fontWeight: 600, fontFamily: 'inherit', cursor: 'pointer',
    borderRadius: R.sm,
    textDecoration: hover ? 'underline' : 'none', textUnderlineOffset: 2,
  };
  return (
    <button
      type="button" className={FOCUS_CLASS}
      {...(data ? { [`data-${data}`]: '' } : null)}
      data-testid={testId}
      onClick={onClick}
      onMouseEnter={hoverable ? () => setHover(true) : undefined}
      onMouseDown={isMobile ? () => setPress(true) : undefined}
      onMouseUp={isMobile ? () => setPress(false) : undefined}
      onMouseLeave={() => { if (hoverable) setHover(false); if (isMobile) setPress(false); }}
      style={style}
    >
      {iconBefore}
      {label}
      {iconAfter}
    </button>
  );
}

// ✕ «Не сейчас»: закрывает до следующего роста контекста. title — полный текст тултипа,
// ariaLabel — короткий (скринридер), по умолчанию = title. size — размеры кнопки; по умолчанию
// как у полосы (20×20, мобила 32×30), ушко (LipEar) на мобиле ставит 32×28.
export function RowNoticeSnooze({ onClick, title, ariaLabel, testId, isMobile, size }: {
  onClick: () => void;
  title: string;
  ariaLabel?: string;
  testId?: string;
  isMobile?: boolean;
  size?: { w: number; h: number };
}) {
  const hoverable = CAN_HOVER && !isMobile;
  const [hover, setHover] = useState(false);
  const [press, setPress] = useState(false);
  const w = size?.w ?? (isMobile ? 32 : 20);
  const h = size?.h ?? (isMobile ? 30 : 20);
  const style: CSSProperties = {
    display: 'inline-grid', placeItems: 'center', flexShrink: 0,
    width: w, height: h,
    border: 'none', background: (hover || press) ? C.bgSelected : 'transparent', padding: 0,
    color: hover ? C.textSecondary : C.textMuted, cursor: 'pointer', borderRadius: R.sm,
  };
  return (
    <button
      type="button" className={FOCUS_CLASS}
      data-testid={testId}
      title={title} aria-label={ariaLabel ?? title}
      onClick={onClick}
      onMouseEnter={hoverable ? () => setHover(true) : undefined}
      onMouseDown={isMobile ? () => setPress(true) : undefined}
      onMouseUp={isMobile ? () => setPress(false) : undefined}
      onMouseLeave={() => { if (hoverable) setHover(false); if (isMobile) setPress(false); }}
      style={style}
    >
      <X size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
    </button>
  );
}

// Действие-ссылка справа: подпись + иконка до/после неё. data — data-{data} (например «undo»),
// testId — data-testid; иконка до (UndoNotice: RotateCcw) или после (ContinueHint: ArrowRight)
export interface RowNoticeActionProps {
  label: ReactNode;
  onClick: () => void;
  testId?: string;
  data?: string;
  iconBefore?: ReactNode;
  iconAfter?: ReactNode;
}

// ✕ «Не сейчас»: закрывает полосу до следующего роста контекста.
// title — полный текст тултипа, ariaLabel — короткий (скринридер), по умолчанию = title
export interface RowNoticeSnoozeProps {
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
  action?: RowNoticeActionProps;
  snooze?: RowNoticeSnoozeProps;
  isMobile?: boolean;
  testId?: string;
  // Дополнительный data-атрибут корня (UndoNotice держит data-context-notice для тестов)
  dataAttr?: string;
}) {
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

  return (
    <div data-row-notice="" {...(dataAttr ? { [`data-${dataAttr}`]: '' } : null)} role="status" {...(testId ? { 'data-testid': testId } : null)} style={bar}>
      <span style={{ display: 'inline-flex', alignItems: 'center', flexShrink: 0 }}>{icon}</span>
      <span style={{ flex: 1, ...ellipsis }}>{text}</span>
      {middle}
      {action && (
        <RowNoticeAction label={action.label} onClick={action.onClick} testId={action.testId}
          data={action.data} iconBefore={action.iconBefore} iconAfter={action.iconAfter} isMobile={isMobile} />
      )}
      {snooze && (
        <RowNoticeSnooze onClick={snooze.onClick} title={snooze.title} ariaLabel={snooze.ariaLabel}
          testId={snooze.testId} isMobile={isMobile} />
      )}
    </div>
  );
}
