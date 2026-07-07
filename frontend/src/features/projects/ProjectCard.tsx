import type { MouseEvent } from 'react';
import type { Project } from '../../types';
import { C, R, FONT, SHADOW } from '../../lib/design';
import { tileColors, firstLetter, pluralChats } from './projectUtil';
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

// Мобильная карточка проекта: плитка + имя (+ меню) + путь + подпись «N чатов · дата».
export function ProjectCard({ project: p, index, online, hasActiveSession, onOpen, onMove, onEdit, onDelete }: Props) {
  useThemeMode();  // перекраска плашки при смене темы
  const [tileBg, tileFg] = tileColors(index);

  const count = p.sessionCount ?? 0;
  const date = new Date(p.updatedAt).toLocaleDateString('ru-RU', { day: 'numeric', month: 'short' });
  const openLabel = `${count} ${pluralChats(count)} · ${date}`;
  const path = p.relativePath || p.rootPath;

  return (
    <div
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
            <span className="pc-pulse" style={{ position: 'absolute', top: 1, right: 1, width: 9, height: 9, borderRadius: '50%', background: C.accent, border: `2px solid ${C.bgMain}`, zIndex: 1 }} />
          </>
        )}
        <div style={{
          width: 50, height: 50, borderRadius: R.xxl, background: tileBg, color: tileFg,
          fontFamily: FONT.serif, fontSize: 22, fontWeight: 600,
          display: 'flex', alignItems: 'center', justifyContent: 'center',
        }}>
          {firstLetter(p.name)}
        </div>
      </div>

      <div style={{ flex: 1, minWidth: 0 }}>
        {/* Имя + меню действий */}
        <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
          <span style={{ flex: 1, fontSize: 16, fontWeight: 600, color: C.textHeading, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
            {p.name}
          </span>
          {online && <ProjectActionsMenu project={p} onMove={onMove} onEdit={onEdit} onDelete={onDelete} />}
        </div>
        {/* Путь */}
        <div style={{ fontFamily: FONT.mono, fontSize: 11.5, color: C.textMuted, margin: '3px 0 6px', whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }} title={p.rootPath}>
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
