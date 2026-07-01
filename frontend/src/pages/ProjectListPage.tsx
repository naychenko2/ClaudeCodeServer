import { useEffect, useState } from 'react';
import type { Project, Session, AuthState } from '../types';
import { api } from '../lib/api';
import { useOnline } from '../hooks/useOnline';
import { OfflineError } from '../lib/offline';
import { C, R, FONT } from '../lib/design';
import type { HubTab } from '../components/HubTabs';
import { HubHeader } from '../components/HubHeader';
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
  onHubTab: (t: HubTab) => void;
}

export function ProjectListPage({ onOpen, onLogout, auth, onHubTab }: Props) {
  const online = useOnline();
  const [projects, setProjects] = useState<Project[]>([]);
  const [activeSessions, setActiveSessions] = useState<Set<string>>(new Set());
  const [search, setSearch] = useState('');
  const [activeDialog, setActiveDialog] = useState<ActiveDialog>(null);
  const [loadState, setLoadState] = useState<'loading' | 'ok' | 'offline' | 'error'>('loading');
  const [retryKey, setRetryKey] = useState(0);

  useEffect(() => {
    setLoadState('loading');
    api.projects.list()
      .then(async list => {
        setProjects(list);
        setLoadState('ok');
        // Параллельно проверяем активные сессии
        const ACTIVE = new Set(['starting', 'working', 'active', 'waiting']);
        const results = await Promise.allSettled(list.map(p => api.sessions.list(p.id)));
        const ids = new Set<string>();
        results.forEach((r, i) => {
          if (r.status === 'fulfilled' && (r.value as Session[]).some((s: Session) => ACTIVE.has(s.status))) {
            ids.add(list[i].id);
          }
        });
        setActiveSessions(ids);
      })
      .catch(e => setLoadState(e instanceof OfflineError ? 'offline' : 'error'));
  // При возврате в онлайн или ручном retry — перезагружаем список
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [online, retryKey]);

  const filtered = projects.filter(p =>
    p.name.toLowerCase().includes(search.toLowerCase()) ||
    p.rootPath.toLowerCase().includes(search.toLowerCase())
  );

  // Сортировка: активные сверху, внутри каждой группы — по дате обновления
  const activeProjects = filtered.filter(p => activeSessions.has(p.id))
    .sort((a, b) => new Date(b.updatedAt).getTime() - new Date(a.updatedAt).getTime());
  const otherProjects = filtered.filter(p => !activeSessions.has(p.id))
    .sort((a, b) => new Date(b.updatedAt).getTime() - new Date(a.updatedAt).getTime());

  const closeDialog = () => setActiveDialog(null);

  return (
    <div style={{ height: '100dvh', background: C.bgMain, fontFamily: FONT.sans, display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
      <HubHeader value="projects" onTab={onHubTab} auth={auth!} onLogout={onLogout} />
      <div style={{ maxWidth: 640, width: '100%', margin: '0 auto', display: 'flex', flexDirection: 'column', flex: 1, minHeight: 0, padding: '0 22px' }}>

        {/* Поиск + кнопки — зафиксированы, не прокручиваются */}
        <div style={{ flexShrink: 0, paddingTop: 18 }}>

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
          {/* Секция: активные проекты */}
          {activeProjects.length > 0 && (
            <>
              <div style={{ display: 'flex', alignItems: 'center', gap: 8, padding: '4px 2px 2px' }}>
                <span className="pc-pulse" style={{ width: 7, height: 7, borderRadius: '50%', background: C.accent, flexShrink: 0 }} />
                <style>{`@keyframes pc-pulse{0%,100%{opacity:1;transform:scale(1)}50%{opacity:0.5;transform:scale(1.15)}} .pc-pulse{animation:pc-pulse 1.5s ease-in-out infinite}`}</style>
                <span style={{ fontSize: 11, fontWeight: 700, color: C.accent, letterSpacing: '0.06em', textTransform: 'uppercase', fontFamily: FONT.sans }}>Активные</span>
              </div>
              {activeProjects.map((p, index) => (
                <ProjectCard
                  key={p.id}
                  project={p}
                  index={index}
                  online={online}
                  hasActiveSession
                  onOpen={onOpen}
                  onEdit={(p, e) => { e.stopPropagation(); setActiveDialog({ type: 'edit', project: p }); }}
                  onDelete={p => setActiveDialog({ type: 'delete', project: p })}
                />
              ))}
              {otherProjects.length > 0 && (
                <div style={{ display: 'flex', alignItems: 'center', gap: 8, padding: '8px 2px 2px' }}>
                  <div style={{ flex: 1, height: 1, background: C.border }} />
                  <span style={{ fontSize: 11, fontWeight: 600, color: C.textMuted, letterSpacing: '0.05em', textTransform: 'uppercase', fontFamily: FONT.sans, flexShrink: 0 }}>Остальные</span>
                  <div style={{ flex: 1, height: 1, background: C.border }} />
                </div>
              )}
            </>
          )}
          {/* Секция: остальные проекты */}
          {otherProjects.map((p, index) => (
            <ProjectCard
              key={p.id}
              project={p}
              index={index + activeProjects.length}
              online={online}
              hasActiveSession={false}
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

    </div>
  );
}
