import { useState } from 'react';
import type { MouseEvent } from 'react';
import type { Project } from '../../types';
import { api } from '../../lib/api';
import { C, FONT, SHADOW } from '../../lib/design';
import { relativeTime } from './projectUtil';
import { ProjectIcon } from './ProjectIcon';
import { ProjectActionsMenu } from './ProjectActionsMenu';
import { SharedBadge } from '../../components/SharedBadge';
import { ConfirmDialog } from '../../components/ui';
import { useMe } from '../../lib/defaultPersona';
import { memberActionErrorText } from './members/membersLogic';
import { ChevronRight, Pin } from 'lucide-react';
import { ICON_SIZE } from '../../components/ui/icons';
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
  // Участник покинул проект (карта п.7): вызывающий убирает строку из списка
  onLeft?: (p: Project) => void;
}

// Десктопная строка проекта: плитка + имя/путь, справа — статус, действия, шеврон.
export function ProjectRow({ project: p, online, hasActiveSession, activityAt, onOpen, onMove, onEdit, onDelete, onRemoveFromSphere, onLeft }: Props) {
  const me = useMe();
  const [hover, setHover] = useState(false);
  const [leaving, setLeaving] = useState(false);
  const [leaveError, setLeaveError] = useState('');
  const pinned = usePinnedIds().includes(p.id);

  const rel = relativeTime(activityAt ?? p.updatedAt);
  const last = hasActiveSession && rel === 'только что' ? 'активна · только что' : rel;
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
      // Отказ: проект остаётся в списке, причина — в подтверждении (текст общий с «Участниками»)
      setLeaveError(memberActionErrorText(e));
    }
  };

  return (
    <>
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
          {/* minWidth: 0 — без него flex-минимум не даёт имени сжаться и метку вынесло бы за ряд */}
          <span style={{ minWidth: 0, fontSize: 15, fontWeight: 600, color: C.textHeading, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
            {p.name}
          </span>
          {/* Метка общего проекта (карта п.6): сразу после имени, до булавки;
              Badge несёт flexShrink:0 — при длинном имени сжимается имя */}
          <SharedBadge project={p} currentUserId={me.userId} />
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
          <ProjectActionsMenu project={p} color={C.textMuted} onMove={onMove} onEdit={onEdit} onDelete={onDelete} onRemoveFromSphere={onRemoveFromSphere}
            onLeave={() => setLeaving(true)} />
        </div>
      )}

      {/* Шеврон */}
      <span style={{ color: C.textMuted, flexShrink: 0, display: 'flex' }}>
        <ChevronRight size={ICON_SIZE.md} strokeWidth={2} />
      </span>
    </div>
    {/* Подтверждение выхода (карта п.42): диалог — СИБЛИНГ строки, не её потомок:
        клик модалки всплывает по React-дереву до onClick строки и открыл бы проект */}
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
