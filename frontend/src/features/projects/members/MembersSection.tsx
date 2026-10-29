// Секция «Участники» в настройках проекта (S1-F3). Два вида одной секции:
//  · владелец — форма приглашения по username, роли в Select, меню «⋯», первое
//    приглашение через ConfirmDialog «Открыть проект для участников?»;
//  · участник (editor/reader) — список без контролов и «Покинуть проект».
// Состояние списка живёт здесь: любая мутация отвечает полным ProjectMembersView.

import { useCallback, useEffect, useState } from 'react';
import { AlertTriangle, Info, UserMinus, Users } from 'lucide-react';
import type { Project, ProjectMember, ProjectMemberRole, ProjectMembersView } from '../../../types';
import { api } from '../../../lib/api';
import { C, FS, SP } from '../../../lib/design';
import { Button, ConfirmDialog, Field, Notice, Select, TextField } from '../../../components/ui';
import { Menu, MenuItem } from '../../../components/ui/Menu';
import { ICON_SIZE } from '../../../components/ui/icons';
import { useMe } from '../../../lib/defaultPersona';
import { isLocalProject } from '../../../lib/projectCapabilities';
import { useIsMobile } from '../../../lib/breakpoints';
import { AccordionSection } from '../dialogs/AccordionSection';
import { MembersList } from './MembersList';
import {
  ROLE_LABEL, inviteErrorText, memberActionErrorText, membersSummary,
} from './membersLogic';

const ROLE_OPTIONS = (['editor', 'reader'] as const).map(value => ({ value, label: ROLE_LABEL[value] }));

interface Props {
  project: Project;
  // owner — управляет; member — только смотрит и может уйти
  mode: 'owner' | 'member';
  // Список изменился — вызывающий обновляет проект в сторе (метка «общий» в списках)
  onMembersChanged?: (members: ProjectMember[]) => void;
  // Участник покинул проект — вызывающий закрывает диалог
  onLeft?: () => void;
  // Раскрыть сразу: пришли по стопке аватаров в шапке (у участника секция открыта всегда)
  defaultOpen?: boolean;
}

export function MembersSection({ project, mode, onMembersChanged, onLeft, defaultOpen }: Props) {
  const me = useMe();
  const isMobile = useIsMobile();
  const isOwner = mode === 'owner';
  // Проект на устройстве нельзя открыть другим людям: формы приглашения нет, вместо неё причина
  const local = isOwner && isLocalProject(project);

  const [view, setView] = useState<ProjectMembersView | null>(null);
  const [loadFailed, setLoadFailed] = useState(false);

  const [username, setUsername] = useState('');
  const [role, setRole] = useState<ProjectMemberRole>('editor');
  const [adding, setAdding] = useState(false);
  const [formError, setFormError] = useState('');
  const [confirmFirst, setConfirmFirst] = useState(false);

  const [busy, setBusy] = useState<ReadonlySet<string>>(new Set());
  const [rowErrors, setRowErrors] = useState<Record<string, string>>({});
  const [menu, setMenu] = useState<{ member: ProjectMember; anchor: DOMRect } | null>(null);
  const [removing, setRemoving] = useState<ProjectMember | null>(null);
  const [leaving, setLeaving] = useState(false);
  const [leaveError, setLeaveError] = useState('');

  const apply = useCallback((next: ProjectMembersView) => {
    setView(next);
    onMembersChanged?.(next.members);
  }, [onMembersChanged]);

  const load = useCallback(async () => {
    setLoadFailed(false);
    try {
      setView(await api.projects.members.list(project.id));
    } catch {
      setLoadFailed(true);
    }
  }, [project.id]);
  useEffect(() => { void load(); }, [load]);

  const members = view?.members ?? [];
  const summary = view ? membersSummary(members.length) : undefined;

  const doAdd = async () => {
    setAdding(true); setFormError('');
    try {
      apply(await api.projects.members.add(project.id, username.trim(), role));
      setUsername('');
    } catch (e) {
      // Введённое не стираем: человек поправит имя, а не набирает заново
      setFormError(inviteErrorText(e, username, members));
    } finally {
      setAdding(false);
    }
  };

  // Первое приглашение открывает проект для людей — спрашиваем один раз, пока участников нет
  const onAddClick = () => {
    if (!username.trim() || adding) return;
    if (members.length === 0) setConfirmFirst(true);
    else void doAdd();
  };

  const withRow = async (userId: string, op: () => Promise<ProjectMembersView>) => {
    setBusy(b => new Set(b).add(userId));
    setRowErrors(r => { const { [userId]: _drop, ...rest } = r; return rest; });
    try {
      apply(await op());
    } catch (e) {
      setRowErrors(r => ({ ...r, [userId]: memberActionErrorText(e) }));
    } finally {
      setBusy(b => { const n = new Set(b); n.delete(userId); return n; });
    }
  };

  const onRoleChange = (m: ProjectMember, next: ProjectMemberRole) =>
    void withRow(m.userId, () => api.projects.members.changeRole(project.id, m.userId, next));

  const confirmRemove = async () => {
    const m = removing;
    setRemoving(null);
    if (m) await withRow(m.userId, () => api.projects.members.remove(project.id, m.userId));
  };

  const confirmLeave = async () => {
    setLeaveError('');
    try {
      await api.projects.members.leave(project.id);
      setLeaving(false);
      onLeft?.();
    } catch (e) {
      setLeaving(false);
      setLeaveError(memberActionErrorText(e));
    }
  };

  const ownerName = view?.owner.displayName?.trim() || view?.owner.username || project.ownerDisplayName || 'владельца';

  return (
    <AccordionSection
      icon={Users}
      title="Участники"
      summary={summary?.text}
      summaryTone={summary?.tone}
      defaultOpen={!isOwner || !!defaultOpen}
    >
      <div style={{ display: 'flex', flexDirection: 'column', gap: SP.md }}>
        {local && (
          <Notice tone="info" icon={Info}>
            Проект хранится на вашем устройстве, поэтому открыть его другим людям нельзя.
          </Notice>
        )}
        {isOwner && !local && (
          <Field label="Имя учётки" error={formError || undefined}>
            <div style={{ display: 'flex', gap: SP.sm, flexDirection: isMobile ? 'column' : 'row', alignItems: isMobile ? 'stretch' : 'flex-start' }}>
              <div style={{ flex: 1, minWidth: 0 }}>
                <TextField value={username} placeholder="maria" disabled={adding} invalid={!!formError}
                  onChange={v => { setUsername(v); if (formError) setFormError(''); }}
                  onEnter={onAddClick} />
              </div>
              <div style={{ display: 'flex', gap: SP.sm }}>
                <div style={{ width: 128, flexShrink: 0 }}>
                  <Select<ProjectMemberRole> value={role} options={ROLE_OPTIONS} disabled={adding}
                    onChange={v => { if (v) setRole(v); }} />
                </div>
                <Button variant="primary" size="md" loading={adding} disabled={!username.trim()}
                  onClick={onAddClick} fullWidth={isMobile}>
                  {adding ? 'Добавляем…' : 'Добавить'}
                </Button>
              </div>
            </div>
          </Field>
        )}

        {!view && !loadFailed && (
          <div aria-hidden style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
            {[0, 1].map(i => <div key={i} style={{ height: 36, borderRadius: 8, background: C.bgInset }} />)}
          </div>
        )}
        {loadFailed && (
          <Notice tone="danger" icon={AlertTriangle} title="Не загрузили участников"
            action={<Button variant="ghost" size="sm" onClick={() => void load()}>Повторить</Button>}>
            Ничего не изменилось. Проверьте связь и повторите.
          </Notice>
        )}
        {view && (
          <MembersList owner={view.owner} members={members} meId={me.userId} canManage={isOwner}
            busy={busy} rowErrors={rowErrors} onRoleChange={onRoleChange}
            onMenu={(member, anchor) => setMenu({ member, anchor })} />
        )}
        {view && isOwner && !local && members.length === 0 && (
          <div style={{ fontSize: FS.sm, color: C.textSecondary, lineHeight: 1.5 }}>
            Пригласите человека с учёткой на этом сервере: он увидит чаты, файлы и задачи проекта.
            Ходы участников идут за ваш счёт.
          </div>
        )}

        {!isOwner && (
          <>
            {leaveError && <div role="alert" style={{ fontSize: FS.sm, color: C.dangerText }}>{leaveError}</div>}
            <Button variant="ghost" size={isMobile ? 'md' : 'sm'} fullWidth={isMobile}
              style={{ color: C.danger, alignSelf: isMobile ? 'stretch' : 'flex-start' }}
              onClick={() => setLeaving(true)}>
              Покинуть проект
            </Button>
          </>
        )}
      </div>

      {menu && (
        <Menu anchor={menu.anchor} onClose={() => setMenu(null)} gap={4}>
          <MenuItem label="Убрать из проекта" danger
            icon={<UserMinus size={ICON_SIZE.sm} />}
            onClick={() => { setRemoving(menu.member); setMenu(null); }} />
        </Menu>
      )}

      {confirmFirst && (
        <ConfirmDialog
          title="Открыть проект для участников?"
          confirmLabel="Открыть и добавить"
          subtitle={
            <ul style={{ margin: 0, paddingLeft: 18, display: 'flex', flexDirection: 'column', gap: SP.xs }}>
              <li>«{username.trim()}» увидит чаты, файлы и задачи проекта.</li>
              <li>Ходы участников идут по вашим настройкам и за ваш счёт.</li>
              <li>Убрать человека или сменить его роль можно в любой момент.</li>
            </ul>
          }
          onConfirm={async () => { setConfirmFirst(false); await doAdd(); }}
          onCancel={() => setConfirmFirst(false)}
        />
      )}

      {removing && (
        <ConfirmDialog
          title={`Убрать ${removing.displayName?.trim() || removing.username || 'участника'} из проекта?`}
          confirmLabel="Убрать из проекта" confirmVariant="danger"
          subtitle={
            <ul style={{ margin: 0, paddingLeft: 18, display: 'flex', flexDirection: 'column', gap: SP.xs }}>
              <li>Его задачи в проекте станут «без исполнителя».</li>
              <li>Его приватные чаты скроются и вернутся, если пригласить его снова.</li>
              <li>Ход, который уже идёт, доработает.</li>
            </ul>
          }
          onConfirm={confirmRemove}
          onCancel={() => setRemoving(null)}
        />
      )}

      {leaving && (
        <ConfirmDialog
          title="Покинуть проект?"
          confirmLabel="Покинуть проект" confirmVariant="danger"
          subtitle={`Проект ${ownerName} пропадёт из вашего списка. Вернуться можно, только если владелец пригласит вас снова.`}
          onConfirm={confirmLeave}
          onCancel={() => setLeaving(false)}
        />
      )}
    </AccordionSection>
  );
}
