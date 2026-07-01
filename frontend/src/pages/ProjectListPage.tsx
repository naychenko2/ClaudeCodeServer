import { useEffect, useState } from 'react';
import type { Project, ProjectGroup, Session, AuthState } from '../types';
import { api } from '../lib/api';
import { useOnline } from '../hooks/useOnline';
import { OfflineError } from '../lib/offline';
import { C, R, FONT } from '../lib/design';
import type { HubTab } from '../components/HubTabs';
import { HubHeader } from '../components/HubHeader';
import { ProjectCard } from '../features/projects/ProjectCard';
import { GroupHeader } from '../features/projects/GroupHeader';
import { CreateDialog } from '../features/projects/dialogs/CreateDialog';
import { AddExistingDialog } from '../features/projects/dialogs/AddExistingDialog';
import { EditDialog } from '../features/projects/dialogs/EditDialog';
import { DeleteDialog } from '../features/projects/dialogs/DeleteDialog';
import { MoveToGroupDialog } from '../features/projects/dialogs/MoveToGroupDialog';
import { GroupManagerDialog } from '../features/projects/dialogs/GroupManagerDialog';

type ActiveDialog =
  | { type: 'create' }
  | { type: 'addExisting' }
  | { type: 'edit'; project: Project }
  | { type: 'delete'; project: Project }
  | { type: 'move'; project: Project }
  | { type: 'groups' }
  | null;

interface Props {
  onOpen: (project: Project) => void;
  onLogout: () => void;
  auth?: AuthState | null;
  onHubTab: (t: HubTab) => void;
}

const ACTIVE_STATUSES = new Set(['starting', 'working', 'active', 'waiting']);

export function ProjectListPage({ onOpen, onLogout, auth, onHubTab }: Props) {
  const online = useOnline();
  const [projects, setProjects] = useState<Project[]>([]);
  const [groups, setGroups] = useState<ProjectGroup[]>([]);
  const [activeSessions, setActiveSessions] = useState<Set<string>>(new Set());
  const [search, setSearch] = useState('');
  const [activeDialog, setActiveDialog] = useState<ActiveDialog>(null);
  const [loadState, setLoadState] = useState<'loading' | 'ok' | 'offline' | 'error'>('loading');
  const [retryKey, setRetryKey] = useState(0);

  useEffect(() => {
    setLoadState('loading');
    Promise.all([api.projects.list(), api.projectGroups.list().catch(() => [] as ProjectGroup[])])
      .then(async ([list, grps]) => {
        setProjects(list);
        setGroups(grps);
        setLoadState('ok');
        // Параллельно проверяем активные сессии
        const results = await Promise.allSettled(list.map(p => api.sessions.list(p.id)));
        const ids = new Set<string>();
        results.forEach((r, i) => {
          if (r.status === 'fulfilled' && (r.value as Session[]).some((s: Session) => ACTIVE_STATUSES.has(s.status))) {
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

  // Сортировка внутри блока: активные сверху, затем по дате обновления
  const sortBlock = (arr: Project[]) => [...arr].sort((a, b) => {
    const aa = activeSessions.has(a.id) ? 1 : 0;
    const bb = activeSessions.has(b.id) ? 1 : 0;
    if (aa !== bb) return bb - aa;
    return new Date(b.updatedAt).getTime() - new Date(a.updatedAt).getTime();
  });

  const orderedGroups = [...groups].sort((a, b) => a.order - b.order);
  // «Без группы» — нет groupId либо ссылка на удалённую группу
  const ungrouped = sortBlock(filtered.filter(p => !p.groupId || !groups.some(g => g.id === p.groupId)));
  const byGroup = orderedGroups.map(g => ({ group: g, items: sortBlock(filtered.filter(p => p.groupId === g.id)) }));

  // Сплошной индекс для ротации цвета плитки — по порядку отрисовки
  const colorIndex = new Map<string, number>();
  let ci = 0;
  ungrouped.forEach(p => colorIndex.set(p.id, ci++));
  byGroup.forEach(({ items }) => items.forEach(p => colorIndex.set(p.id, ci++)));

  const closeDialog = () => setActiveDialog(null);
  const upsertProject = (updated: Project) => setProjects(prev => prev.map(p => p.id === updated.id ? updated : p));

  const renderCard = (p: Project) => (
    <ProjectCard
      key={p.id}
      project={p}
      index={colorIndex.get(p.id) ?? 0}
      online={online}
      hasActiveSession={activeSessions.has(p.id)}
      onOpen={onOpen}
      onMove={pr => setActiveDialog({ type: 'move', project: pr })}
      onEdit={(pr, e) => { e.stopPropagation(); setActiveDialog({ type: 'edit', project: pr }); }}
      onDelete={pr => setActiveDialog({ type: 'delete', project: pr })}
    />
  );

  const hasAny = filtered.length > 0;

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

        {/* Кнопки создания + управление группами */}
        {online && (
          <div style={{ display: 'flex', gap: 8, marginBottom: 12, flexWrap: 'wrap' }}>
            <button
              onClick={() => setActiveDialog({ type: 'create' })}
              style={{
                flex: 1, minWidth: 130, display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 7,
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
                flex: 1, minWidth: 130, display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 7,
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
            <button
              onClick={() => setActiveDialog({ type: 'groups' })}
              title="Управление группами"
              style={{
                flexShrink: 0, display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 7,
                background: 'none', color: C.textSecondary, border: `1px solid ${C.border}`,
                borderRadius: R.xl, padding: '9px 14px',
                fontSize: 14, fontWeight: 500, fontFamily: 'inherit', cursor: 'pointer',
              }}
            >
              <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                <line x1="8" y1="6" x2="21" y2="6"/><line x1="8" y1="12" x2="21" y2="12"/><line x1="8" y1="18" x2="21" y2="18"/>
                <line x1="3" y1="6" x2="3.01" y2="6"/><line x1="3" y1="12" x2="3.01" y2="12"/><line x1="3" y1="18" x2="3.01" y2="18"/>
              </svg>
              Группы
            </button>
          </div>
        )}

        </div>{/* конец зафиксированной шапки */}

        {/* Прокручиваемая область */}
        <div style={{ flex: 1, overflowY: 'auto', paddingBottom: 14, paddingRight: 6 }}>

        <div style={{ display: 'flex', flexDirection: 'column', gap: 11 }}>
          {/* Проекты без группы — сверху, без заголовка */}
          {ungrouped.map(renderCard)}

          {/* Группы по порядку */}
          {byGroup.map(({ group, items }) => {
            // Пустые группы прячем при активном поиске, показываем при пустом
            if (items.length === 0 && search !== '') return null;
            return (
              <div key={group.id} style={{ display: 'flex', flexDirection: 'column', gap: 11 }}>
                <GroupHeader group={group} count={items.length} />
                {items.map(renderCard)}
                {items.length === 0 && (
                  <div style={{ fontSize: 12.5, color: C.textMuted, padding: '0 2px 2px' }}>
                    Пусто — переместите сюда проект через меню «⋯»
                  </div>
                )}
              </div>
            );
          })}
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
        {loadState === 'ok' && !hasAny && search === '' && (
          <div style={{ textAlign: 'center', padding: '48px 0 0', color: C.textMuted, fontSize: 14 }}>
            Нет проектов. Создайте первый выше.
          </div>
        )}
        {loadState === 'ok' && !hasAny && search !== '' && (
          <div style={{ textAlign: 'center', padding: '48px 0 0', color: C.textMuted, fontSize: 14 }}>
            Ничего не найдено по запросу «{search}»
          </div>
        )}


        </div>{/* конец прокручиваемой области */}
      </div>

      {activeDialog?.type === 'create' && (
        <CreateDialog
          groups={orderedGroups}
          onSuccess={p => { setProjects(prev => [...prev, p]); closeDialog(); }}
          onClose={closeDialog}
        />
      )}
      {activeDialog?.type === 'addExisting' && (
        <AddExistingDialog
          groups={orderedGroups}
          onSuccess={p => { setProjects(prev => [...prev, p]); closeDialog(); }}
          onClose={closeDialog}
        />
      )}
      {activeDialog?.type === 'edit' && (
        <EditDialog
          project={activeDialog.project}
          groups={orderedGroups}
          onSuccess={updated => { upsertProject(updated); closeDialog(); }}
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
      {activeDialog?.type === 'move' && (
        <MoveToGroupDialog
          project={activeDialog.project}
          groups={orderedGroups}
          onSuccess={updated => { upsertProject(updated); closeDialog(); }}
          onClose={closeDialog}
        />
      )}
      {activeDialog?.type === 'groups' && (
        <GroupManagerDialog
          groups={orderedGroups}
          onChange={setGroups}
          onClose={closeDialog}
        />
      )}

    </div>
  );
}
