import { useId, useState } from 'react';
import type { ReactNode } from 'react';
import { ChevronDown, type LucideIcon } from 'lucide-react';
import { C, FS, R } from '../../../lib/design';
import { ICON_STROKE } from '../../../components/ui/icons';
import { useIsMobile } from '../../../lib/breakpoints';

// Тон однострочной сводки статуса в заголовке секции.
// Спека: docs/mockups/edit-project-compact-proposal.md, раздел «Спецификация для Киры».
export type AccordionSummaryTone = 'neutral' | 'ok' | 'err';

interface Props {
  icon: LucideIcon;
  title: string;
  summary?: string;
  summaryTone?: AccordionSummaryTone;
  defaultOpen?: boolean;
  children: ReactNode;
}

// Сворачиваемая секция диалога «Редактировать проект»: строка-заголовок 44px
// (иконка + название + однострочная сводка + chevron) и тело с существующим
// содержимым секции.
//
// Тело всегда смонтировано, скрытие — через display (как в макете). Это не
// косметика: git-статус, список MCP-серверов, фон и sync-метки грузятся в
// собственных эффектах секций при открытии диалога, и сводки в заголовках
// должны читать актуальное состояние без раскрытия. Условный рендер тела
// сломал бы эту загрузку и оставил бы заголовки без сводок до первого клика.
export function AccordionSection({
  icon: Icon, title, summary, summaryTone = 'neutral', defaultOpen = false, children,
}: Props) {
  const isMobile = useIsMobile();
  const [open, setOpen] = useState(defaultOpen);
  const [hovered, setHovered] = useState(false);
  const reactId = useId();
  const headId = `${reactId}-head`;
  const bodyId = `${reactId}-body`;

  const sumColor =
    summaryTone === 'ok' ? C.successText
      : summaryTone === 'err' ? C.dangerText
        : C.textMuted;
  // Hover — только десктоп: на мобильной шторке строка и так tap-зона 44px,
  // подсветка при тапе лишняя.
  const showHover = !isMobile && hovered && !open;

  return (
    <div style={{
      background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.xl,
      overflow: 'hidden',
    }}>
      <button
        type="button"
        id={headId}
        aria-expanded={open}
        aria-controls={bodyId}
        onClick={() => setOpen(o => !o)}
        onMouseEnter={() => setHovered(true)}
        onMouseLeave={() => setHovered(false)}
        style={{
          display: 'flex', alignItems: 'center', gap: 10,
          width: '100%', minHeight: 44, padding: '10px 14px',
          border: 'none', cursor: 'pointer', textAlign: 'left', fontFamily: 'inherit',
          background: showHover ? C.bgSelected : 'transparent',
          transition: 'background 0.12s',
        }}
      >
        <Icon size={15} strokeWidth={ICON_STROKE} style={{ flexShrink: 0, color: C.textMuted }} />
        <span style={{
          flex: '0 1 auto', minWidth: 0,
          fontSize: FS.base, fontWeight: 600, color: C.textHeading,
          overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
        }}>
          {title}
        </span>
        {summary && (
          <span
            title={summary}
            style={{
              marginLeft: 'auto', flex: '0 1 auto', minWidth: 0, textAlign: 'right',
              fontSize: FS.sm, color: sumColor, fontWeight: summaryTone === 'err' ? 600 : 400,
              overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
            }}
          >
            {summary}
          </span>
        )}
        <ChevronDown size={14} strokeWidth={ICON_STROKE}
          style={{ flexShrink: 0, color: C.textMuted, transition: 'transform .15s', transform: open ? 'rotate(180deg)' : 'none' }} />
      </button>
      <div
        id={bodyId}
        role="region"
        aria-labelledby={headId}
        style={{
          display: open ? 'block' : 'none',
          borderTop: `1px solid ${C.borderLight}`, padding: '10px 12px 12px',
        }}
      >
        {children}
      </div>
    </div>
  );
}
