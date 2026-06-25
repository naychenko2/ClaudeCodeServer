import type { CSSProperties, MouseEvent } from 'react';
import type { Project } from '../../types';
import { C, R, FONT, SHADOW } from '../../lib/design';

const TILE_COLORS: [string, string][] = [
  ['#E7F0E8', '#3F7A4F'],
  ['#E6EEF5', '#3E7CA6'],
  ['#FBEBE0', '#C2693B'],
  ['#F2E6F0', '#8E4A82'],
];

function sessionsLabel(n: number): string {
  const m10 = n % 10, m100 = n % 100;
  let w = 'чатов';
  if (m10 === 1 && m100 !== 11) w = 'чат';
  else if (m10 >= 2 && m10 <= 4 && (m100 < 10 || m100 >= 20)) w = 'чата';
  return `${n} ${w}`;
}

const cardIconBtn: CSSProperties = {
  width: 26, height: 26, borderRadius: R.sm, display: 'flex', alignItems: 'center', justifyContent: 'center',
  color: C.textMuted, background: 'none', border: 'none', cursor: 'pointer', flexShrink: 0,
};

interface Props {
  project: Project;
  index: number;
  online: boolean;
  onOpen: (p: Project) => void;
  onEdit: (p: Project, e: MouseEvent) => void;
  onDelete: (p: Project) => void;
}

export function ProjectCard({ project: p, index, online, onOpen, onEdit, onDelete }: Props) {
  const [tileBg, tileFg] = TILE_COLORS[index % TILE_COLORS.length];
  const letter = p.name.charAt(0).toUpperCase() || '?';

  return (
    <div
      key={p.id}
      onClick={() => onOpen(p)}
      style={{
        display: 'flex', alignItems: 'center', gap: 14, background: C.bgWhite,
        border: `1px solid ${C.borderLight}`, borderRadius: 16, padding: 14,
        cursor: 'pointer', boxShadow: SHADOW.card,
      }}
    >
      <div style={{
        width: 50, height: 50, borderRadius: R.xxl, background: tileBg, color: tileFg,
        fontFamily: FONT.serif, fontSize: 22, fontWeight: 600,
        display: 'flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0,
      }}>
        {letter}
      </div>

      <div style={{ flex: 1, minWidth: 0 }}>
        <div style={{ fontSize: 16, fontWeight: 600, color: C.textHeading, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
          {p.name}
        </div>
        <div style={{ fontSize: 12, color: C.textSecondary, display: 'flex', alignItems: 'center', gap: 7 }}>
          <span>{sessionsLabel(p.sessionCount ?? 0)}</span>
          <span style={{ color: C.border }}>·</span>
          <span style={{ color: C.textMuted }}>
            {new Date(p.updatedAt).toLocaleDateString('ru-RU', { day: 'numeric', month: 'short' })}
          </span>
        </div>
      </div>

      {online && (
        <div style={{ display: 'flex', gap: 4, flexShrink: 0 }} onClick={e => e.stopPropagation()}>
          <button onClick={e => onEdit(p, e)} title="Редактировать" style={cardIconBtn}>
            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
              <path d="M11 4H4a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-7"/>
              <path d="M18.5 2.5a2.121 2.121 0 0 1 3 3L12 15l-4 1 1-4 9.5-9.5z"/>
            </svg>
          </button>
          <button onClick={e => { e.stopPropagation(); onDelete(p); }} title="Удалить" style={cardIconBtn}>
            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
              <polyline points="3 6 5 6 21 6"/>
              <path d="M19 6l-1 14a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2L5 6"/>
              <path d="M10 11v6"/>
              <path d="M14 11v6"/>
              <path d="M9 6V4a1 1 0 0 1 1-1h4a1 1 0 0 1 1 1v2"/>
            </svg>
          </button>
        </div>
      )}
    </div>
  );
}
