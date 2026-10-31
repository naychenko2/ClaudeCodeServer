// Человек-исполнитель задачи в общем проекте и строка «Поставил …» (S2b-F4).
// Данные — assigneeUserId/createdByUserId задачи + полный список участников проекта
// (общий кэш подписи автора в чате) + formerMembers проекта. У задач без этих полей
// (личный проект, старый бэк) ничего не рендерим и ничего не грузим.

import { useEffect, useState } from 'react';
import { C, FONT, FS, SP } from '../../lib/design';
import { Badge } from '../../components/ui';
import { UserAvatar } from '../../components/ui/UserAvatar';
import { onProjectMembersChanged } from '../../lib/signalr';
import { getCachedProject } from '../projects/useAllProjects';
import { loadView } from '../projects/members/MessageAuthorLine';
import type { Project, ProjectMembersView } from '../../types';

export const UNASSIGNED_LABEL = 'Без исполнителя';
const UNKNOWN_NAME = 'Участник';

type NameSrc = { displayName?: string | null; username?: string | null } | undefined;
const nameOf = (p: NameSrc) => p?.displayName?.trim() || p?.username?.trim() || UNKNOWN_NAME;

export type TaskAssignee =
  | { state: 'member'; userId: string; name: string }
  // Исполнитель не из участников проекта; formerName — он среди бывших (formerMembers)
  | { state: 'none'; formerName?: string };

// Чистая логика: владелец тоже считается участником. view === null (список ещё не пришёл) → null,
// чтобы не мигнуть «Без исполнителя» на загрузке
export function resolveTaskAssignee(
  userId: string | null | undefined,
  view: Pick<ProjectMembersView, 'members' | 'owner'> | null | undefined,
  project: Pick<Project, 'formerMembers'> | null | undefined,
): TaskAssignee | null {
  if (!userId || !view) return null;
  if (view.owner.userId === userId) return { state: 'member', userId, name: nameOf(view.owner) };
  const m = view.members.find(x => x.userId === userId);
  if (m) return { state: 'member', userId, name: nameOf(m) };
  const former = project?.formerMembers?.find(x => x.userId === userId);
  return former ? { state: 'none', formerName: nameOf(former) } : { state: 'none' };
}

// Имя того, кто поставил задачу; запасное — «Участник» (бывший тоже показываем по имени)
export function resolveCreatorName(
  userId: string | null | undefined,
  view: Pick<ProjectMembersView, 'members' | 'owner'> | null | undefined,
  project: Pick<Project, 'formerMembers'> | null | undefined,
): string | null {
  if (!userId) return null;
  if (view?.owner.userId === userId) return nameOf(view.owner);
  return nameOf(view?.members.find(x => x.userId === userId) ?? project?.formerMembers?.find(x => x.userId === userId));
}

// «12 окт» — без точки после сокращения месяца
export function formatCreatedDate(iso: string | undefined): string | null {
  if (!iso) return null;
  const d = new Date(iso);
  if (isNaN(d.getTime())) return null;
  return d.toLocaleDateString('ru-RU', { day: 'numeric', month: 'short' }).replace(/\.$/, '');
}

// Список участников проекта; живёт, пока проект известен и people нужны
export function useProjectPeople(projectId: string | undefined, enabled: boolean) {
  const [view, setView] = useState<ProjectMembersView | null>(null);
  useEffect(() => {
    if (!enabled || !projectId) return;
    let alive = true;
    const pull = (fresh: boolean) => {
      loadView(projectId, fresh).then(v => { if (alive) setView(v); }).catch(() => { /* вид останется без имён */ });
    };
    pull(false);
    const off = onProjectMembersChanged(({ projectId: id }) => { if (id === projectId) pull(true); });
    return () => { alive = false; off(); };
  }, [enabled, projectId]);
  return { view, project: projectId ? getCachedProject(projectId) : undefined };
}

interface Props {
  projectId?: string;
  assigneeUserId?: string | null;
  size?: 18 | 20;
  // Действие «Назначить» у строки бывшего исполнителя; нет — строка без кнопки
  onAssign?: () => void;
}

function Inner({ projectId, assigneeUserId, size = 20 }: Props) {
  const { view, project } = useProjectPeople(projectId, true);
  const a = resolveTaskAssignee(assigneeUserId, view, project);
  if (!a) return null;
  if (a.state === 'none') {
    return (
      <Badge tone="warning" size="xs" title={a.formerName ? `Была на ${a.formerName} — больше не в проекте` : UNASSIGNED_LABEL}>
        {UNASSIGNED_LABEL}
      </Badge>
    );
  }
  return (
    <span
      data-testid="task-user-badge"
      title={a.name}
      style={{ display: 'inline-flex', alignItems: 'center', gap: SP.xs, minWidth: 0 }}
    >
      <UserAvatar userId={a.userId} displayName={a.name} size={size} />
      <span style={{
        fontFamily: FONT.sans, fontSize: 11, fontWeight: 600, color: C.textSecondary,
        overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
      }}>
        {a.name}
      </span>
    </span>
  );
}

// Нет assigneeUserId — не рендерим и не грузим (личный проект, старый бэк)
export function TaskUserBadge(props: Props) {
  if (!props.assigneeUserId) return null;
  return <Inner {...props} />;
}

// Строка «была на <имя> · Назначить» под бейджем «Без исполнителя» (деталь задачи).
// «Назначить» — кнопкой только когда вызывающий дал onAssign; иначе строка без действия
export function FormerAssigneeLine({ projectId, assigneeUserId, onAssign }: Props) {
  const { view, project } = useProjectPeople(projectId, !!assigneeUserId);
  const a = resolveTaskAssignee(assigneeUserId, view, project);
  if (!a || a.state !== 'none' || !a.formerName) return null;
  return (
    <span data-testid="former-assignee" style={{ fontFamily: FONT.sans, fontSize: FS.xs, color: C.textMuted }}>
      была на {a.formerName}
      {onAssign && (
        <>
          {' · '}
          <button type="button" onClick={onAssign} style={{
            all: 'unset', cursor: 'pointer', color: C.accent, fontWeight: 600,
          }}>Назначить</button>
        </>
      )}
    </span>
  );
}

// «Поставил Андрей · 12 окт»; createdByUserId нет → строки нет
export function CreatedByLine({ projectId, createdByUserId, createdAt }: {
  projectId?: string; createdByUserId?: string | null; createdAt?: string;
}) {
  const { view, project } = useProjectPeople(projectId, !!createdByUserId);
  if (!createdByUserId) return null;
  const name = resolveCreatorName(createdByUserId, view, project);
  const date = formatCreatedDate(createdAt);
  return (
    <div data-testid="created-by" style={{ fontFamily: FONT.sans, fontSize: FS.xs, color: C.textMuted, marginTop: 6 }}>
      Поставил {name}{date ? ` · ${date}` : ''}
    </div>
  );
}
