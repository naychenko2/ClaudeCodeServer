import { useState, useEffect } from 'react';
import { AlertTriangle, ChevronDown, ChevronRight, Lock, Plus, Trash2 } from 'lucide-react';
import type { UserProfile } from '../types';
import { api } from '../lib/api';
import { C, R, FONT, SHADOW, MODAL_W, SP, FS } from '../lib/design';
import { Modal, ModalActions, TextField, Button, Notice, SegmentedControl } from './ui';
import { ICON_SIZE, ICON_STROKE } from './ui/icons';
import { useIsMobile } from '../lib/breakpoints';
import { FLAGS, useFeature } from '../lib/featureFlags';
import { ChildProfileEditor } from '../features/childMode/ChildProfileEditor';
import { childApi } from '../features/childMode/api';
import type { ChildProfile } from '../features/childMode/types';

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
        ? 'Процессы пользователя (ИИ, терминал, dev-серверы) работают изолированно в Docker-песочнице'
        : 'Процессы пользователя (ИИ, терминал, dev-серверы) работают на машине сервера с полным доступом'}
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

// Состояние детского профиля одного пользователя в этой сессии модалки (Д1, дефект 06201244):
// снимок приходит только из GET (или из ответа сохранения здесь — это тот же ответ сервера).
// До снимка редактор не монтируется: с пустым черновиком он затёр бы живой профиль.
type ProfileState =
  | { status: 'loading' }
  | { status: 'error'; text: string }
  | { status: 'loaded'; profile: ChildProfile | null; tutorPersonaId: string };

export function UserManagementModal({ currentUserId, onClose }: Props) {
  const [users, setUsers] = useState<UserProfile[]>([]);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState('');
  const [dialog, setDialog] = useState<Dialog | null>(null);
  const isMobile = useIsMobile();
  // Флаг child-mode прячет только этот интерфейс включения (ADR-041): ограничения ребёнка
  // зависят только от его профиля, выключенный флаг не снимает ни одного запрета.
  const childModeUi = useFeature(FLAGS.childMode);
  // Живой профиль каждого пользователя по id (Д1): список пользователей childProfile не отдаёт,
  // он читается отдельным GET при раскрытии раздела. Ключ — user.id: ответ для прежнего
  // пользователя не попадает в чужое состояние (тот же приём, что сверка user.id в save).
  const [profileStates, setProfileStates] = useState<Record<string, ProfileState>>({});
  // Какие разделы раскрыты в этой сессии. По умолчанию свёрнуты: на десятке пользователей
  // десяток открытых редакторов утащил бы строки (R-KM-F12, минор 3, решение Софьи).
  const [childOpen, setChildOpen] = useState<Record<string, boolean>>({});

  // Загрузка живого профиля одного пользователя (UsersController.GetChildProfile). Пока снимок
  // не пришёл — редактора нет: его пустой черновик затёр бы живой профиль при сохранении
  // (дефект 06201244). Ответ и отказ пишутся только состоянию «жду» того же id: ответ для
  // другого пользователя в чужое состояние не попадает (тот же приём, что сверка user.id в save).
  const loadProfile = (userId: string) => {
    setProfileStates(prev => (prev[userId]?.status === 'loading' ? prev : { ...prev, [userId]: { status: 'loading' } }));
    childApi.getProfile(userId)
      .then((res) => setProfileStates(prev => (
        prev[userId]?.status === 'loading'
          ? { ...prev, [userId]: { status: 'loaded', profile: res.profile ?? null, tutorPersonaId: res.tutorPersonaId ?? '' } }
          : prev
      )))
      // Текст отказа — серверный (body.message, тот же путь, что у loadErrorText в
      // ChildProfileEditor.tsx:139): Notice уже говорит «Профиль ребёнка не загрузился»,
      // выдуманное тело эту строку дублировала бы (хвост 69c3ee96). Серверного текста нет — пустое.
      .catch((e) => setProfileStates(prev => (
        prev[userId]?.status === 'loading'
          ? { ...prev, [userId]: { status: 'error', text: (e as { body?: { message?: string } } | null)?.body?.message ?? '' } }
          : prev
      )));
  };

  // Раздел «Детский режим» — только пользователям песочницы: ограничения ребёнка работают
  // только в container, `local`-пользователям сервер отказывает not_container (REJECT_TEXT
  // в ChildProfileEditor). Свёрнут в строку статуса; редактор — только в раскрытом виде
  // и только по загруженному профилю.
  const childSection = (user: UserProfile) => {
    if (user.executionEnvironment !== 'container') return null;
    const open = childOpen[user.id] === true;
    const st = profileStates[user.id];
    // Строка статуса — три состояния (решение Софьи, хвост 69c3ee96): «включён»/«выключен»
    // утверждает только снимок; без снимка (ещё не запрашивался или в пути) — нейтральное
    // «…»: для реального ребёнка «выключен» до ответа GET было бы ложью; при отказе —
    // «не загрузилось» (причина и «Повторить» — в Notice после раскрытия).
    const enabled = st?.status === 'loaded' && st.profile?.enabled === true;
    const statusLine = st?.status === 'loaded' ? `Детский режим: ${enabled ? 'включён' : 'выключен'}`
      : st?.status === 'error' ? 'Детский режим: не загрузилось'
      : 'Детский режим…';
    return (
      <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm, ...(isMobile ? {} : { padding: '0 16px 12px' }) }}>
        <Button
          variant="ghost" size="md" fullWidth
          dataTestId="child-section-toggle"
          ariaExpanded={open}
          leftIcon={open
            ? <ChevronDown size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
            : <ChevronRight size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
          onClick={() => {
            const willOpen = !(childOpen[user.id] === true);
            setChildOpen(prev => ({ ...prev, [user.id]: willOpen }));
            // Раскрытие — запрос живого профиля (Д1): редактор без снимка не открывается.
            // Пока запрос в пути — второго нет; «error» при раскрытии не молчит — тоже попытка.
            if (willOpen && st?.status !== 'loading') loadProfile(user.id);
          }}
        >
          {statusLine}
        </Button>
        {open && st?.status === 'loading' && (
          <div data-testid="child-profile-loading" style={{ padding: SP.md, color: C.textMuted, fontSize: FS.sm }}>
            Загрузка…
          </div>
        )}
        {open && st?.status === 'error' && st && (
          <Notice
            tone="danger" icon={AlertTriangle} title="Профиль ребёнка не загрузился" data-testid="child-profile-error"
            action={<Button variant="secondary" size="md" dataTestId="child-profile-retry" onClick={() => loadProfile(user.id)}>Повторить</Button>}
          >
            {/* тело — только серверный текст (после catch выше он server message или пусто):
                заголовок Notice уже называет причину, повторять её нечем */}
            {st.text || null}
          </Notice>
        )}
        {open && st?.status === 'loaded' && st && (
          <ChildProfileEditor
            user={{ id: user.id, username: user.username, childProfile: st.profile, tutorPersonaId: st.tutorPersonaId }}
            // Ответ сохранения — живой профиль сервера: им и обновляем состояние (строка статуса
            // после сохранения обязана показать то, что сохранилось).
            onSaved={(p) => setProfileStates(prev => {
              const cur = prev[user.id];
              return cur?.status === 'loaded'
                ? { ...prev, [user.id]: { status: 'loaded', profile: p, tutorPersonaId: cur.tutorPersonaId } }
                : prev;
            })}
          />
        )}
      </div>
    );
  };

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
        // В titleNode справа primary-кнопка «Добавить» — встроенный крестик встал
        // бы правее неё и сломал порядок «главное действие у края». Вместо него
        // явный выход футером «Закрыть».
        hideCloseButton
        footer={
          <div style={{ display: 'flex', justifyContent: 'flex-end' }}>
            <Button variant="secondary" size="md" onClick={onClose}>
              Закрыть
            </Button>
          </div>
        }
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
                    data-testid={`user-row-${user.id}`}
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

                    {/* Раздел профиля ребёнка — после основных полей, под флагом (ADR-041) */}
                    {childModeUi && childSection(user)}
                  </div>
                );
              }

              // Десктопный вид: горизонтальная строка; раздел профиля — под строкой
              return (
                <div
                  key={user.id}
                  data-testid={`user-row-${user.id}`}
                  style={{
                    borderBottom: !isLast ? `1px solid ${C.borderLight}` : 'none',
                    background: C.bgWhite,
                  }}
                >
                  <div style={{ display: 'flex', alignItems: 'center', gap: 12, padding: '10px 16px' }}>
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
                  {childModeUi && childSection(user)}
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
    } catch (e: unknown) {
      setError(e instanceof Error ? e.message : 'Ошибка создания');
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
      <TextField value={username} onChange={setUsername} autoComplete="username" placeholder="Имя пользователя" autoFocus />
      <TextField type="password" value={password} onChange={setPassword} autoComplete="new-password" placeholder="Пароль (не менее 8 символов)" />
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
            ? 'AI, терминал и dev-серверы работают на машине сервера с полным доступом.'
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
    } catch (e: unknown) {
      setError(e instanceof Error ? e.message : 'Ошибка удаления');
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
    } catch (e: unknown) {
      setError(e instanceof Error ? e.message : 'Ошибка сброса пароля');
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
        autoComplete="new-password"
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
