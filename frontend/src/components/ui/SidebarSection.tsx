// Сворачиваемая секция сайдбара: шеврон, заголовок капсом, счётчик и правый слот действий.
//
// Примитив, а не два похожих заголовка по месту: в одной колонке рядом живут свойства
// документа и комментарии к нему, и два разных вида заголовка читались бы как два разных
// элемента интерфейса. Состояние переживает перезагрузку (storageKey) — это привычка
// чтения, а не данные, поэтому ключ общий, не по документу.

import { useState, type ReactNode } from 'react';
import { ChevronDown, ChevronRight } from 'lucide-react';
import { C, FONT, SP } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from './icons';

export function SidebarSection({ title, count, hint, actions, collapsedActions, storageKey, defaultOpen = true, children }: {
  title: string;
  // Число справа от заголовка (сколько свойств, сколько комментариев)
  count?: number;
  // Приписка после счётчика — «3 откр.» и подобное
  hint?: string;
  // Кнопки секции: видны только когда она раскрыта — свёрнутая секция это одна строка
  actions?: ReactNode;
  // Наоборот — контрол для СВЁРНУТОЙ секции: главное её значение остаётся под рукой,
  // и менять его можно не раскрывая (в раскрытой оно и так есть, дублировать незачем)
  collapsedActions?: ReactNode;
  storageKey?: string;
  defaultOpen?: boolean;
  children: ReactNode;
}) {
  const [open, setOpen] = useState(() => {
    if (!storageKey) return defaultOpen;
    const saved = localStorage.getItem(storageKey);
    return saved === null ? defaultOpen : saved === '1';
  });

  const toggle = () => setOpen(v => {
    if (storageKey) localStorage.setItem(storageKey, v ? '0' : '1');
    return !v;
  });

  return (
    <div>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap' }}>
        <button onClick={toggle} style={headStyle}>
          {open
            ? <ChevronDown size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ color: C.textMuted }} />
            : <ChevronRight size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ color: C.textMuted }} />}
          {title}
          {count !== undefined && (
            <span style={{ color: C.textMuted, fontWeight: 400 }}>
              {count}{hint ? ` · ${hint}` : ''}
            </span>
          )}
        </button>
        {(open ? actions : collapsedActions) && (
          <div style={{
            display: 'flex', alignItems: 'center', gap: SP.sm, marginLeft: 'auto',
            // wrap — страховка на утянутый до минимума сайдбар: вместо обрезки контролы
            // встанут друг под другом
            flexWrap: 'wrap', justifyContent: 'flex-end', minWidth: 0,
          }}>{open ? actions : collapsedActions}</div>
        )}
      </div>
      {/* Раскрытие высотой grid-строки (0fr → 1fr): содержимое не нужно мерить, а свёрнутая
          секция занимает РОВНО заголовок — ни отступа, ни пустой полосы под ним.
          visibility гасится ПОСЛЕ анимации, иначе скрытые контролы остаются в обходе табом */}
      <div style={{
        display: 'grid',
        gridTemplateRows: open ? '1fr' : '0fr',
        transition: open
          ? `grid-template-rows ${ANIM_MS}ms ease`
          : `grid-template-rows ${ANIM_MS}ms ease, visibility 0s linear ${ANIM_MS}ms`,
        visibility: open ? 'visible' : 'hidden',
      }}>
        <div style={{ overflow: 'hidden', minHeight: 0 }}>
          <div style={{ paddingTop: SP.xs }}>{children}</div>
        </div>
      </div>
    </div>
  );
}

const ANIM_MS = 160;

const headStyle: React.CSSProperties = {
  display: 'flex', alignItems: 'center', gap: SP.xs,
  padding: `${SP.xs}px 0`, border: 'none', background: 'transparent', cursor: 'pointer',
  fontFamily: FONT.sans, fontSize: 11.5, fontWeight: 600, color: C.textSecondary,
  textTransform: 'uppercase', letterSpacing: '.03em',
};
