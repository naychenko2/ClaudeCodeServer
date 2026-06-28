import type { CSSProperties, MouseEvent } from 'react';
import type { Project } from '../../types';
import { C, R, FONT, SHADOW } from '../../lib/design';

const TILE_COLORS: [string, string][] = [
  ['#E7F0E8', '#3F7A4F'],
  ['#E6EEF5', '#3E7CA6'],
  ['#FBEBE0', '#C2693B'],
  ['#F2E6F0', '#8E4A82'],
];

const cardIconBtn: CSSProperties = {
  width: 26, height: 26, borderRadius: R.sm, display: 'flex', alignItems: 'center', justifyContent: 'center',
  color: C.textMuted, background: 'none', border: 'none', cursor: 'pointer', flexShrink: 0,
};

interface Props {
  project: Project;
  index: number;
  online: boolean;
  hasActiveSession?: boolean;
  onOpen: (p: Project) => void;
  onEdit: (p: Project, e: MouseEvent) => void;
  onDelete: (p: Project) => void;
}

export function ProjectCard({ project: p, index, online, hasActiveSession, onOpen, onEdit, onDelete }: Props) {
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
      <div style={{ position: 'relative', flexShrink: 0 }}>
        {hasActiveSession && (
          <>
            <style>{`@keyframes pc-pulse{0%,100%{opacity:1;transform:scale(1)}50%{opacity:0.5;transform:scale(1.15)}} .pc-pulse{animation:pc-pulse 1.5s ease-in-out infinite}`}</style>
            <span className="pc-pulse" style={{ position: 'absolute', top: 1, right: 1, width: 9, height: 9, borderRadius: '50%', background: C.accent, border: '2px solid #F4F0E8', zIndex: 1 }} />
          </>
        )}
        <div style={{
          width: 50, height: 50, borderRadius: R.xxl, background: tileBg, color: tileFg,
          fontFamily: FONT.serif, fontSize: 22, fontWeight: 600,
          display: 'flex', alignItems: 'center', justifyContent: 'center',
        }}>
          {letter}
        </div>
      </div>

      <div style={{ flex: 1, minWidth: 0 }}>
        <div style={{ fontSize: 16, fontWeight: 600, color: C.textHeading, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
          {p.name}
        </div>
        {/* Статистика */}
        <div style={{ display: 'flex', alignItems: 'center', gap: 8, fontSize: 12, color: C.textMuted, fontFamily: FONT.sans }}>
          {/* Чаты */}
          <div style={{ display: 'flex', alignItems: 'center', gap: 3, color: C.textSecondary }}>
            <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
              <path d="M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z"/>
            </svg>
            <span>{p.sessionCount ?? 0}</span>
          </div>
          {/* Дата */}
          <div style={{ display: 'flex', alignItems: 'center', gap: 3, color: C.textMuted }}>
            <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
              <rect x="3" y="4" width="18" height="18" rx="2" ry="2"/>
              <line x1="16" y1="2" x2="16" y2="6"/>
              <line x1="8" y1="2" x2="8" y2="6"/>
              <line x1="3" y1="10" x2="21" y2="10"/>
            </svg>
            <span>{new Date(p.updatedAt).toLocaleDateString('ru-RU', { day: 'numeric', month: 'short' })}</span>
          </div>
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
