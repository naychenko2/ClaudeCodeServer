import { useState, useEffect } from 'react';
import { Lock, Plus, Trash2 } from 'lucide-react';
import type { UserProfile } from '../types';
import { api } from '../lib/api';
import { C, R, FONT, SHADOW, MODAL_W } from '../lib/design';
import { Modal, ModalActions, TextField, Button, SegmentedControl } from './ui';
import { ICON_SIZE, ICON_STROKE } from './ui/icons';
import { useIsMobile } from '../lib/breakpoints';

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

// Бейдж среды исполнения: у каждого пользователя видно, где работают его процессы
function EnvBadge({ env }: { env?: 'local' | 'container' }) {
  const sandboxed = env === 'container';
  return (
    <span
      title={sandboxed
        ? 'Процессы пользователя (Claude, терминал, dev-серверы) работают изолированно в Docker-песочнице'
        : 'Процессы пользователя (Claude, терминал, dev-серверы) работают на машине сервера с полным доступом'}
      style={{
        display: 'inline-block',
        padding: '2px 8px',
        borderRadius: R.sm,
        fontSize: 11,
        fontWeight: 600,
        letterSpacing: '0.04em',
        background: C.bgPanel,
        color: C.textSecondary,
      }}
    >
      {sandboxed ? '📦 Песочница' : '🖥 Сервер'}
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
  const isMobile = useIsMobile();

  useEffect(() => {
    api.users.list()
      .then(list => { setUsers(list); setLoading(false); })
      .catch(e => { setLoadError(e.message ?? 'Ошибка загрузки'); setLoading(false); });
  }, []);

  const removeUser = (id: string) => setUsers(prev => prev.filter(u => u.id !== id));
  const upsertUser = (u: UserProfile) => setUsers(prev =>
    prev.some(x => x.id === u.id) ? prev.map(x => x.id === u.id ? u : x) : [...prev, u]
  );

  // Заголовок: на мобиле — column, кнопка под названием; на десктопе — row
  const titleNode = isMobile ? (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
      <span>Пользователи</span>
      <Button
        size="sm"
        variant="primary"
        onClick={() => setDialog({ kind: 'add' })}
        leftIcon={
          <Plus size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
        }
      >
        Добавить пользователя
      </Button>
    </div>
  ) : (
    <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
      <span>Пользователи</span>
      <Button
        size="sm"
        variant="primary"
        onClick={() => setDialog({ kind: 'add' })}
        leftIcon={
          <Plus size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
        }
      >
        Добавить
      </Button>
    </div>
  );

  return (
    <>
      <Modal
        title={titleNode}
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
              const isLast = i === users.length - 1;

              if (isMobile) {
                // Мобильный вид: карточка с вертикальным layout
                return (
                  <div
                    key={user.id}
                    style={{
                      display: 'flex', flexDirection: 'column', gap: 10,
                      padding: '12px 14px',
                      borderBottom: !isLast ? `1px solid ${C.borderLight}` : 'none',
                      background: C.bgWhite,
                    }}
                  >
                    {/* Верхняя строка: аватар + имя + бейдж роли */}
                    <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
                      <Avatar username={user.username} />
                      <div style={{ flex: 1, minWidth: 0 }}>
                        <div style={{ display: 'flex', alignItems: 'center', gap: 6, flexWrap: 'wrap' }}>
                          <span style={{
                            fontSize: 14, fontWeight: 600, color: C.textHeading,
                            overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
                          }}>
                            {user.username}
                          </span>
                          {isSelf && (
                            <span style={{ fontSize: 11, color: C.textMuted, flexShrink: 0 }}>(вы)</span>
                          )}
                        </div>
                        <div style={{ fontSize: 11.5, color: C.textMuted, marginTop: 2 }}>
                          {new Date(user.createdAt).toLocaleDateString('ru-RU', { day: 'numeric', month: 'short', year: 'numeric' })}
                        </div>
                      </div>
                      <div style={{ display: 'flex', flexDirection: 'column', gap: 4, alignItems: 'flex-end' }}>
                        <RoleBadge role={user.role} />
                        <EnvBadge env={user.executionEnvironment} />
                      </div>
                    </div>

                    {/* Нижняя строка: кнопки действий — растянутые */}
                    {!isSelf && (
                      <div style={{ display: 'flex', gap: 8 }}>
                        <button
                          onClick={() => setDialog({ kind: 'resetPassword', user })}
                          title="Сбросить пароль"
                          style={{ ...actionBtn(false), flex: 1, justifyContent: 'center' }}
                        >
                          <Lock size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
                          Сброс пароля
                        </button>
                        <button
                          onClick={() => setDialog({ kind: 'delete', user })}
                          title="Удалить пользователя"
                          style={{ ...actionBtn(false, true), flex: 1, justifyContent: 'center' }}
                        >
                          <Trash2 size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
                          Удалить
                        </button>
                      </div>
                    )}
                  </div>
                );
              }

              // Десктопный вид: горизонтальная строка
              return (
                <div
                  key={user.id}
                  style={{
                    display: 'flex', alignItems: 'center', gap: 12,
                    padding: '10px 16px',
                    borderBottom: !isLast ? `1px solid ${C.borderLight}` : 'none',
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

                  <EnvBadge env={user.executionEnvironment} />
                  <RoleBadge role={user.role} />

                  <div style={{ display: 'flex', gap: 6, flexShrink: 0 }}>
                    <button
                      disabled={isSelf}
                      onClick={() => setDialog({ kind: 'resetPassword', user })}
                      title="Сбросить пароль"
                      style={actionBtn(isSelf)}
                    >
                      <Lock size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
                      Сброс пароля
                    </button>
                    <button
                      disabled={isSelf}
                      onClick={() => setDialog({ kind: 'delete', user })}
                      title="Удалить пользователя"
                      style={actionBtn(isSelf, true)}
                    >
                      <Trash2 size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
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
  const [env, setEnv] = useState<'local' | 'container'>('local');
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(false);

  const handleCreate = async () => {
    if (!username.trim()) { setError('Введите имя пользователя'); return; }
    if (password.length < 8) { setError('Пароль — не менее 8 символов'); return; }
    setError('');
    setLoading(true);
    try {
      const user = await api.users.create({
        username: username.trim(), password, role, executionEnvironment: env,
      });
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
      <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
        <span style={{ fontSize: 12, fontWeight: 600, color: C.textSecondary, textTransform: 'uppercase', letterSpacing: '0.05em' }}>
          Среда исполнения
        </span>
        <SegmentedControl
          value={env}
          options={[
            { value: 'local', label: 'Сервер' },
            { value: 'container', label: '📦 Песочница' },
          ]}
          onChange={v => setEnv(v as 'local' | 'container')}
        />
        <span style={{ fontSize: 12, color: C.textMuted, lineHeight: 1.5 }}>
          {env === 'local'
            ? 'Claude, терминал и dev-серверы работают на машине сервера с полным доступом.'
            : 'Всё исполняется в изолированном Docker-контейнере: пользователь не видит файлы сервера. Сменить среду после появления чатов нельзя.'}
        </span>
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
