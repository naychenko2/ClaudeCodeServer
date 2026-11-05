// Список участников в настройках проекта (S1-F3): владелец первой строкой, дальше
// участники. Чисто презентационный: данные и действия приносит MembersSection, чтобы
// вид «владелец управляет / участник смотрит» проверялся статическим рендером.

import type { ReactNode } from 'react';
import { Crown, MoreVertical } from 'lucide-react';
import type { ProjectMember, ProjectMemberRole, ProjectOwnerRef } from '../../../types';
import { C, FONT, FS, SP } from '../../../lib/design';
import { Badge, IconButton, Select, UserAvatar } from '../../../components/ui';
import { ICON_SIZE } from '../../../components/ui/icons';
import { ROLE_LABEL } from './membersLogic';

const ROLE_OPTIONS = (['editor', 'reader'] as const).map(value => ({ value, label: ROLE_LABEL[value] }));

interface Props {
  owner: ProjectOwnerRef;
  members: ProjectMember[];
  meId: string | null;
  // Владелец может менять роли и убирать; участник видит роли подписью
  canManage: boolean;
  // Строки, по которым идёт запрос (userId), и подписи сбоя под строкой
  busy: ReadonlySet<string>;
  rowErrors: Readonly<Record<string, string>>;
  onRoleChange: (member: ProjectMember, role: ProjectMemberRole) => void;
  onMenu: (member: ProjectMember, anchor: DOMRect) => void;
}

function Row({ userId, name, username, isMe, children, error }: {
  userId: string; name: string; username?: string; isMe: boolean;
  children?: ReactNode; error?: string;
}) {
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xs }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.md, minHeight: 36 }}>
        <UserAvatar userId={userId} displayName={name} username={username} size={28} />
        <div style={{ minWidth: 0, flex: 1, fontFamily: FONT.sans }}>
          <div style={{
            fontSize: FS.base, fontWeight: 600, color: C.textHeading,
            overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
          }}>
            {name}{isMe && <span style={{ fontWeight: 400, color: C.textMuted }}> (вы)</span>}
          </div>
          {username && username !== name && (
            <div style={{
              fontSize: FS.xs, color: C.textMuted,
              overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
            }}>
              {username}
            </div>
          )}
        </div>
        {children}
      </div>
      {error && <div role="alert" style={{ fontSize: FS.sm, color: C.dangerText, paddingLeft: 28 + SP.md }}>{error}</div>}
    </div>
  );
}

export function MembersList({ owner, members, meId, canManage, busy, rowErrors, onRoleChange, onMenu }: Props) {
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
      <Row userId={owner.userId} name={owner.displayName?.trim() || owner.username}
        username={owner.username} isMe={owner.userId === meId}>
        <Badge tone="accent" size="xs" icon={<Crown size={ICON_SIZE.xs} />}>владелец</Badge>
      </Row>
      {members.map(m => {
        const name = m.displayName?.trim() || m.username || m.userId;
        const isBusy = busy.has(m.userId);
        return (
          <Row key={m.userId} userId={m.userId} name={name} username={m.username}
            isMe={m.userId === meId} error={rowErrors[m.userId]}>
            {canManage ? (
              <>
                <div style={{ width: 128, flexShrink: 0 }}>
                  <Select<ProjectMemberRole> value={m.role} options={ROLE_OPTIONS} disabled={isBusy}
                    title={`Роль: ${name}`}
                    onChange={v => { if (v && v !== m.role) onRoleChange(m, v); }} />
                </div>
                <IconButton size="sm" title="Действия" ariaLabel={`Действия: ${name}`} disabled={isBusy}
                  onClick={e => onMenu(m, e.currentTarget.getBoundingClientRect())}>
                  <MoreVertical size={ICON_SIZE.sm} />
                </IconButton>
              </>
            ) : (
              <Badge tone="neutral" size="xs">{ROLE_LABEL[m.role]}</Badge>
            )}
          </Row>
        );
      })}
    </div>
  );
}
