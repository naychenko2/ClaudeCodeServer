import { useEffect, useState, useCallback } from 'react';
import type { Project, AuthState } from '../types';
import { api } from '../lib/api';
import { useOnline } from '../hooks/useOnline';
import { OfflineError } from '../lib/offline';
import { UserManagementModal } from '../components/UserManagementModal';
import { ChangePasswordDialog } from '../components/ChangePasswordDialog';
import { C, R, FONT } from '../lib/design';
import { AvatarMenu } from '../features/projects/AvatarMenu';
import { ProjectCard } from '../features/projects/ProjectCard';
import { CreateDialog } from '../features/projects/dialogs/CreateDialog';
import { AddExistingDialog } from '../features/projects/dialogs/AddExistingDialog';
import { EditDialog } from '../features/projects/dialogs/EditDialog';
import { DeleteDialog } from '../features/projects/dialogs/DeleteDialog';

type ActiveDialog =
  | { type: 'create' }
  | { type: 'addExisting' }
  | { type: 'edit'; project: Project }
  | { type: 'delete'; project: Project }
  | null;

interface Props {
  onOpen: (project: Project) => void;
  onLogout: () => void;
  auth?: AuthState | null;
}

export function ProjectListPage({ onOpen, onLogout, auth }: Props) {
  const online = useOnline();
  const [projects, setProjects] = useState<Project[]>([]);
  const [search, setSearch] = useState('');
  const [activeDialog, setActiveDialog] = useState<ActiveDialog>(null);
  const [loadState, setLoadState] = useState<'loading' | 'ok' | 'offline' | 'error'>('loading');
  const [retryKey, setRetryKey] = useState(0);
  const [copiedProjects, setCopiedProjects] = useState(false);
  const [showUserMgmt, setShowUserMgmt] = useState(false);
  const [showChangePassword, setShowChangePassword] = useState(false);

  const isAdmin = auth?.role === 'admin';
  const username = auth?.username ?? '';
  const serverUrl = localStorage.getItem('cc_server_url') ?? '';

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

  const closeDialog = () => setActiveDialog(null);

  return (
    <div style={{ height: '100dvh', background: C.bgMain, fontFamily: FONT.sans, display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
      <div style={{ maxWidth: 640, width: '100%', margin: '0 auto', display: 'flex', flexDirection: 'column', height: '100%', padding: '0 22px' }}>

        {/* Шапка + поиск — зафиксированы, не прокручиваются */}
        <div style={{ flexShrink: 0 }}>

        {/* Шапка */}
        <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: 12, marginBottom: 20, paddingTop: 20 }}>
          <h1 style={{
            fontFamily: FONT.serif, fontSize: 30, fontWeight: 500, margin: 0,
            letterSpacing: '-0.01em', color: C.textHeading, flexShrink: 0,
          }}>
            Проекты
          </h1>

          <div style={{ display: 'flex', alignItems: 'center', gap: 8, minWidth: 0, flexShrink: 1 }}>
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

            <AvatarMenu
              username={username}
              isAdmin={isAdmin}
              serverUrl={serverUrl}
              onLogout={onLogout}
              onShowChangePassword={() => setShowChangePassword(true)}
            />
          </div>
        </div>

        {/* Поиск */}
        <div style={{
          display: 'flex', alignItems: 'center', background: C.bgWhite,
          border: `1px solid ${C.border}`, borderRadius: R.xl, padding: '0 13px', height: 44, marginBottom: 10,
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

        {/* Кнопки создания */}
        {online && (
          <div style={{ display: 'flex', gap: 8, marginBottom: 12 }}>
            <button
              onClick={() => setActiveDialog({ type: 'create' })}
              style={{
                flex: 1, display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 7,
                background: C.accent, color: C.onAccent, border: 'none',
                borderRadius: R.xl, padding: '9px 16px',
                fontSize: 14, fontWeight: 600, fontFamily: 'inherit', cursor: 'pointer',
              }}
            >
              <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5">
                <line x1="12" y1="5" x2="12" y2="19"/>
                <line x1="5" y1="12" x2="19" y2="12"/>
              </svg>
              Новый проект
            </button>
            <button
              onClick={() => setActiveDialog({ type: 'addExisting' })}
              style={{
                flex: 1, display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 7,
                background: 'none', color: C.textSecondary, border: `1px solid ${C.border}`,
                borderRadius: R.xl, padding: '9px 16px',
                fontSize: 14, fontWeight: 500, fontFamily: 'inherit', cursor: 'pointer',
              }}
            >
              <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                <path d="M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z"/>
              </svg>
              Добавить существующий
            </button>
          </div>
        )}

        </div>{/* конец зафиксированной шапки */}

        {/* Прокручиваемая область */}
        <div style={{ flex: 1, overflowY: 'auto', paddingBottom: 14, paddingRight: 6 }}>

        <div style={{ display: 'flex', flexDirection: 'column', gap: 11 }}>
          {filtered.map((p, index) => (
            <ProjectCard
              key={p.id}
              project={p}
              index={index}
              online={online}
              onOpen={onOpen}
              onEdit={(p, e) => { e.stopPropagation(); setActiveDialog({ type: 'edit', project: p }); }}
              onDelete={p => setActiveDialog({ type: 'delete', project: p })}
            />
          ))}
        </div>

        {loadState === 'offline' && (
          <div style={{ textAlign: 'center', padding: '48px 0 0' }}>
            <div style={{ color: C.textMuted, fontSize: 14, marginBottom: 12 }}>
              Сервер недоступен — нет сохранённых данных для офлайн-доступа
            </div>
            <button onClick={() => setRetryKey(k => k + 1)} style={{ fontSize: 13, color: C.accent, background: 'none', border: 'none', cursor: 'pointer', textDecoration: 'underline', fontFamily: 'inherit' }}>
              Повторить
            </button>
          </div>
        )}
        {loadState === 'error' && (
          <div style={{ textAlign: 'center', padding: '48px 0 0' }}>
            <div style={{ color: C.textMuted, fontSize: 14, marginBottom: 12 }}>
              Ошибка загрузки проектов
            </div>
            <button onClick={() => setRetryKey(k => k + 1)} style={{ fontSize: 13, color: C.accent, background: 'none', border: 'none', cursor: 'pointer', textDecoration: 'underline', fontFamily: 'inherit' }}>
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

        </div>{/* конец прокручиваемой области */}
      </div>

      {activeDialog?.type === 'create' && (
        <CreateDialog
          onSuccess={p => { setProjects(prev => [...prev, p]); closeDialog(); }}
          onClose={closeDialog}
        />
      )}
      {activeDialog?.type === 'addExisting' && (
        <AddExistingDialog
          onSuccess={p => { setProjects(prev => [...prev, p]); closeDialog(); }}
          onClose={closeDialog}
        />
      )}
      {activeDialog?.type === 'edit' && (
        <EditDialog
          project={activeDialog.project}
          onSuccess={updated => { setProjects(prev => prev.map(p => p.id === updated.id ? updated : p)); closeDialog(); }}
          onClose={closeDialog}
        />
      )}
      {activeDialog?.type === 'delete' && (
        <DeleteDialog
          project={activeDialog.project}
          onSuccess={() => { setProjects(prev => prev.filter(p => p.id !== activeDialog.project.id)); closeDialog(); }}
          onClose={closeDialog}
        />
      )}

      {showUserMgmt && (
        <UserManagementModal currentUserId={auth?.id} onClose={() => setShowUserMgmt(false)} />
      )}
      {showChangePassword && (
        <ChangePasswordDialog onClose={() => setShowChangePassword(false)} />
      )}
    </div>
  );
}
