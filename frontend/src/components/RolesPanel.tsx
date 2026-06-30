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

// Панель «Команда»: список ролей-собеседников проекта. Тык по роли = новый чат с ней.
export function RolesPanel({ project, onStartChat, isMobile = false }: Props) {
  const [roles, setRoles] = useState<Role[]>([]);
  const [creating, setCreating] = useState(false);
  const [editTarget, setEditTarget] = useState<Role | null>(null);
  const [deleteTarget, setDeleteTarget] = useState<Role | null>(null);
  const [starting, setStarting] = useState<string | null>(null);

  useEffect(() => {
    api.roles.list(project.id).then(setRoles).catch(() => {});
  }, [project.id]);

  const startChat = async (role: Role) => {
    if (starting) return;
    setStarting(role.id);
    try {
      // mode 'auto' как у обычного нового чата; имя сессии = имя роли; roleId — последним
      const s = await api.sessions.create(project.id, 'auto', undefined, role.name, undefined, undefined, undefined, role.id);
      onStartChat(s);
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

  const handleDelete = async () => {
    if (!deleteTarget) return;
    try {
      await api.roles.delete(project.id, deleteTarget.id);
    } catch {
      setDeleteTarget(null);
      return;
    }
    setRoles(prev => prev.filter(r => r.id !== deleteTarget.id));
    setDeleteTarget(null);
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
          + Новая роль
        </button>
      </div>

      <div style={{ flex: 1, overflowY: 'auto', padding: '8px 8px' }}>
        {roles.length === 0 ? (
          <div style={{ padding: '28px 18px', textAlign: 'center', color: C.textMuted, fontSize: 13, lineHeight: 1.5 }}>
            Пока нет ролей.<br />Создайте первого собеседника — например «Игорь, бэкендер».
          </div>
        ) : roles.map(role => (
          <div
            key={role.id}
            onClick={() => startChat(role)}
            title="Начать чат с ролью"
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
              {iconBtn(e => { e.stopPropagation(); setEditTarget(role); }, 'Редактировать роль', false, (
                <>
                  <path d="M11 4H4a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-7" />
                  <path d="M18.5 2.5a2.121 2.121 0 0 1 3 3L12 15l-4 1 1-4 9.5-9.5z" />
                </>
              ))}
              {iconBtn(e => { e.stopPropagation(); setDeleteTarget(role); }, 'Удалить роль', true, (
                <>
                  <polyline points="3 6 5 6 21 6" />
                  <path d="M19 6l-1 14H6L5 6" />
                  <path d="M10 11v6M14 11v6" />
                  <path d="M9 6V4h6v2" />
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

      {deleteTarget && (
        <Modal
          title="Удалить роль?"
          width={MODAL_W.confirm}
          onClose={() => setDeleteTarget(null)}
          subtitle={
            <>
              Роль «<strong style={{ color: C.textPrimary, fontWeight: 600 }}>{deleteTarget.name}</strong>» будет удалена. Существующие чаты с ней останутся.
            </>
          }
          footer={
            <ModalActions
              confirmLabel="Удалить"
              confirmVariant="danger"
              onConfirm={handleDelete}
              onCancel={() => setDeleteTarget(null)}
            />
          }
        />
      )}
    </div>
  );
}
