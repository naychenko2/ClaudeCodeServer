import type { ReactNode, CSSProperties, MouseEvent, KeyboardEvent, AriaAttributes } from 'react';

type PressKey = Pick<KeyboardEvent, 'key' | 'target' | 'currentTarget' | 'preventDefault'>;

// Клавиатурная активация области: Enter/Space — нажатие. Клавиша со вложенной кнопки
// всплывает сюда: её не трогаем, иначе preventDefault погасит нативную активацию
// вложенной кнопки, а onPress сработает вместо неё. Возвращает true, если нажатие наше.
export function handlePressableKey<E extends PressKey>(e: E, onPress: (e: E) => void): boolean {
  if (e.target !== e.currentTarget) return false;
  if (e.key !== 'Enter' && e.key !== ' ') return false;
  e.preventDefault();
  onPress(e);
  return true;
}

// Нажимаемая область: div с role="button" вместо <button>, когда внутри живут свои
// кнопки (стек участников группового чата в мобильной шапке) — вложенный <button>
// в <button> невалиден (React hydration error). Клавиатурная доступность — вручную:
// tabIndex + Enter/Space. Отклик на нажатие и кольцо фокуса — по флагу `feedback`
// (класс .cc-pressable в index.css: подложка C.bgSelected, общее кольцо фокуса);
// без флага вид целиком задаёт вызывающий.
export function PressableArea({
  onPress, title, children, feedback = false, className, style,
  'aria-label': ariaLabel, 'aria-haspopup': ariaHaspopup,
}: {
  onPress: (e: MouseEvent | KeyboardEvent) => void;
  title?: string;
  children?: ReactNode;
  feedback?: boolean;
  className?: string;
  style?: CSSProperties;
  'aria-label'?: string;
  'aria-haspopup'?: AriaAttributes['aria-haspopup'];
}) {
  const cls = [feedback ? 'cc-pressable' : '', className ?? ''].filter(Boolean).join(' ');
  return (
    <div
      role="button"
      tabIndex={0}
      onClick={onPress}
      onKeyDown={e => handlePressableKey(e, onPress)}
      title={title}
      aria-label={ariaLabel}
      aria-haspopup={ariaHaspopup}
      className={cls || undefined}
      style={{ cursor: 'pointer', ...style }}
    >
      {children}
    </div>
  );
}
