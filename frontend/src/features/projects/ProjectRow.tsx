import { useState } from 'react';
import type { MouseEvent } from 'react';
import type { Project } from '../../types';
import { C, FONT, SHADOW } from '../../lib/design';
import { tileColors, firstLetter, relativeTime } from './projectUtil';
import { ProjectActionsMenu } from './ProjectActionsMenu';
import { useThemeMode } from '../../lib/themeMode';

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

// Десктопная строка проекта: плитка + имя/путь, справа — статус, действия, шеврон.
export function ProjectRow({ project: p, index, online, hasActiveSession, onOpen, onMove, onEdit, onDelete }: Props) {
  useThemeMode();  // перекраска плашки при смене темы
  const [tileBg, tileFg] = tileColors(index);
  const [hover, setHover] = useState(false);

  const rel = relativeTime(p.updatedAt);
  const last = hasActiveSession && rel === 'только что' ? 'активна · только что' : rel;
  const path = p.relativePath || p.rootPath;

  return (
    <div
      onClick={() => onOpen(p)}
      onMouseEnter={() => setHover(true)}
      onMouseLeave={() => setHover(false)}
      style={{
        display: 'flex', alignItems: 'center', gap: 14, background: C.bgWhite,
        border: `1px solid ${C.borderLight}`, borderRadius: 14, padding: '12px 16px',
        cursor: 'pointer', boxShadow: SHADOW.card,
      }}
    >
      <div style={{
        width: 42, height: 42, borderRadius: 12, background: tileBg, color: tileFg,
        fontFamily: FONT.serif, fontSize: 19, fontWeight: 700, flexShrink: 0,
        display: 'flex', alignItems: 'center', justifyContent: 'center',
      }}>
        {firstLetter(p.name)}
      </div>

      <div style={{ flex: 1, minWidth: 0 }}>
        <div style={{ fontSize: 15, fontWeight: 600, color: C.textHeading, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
          {p.name}
        </div>
        <div style={{ fontFamily: FONT.mono, fontSize: 11, color: C.textMuted, marginTop: 2, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }} title={p.rootPath}>
          {path}
        </div>
      </div>

      {/* Статус активности / время */}
      <div style={{ display: 'flex', alignItems: 'center', gap: 7, flexShrink: 0 }}>
        {hasActiveSession && (
          <span style={{ width: 7, height: 7, borderRadius: '50%', background: C.success, flexShrink: 0 }} />
        )}
        <span style={{ fontSize: 12, color: C.textSecondary, whiteSpace: 'nowrap' }}>{last}</span>
      </div>

      {/* Действия (появляются при наведении) */}
      {online && (
        <div style={{ opacity: hover ? 1 : 0, transition: 'opacity 0.12s', flexShrink: 0 }}>
          <ProjectActionsMenu project={p} color={C.textMuted} onMove={onMove} onEdit={onEdit} onDelete={onDelete} />
        </div>
      )}

      {/* Шеврон */}
      <span style={{ color: C.textMuted, flexShrink: 0, display: 'flex' }}>
        <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
          <polyline points="9 18 15 12 9 6"/>
        </svg>
      </span>
    </div>
  );
}
