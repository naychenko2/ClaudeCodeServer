import { useEffect, useState, useCallback, useRef } from 'react';
import type { Project, AuthState } from '../types';
import { api } from '../lib/api';
import { useOnline } from '../hooks/useOnline';
import { OfflineError } from '../lib/offline';
import { ProjectSyncToggle } from '../components/ProjectSyncToggle';
import { ConnectionStatus } from '../components/ConnectionStatus';
import { UserManagementModal } from '../components/UserManagementModal';
import { ChangePasswordDialog } from '../components/ChangePasswordDialog';
import { C, R, FONT, SHADOW, MODAL_W, Z } from '../lib/design';
import { Modal, ModalActions, TextField } from '../components/ui';

interface Props {
  onOpen: (project: Project) => void;
  onLogout: () => void;
  auth?: AuthState | null;
}

// Склонение: «1 чат», «3 чата», «5 чатов»
function sessionsLabel(n: number): string {
  const m10 = n % 10, m100 = n % 100;
  let w = 'чатов';
  if (m10 === 1 && m100 !== 11) w = 'чат';
  else if (m10 >= 2 && m10 <= 4 && (m100 < 10 || m100 >= 20)) w = 'чата';
  return `${n} ${w}`;
}

// Цветовые плитки для карточек проектов
const TILE_COLORS: [string, string][] = [
  ['#E7F0E8', '#3F7A4F'],
  ['#E6EEF5', '#3E7CA6'],
  ['#FBEBE0', '#C2693B'],
  ['#F2E6F0', '#8E4A82'],
];

export function ProjectListPage({ onOpen, onLogout, auth }: Props) {
  const online = useOnline();
  const [projects, setProjects] = useState<Project[]>([]);
  const [search, setSearch] = useState('');
  const [showCreateNew, setShowCreateNew] = useState(false);
  const [showAddExisting, setShowAddExisting] = useState(false);
  const [newName, setNewName] = useState('');
  const [newPath, setNewPath] = useState('');
  const [deleteTarget, setDeleteTarget] = useState<Project | null>(null);
  const [editTarget, setEditTarget] = useState<Project | null>(null);
  const [editName, setEditName] = useState('');
  const [editPath, setEditPath] = useState('');
  const [error, setError] = useState('');
  const [loadState, setLoadState] = useState<'loading' | 'ok' | 'offline' | 'error'>('loading');
  const [retryKey, setRetryKey] = useState(0);
  const [copiedProjects, setCopiedProjects] = useState(false);
  const [newSync, setNewSync] = useState(false);
  const [showUserMgmt, setShowUserMgmt] = useState(false);
  const [showChangePassword, setShowChangePassword] = useState(false);
  const [avatarDropdownOpen, setAvatarDropdownOpen] = useState(false);
  const avatarRef = useRef<HTMLDivElement>(null);

  const isAdmin = auth?.role === 'admin';
  const username = auth?.username ?? '';
  const serverUrl = localStorage.getItem('cc_server_url') ?? '';

  // Закрываем дропдаун при клике вне аватара
  useEffect(() => {
    if (!avatarDropdownOpen) return;
    const handler = (e: MouseEvent) => {
      if (avatarRef.current && !avatarRef.current.contains(e.target as Node)) {
        setAvatarDropdownOpen(false);
      }
    };
    document.addEventListener('mousedown', handler);
    return () => document.removeEventListener('mousedown', handler);
  }, [avatarDropdownOpen]);

  const handleCopyProjects = useCallback(() => {
    const url = `${window.location.origin}/projects/`;
    navigator.clipboard.writeText(url).then(() => {
      setCopiedProjects(true);
      setTimeout(() => setCopiedProjects(false), 1500);
    });
  }, []);

  useEffect(() => {
    setLoadState('loading');
    api.projects.list()
      .then(list => { setProjects(list); setLoadState('ok'); })
      .catch(e => setLoadState(e instanceof OfflineError ? 'offline' : 'error'));
  // При возврате в онлайн или ручном retry — перезагружаем список
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [online, retryKey]);

  const filtered = projects.filter(p =>
    p.name.toLowerCase().includes(search.toLowerCase()) ||
    p.rootPath.toLowerCase().includes(search.toLowerCase())
  );

  const resetNewForm = () => { setNewName(''); setNewPath(''); setError(''); setNewSync(false); };

  const handleCreateNew = async () => {
    setError('');
    try {
      const p = await api.projects.create(newName.trim(), null);
      setProjects(prev => [...prev, p]);
      if (newSync) api.sync.add(p.id, '', true).catch(() => {});
      setShowCreateNew(false);
      resetNewForm();
    } catch (e: any) {
      setError(e.message);
    }
  };

  const handleAddExisting = async () => {
    setError('');
    try {
      const p = await api.projects.create(newName.trim(), newPath.trim() || null);
      setProjects(prev => [...prev, p]);
      if (newSync) api.sync.add(p.id, '', true).catch(() => {});
      setShowAddExisting(false);
      resetNewForm();
    } catch (e: any) {
      setError(e.message);
    }
  };

  const handleDelete = async () => {
    if (!deleteTarget) return;
    // Кнопка удаления скрыта офлайн, но сеть могла упасть между показом и кликом —
    // защищаемся от unhandled rejection
    try {
      await api.projects.delete(deleteTarget.id);
      setProjects(prev => prev.filter(p => p.id !== deleteTarget.id));
    } catch { /* офлайн/сбой — проект остаётся, повторим онлайн */ }
    setDeleteTarget(null);
  };

  const openEdit = (p: Project, e: React.MouseEvent) => {
    e.stopPropagation();
    setEditTarget(p);
    setEditName(p.name);
    setEditPath(p.rootPath);
    setError('');
  };

  const handleEdit = async () => {
    if (!editTarget) return;
    setError('');
    try {
      const updated = await api.projects.update(editTarget.id, { name: editName.trim(), rootPath: editPath.trim() });
      setProjects(prev => prev.map(p => p.id === updated.id ? updated : p));
      setEditTarget(null);
    } catch (e: any) {
      setError(e.message);
    }
  };

  const errorLine = error ? <div style={{ color: C.danger, fontSize: 13 }}>{error}</div> : null;

  return (
    <div style={{ minHeight: '100vh', background: C.bgMain, fontFamily: FONT.sans, padding: '4px 22px 14px' }}>
      <div style={{ maxWidth: 640, margin: '0 auto' }}>

        {/* Шапка */}
        <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: 12, marginBottom: 20, paddingTop: 20 }}>
          <h1 style={{
            fontFamily: FONT.serif, fontSize: 30, fontWeight: 500, margin: 0,
            letterSpacing: '-0.01em', color: C.textHeading, flexShrink: 0,
          }}>
            Проекты
          </h1>

          <div style={{ display: 'flex', alignItems: 'center', gap: 8, minWidth: 0, flexShrink: 1 }}>
            {/* Кнопка управления пользователями — только для admin */}
            {isAdmin && (
              <button
                onClick={() => setShowUserMgmt(true)}
                title="Управление пользователями"
                style={{
                  width: 32, height: 32, borderRadius: R.md, border: `1px solid ${C.border}`,
                  background: 'none', cursor: 'pointer', display: 'flex', alignItems: 'center',
                  justifyContent: 'center', color: C.textMuted, flexShrink: 0,
                }}
              >
                <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8">
                  <circle cx="12" cy="12" r="3"/>
                  <path d="M12 1v3M12 20v3M4.22 4.22l2.12 2.12M17.66 17.66l2.12 2.12M1 12h3M20 12h3M4.22 19.78l2.12-2.12M17.66 6.34l2.12-2.12"/>
                </svg>
              </button>
            )}

            {/* Аватар с дропдауном */}
            <div ref={avatarRef} style={{ position: 'relative', flexShrink: 0 }}>
              <div
                onClick={() => setAvatarDropdownOpen(o => !o)}
                style={{
                  display: 'flex', alignItems: 'center', gap: 7, background: C.bgPanel,
                  borderRadius: 20, padding: '5px 11px 5px 7px', cursor: 'pointer',
                  minWidth: 0, maxWidth: 220, overflow: 'hidden',
                }}
              >
                <div style={{
                  width: 22, height: 22, borderRadius: '50%', background: C.accent,
                  color: C.onAccent, fontSize: 11, fontWeight: 700,
                  display: 'flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0,
                }}>
                  {username ? username.slice(0, 2).toUpperCase() : 'ME'}
                </div>
                <ConnectionStatus variant="badge" label={serverUrl || 'localhost'} />
              </div>

              {/* Дропдаун меню */}
              {avatarDropdownOpen && (
                <div style={{
                  position: 'absolute', top: 'calc(100% + 6px)', right: 0,
                  background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.xl,
                  boxShadow: SHADOW.dropdown, zIndex: Z.dropdown,
                  minWidth: 190, overflow: 'hidden', padding: '4px 0',
                }}>
                  {/* Имя пользователя */}
                  <div style={{
                    padding: '8px 14px 6px', fontSize: 12, color: C.textMuted,
                    borderBottom: `1px solid ${C.borderLight}`, marginBottom: 4,
                  }}>
                    <span style={{ fontWeight: 600, color: C.textHeading }}>{username}</span>
                    {isAdmin && (
                      <span style={{ marginLeft: 6, fontSize: 11, color: C.accent }}>admin</span>
                    )}
                  </div>
                  <button
                    onClick={() => { setAvatarDropdownOpen(false); setShowChangePassword(true); }}
                    style={dropdownItem}
                  >
                    <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8">
                      <rect x="3" y="11" width="18" height="11" rx="2" ry="2"/>
                      <path d="M7 11V7a5 5 0 0 1 10 0v4"/>
                    </svg>
                    Сменить пароль
                  </button>
                  <button
                    onClick={() => { setAvatarDropdownOpen(false); onLogout(); }}
                    style={{ ...dropdownItem, color: C.danger }}
                  >
                    <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8">
                      <path d="M9 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h4"/>
                      <polyline points="16 17 21 12 16 7"/>
                      <line x1="21" y1="12" x2="9" y2="12"/>
                    </svg>
                    Выйти
                  </button>
                </div>
              )}
            </div>
          </div>
        </div>

        {/* Поиск */}
        <div style={{
          display: 'flex', alignItems: 'center', background: C.bgWhite,
          border: `1px solid ${C.border}`, borderRadius: R.xl, padding: '0 13px', height: 44, marginBottom: 16,
        }}>
          <span style={{ color: C.textMuted, marginRight: 8, display: 'flex', alignItems: 'center' }}>
            <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
              <circle cx="11" cy="11" r="7"/>
              <path d="m21 21-4.3-4.3"/>
            </svg>
          </span>
          <input
            placeholder="Поиск проектов…"
            value={search}
            onChange={e => setSearch(e.target.value)}
            style={{ border: 'none', background: 'none', flex: 1, fontSize: 14.5, color: C.textHeading, fontFamily: 'inherit', outline: 'none' }}
          />
        </div>

        {/* Список проектов */}
        <div style={{ display: 'flex', flexDirection: 'column', gap: 11 }}>
          {filtered.map((p, index) => {
            const [tileBg, tileFg] = TILE_COLORS[index % TILE_COLORS.length];
            const letter = p.name.charAt(0).toUpperCase() || '?';
            return (
              <div
                key={p.id}
                onClick={() => onOpen(p)}
                style={{
                  display: 'flex', alignItems: 'center', gap: 14, background: C.bgWhite,
                  border: `1px solid ${C.borderLight}`, borderRadius: 16, padding: 14,
                  cursor: 'pointer', boxShadow: SHADOW.card,
                }}
              >
                {/* Цветная плитка с буквой */}
                <div style={{
                  width: 50, height: 50, borderRadius: R.xxl, background: tileBg, color: tileFg,
                  fontFamily: FONT.serif, fontSize: 22, fontWeight: 600,
                  display: 'flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0,
                }}>
                  {letter}
                </div>

                {/* Текстовая часть */}
                <div style={{ flex: 1, minWidth: 0 }}>
                  <div style={{ fontSize: 16, fontWeight: 600, color: C.textHeading, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
                    {p.name}
                  </div>
                  <div style={{ fontFamily: FONT.mono, fontSize: 11.5, color: C.textMuted, margin: '3px 0 6px', whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
                    {p.rootPath}
                  </div>
                  {/* Число чатов + дата (MA13) */}
                  <div style={{ fontSize: 12, color: C.textSecondary, display: 'flex', alignItems: 'center', gap: 7 }}>
                    <span>{sessionsLabel(p.sessionCount ?? 0)}</span>
                    <span style={{ color: C.border }}>·</span>
                    <span style={{ color: C.textMuted }}>
                      {new Date(p.updatedAt).toLocaleDateString('ru-RU', { day: 'numeric', month: 'short' })}
                    </span>
                  </div>
                </div>

                {/* Кнопки действий — только онлайн */}
                {online && (
                <div style={{ display: 'flex', gap: 4, flexShrink: 0 }} onClick={e => e.stopPropagation()}>
                  <button onClick={e => openEdit(p, e)} title="Редактировать" style={cardIconBtn}>
                    <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                      <path d="M11 4H4a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-7"/>
                      <path d="M18.5 2.5a2.121 2.121 0 0 1 3 3L12 15l-4 1 1-4 9.5-9.5z"/>
                    </svg>
                  </button>
                  <button onClick={e => { e.stopPropagation(); setDeleteTarget(p); }} title="Удалить" style={cardIconBtn}>
                    <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                      <polyline points="3 6 5 6 21 6"/>
                      <path d="M19 6l-1 14a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2L5 6"/>
                      <path d="M10 11v6"/>
                      <path d="M14 11v6"/>
                      <path d="M9 6V4a1 1 0 0 1 1-1h4a1 1 0 0 1 1 1v2"/>
                    </svg>
                  </button>
                </div>
                )}
              </div>
            );
          })}

          {/* Кнопки создания — только онлайн */}
          {online && (
          <div style={{ display: 'flex', gap: 10 }}>
            <div
              onClick={() => { resetNewForm(); setShowCreateNew(true); }}
              style={{
                flex: 1, border: `1.5px dashed ${C.dashed}`, borderRadius: 16, padding: 15,
                display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 8,
                color: C.accent, fontSize: 14, fontWeight: 600, cursor: 'pointer',
              }}
            >
              <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5">
                <line x1="12" y1="5" x2="12" y2="19"/>
                <line x1="5" y1="12" x2="19" y2="12"/>
              </svg>
              Создать новый проект
            </div>
            <div
              onClick={() => { resetNewForm(); setShowAddExisting(true); }}
              style={{
                flex: 1, border: `1.5px dashed ${C.dashed}`, borderRadius: 16, padding: 15,
                display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 8,
                color: C.textMuted, fontSize: 14, fontWeight: 500, cursor: 'pointer',
              }}
            >
              <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                <path d="M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z"/>
              </svg>
              Добавить существующий проект
            </div>
          </div>
          )}
        </div>

        {/* Empty state */}
        {loadState === 'offline' && (
          <div style={{ textAlign: 'center', padding: '48px 0 0' }}>
            <div style={{ color: C.textMuted, fontSize: 14, marginBottom: 12 }}>
              Сервер недоступен — нет сохранённых данных для офлайн-доступа
            </div>
            <button
              onClick={() => setRetryKey(k => k + 1)}
              style={{ fontSize: 13, color: C.accent, background: 'none', border: 'none', cursor: 'pointer', textDecoration: 'underline', fontFamily: 'inherit' }}
            >
              Повторить
            </button>
          </div>
        )}
        {loadState === 'error' && (
          <div style={{ textAlign: 'center', padding: '48px 0 0' }}>
            <div style={{ color: C.textMuted, fontSize: 14, marginBottom: 12 }}>
              Ошибка загрузки проектов
            </div>
            <button
              onClick={() => setRetryKey(k => k + 1)}
              style={{ fontSize: 13, color: C.accent, background: 'none', border: 'none', cursor: 'pointer', textDecoration: 'underline', fontFamily: 'inherit' }}
            >
              Повторить
            </button>
          </div>
        )}
        {loadState === 'ok' && filtered.length === 0 && search === '' && (
          <div style={{ textAlign: 'center', padding: '48px 0 0', color: C.textMuted, fontSize: 14 }}>
            Нет проектов. Создайте первый выше.
          </div>
        )}
        {loadState === 'ok' && filtered.length === 0 && search !== '' && (
          <div style={{ textAlign: 'center', padding: '48px 0 0', color: C.textMuted, fontSize: 14 }}>
            Ничего не найдено по запросу «{search}»
          </div>
        )}

        {/* Папка для локальной работы */}
        {online && (
          <div style={{ marginTop: 20, padding: '12px 14px', background: C.bgPanel, border: `1px solid ${C.border}`, borderRadius: R.xl }}>
            <div style={{ fontSize: 11, fontWeight: 700, color: C.textMuted, letterSpacing: '0.06em', marginBottom: 8 }}>
              ПАПКА ДЛЯ ЛОКАЛЬНОЙ РАБОТЫ
            </div>
            <div style={{ display: 'flex', alignItems: 'center', gap: 8, background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.lg, padding: '7px 10px' }}>
              <span style={{ flex: 1, fontFamily: FONT.mono, fontSize: 12, color: C.textPrimary, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                {`${window.location.origin}/projects/`}
              </span>
              <button
                onClick={handleCopyProjects}
                title="Скопировать URL"
                style={{ background: 'none', border: 'none', cursor: 'pointer', padding: 0, display: 'flex', alignItems: 'center', color: copiedProjects ? '#3F7A4F' : C.textMuted, flexShrink: 0 }}
              >
                {copiedProjects
                  ? <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round"><polyline points="20 6 9 17 4 12"/></svg>
                  : <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><rect x="9" y="9" width="13" height="13" rx="2"/><path d="M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1"/></svg>
                }
              </button>
            </div>
            <div style={{ marginTop: 7, fontSize: 11.5, color: C.textMuted, lineHeight: 1.5 }}>
              Подключите как сетевой диск — все проекты будут доступны как папки. Войти как <span style={{ fontFamily: FONT.mono }}>username:password</span>.
            </div>
          </div>
        )}
      </div>

      {/* Диалог «Создать новый проект» — путь генерируется автоматически */}
      {showCreateNew && (
        <Modal
          title="Создать новый проект"
          width={MODAL_W.form}
          onClose={() => { setShowCreateNew(false); resetNewForm(); }}
          footer={
            <ModalActions
              confirmLabel="Создать"
              onConfirm={handleCreateNew}
              onCancel={() => { setShowCreateNew(false); resetNewForm(); }}
            />
          }
        >
          {errorLine}
          <TextField value={newName} onChange={setNewName} placeholder="Название" autoFocus />
          <SyncToggleRow enabled={newSync} onChange={setNewSync} />
        </Modal>
      )}

      {/* Диалог «Добавить существующий проект» — пользователь указывает путь */}
      {showAddExisting && (
        <Modal
          title="Добавить существующий проект"
          width={MODAL_W.form}
          onClose={() => { setShowAddExisting(false); resetNewForm(); }}
          footer={
            <ModalActions
              confirmLabel="Добавить"
              onConfirm={handleAddExisting}
              onCancel={() => { setShowAddExisting(false); resetNewForm(); }}
            />
          }
        >
          {errorLine}
          <TextField value={newName} onChange={setNewName} placeholder="Название" autoFocus />
          <TextField value={newPath} onChange={setNewPath} placeholder="Путь к папке" mono />
          <SyncToggleRow enabled={newSync} onChange={setNewSync} />
        </Modal>
      )}

      {/* Диалог редактирования */}
      {editTarget && (
        <Modal
          title="Редактировать проект"
          width={MODAL_W.form}
          onClose={() => { setEditTarget(null); setError(''); }}
          footer={
            <ModalActions
              confirmLabel="Сохранить"
              onConfirm={handleEdit}
              onCancel={() => { setEditTarget(null); setError(''); }}
            />
          }
        >
          {errorLine}
          <TextField value={editName} onChange={setEditName} placeholder="Название" />
          <TextField value={editPath} onChange={setEditPath} placeholder="Путь к папке" mono />
          <ProjectSyncToggle projectId={editTarget.id} online={online} />
        </Modal>
      )}

      {/* Диалог удаления */}
      {deleteTarget && (
        <Modal
          title="Удалить проект?"
          width={MODAL_W.confirm}
          onClose={() => setDeleteTarget(null)}
          subtitle={
            <>
              Проект «<strong style={{ color: C.textPrimary, fontWeight: 600 }}>{deleteTarget.name}</strong>» будет удалён без возможности восстановления. Файлы на диске не затрагиваются.
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

      {/* Управление пользователями (только admin) */}
      {showUserMgmt && (
        <UserManagementModal currentUserId={auth?.id} onClose={() => setShowUserMgmt(false)} />
      )}

      {/* Смена пароля */}
      {showChangePassword && (
        <ChangePasswordDialog onClose={() => setShowChangePassword(false)} />
      )}
    </div>
  );
}

// Иконка-кнопка действия в карточке проекта
const cardIconBtn: React.CSSProperties = {
  width: 26, height: 26, borderRadius: R.sm, display: 'flex', alignItems: 'center', justifyContent: 'center',
  color: C.textMuted, background: 'none', border: 'none', cursor: 'pointer', flexShrink: 0,
};

// Пункт дропдаун-меню аватара
const dropdownItem: React.CSSProperties = {
  display: 'flex', alignItems: 'center', gap: 9,
  width: '100%', textAlign: 'left', padding: '8px 14px',
  background: 'none', border: 'none', cursor: 'pointer',
  fontSize: 13.5, fontWeight: 500, fontFamily: 'inherit',
  color: C.textPrimary,
};

function SyncToggleRow({ enabled, onChange }: { enabled: boolean; onChange: (v: boolean) => void }) {
  return (
    <div style={{
      display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 12,
      padding: '12px 14px', background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.xl,
    }}>
      <div>
        <div style={{ fontSize: 14, fontWeight: 600, color: C.textHeading }}>Синхронизировать весь проект</div>
        <div style={{ fontSize: 12, color: C.textMuted, marginTop: 2 }}>
          {enabled ? 'Файлы будут скачаны для офлайн-доступа' : 'Скачать все файлы проекта для офлайна'}
        </div>
      </div>
      <button
        onClick={() => onChange(!enabled)}
        style={{
          position: 'relative', width: 44, height: 26, borderRadius: 999, border: 'none',
          cursor: 'pointer', flexShrink: 0,
          background: enabled ? C.accent : C.track, transition: 'background 0.15s',
        }}
      >
        <span style={{ position: 'absolute', top: 3, left: enabled ? 21 : 3, width: 20, height: 20, borderRadius: '50%', background: C.bgWhite, transition: 'left 0.15s', boxShadow: SHADOW.thumb }} />
      </button>
    </div>
  );
}
