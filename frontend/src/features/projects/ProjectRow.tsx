import { useState } from 'react';
import type { MouseEvent } from 'react';
import type { Project } from '../../types';
import { C, FONT, SHADOW } from '../../lib/design';
import { relativeTime } from './projectUtil';
import { ProjectIcon } from './ProjectIcon';
import { ProjectActionsMenu } from './ProjectActionsMenu';
import { ChevronRight, Pin } from 'lucide-react';
import { ICON_SIZE } from '../../components/ui/icons';
import { usePinnedIds } from '../../lib/pinnedProjects';

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
export function ProjectRow({ project: p, online, hasActiveSession, onOpen, onMove, onEdit, onDelete }: Props) {
  const [hover, setHover] = useState(false);
  const pinned = usePinnedIds().includes(p.id);

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
      <ProjectIcon project={p} size={42} radius={12} />

      <div style={{ flex: 1, minWidth: 0 }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 6, minWidth: 0 }}>
          <span style={{ fontSize: 15, fontWeight: 600, color: C.textHeading, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
            {p.name}
          </span>
          {pinned && <Pin size={13} strokeWidth={2} color={C.accent} style={{ flexShrink: 0 }} aria-label="Закреплён" />}
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
        <ChevronRight size={ICON_SIZE.md} strokeWidth={2} />
      </span>
    </div>
  );
}
