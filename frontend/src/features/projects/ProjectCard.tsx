import { useState } from 'react';
import type { MouseEvent, ReactNode } from 'react';
import type { Project } from '../../types';
import { C, R, FONT, SHADOW, Z } from '../../lib/design';

const TILE_COLORS: [string, string][] = [
  ['#E7F0E8', '#3F7A4F'],
  ['#E6EEF5', '#3E7CA6'],
  ['#FBEBE0', '#C2693B'],
  ['#F2E6F0', '#8E4A82'],
];

// Инициалы для плитки: до 2 букв (первые буквы двух слов, иначе первые 2 символа)
function initials(name: string): string {
  const words = name.trim().split(/[\s._-]+/).filter(Boolean);
  if (words.length >= 2) return (words[0][0] + words[1][0]).toUpperCase();
  const w = words[0] ?? '';
  return (w.slice(0, 2) || '?').toUpperCase();
}

function pluralChats(n: number): string {
  const m10 = n % 10, m100 = n % 100;
  if (m10 === 1 && m100 !== 11) return 'чат';
  if (m10 >= 2 && m10 <= 4 && (m100 < 10 || m100 >= 20)) return 'чата';
  return 'чатов';
}

interface Props {
  project: Project;
  index: number;
  online: boolean;
  hasActiveSession?: boolean;
  onOpen: (p: Project) => void;
  onMove: (p: Project) => void;
  onEdit: (p: Project, e: MouseEvent) => void;
  onDelete: (p: Project) => void;
}

export function ProjectCard({ project: p, index, online, hasActiveSession, onOpen, onMove, onEdit, onDelete }: Props) {
  const [tileBg, tileFg] = TILE_COLORS[index % TILE_COLORS.length];
  const [menuOpen, setMenuOpen] = useState(false);

  const count = p.sessionCount ?? 0;
  const date = new Date(p.updatedAt).toLocaleDateString('ru-RU', { day: 'numeric', month: 'short' });
  const openLabel = `${count} ${pluralChats(count)} · ${date}`;
  const path = p.relativePath || p.rootPath;

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
          {initials(p.name)}
        </div>
      </div>

      <div style={{ flex: 1, minWidth: 0 }}>
        {/* Имя + меню действий */}
        <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
          <span style={{ flex: 1, fontSize: 16, fontWeight: 600, color: C.textHeading, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
            {p.name}
          </span>
          {online && (
            <div style={{ position: 'relative', flexShrink: 0 }} onClick={e => e.stopPropagation()}>
              <button
                onClick={() => setMenuOpen(v => !v)}
                title="Действия"
                style={{
                  width: 26, height: 26, borderRadius: R.sm, display: 'flex', alignItems: 'center', justifyContent: 'center',
                  color: '#B0A697', background: 'none', border: 'none', cursor: 'pointer',
                }}
              >
                <svg width="16" height="16" viewBox="0 0 24 24" fill="currentColor">
                  <circle cx="12" cy="5" r="1.6"/><circle cx="12" cy="12" r="1.6"/><circle cx="12" cy="19" r="1.6"/>
                </svg>
              </button>
              {menuOpen && (
                <>
                  <div style={{ position: 'fixed', inset: 0, zIndex: Z.dropdown }} onClick={() => setMenuOpen(false)} />
                  <div style={{
                    position: 'absolute', top: 30, right: 0, zIndex: Z.dropdown + 1,
                    background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.xl,
                    boxShadow: SHADOW.dropdown, padding: 5, minWidth: 200, display: 'flex', flexDirection: 'column',
                  }}>
                    <MenuItem label="Переместить в группу" onClick={() => { setMenuOpen(false); onMove(p); }}
                      icon={<path d="M3 7a2 2 0 0 1 2-2h4l2 3h8a2 2 0 0 1 2 2v7a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z"/>} />
                    <MenuItem label="Редактировать" onClick={(e) => { setMenuOpen(false); onEdit(p, e); }}
                      icon={<><path d="M11 4H4a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-7"/><path d="M18.5 2.5a2.121 2.121 0 0 1 3 3L12 15l-4 1 1-4 9.5-9.5z"/></>} />
                    <MenuItem label="Удалить" danger onClick={() => { setMenuOpen(false); onDelete(p); }}
                      icon={<><polyline points="3 6 5 6 21 6"/><path d="M19 6l-1 14a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2L5 6"/><path d="M10 11v6"/><path d="M14 11v6"/><path d="M9 6V4a1 1 0 0 1 1-1h4a1 1 0 0 1 1 1v2"/></>} />
                  </div>
                </>
              )}
            </div>
          )}
        </div>
        {/* Путь */}
        <div style={{ fontFamily: FONT.mono, fontSize: 11.5, color: '#9A8F7E', margin: '3px 0 6px', whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }} title={p.rootPath}>
          {path}
        </div>
        {/* Подпись: чаты · дата */}
        <div style={{ fontSize: 12, color: C.textSecondary, fontFamily: FONT.sans }}>
          {openLabel}
        </div>
      </div>
    </div>
  );
}

function MenuItem({ label, icon, onClick, danger }: { label: string; icon: ReactNode; onClick: (e: MouseEvent) => void; danger?: boolean }) {
  const [hover, setHover] = useState(false);
  const color = danger ? C.danger : C.textPrimary;
  return (
    <button
      onClick={onClick}
      onMouseEnter={() => setHover(true)}
      onMouseLeave={() => setHover(false)}
      style={{
        display: 'flex', alignItems: 'center', gap: 10, width: '100%', textAlign: 'left',
        background: hover ? C.bgSelected : 'none', border: 'none', borderRadius: R.md,
        padding: '9px 10px', cursor: 'pointer', color, fontSize: 13.5, fontFamily: FONT.sans,
      }}
    >
      <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" style={{ flexShrink: 0 }}>
        {icon}
      </svg>
      {label}
    </button>
  );
}
