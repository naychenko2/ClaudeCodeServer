import { useState, useEffect } from 'react';
import type { UserProfile } from '../types';
import { api } from '../lib/api';
import { C, R, FONT, SHADOW, MODAL_W } from '../lib/design';
import { Modal, ModalActions, TextField, Button, SegmentedControl } from './ui';

interface Props {
  currentUserId?: string;
  onClose: () => void;
}

// Инициалы из username: первые две буквы или первые буквы двух слов
function initials(username: string): string {
  const parts = username.trim().split(/[\s_-]+/).filter(Boolean);
  if (parts.length >= 2) return (parts[0][0] + parts[1][0]).toUpperCase();
  return username.slice(0, 2).toUpperCase();
}

// Бейдж роли
function RoleBadge({ role }: { role: 'admin' | 'user' }) {
  const isAdmin = role === 'admin';
  return (
    <span style={{
      display: 'inline-block',
      padding: '2px 8px',
      borderRadius: R.sm,
      fontSize: 11,
      fontWeight: 600,
      letterSpacing: '0.04em',
      background: isAdmin ? C.accentLight : C.bgPanel,
      color: isAdmin ? C.accent : C.textSecondary,
    }}>
      {isAdmin ? 'Администратор' : 'Пользователь'}
    </span>
  );
}

// Аватар с инициалами
function Avatar({ username }: { username: string }) {
  return (
    <div style={{
      width: 28, height: 28, borderRadius: '50%',
      background: C.accentLight, color: C.accent,
      fontSize: 11, fontWeight: 700,
      display: 'flex', alignItems: 'center', justifyContent: 'center',
      flexShrink: 0, fontFamily: FONT.sans,
    }}>
      {initials(username)}
    </div>
  );
}

type Dialog =
  | { kind: 'add' }
  | { kind: 'delete'; user: UserProfile }
  | { kind: 'resetPassword'; user: UserProfile };

export function UserManagementModal({ currentUserId, onClose }: Props) {
  const [users, setUsers] = useState<UserProfile[]>([]);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState('');
  const [dialog, setDialog] = useState<Dialog | null>(null);

  useEffect(() => {
    api.users.list()
      .then(list => { setUsers(list); setLoading(false); })
      .catch(e => { setLoadError(e.message ?? 'Ошибка загрузки'); setLoading(false); });
  }, []);

  const removeUser = (id: string) => setUsers(prev => prev.filter(u => u.id !== id));
  const upsertUser = (u: UserProfile) => setUsers(prev =>
    prev.some(x => x.id === u.id) ? prev.map(x => x.id === u.id ? u : x) : [...prev, u]
  );

  return (
    <>
      <Modal
        title={
          <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
            <span>Пользователи</span>
            <Button
              size="sm"
              variant="primary"
              onClick={() => setDialog({ kind: 'add' })}
              leftIcon={
                <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5">
                  <line x1="12" y1="5" x2="12" y2="19"/><line x1="5" y1="12" x2="19" y2="12"/>
                </svg>
              }
            >
              Добавить
            </Button>
          </div>
        }
        width={620}
        onClose={onClose}
      >
        {loading && (
          <div style={{ color: C.textMuted, fontSize: 14, padding: '12px 0' }}>Загрузка…</div>
        )}
        {loadError && (
          <div style={{ color: C.danger, fontSize: 13 }}>{loadError}</div>
        )}
        {!loading && !loadError && (
          <div style={{
            border: `1px solid ${C.border}`, borderRadius: R.xl, overflow: 'hidden',
            boxShadow: SHADOW.card,
          }}>
            {users.map((user, i) => {
              const isSelf = user.id === currentUserId;
              return (
                <div
                  key={user.id}
                  style={{
                    display: 'flex', alignItems: 'center', gap: 12,
                    padding: '10px 16px',
                    borderBottom: i < users.length - 1 ? `1px solid ${C.borderLight}` : 'none',
                    background: C.bgWhite,
                  }}
                >
                  <Avatar username={user.username} />

                  <div style={{ flex: 1, minWidth: 0 }}>
                    <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                      <span style={{ fontSize: 14, fontWeight: 600, color: C.textHeading }}>
                        {user.username}
                      </span>
                      {isSelf && (
                        <span style={{ fontSize: 11, color: C.textMuted }}>(вы)</span>
                      )}
                    </div>
                    <div style={{ fontSize: 11.5, color: C.textMuted, marginTop: 2 }}>
                      {new Date(user.createdAt).toLocaleDateString('ru-RU', { day: 'numeric', month: 'short', year: 'numeric' })}
                    </div>
                  </div>

                  <RoleBadge role={user.role} />

                  <div style={{ display: 'flex', gap: 6, flexShrink: 0 }}>
                    <button
                      disabled={isSelf}
                      onClick={() => setDialog({ kind: 'resetPassword', user })}
                      title="Сбросить пароль"
                      style={actionBtn(isSelf)}
                    >
                      <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                        <rect x="3" y="11" width="18" height="11" rx="2" ry="2"/>
                        <path d="M7 11V7a5 5 0 0 1 10 0v4"/>
                      </svg>
                      Сброс пароля
                    </button>
                    <button
                      disabled={isSelf}
                      onClick={() => setDialog({ kind: 'delete', user })}
                      title="Удалить пользователя"
                      style={actionBtn(isSelf, true)}
                    >
                      <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                        <polyline points="3 6 5 6 21 6"/>
                        <path d="M19 6l-1 14a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2L5 6"/>
                        <path d="M10 11v6"/><path d="M14 11v6"/>
                        <path d="M9 6V4a1 1 0 0 1 1-1h4a1 1 0 0 1 1 1v2"/>
                      </svg>
                    </button>
                  </div>
                </div>
              );
            })}
            {users.length === 0 && (
              <div style={{ padding: '24px', textAlign: 'center', color: C.textMuted, fontSize: 14 }}>
                Нет пользователей
              </div>
            )}
          </div>
        )}
      </Modal>

      {dialog?.kind === 'add' && (
        <AddUserDialog
          onClose={() => setDialog(null)}
          onCreated={u => { upsertUser(u); setDialog(null); }}
        />
      )}
      {dialog?.kind === 'delete' && (
        <DeleteUserDialog
          user={dialog.user}
          onClose={() => setDialog(null)}
          onDeleted={() => { removeUser(dialog.user.id); setDialog(null); }}
        />
      )}
      {dialog?.kind === 'resetPassword' && (
        <ResetPasswordDialog
          user={dialog.user}
          onClose={() => setDialog(null)}
        />
      )}
    </>
  );
}

// --- Диалог добавления пользователя ---

function AddUserDialog({ onClose, onCreated }: {
  onClose: () => void;
  onCreated: (u: UserProfile) => void;
}) {
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [role, setRole] = useState<'user' | 'admin'>('user');
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(false);

  const handleCreate = async () => {
    if (!username.trim()) { setError('Введите имя пользователя'); return; }
    if (password.length < 8) { setError('Пароль — не менее 8 символов'); return; }
    setError('');
    setLoading(true);
    try {
      const user = await api.users.create({ username: username.trim(), password, role });
      onCreated(user);
    } catch (e: any) {
      setError(e.message ?? 'Ошибка создания');
      setLoading(false);
    }
  };

  return (
    <Modal
      title="Добавить пользователя"
      width={MODAL_W.form}
      onClose={onClose}
      footer={
        <ModalActions
          confirmLabel="Создать"
          onConfirm={handleCreate}
          onCancel={onClose}
          loading={loading}
        />
      }
    >
      {error && <div style={{ color: C.danger, fontSize: 13 }}>{error}</div>}
      <TextField value={username} onChange={setUsername} placeholder="Имя пользователя" autoFocus />
      <TextField type="password" value={password} onChange={setPassword} placeholder="Пароль (не менее 8 символов)" />
      <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
        <span style={{ fontSize: 12, fontWeight: 600, color: C.textSecondary, textTransform: 'uppercase', letterSpacing: '0.05em' }}>
          Роль
        </span>
        <SegmentedControl
          value={role}
          options={[
            { value: 'user', label: 'Пользователь' },
            { value: 'admin', label: 'Администратор' },
          ]}
          onChange={v => setRole(v as 'user' | 'admin')}
        />
      </div>
    </Modal>
  );
}

// --- Диалог удаления пользователя ---

function DeleteUserDialog({ user, onClose, onDeleted }: {
  user: UserProfile;
  onClose: () => void;
  onDeleted: () => void;
}) {
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');

  const handleDelete = async () => {
    setLoading(true);
    try {
      await api.users.delete(user.id);
      onDeleted();
    } catch (e: any) {
      setError(e.message ?? 'Ошибка удаления');
      setLoading(false);
    }
  };

  return (
    <Modal
      title="Удалить пользователя?"
      width={MODAL_W.confirm}
      onClose={onClose}
      subtitle={
        <>
          Пользователь «<strong style={{ color: C.textPrimary, fontWeight: 600 }}>{user.username}</strong>» будет удалён без возможности восстановления.
          {error && <div style={{ color: C.danger, marginTop: 8 }}>{error}</div>}
        </>
      }
      footer={
        <ModalActions
          confirmLabel="Удалить"
          confirmVariant="danger"
          onConfirm={handleDelete}
          onCancel={onClose}
          loading={loading}
        />
      }
    />
  );
}

// --- Диалог сброса пароля ---

function ResetPasswordDialog({ user, onClose }: {
  user: UserProfile;
  onClose: () => void;
}) {
  const [newPassword, setNewPassword] = useState('');
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(false);

  const handleReset = async () => {
    if (newPassword.length < 8) { setError('Пароль — не менее 8 символов'); return; }
    setError('');
    setLoading(true);
    try {
      await api.users.resetPassword(user.id, newPassword);
      onClose();
    } catch (e: any) {
      setError(e.message ?? 'Ошибка сброса пароля');
      setLoading(false);
    }
  };

  return (
    <Modal
      title={`Сброс пароля: ${user.username}`}
      width={MODAL_W.form}
      onClose={onClose}
      footer={
        <ModalActions
          confirmLabel="Установить пароль"
          onConfirm={handleReset}
          onCancel={onClose}
          loading={loading}
        />
      }
    >
      {error && <div style={{ color: C.danger, fontSize: 13 }}>{error}</div>}
      <TextField
        type="password"
        value={newPassword}
        onChange={setNewPassword}
        placeholder="Новый пароль (не менее 8 символов)"
        autoFocus
        onEnter={handleReset}
      />
    </Modal>
  );
}

// Стиль кнопки действия в строке таблицы
function actionBtn(disabled: boolean, danger = false): React.CSSProperties {
  return {
    display: 'inline-flex', alignItems: 'center', gap: 5,
    padding: '4px 9px', borderRadius: R.md, border: `1px solid ${C.border}`,
    background: 'none', cursor: disabled ? 'not-allowed' : 'pointer',
    fontSize: 12, fontWeight: 500, fontFamily: 'inherit',
    color: disabled ? C.textMuted : danger ? C.danger : C.textSecondary,
    opacity: disabled ? 0.45 : 1,
    transition: 'color 0.12s, border-color 0.12s',
  };
}
