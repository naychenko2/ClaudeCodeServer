import { useMemo, useState } from 'react';
import { MessageSquare, CheckCircle2, Brain, Plus, ChevronRight } from 'lucide-react';
import type { LucideIcon } from 'lucide-react';
import type { Persona, Session } from '../../types';
import { C, FONT, R, SP } from '../../lib/design';
import { getPersonaById, personaLabel } from '../../lib/personas';
import { relativeTime } from '../projects/projectUtil';
import { openTaskInSection } from '../../lib/tasks';
import { type ActivityItem, groupByDay } from './personasActivity';

// Лента «Активность» хаба персон: таймлайн по дням (в стиле GroupTimeline из
// «Что нового» — маркеры-кружочки единым accent-цветом), фильтр по помощнику,
// «Показать ещё N» вместо infinite-scroll (данные уже целиком в памяти после
// фетча — см. personasActivity.ts), кнопка разворота на всю ширину контентной
// зоны — управляется родителем (PersonasHub), эта лента только читает expanded.

const MEMORY_TYPE_LABEL: Record<string, string> = {
  semantic: 'Новый факт', episodic: 'Новый эпизод', procedural: 'Новый приём',
};

function describeItem(item: ActivityItem): { Icon: LucideIcon; label: string; detail?: string } {
  switch (item.kind) {
    case 'chat':
      return { Icon: MessageSquare, label: 'Разговор', detail: item.session?.lastMessage?.trim() || item.session?.name?.trim() };
    case 'task':
      return { Icon: CheckCircle2, label: 'Задача выполнена', detail: item.task?.title };
    case 'memory':
      return { Icon: Brain, label: MEMORY_TYPE_LABEL[item.memoryEntry?.type ?? 'semantic'] ?? 'Новый факт', detail: item.memoryEntry?.text };
    case 'created':
      return { Icon: Plus, label: 'Новый помощник' };
  }
}

const REVEAL_STEP = 8;
const REVEAL_INITIAL = 6;

export function PersonaActivityFeed({ personas, items, loading, expanded, onToggleExpanded, onOpenSession, onOpenPersonaView }: {
  personas: Persona[];
  items: ActivityItem[];
  loading: boolean;
  expanded: boolean;
  onToggleExpanded: () => void;
  onOpenSession: (s: Session) => void;
  onOpenPersonaView: (id: string, view?: 'memory') => void;
}) {
  const [filterId, setFilterId] = useState<string>('all');
  const [revealCount, setRevealCount] = useState(REVEAL_INITIAL);

  const filtered = useMemo(
    () => filterId === 'all' ? items : items.filter(i => i.personaId === filterId),
    [items, filterId],
  );
  const shown = filtered.slice(0, revealCount);
  const grouped = useMemo(() => groupByDay(shown), [shown]);
  const more = filtered.length - shown.length;

  const setFilter = (id: string) => { setFilterId(id); setRevealCount(REVEAL_INITIAL); };

  const handleClick = (item: ActivityItem) => {
    if (item.kind === 'chat' && item.session) onOpenSession(item.session);
    else if (item.kind === 'task' && item.task) openTaskInSection(item.task);
    else if (item.kind === 'memory') onOpenPersonaView(item.personaId, 'memory');
    else onOpenPersonaView(item.personaId);
  };

  return (
    <div>
      <div style={{ display: 'flex', alignItems: 'flex-end', justifyContent: 'space-between', gap: 12, marginBottom: 14 }}>
        <div>
          <div style={heading}>Активность</div>
          <div style={subheading}>Что происходило недавно</div>
        </div>
        <button type="button" onClick={onToggleExpanded} style={expandBtn}>
          {expanded ? 'Свернуть' : 'Показать всё'}
          <ChevronRight size={12} strokeWidth={2.2} style={{ transform: expanded ? 'rotate(180deg)' : 'none', transition: 'transform 0.2s ease', flexShrink: 0 }} />
        </button>
      </div>

      {personas.length > 1 && (
        <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6, marginBottom: 16 }}>
          <FilterChip active={filterId === 'all'} onClick={() => setFilter('all')}>Все</FilterChip>
          {personas.map(p => (
            <FilterChip key={p.id} active={filterId === p.id} onClick={() => setFilter(p.id)}>{p.name}</FilterChip>
          ))}
        </div>
      )}

      {loading && items.length === 0 ? (
        <div style={emptyBox}>Загружаю…</div>
      ) : filtered.length === 0 ? (
        <div style={emptyBox}>
          {items.length === 0 ? 'Пока нет активности — начните разговор с помощником.' : 'У этого помощника пока нет активности.'}
        </div>
      ) : (
        <div style={{ display: 'flex', flexDirection: 'column', gap: 18 }}>
          {grouped.map(g => (
            <div key={g.bucket}>
              <div style={dayLabel}>{g.bucket}</div>
              <div style={{ position: 'relative', paddingLeft: 26 }}>
                <div style={trackLine} />
                <div style={{ display: 'flex', flexDirection: 'column', gap: 2 }}>
                  {g.items.map(item => {
                    const persona = getPersonaById(item.personaId);
                    const { Icon, label, detail } = describeItem(item);
                    return (
                      <button
                        key={item.id}
                        type="button"
                        onClick={() => handleClick(item)}
                        onMouseEnter={e => { e.currentTarget.style.background = C.bgSelected; }}
                        onMouseLeave={e => { e.currentTarget.style.background = 'transparent'; }}
                        style={itemRow}
                      >
                        <span style={marker}><Icon size={11} strokeWidth={2.2} /></span>
                        <div style={{ flex: 1, minWidth: 0 }}>
                          <div style={{ display: 'flex', alignItems: 'baseline', justifyContent: 'space-between', gap: 8, flexWrap: 'wrap' }}>
                            <span style={itemWho}>
                              {persona ? personaLabel(persona) : 'Помощник'} <span style={itemAction}>· {label}</span>
                            </span>
                            <span style={itemTime}>{relativeTime(item.at)}</span>
                          </div>
                          {detail && <div style={itemDetail}>{detail}</div>}
                        </div>
                      </button>
                    );
                  })}
                </div>
              </div>
            </div>
          ))}
          {more > 0 && (
            <button type="button" onClick={() => setRevealCount(c => c + REVEAL_STEP)} style={moreBtn}>
              Показать ещё {Math.min(more, REVEAL_STEP)}
            </button>
          )}
        </div>
      )}
    </div>
  );
}

function FilterChip({ active, onClick, children }: { active: boolean; onClick: () => void; children: React.ReactNode }) {
  return (
    <button type="button" onClick={onClick} style={{ ...chipBase, ...(active ? chipActive : null) }}>
      {children}
    </button>
  );
}

const heading: React.CSSProperties = {
  fontFamily: FONT.serif, fontSize: 19, fontWeight: 700, color: C.textHeading,
};
const subheading: React.CSSProperties = {
  fontSize: 12.5, color: C.textSecondary, marginTop: 4, fontFamily: FONT.sans,
};
const expandBtn: React.CSSProperties = {
  flexShrink: 0, display: 'flex', alignItems: 'center', gap: 5, background: 'none', border: 'none',
  padding: 0, fontFamily: FONT.sans, fontSize: 12.5, fontWeight: 600, color: C.accent, cursor: 'pointer',
};
const chipBase: React.CSSProperties = {
  fontFamily: FONT.sans, fontSize: 11.5, fontWeight: 600, padding: '5px 11px', borderRadius: R.pill,
  border: `1px solid ${C.border}`, background: C.bgWhite, color: C.textMuted, cursor: 'pointer',
};
const chipActive: React.CSSProperties = {
  background: C.accent, color: C.onAccent, borderColor: C.accent,
};
const emptyBox: React.CSSProperties = {
  border: `1px dashed ${C.dashed}`, borderRadius: R.xl, padding: '22px 16px', textAlign: 'center',
  fontSize: 12.5, color: C.textMuted, fontFamily: FONT.sans, lineHeight: 1.5,
};
const dayLabel: React.CSSProperties = {
  fontFamily: FONT.mono, fontSize: 11, fontWeight: 600, letterSpacing: '0.04em', textTransform: 'uppercase',
  color: C.textMuted, marginBottom: 10,
};
const trackLine: React.CSSProperties = {
  position: 'absolute', left: 10, top: 2, bottom: 2, width: 2, background: C.borderLight,
};
const itemRow: React.CSSProperties = {
  position: 'relative', display: 'flex', alignItems: 'flex-start', gap: 12, width: '100%', textAlign: 'left',
  background: 'transparent', border: 'none', borderRadius: R.md, padding: '8px 6px 8px 0', cursor: 'pointer',
  fontFamily: FONT.sans,
};
const marker: React.CSSProperties = {
  position: 'absolute', left: -26, top: 7, width: 20, height: 20, borderRadius: R.full,
  background: C.bgMain, border: `2px solid ${C.accent}`, color: C.accent,
  display: 'flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0,
};
const itemWho: React.CSSProperties = { fontSize: 13, fontWeight: 600, color: C.textHeading };
const itemAction: React.CSSProperties = { fontWeight: 400, color: C.textMuted };
const itemTime: React.CSSProperties = { fontSize: 11, color: C.textSecondary, flexShrink: 0, whiteSpace: 'nowrap' };
const itemDetail: React.CSSProperties = {
  fontSize: 12, color: C.textMuted, marginTop: 2, lineHeight: 1.5,
  overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
};
const moreBtn: React.CSSProperties = {
  width: '100%', display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 6,
  padding: `${SP.sm}px`, borderRadius: R.lg, border: `1px dashed ${C.dashed}`, background: 'transparent',
  color: C.textSecondary, fontFamily: FONT.sans, fontSize: 12, fontWeight: 600, cursor: 'pointer',
};
