// Список задач в работе под заголовком «Сейчас» (макет feature-summary-2, «Итерация 2»).
// Только отрисовка: слова, порядок и «до 4 строк» приходят из briefActiveView.
// Строка — кнопка во всю ширину с переходом в задачу; на 360 время уходит второй строкой,
// отступа под глиф нет, высота касания 40.

import { useState } from 'react';
import type { CSSProperties, ReactNode } from 'react';
import { C, R, SP, FS, FONT, SHADOW } from '../../lib/design';
import { Button } from '../ui/Button';
import type { BriefActiveRow, BriefActiveView } from './FeatureBriefModel';

const GLYPH_SIZE = 12;

// Глифы 12 px: работа — точка, ревью — кольцо, ждёт вас — ромб, ошибка — круг, пауза — два штриха.
const ROW_GLYPH: Record<BriefActiveRow['state'], ReactNode> = {
  working: <circle cx={6} cy={6} r={3.5} fill={C.accent} />,
  review: <circle cx={6} cy={6} r={4} fill="none" stroke={C.accent} strokeWidth={1.5} />,
  waiting_you: <path d="M6 1 11 6 6 11 1 6Z" fill={C.info} />,
  error: <circle cx={6} cy={6} r={5} fill={C.danger} />,
  held: (
    <>
      <rect x={3} y={2.5} width={2} height={7} rx={0.6} fill={C.textMuted} />
      <rect x={7} y={2.5} width={2} height={7} rx={0.6} fill={C.textMuted} />
    </>
  ),
};

function ActiveRow({ row, isMobile, onOpen }: { row: BriefActiveRow; isMobile: boolean; onOpen: () => void }) {
  const [hover, setHover] = useState(false);
  const [focused, setFocused] = useState(false);
  const held = row.state === 'held';
  const style: CSSProperties = {
    display: 'flex', alignItems: 'flex-start', gap: SP.sm, width: '100%', boxSizing: 'border-box',
    padding: `${SP.xs}px ${SP.sm}px`, minHeight: isMobile ? 40 : undefined, borderRadius: R.md,
    border: 'none', textAlign: 'left', cursor: 'pointer', fontFamily: FONT.sans, fontSize: FS.base,
    background: hover ? C.bgSelected : row.state === 'waiting_you' ? C.infoBg : 'transparent',
    boxShadow: focused ? SHADOW.focus : 'none',
  };
  return (
    <button
      type="button"
      data-testid="brief-active-row"
      data-state={row.state}
      aria-label={row.ariaLabel}
      onClick={onOpen}
      onMouseEnter={() => setHover(true)}
      onMouseLeave={() => setHover(false)}
      onFocus={e => setFocused(e.currentTarget.matches(':focus-visible'))}
      onBlur={() => setFocused(false)}
      style={style}
    >
      <svg width={GLYPH_SIZE} height={GLYPH_SIZE} viewBox="0 0 12 12" aria-hidden style={{ flexShrink: 0, marginTop: 4 }}>{ROW_GLYPH[row.state]}</svg>
      <span style={{
        flex: 1, minWidth: 0, display: 'flex', gap: SP.sm,
        flexDirection: isMobile ? 'column' : 'row', justifyContent: 'space-between', alignItems: isMobile ? 'flex-start' : 'baseline',
      }}>
        <span data-testid="brief-active-text" style={{ minWidth: 0, color: held ? C.textSecondary : C.textPrimary }}>
          <b style={{ fontWeight: 600 }}>{row.title}</b>
          {row.who && <span style={{ color: C.textSecondary }}> · {row.who}</span>}
          {row.stopped && <span data-testid="brief-active-stopped" style={{ color: C.dangerText, fontWeight: 600 }}> {row.stopped}</span>}
        </span>
        {row.time && <span data-testid="brief-active-time" style={{ fontSize: FS.sm, color: C.textMuted, whiteSpace: 'nowrap', flexShrink: 0 }}>{row.time}</span>}
      </span>
    </button>
  );
}

export function ActiveList({ view, isMobile, onOpenTask, onOpenTasks }: {
  view: BriefActiveView; isMobile: boolean; onOpenTask?: (taskId: string) => void; onOpenTasks?: () => void;
}) {
  if (!view.rows.length) return null;
  return (
    <div
      data-testid="brief-active"
      style={{ marginTop: SP.sm, marginLeft: isMobile ? 0 : SP.xl, display: 'flex', flexDirection: 'column', gap: SP.xxs }}
    >
      <div role="list" style={{ display: 'flex', flexDirection: 'column', gap: SP.xxs }}>
        {view.rows.map(r => (
          <div key={r.taskId} role="listitem">
            <ActiveRow row={r} isMobile={isMobile} onOpen={() => onOpenTask?.(r.taskId)} />
          </div>
        ))}
      </div>
      {view.tail && (
        <Button
          variant="ghost"
          size="xs"
          dataTestId="brief-active-tail"
          onClick={() => onOpenTasks?.()}
          style={{
            background: 'transparent', border: 'none', color: C.accent, fontSize: FS.sm,
            alignSelf: 'flex-start', textAlign: 'left', height: 'auto',
            minHeight: isMobile ? 40 : 24,
          }}
        >
          {view.tail}
        </Button>
      )}
    </div>
  );
}
