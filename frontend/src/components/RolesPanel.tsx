import { useState, useEffect } from 'react';
import type { Project, Role, Session } from '../types';
import { api } from '../lib/api';
import { C, R, SHADOW, MODAL_W } from '../lib/design';
import { Modal, ModalActions } from './ui';
import { RoleAvatar } from './RoleAvatar';
import { RoleEditorDialog } from './RoleEditorDialog';

interface Props {
  project: Project;
  onStartChat: (session: Session) => void;   // открыть свежесозданный чат с ролью
  isMobile?: boolean;
}

// Панель «Команда» проекта: роли, прикомандированные к проекту. Тык по роли = открыть
// существующий чат проекта с ней (или создать первый). Найм — через попап нового члена
// команды (вручную / собеседование / нанять существующего из пула);
// удаление = ОТКРЕПЛЕНИЕ (роль остаётся в пуле, её память о проекте сохраняется).
export function RolesPanel({ project, onStartChat, isMobile = false }: Props) {
  const [roles, setRoles] = useState<Role[]>([]);
  const [creating, setCreating] = useState(false);
  const [editTarget, setEditTarget] = useState<Role | null>(null);
  const [unassignTarget, setUnassignTarget] = useState<Role | null>(null);
  const [starting, setStarting] = useState<string | null>(null);

  useEffect(() => {
    api.roles.list(project.id).then(setRoles).catch(() => {});
  }, [project.id]);

  // mode 'auto' как у обычного нового чата; имя сессии = имя роли; roleId — последним
  const createChat = (role: Role) =>
    api.sessions.create(project.id, 'auto', undefined, role.name, undefined, undefined, undefined, role.id);

  // Тык по сотруднику = продолжить существующий разговор с ним (как в мессенджере).
  // Новый чат создаётся, только если чатов с этой ролью в проекте ещё нет;
  // ещё один разговор — иконкой «Новый чат» на карточке.
  const startChat = async (role: Role, forceNew = false) => {
    if (starting) return;
    setStarting(role.id);
    try {
      if (!forceNew) {
        const sessions = await api.sessions.list(project.id);
        const existing = sessions.find(s => s.roleId === role.id);   // список отсортирован по свежести
        if (existing) {
          onStartChat(existing);
          return;
        }
      }
      onStartChat(await createChat(role));
    } catch {
      /* офлайн/сбой — ничего не меняем */
    } finally {
      setStarting(null);
    }
  };

  const handleSaved = (saved: Role) => {
    setRoles(prev => prev.some(r => r.id === saved.id)
      ? prev.map(r => (r.id === saved.id ? saved : r))
      : [...prev, saved]);
    setCreating(false);
    setEditTarget(null);
  };

  const handleUnassign = async () => {
    if (!unassignTarget) return;
    try {
      await api.roles.unassign(project.id, unassignTarget.id);
    } catch {
      setUnassignTarget(null);
      return;
    }
    setRoles(prev => prev.filter(r => r.id !== unassignTarget.id));
    setUnassignTarget(null);
  };

  const iconBtn = (onClick: (e: React.MouseEvent) => void, title: string, danger: boolean, path: React.ReactNode) => (
    <button
      onClick={onClick}
      title={title}
      style={{
        background: 'none', border: 'none', cursor: 'pointer', color: C.textMuted, padding: 0,
        flexShrink: 0, width: 24, height: 24, borderRadius: R.sm,
        display: 'flex', alignItems: 'center', justifyContent: 'center',
      }}
      onMouseEnter={e => { e.currentTarget.style.color = danger ? C.danger : C.textPrimary; e.currentTarget.style.background = danger ? C.dangerBg : C.bgPanel; }}
      onMouseLeave={e => { e.currentTarget.style.color = C.textMuted; e.currentTarget.style.background = 'none'; }}
    >
      <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">{path}</svg>
    </button>
  );

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%' }}>
      <div style={{ padding: '10px 12px', borderBottom: `1px solid ${C.divider}` }}>
        <button
          onClick={() => setCreating(true)}
          style={{
            width: '100%', padding: 11, borderRadius: R.xl,
            border: `1.5px dashed ${C.dashed}`, background: 'none', cursor: 'pointer',
            fontSize: 13, color: C.accent, fontWeight: 600,
            display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 7,
          }}
        >
          + Новый член команды
        </button>
      </div>

      <div style={{ flex: 1, overflowY: 'auto', padding: '8px 8px' }}>
        {roles.length === 0 ? (
          <div style={{ padding: '28px 18px', textAlign: 'center', color: C.textMuted, fontSize: 13, lineHeight: 1.5 }}>
            В команде проекта пока никого.<br />Наймите сотрудника — нового или из общего пула.
          </div>
        ) : roles.map(role => (
          <div
            key={role.id}
            onClick={() => startChat(role)}
            title="Открыть чат с сотрудником"
            style={{
              position: 'relative', display: 'flex', alignItems: 'center', gap: 10,
              paddingTop: isMobile ? 12 : 10, paddingBottom: isMobile ? 12 : 10,
              paddingLeft: isMobile ? 14 : 11, paddingRight: isMobile ? 14 : 11,
              borderRadius: isMobile ? 16 : R.xl, marginBottom: 5, cursor: 'pointer',
              background: C.bgWhite, border: `1px solid ${C.borderLight}`, boxShadow: SHADOW.card,
              opacity: starting === role.id ? 0.6 : 1,
            }}
          >
            <RoleAvatar name={role.name} avatar={role.avatar} color={role.color} size={36} />
            <div style={{ flex: 1, minWidth: 0 }}>
              <div style={{ fontSize: 13.5, fontWeight: 600, color: C.textHeading, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                {role.name || 'Без имени'}
              </div>
              {role.title && (
                <div style={{ fontSize: 12, color: C.textMuted, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                  {role.title}
                </div>
              )}
            </div>
            <div style={{ display: 'flex', flexShrink: 0 }}>
              {iconBtn(e => { e.stopPropagation(); startChat(role, true); }, 'Новый чат с сотрудником', false, (
                <>
                  <path d="M21 11.5a8.5 8.5 0 0 1-12 7.7L3 21l1.8-6A8.5 8.5 0 1 1 21 11.5z" />
                  <path d="M12 8v6M9 11h6" />
                </>
              ))}
              {iconBtn(e => { e.stopPropagation(); setEditTarget(role); }, 'Редактировать', false, (
                <>
                  <path d="M11 4H4a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-7" />
                  <path d="M18.5 2.5a2.121 2.121 0 0 1 3 3L12 15l-4 1 1-4 9.5-9.5z" />
                </>
              ))}
              {iconBtn(e => { e.stopPropagation(); setUnassignTarget(role); }, 'Убрать из команды проекта', true, (
                <>
                  <circle cx="9" cy="7" r="4" />
                  <path d="M2 21v-2a4 4 0 0 1 4-4h6" />
                  <path d="M17 8l5 5M22 8l-5 5" />
                </>
              ))}
            </div>
          </div>
        ))}
      </div>

      {(creating || editTarget) && (
        <RoleEditorDialog
          projectId={project.id}
          role={editTarget ?? undefined}
          onSaved={handleSaved}
          onClose={() => { setCreating(false); setEditTarget(null); }}
        />
      )}

      {unassignTarget && (
        <Modal
          title="Убрать из команды проекта?"
          width={MODAL_W.confirm}
          onClose={() => setUnassignTarget(null)}
          subtitle={
            <>
              Сотрудник «<strong style={{ color: C.textPrimary, fontWeight: 600 }}>{unassignTarget.name}</strong>» будет откреплён от проекта.
              Он останется в общем пуле («Сотрудники»), его память об этом проекте и существующие чаты сохранятся.
            </>
          }
          footer={
            <ModalActions
              confirmLabel="Убрать"
              confirmVariant="danger"
              onConfirm={handleUnassign}
              onCancel={() => setUnassignTarget(null)}
            />
          }
        />
      )}

    </div>
  );
}
