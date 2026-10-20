import { useState } from 'react';
import type { CSSProperties } from 'react';
import { ListChecks, MessageCircle } from 'lucide-react';
import { C, R, FS, FONT, SHADOW } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';

// Цель навигации «чат / задача» (волна 3, макет §9а): ОДНА пара кнопок на всю
// страницу фичи — где назван чат или задача, цели стоят рядом, у каждой своя
// иконка (облачко / список) и своё действие. Здесь — заглушка-обёртка: вид по
// макету, клик вызывает onOpen; полную логику (архив, «чата нет», возврат на
// вкладку) наполнит Ф9a.
//
// «чат»: 24, поля 0 7, R.sm, рамка borderLight, фон bgWhite, 11/600 accent,
// иконка MessageCircle; наведение — accentLight + рамка accentMuted.
// «задача»: то же, текст textSecondary, иконка ListChecks; наведение — bgSelected.
// Фокус — SHADOW.focus (как у кнопок кита). На 360 — minHeight 40 (тач-цель).
export function NavTargetButton({ kind, label, title, onOpen, isMobile = false }: {
  // chat — облачко, task — список.
  kind: 'chat' | 'task';
  // Видимая подпись («чат» / «задача»); не задана — иконка с aria-label.
  label?: string;
  // Полная подсказка («Открыть чат «…»»); не задана — нет title.
  title?: string;
  onOpen: () => void;
  isMobile?: boolean;
}) {
  const [hover, setHover] = useState(false);
  const isChat = kind === 'chat';
  const Icon = isChat ? MessageCircle : ListChecks;
  const style: CSSProperties = {
    display: 'inline-flex', alignItems: 'center', gap: 4, flexShrink: 0,
    height: isMobile ? 40 : 24, minHeight: isMobile ? 40 : undefined,
    padding: '0 7px',
    border: `1px solid ${hover ? (isChat ? C.accentMuted : C.borderLight) : C.borderLight}`,
    background: isChat ? (hover ? C.accentLight : C.bgWhite) : (hover ? C.bgSelected : C.bgWhite),
    borderRadius: R.sm,
    fontFamily: FONT.sans, fontSize: FS.xs, fontWeight: 600,
    color: isChat ? C.accent : (hover ? C.textHeading : C.textSecondary),
    cursor: 'pointer',
  };
  return (
    <button
      type="button"
      onClick={onOpen}
      title={title}
      aria-label={label ? (title ?? label) : (title ?? (isChat ? 'чат' : 'задача'))}
      onMouseEnter={() => setHover(true)}
      onMouseLeave={() => setHover(false)}
      onFocus={e => { e.currentTarget.style.boxShadow = SHADOW.focus; }}
      onBlur={e => { e.currentTarget.style.boxShadow = 'none'; }}
      style={style}
    >
      <Icon size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />
      {label}
    </button>
  );
}
