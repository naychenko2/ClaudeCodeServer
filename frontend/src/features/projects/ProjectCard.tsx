import { useState } from 'react';
import type { MouseEvent } from 'react';
import type { Project } from '../../types';
import { api } from '../../lib/api';
import { C, FONT, SHADOW } from '../../lib/design';
import { pluralChats } from './projectUtil';
import { ProjectIcon } from './ProjectIcon';
import { ProjectActionsMenu } from './ProjectActionsMenu';
import { SharedBadge } from '../../components/SharedBadge';
import { ConfirmDialog } from '../../components/ui';
import { useMe } from '../../lib/defaultPersona';
import { memberActionErrorText } from './members/membersLogic';
import { Pin } from 'lucide-react';
import { usePinnedIds } from '../../lib/pinnedProjects';

interface Props {
  project: Project;
  index: number;
  online: boolean;
  hasActiveSession?: boolean;
  // Последняя активность (свежее из проекта и его чатов); нет — берём updatedAt проекта
  activityAt?: string;
  onOpen: (p: Project) => void;
  onMove: (p: Project) => void;
  onEdit: (p: Project, e: MouseEvent) => void;
  onDelete: (p: Project) => void;
  onRemoveFromSphere?: (p: Project) => void;
  // Участник покинул проект (карта п.7): вызывающий убирает карточку из списка
  onLeft?: (p: Project) => void;
}

// Мобильная карточка проекта: плитка + имя (+ меню) + путь + подпись «N чатов · дата».
export function ProjectCard({ project: p, online, hasActiveSession, activityAt, onOpen, onMove, onEdit, onDelete, onRemoveFromSphere, onLeft }: Props) {
  const me = useMe();
  const [leaving, setLeaving] = useState(false);
  const [leaveError, setLeaveError] = useState('');
  const pinned = usePinnedIds().includes(p.id);

  const count = p.sessionCount ?? 0;
  const date = new Date(activityAt ?? p.updatedAt).toLocaleDateString('ru-RU', { day: 'numeric', month: 'short' });
  const openLabel = `${count} ${pluralChats(count)} · ${date}`;
  const path = p.relativePath || p.rootPath;

  // Имя владельца — из данных проекта, как в секции «Участники» (MembersSection:130);
  // нет имени — датив «владельца»
  const ownerName = p.ownerDisplayName?.trim() || p.ownerUsername?.trim() || 'владельца';
  const confirmLeave = async () => {
    setLeaveError('');
    try {
      await api.projects.members.leave(p.id);
      setLeaving(false);
      onLeft?.(p);
    } catch (e) {
      // Отказ: карточка остаётся в списке, причина — в подтверждении (текст общий с «Участниками»)
      setLeaveError(memberActionErrorText(e));
    }
  };

  return (
    <>
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
        <ProjectIcon project={p} size={50} radius={16} />
      </div>

      <div style={{ flex: 1, minWidth: 0 }}>
        {/* Имя + метка + меню действий */}
        <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
          {/* Метка общего проекта (карта п.6): сразу после имени. Имя и метка в одном
              flex-обёрнутом span: не влезает — имя сокращается (minWidth: 0), метка
              остаётся (Badge несёт flexShrink:0). Паттерн — ProjectsWidget/ProjectPalette */}
          <span style={{ flex: 1, minWidth: 0, display: 'flex', alignItems: 'center', gap: 6 }}>
            <span style={{ minWidth: 0, fontSize: 16, fontWeight: 600, color: C.textHeading, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
              {p.name}
            </span>
            <SharedBadge project={p} currentUserId={me.userId} />
          </span>
          {pinned && <Pin size={13} strokeWidth={2} color={C.accent} style={{ flexShrink: 0 }} aria-label="Закреплён" />}
          {online && <ProjectActionsMenu project={p} onMove={onMove} onEdit={onEdit} onDelete={onDelete} onRemoveFromSphere={onRemoveFromSphere}
            onLeave={() => setLeaving(true)} />}
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
    {/* Подтверждение выхода (карта п.42): диалог — СИБЛИНГ карточки, не её потомок:
        клик модалки всплывает по React-дереву до onClick карточки и открыл бы проект */}
    {leaving && (
      <ConfirmDialog
        title="Покинуть проект?"
        confirmLabel="Покинуть проект" confirmVariant="danger"
        subtitle={<>
          <div>Проект {ownerName} пропадёт из вашего списка. Вернуться можно, только если владелец пригласит вас снова.</div>
          {leaveError && <div role="alert" style={{ color: C.dangerText, marginTop: 6 }}>{leaveError}</div>}
        </>}
        onConfirm={confirmLeave}
        onCancel={() => { setLeaving(false); setLeaveError(''); }}
      />
    )}
    </>
  );
}
