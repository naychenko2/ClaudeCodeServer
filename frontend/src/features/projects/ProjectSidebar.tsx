import type { ReactNode } from 'react';
import type { ProjectGroup } from '../../types';
import { C, R, FONT, SHADOW } from '../../lib/design';
import { IconButton } from '../../components/ui';
import { Pin, Settings, LayoutGrid, Inbox } from 'lucide-react';
import { ICON_SIZE } from '../../components/ui/icons';

export type ProjectView = 'all' | 'sleeping' | string;   // string = groupId

interface Props {
  view: ProjectView;
  onSelect: (v: ProjectView) => void;
  total: number;
  groups: { group: ProjectGroup; count: number }[];
  sleepingCount: number;
  onCollapse?: () => void;
  onPin?: () => void;        // «закрепить» — показывается в режиме drawer (open)
  onManageGroups?: () => void;
}

// Левый сайдбар навигации: «Все проекты» + список групп + «Без группы».
export function ProjectSidebar({ view, onSelect, total, groups, sleepingCount, onCollapse, onPin, onManageGroups }: Props) {
  return (
    <aside style={{
      width: '100%', height: '100%', boxSizing: 'border-box', background: C.bgPanel,
      padding: 14, overflowY: 'auto', display: 'flex', flexDirection: 'column',
    }}>
      {onCollapse && (
        <div style={{ display: 'flex', alignItems: 'center', gap: 6, marginBottom: 8, minHeight: 28 }}>
          <span style={{ flex: 1, minWidth: 0, fontSize: 14, fontWeight: 600, color: C.textHeading, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>Проекты</span>
          {/* Пин — самая правая кнопка: onPin (в режиме drawer — закрепить) либо onCollapse (открепить-свернуть) */}
          <IconButton
            onClick={onPin ?? onCollapse}
            title={onPin ? 'Закрепить панель' : 'Открепить панель'}
            size="sm"
          >
            <Pin size={ICON_SIZE.sm} strokeWidth={2} fill={onPin ? 'none' : 'currentColor'} />
          </IconButton>
        </div>
      )}
      <Row
        selected={view === 'all'}
        onClick={() => onSelect('all')}
        label="Все проекты"
        count={total}
        icon={<LayoutGrid size={ICON_SIZE.sm} strokeWidth={2} />}
      />

      <div style={{ display: 'flex', alignItems: 'center', gap: 8, margin: '13px 4px 9px' }}>
        <span style={{ flex: 1, fontFamily: FONT.mono, fontSize: 10, letterSpacing: '0.08em', color: C.textMuted }}>
          ГРУППЫ
        </span>
        {onManageGroups && (
          <IconButton onClick={onManageGroups} title="Управление группами" size="sm">
            <Settings size={ICON_SIZE.sm} strokeWidth={2} />
          </IconButton>
        )}
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
            <span style={{ fontSize: 11.5, color: C.textMuted, flexShrink: 0 }}>{count}</span>
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
        icon={<Inbox size={ICON_SIZE.sm} strokeWidth={2} />}
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
      <span style={{ display: 'flex', flexShrink: 0, color: muted && !selected ? C.textMuted : C.textSecondary }}>
        {icon}
      </span>
      <span style={{
        flex: 1, fontSize: 13.5, fontWeight: selected ? 700 : 600,
        color: selected ? C.textHeading : (muted ? C.textMuted : C.textPrimary),
      }}>
        {label}
      </span>
      <span style={{ fontSize: 11.5, color: C.textMuted, flexShrink: 0 }}>{count}</span>
    </div>
  );
}
