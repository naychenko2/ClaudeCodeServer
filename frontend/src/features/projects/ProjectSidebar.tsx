import type { ReactNode } from 'react';
import type { ProjectGroup } from '../../types';
import { C, R, FONT, SHADOW } from '../../lib/design';

export type ProjectView = 'all' | 'sleeping' | string;   // string = groupId

interface Props {
  view: ProjectView;
  onSelect: (v: ProjectView) => void;
  total: number;
  groups: { group: ProjectGroup; count: number }[];
  sleepingCount: number;
}

// Левый сайдбар навигации: «Все проекты» + список групп + «Спящие».
export function ProjectSidebar({ view, onSelect, total, groups, sleepingCount }: Props) {
  return (
    <aside style={{
      width: 262, flexShrink: 0, background: C.bgPanel, borderRight: `1px solid ${C.divider}`,
      padding: 14, overflowY: 'auto', display: 'flex', flexDirection: 'column',
    }}>
      <Row
        selected={view === 'all'}
        onClick={() => onSelect('all')}
        label="Все проекты"
        count={total}
        icon={<>
          <rect x="3" y="3" width="7" height="7" rx="1.5"/><rect x="14" y="3" width="7" height="7" rx="1.5"/>
          <rect x="3" y="14" width="7" height="7" rx="1.5"/><rect x="14" y="14" width="7" height="7" rx="1.5"/>
        </>}
      />

      <div style={{ fontFamily: FONT.mono, fontSize: 10, letterSpacing: '0.08em', color: '#9A8F7E', margin: '13px 4px 9px' }}>
        ГРУППЫ
      </div>

      {groups.map(({ group: g, count }) => {
        const selected = view === g.id;
        return (
          <div
            key={g.id}
            onClick={() => onSelect(g.id)}
            style={{
              display: 'flex', alignItems: 'center', gap: 11, padding: '9px 11px', borderRadius: R.lg,
              marginBottom: 3, cursor: 'pointer',
              background: selected ? C.bgWhite : 'transparent', boxShadow: selected ? SHADOW.card : 'none',
            }}
          >
            <span style={{ width: 4, height: 17, borderRadius: 2, background: g.color || C.textMuted, flexShrink: 0 }} />
            <span style={{
              flex: 1, minWidth: 0, fontSize: 13.5, fontWeight: selected ? 700 : 500,
              color: selected ? C.textHeading : C.textPrimary, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
            }}>
              {g.name}
            </span>
            <span style={{ fontSize: 11.5, color: '#9A8F7E', flexShrink: 0 }}>{count}</span>
          </div>
        );
      })}
      {groups.length === 0 && (
        <div style={{ fontSize: 12.5, color: C.textMuted, padding: '2px 11px 4px' }}>Групп пока нет</div>
      )}

      <Row
        selected={view === 'sleeping'}
        onClick={() => onSelect('sleeping')}
        label="Без группы"
        count={sleepingCount}
        muted
        icon={<><path d="M22 12h-6l-2 3h-4l-2-3H2"/><path d="M5.45 5.11 2 12v6a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2v-6l-3.45-6.89A2 2 0 0 0 16.76 4H7.24a2 2 0 0 0-1.79 1.11z"/></>}
      />

      <div style={{ flex: 1 }} />
    </aside>
  );
}

function Row({ selected, onClick, icon, label, count, muted }: {
  selected: boolean; onClick: () => void; icon: ReactNode; label: string; count: number; muted?: boolean;
}) {
  return (
    <div
      onClick={onClick}
      style={{
        display: 'flex', alignItems: 'center', gap: 10, padding: '9px 11px', borderRadius: R.lg,
        marginTop: muted ? 3 : 0, cursor: 'pointer',
        background: selected ? C.bgWhite : 'transparent', boxShadow: selected ? SHADOW.card : 'none',
      }}
    >
      <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke={muted && !selected ? '#A89C88' : C.textSecondary} strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" style={{ flexShrink: 0 }}>
        {icon}
      </svg>
      <span style={{
        flex: 1, fontSize: 13.5, fontWeight: selected ? 700 : 600,
        color: selected ? C.textHeading : (muted ? '#8A8072' : C.textPrimary),
      }}>
        {label}
      </span>
      <span style={{ fontSize: 11.5, color: '#9A8F7E', flexShrink: 0 }}>{count}</span>
    </div>
  );
}
