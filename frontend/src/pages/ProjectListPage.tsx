import { useEffect, useState } from 'react';
import type { ReactNode } from 'react';
import type { Project, ProjectGroup, Session, AuthState } from '../types';
import { api } from '../lib/api';
import { useOnline } from '../hooks/useOnline';
import { OfflineError } from '../lib/offline';
import { C, R, FONT, MODAL_W } from '../lib/design';
import { Modal } from '../components/ui';
import type { HubTab } from '../components/HubTabs';
import { HubHeader } from '../components/HubHeader';
import { ProjectCard } from '../features/projects/ProjectCard';
import { ProjectRow } from '../features/projects/ProjectRow';
import { GroupHeader } from '../features/projects/GroupHeader';
import { ProjectSidebar } from '../features/projects/ProjectSidebar';
import type { ProjectView } from '../features/projects/ProjectSidebar';
import { CreateDialog } from '../features/projects/dialogs/CreateDialog';
import { AddExistingDialog } from '../features/projects/dialogs/AddExistingDialog';
import { EditDialog } from '../features/projects/dialogs/EditDialog';
import { DeleteDialog } from '../features/projects/dialogs/DeleteDialog';
import { MoveToGroupDialog } from '../features/projects/dialogs/MoveToGroupDialog';
import { GroupManagerDialog } from '../features/projects/dialogs/GroupManagerDialog';

type ActiveDialog =
  | { type: 'addChoose' }
  | { type: 'create' }
  | { type: 'addExisting' }
  | { type: 'edit'; project: Project }
  | { type: 'delete'; project: Project }
  | { type: 'move'; project: Project }
  | { type: 'groups' }
  | null;

type SortMode = 'activity' | 'name';

interface Props {
  onOpen: (project: Project) => void;
  onLogout: () => void;
  auth?: AuthState | null;
  onHubTab: (t: HubTab) => void;
}

const ACTIVE_STATUSES = new Set(['starting', 'working', 'active', 'waiting']);

// Двухпанельный лейаут включается на широких экранах (планшет/десктоп)
function useWide(bp = 900) {
  const [wide, setWide] = useState(() =>
    typeof window !== 'undefined' ? window.matchMedia(`(min-width: ${bp}px)`).matches : true);
  useEffect(() => {
    const mq = window.matchMedia(`(min-width: ${bp}px)`);
    const h = (e: MediaQueryListEvent) => setWide(e.matches);
    setWide(mq.matches);
    if (mq.addEventListener) mq.addEventListener('change', h); else mq.addListener(h);
    return () => { if (mq.removeEventListener) mq.removeEventListener('change', h); else mq.removeListener(h); };
  }, [bp]);
  return wide;
}

export function ProjectListPage({ onOpen, onLogout, auth, onHubTab }: Props) {
  const online = useOnline();
  const wide = useWide();
  const [projects, setProjects] = useState<Project[]>([]);
  const [groups, setGroups] = useState<ProjectGroup[]>([]);
  const [activeSessions, setActiveSessions] = useState<Set<string>>(new Set());
  const [search, setSearch] = useState('');
  const [view, setView] = useState<ProjectView>('all');
  const [sortMode, setSortMode] = useState<SortMode>('activity');
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
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [online, retryKey]);

  const filtered = projects.filter(p =>
    p.name.toLowerCase().includes(search.toLowerCase()) ||
    p.rootPath.toLowerCase().includes(search.toLowerCase())
  );

  const sortBlock = (arr: Project[]) => [...arr].sort((a, b) => {
    if (sortMode === 'name') return a.name.localeCompare(b.name, 'ru');
    const aa = activeSessions.has(a.id) ? 1 : 0;
    const bb = activeSessions.has(b.id) ? 1 : 0;
    if (aa !== bb) return bb - aa;
    return new Date(b.updatedAt).getTime() - new Date(a.updatedAt).getTime();
  });

  const orderedGroups = [...groups].sort((a, b) => a.order - b.order);
  const ungrouped = sortBlock(filtered.filter(p => !p.groupId || !groups.some(g => g.id === p.groupId)));
  const byGroup = orderedGroups.map(g => ({ group: g, items: sortBlock(filtered.filter(p => p.groupId === g.id)) }));

  // Сплошной индекс для ротации цвета плитки
  const colorIndex = new Map<string, number>();
  let ci = 0;
  ungrouped.forEach(p => colorIndex.set(p.id, ci++));
  byGroup.forEach(({ items }) => items.forEach(p => colorIndex.set(p.id, ci++)));

  const closeDialog = () => setActiveDialog(null);
  const upsertProject = (updated: Project) => setProjects(prev => prev.map(p => p.id === updated.id ? updated : p));
  const idx = (p: Project) => colorIndex.get(p.id) ?? 0;
  const hasAny = filtered.length > 0;

  // Секции для десктопа в зависимости от выбранного пункта сайдбара
  type Section = { key: string; name: string; color?: string; items: Project[] };
  const UNGROUPED_COLOR = '#C4BBA9';
  let sections: Section[] = [];
  let title = 'Все проекты';
  if (view === 'all') {
    sections = byGroup.map(({ group, items }) => ({ key: group.id, name: group.name, color: group.color, items }));
    if (ungrouped.length) sections.push({ key: '__ungrouped', name: 'Без группы', color: UNGROUPED_COLOR, items: ungrouped });
  } else if (view === 'sleeping') {
    title = 'Без группы';
    sections = [{ key: '__ungrouped', name: 'Без группы', color: UNGROUPED_COLOR, items: ungrouped }];
  } else {
    const g = byGroup.find(x => x.group.id === view);
    title = g?.group.name ?? 'Проекты';
    sections = g ? [{ key: g.group.id, name: g.group.name, color: g.group.color, items: g.items }] : [];
  }

  // ===== Общие диалоги =====
  const dialogs = (
    <>
      {activeDialog?.type === 'addChoose' && (
        <Modal title="Добавить проект" width={MODAL_W.form} onClose={closeDialog} footer={null}>
          <div style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
            <AddChoice
              title="Новый проект"
              subtitle="Создать новую папку проекта"
              icon={<><line x1="12" y1="5" x2="12" y2="19"/><line x1="5" y1="12" x2="19" y2="12"/></>}
              onClick={() => setActiveDialog({ type: 'create' })}
            />
            <AddChoice
              title="Существующая папка"
              subtitle="Добавить проект по пути к папке"
              icon={<path d="M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z"/>}
              onClick={() => setActiveDialog({ type: 'addExisting' })}
            />
          </div>
        </Modal>
      )}
      {activeDialog?.type === 'create' && (
        <CreateDialog
          groups={orderedGroups}
          defaultGroupId={view !== 'all' && view !== 'sleeping' ? view : undefined}
          onSuccess={p => { setProjects(prev => [...prev, p]); closeDialog(); }}
          onClose={closeDialog}
        />
      )}
      {activeDialog?.type === 'addExisting' && (
        <AddExistingDialog
          groups={orderedGroups}
          defaultGroupId={view !== 'all' && view !== 'sleeping' ? view : undefined}
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
          onChange={g => { setGroups(g); if (view !== 'all' && view !== 'sleeping' && !g.some(x => x.id === view)) setView('all'); }}
          onClose={closeDialog}
        />
      )}
    </>
  );

  const emptyBlock = (msg: string) => (
    <div style={{ textAlign: 'center', padding: '48px 0 0', color: C.textMuted, fontSize: 14 }}>{msg}</div>
  );
  const retryBlock = (msg: string) => (
    <div style={{ textAlign: 'center', padding: '48px 0 0' }}>
      <div style={{ color: C.textMuted, fontSize: 14, marginBottom: 12 }}>{msg}</div>
      <button onClick={() => setRetryKey(k => k + 1)} style={{ fontSize: 13, color: C.accent, background: 'none', border: 'none', cursor: 'pointer', textDecoration: 'underline', fontFamily: 'inherit' }}>
        Повторить
      </button>
    </div>
  );

  const addButton = online && loadState === 'ok' && (
    <button
      onClick={() => setActiveDialog({ type: 'addChoose' })}
      style={{
        display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 8,
        border: `1.5px dashed ${C.dashed}`, borderRadius: 16, padding: 15, marginTop: 3,
        background: 'none', color: '#BE5536', fontSize: 14.5, fontWeight: 600,
        fontFamily: FONT.sans, cursor: 'pointer', width: '100%',
      }}
    >
      <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round">
        <path d="M12 5v14M5 12h14"/>
      </svg>
      Добавить проект
    </button>
  );

  // ===== Десктоп/планшет: две панели =====
  if (wide) {
    return (
      <div style={{ height: '100dvh', background: C.bgMain, fontFamily: FONT.sans, display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
        <HubHeader value="projects" onTab={onHubTab} auth={auth!} onLogout={onLogout} />
        <div style={{ flex: 1, minHeight: 0, display: 'flex' }}>
          <ProjectSidebar
            view={view}
            onSelect={setView}
            total={filtered.length}
            groups={byGroup.map(({ group, items }) => ({ group, count: items.length }))}
            sleepingCount={ungrouped.length}
          />
          <main style={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', background: C.bgMain }}>
            {/* Шапка панели: заголовок + сортировка + Проект */}
            <div style={{ flexShrink: 0, padding: '20px 26px 14px', display: 'flex', alignItems: 'center', gap: 12 }}>
              <div style={{ flex: 1, minWidth: 0, fontFamily: FONT.serif, fontSize: 24, color: C.textHeading, letterSpacing: '-0.01em', whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
                {title}
              </div>
              <button
                onClick={() => setSortMode(m => m === 'activity' ? 'name' : 'activity')}
                title="Сортировка"
                style={{
                  flexShrink: 0, display: 'inline-flex', alignItems: 'center', gap: 7,
                  fontSize: 12.5, color: '#5C5246', fontWeight: 600, fontFamily: FONT.sans,
                  background: C.bgPanel, border: `1px solid ${C.border}`, padding: '7px 12px', borderRadius: R.pill, cursor: 'pointer',
                }}
              >
                {sortMode === 'activity' ? 'По активности' : 'По названию'}
              </button>
              {online && (
                <button
                  onClick={() => setActiveDialog({ type: 'addChoose' })}
                  style={{
                    flexShrink: 0, display: 'inline-flex', alignItems: 'center', gap: 7, height: 38, padding: '0 15px',
                    borderRadius: R.lg, background: C.accent, color: C.onAccent, fontSize: 13.5, fontWeight: 600,
                    fontFamily: FONT.sans, border: 'none', cursor: 'pointer',
                  }}
                >
                  <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round">
                    <path d="M12 5v14M5 12h14"/>
                  </svg>
                  Проект
                </button>
              )}
            </div>

            {/* Список секций */}
            <div style={{ flex: 1, minHeight: 0, overflowY: 'auto', padding: '0 26px 18px' }}>
              {loadState === 'offline' && retryBlock('Сервер недоступен — нет сохранённых данных для офлайн-доступа')}
              {loadState === 'error' && retryBlock('Ошибка загрузки проектов')}
              {loadState === 'ok' && !hasAny && emptyBlock(search ? `Ничего не найдено по запросу «${search}»` : 'Нет проектов. Добавьте первый.')}

              {loadState === 'ok' && sections.map(sec => (
                <div key={sec.key} style={{ marginBottom: 20 }}>
                  <div style={{ display: 'flex', alignItems: 'center', gap: 10, marginBottom: 9 }}>
                    <span style={{ width: 5, height: 18, borderRadius: 2, background: sec.color || UNGROUPED_COLOR, flexShrink: 0 }} />
                    <span style={{ fontSize: 14, fontWeight: 700, color: C.textPrimary }}>{sec.name}</span>
                    <span style={{ fontSize: 11.5, color: '#9A8F7E' }}>{sec.items.length}</span>
                    <div style={{ flex: 1, height: 1, background: '#E4DDCE' }} />
                  </div>
                  <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
                    {sec.items.map(p => (
                      <ProjectRow
                        key={p.id}
                        project={p}
                        index={idx(p)}
                        online={online}
                        hasActiveSession={activeSessions.has(p.id)}
                        onOpen={onOpen}
                        onMove={pr => setActiveDialog({ type: 'move', project: pr })}
                        onEdit={(pr, e) => { e.stopPropagation(); setActiveDialog({ type: 'edit', project: pr }); }}
                        onDelete={pr => setActiveDialog({ type: 'delete', project: pr })}
                      />
                    ))}
                    {sec.items.length === 0 && (
                      <div style={{ fontSize: 12.5, color: C.textMuted, padding: '0 2px 2px' }}>
                        Пусто — переместите сюда проект через меню «⋯»
                      </div>
                    )}
                  </div>
                </div>
              ))}

              {loadState === 'ok' && (view === 'all' || view === 'sleeping') && (
                <div style={{ maxWidth: 720 }}>{addButton}</div>
              )}
            </div>
          </main>
        </div>
        {dialogs}
      </div>
    );
  }

  // ===== Мобильный: одна колонка =====
  return (
    <div style={{ height: '100dvh', background: C.bgMain, fontFamily: FONT.sans, display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
      <HubHeader value="projects" onTab={onHubTab} auth={auth!} onLogout={onLogout} />
      <div style={{ maxWidth: 640, width: '100%', margin: '0 auto', display: 'flex', flexDirection: 'column', flex: 1, minHeight: 0, padding: '0 22px' }}>

        {/* Поиск + управление группами */}
        <div style={{ flexShrink: 0, paddingTop: 18, paddingBottom: 10, display: 'flex', alignItems: 'center', gap: 8 }}>
          <div style={{
            flex: 1, minWidth: 0, display: 'flex', alignItems: 'center', background: C.bgWhite,
            border: `1px solid ${C.border}`, borderRadius: R.xl, padding: '0 13px', height: 44,
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
              style={{ border: 'none', background: 'none', flex: 1, minWidth: 0, fontSize: 14.5, color: C.textHeading, fontFamily: 'inherit', outline: 'none' }}
            />
          </div>
          {online && (
            <button
              onClick={() => setActiveDialog({ type: 'groups' })}
              title="Управление группами"
              style={{
                flexShrink: 0, width: 44, height: 44, display: 'flex', alignItems: 'center', justifyContent: 'center',
                background: C.bgWhite, color: C.textSecondary, border: `1px solid ${C.border}`, borderRadius: R.xl, cursor: 'pointer',
              }}
            >
              <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                <line x1="8" y1="6" x2="21" y2="6"/><line x1="8" y1="12" x2="21" y2="12"/><line x1="8" y1="18" x2="21" y2="18"/>
                <line x1="3" y1="6" x2="3.01" y2="6"/><line x1="3" y1="12" x2="3.01" y2="12"/><line x1="3" y1="18" x2="3.01" y2="18"/>
              </svg>
            </button>
          )}
        </div>

        {/* Прокручиваемая область */}
        <div style={{ flex: 1, overflowY: 'auto', paddingBottom: 14, paddingRight: 6 }}>
          <div style={{ display: 'flex', flexDirection: 'column', gap: 11 }}>
            {ungrouped.map(p => (
              <ProjectCard key={p.id} project={p} index={idx(p)} online={online} hasActiveSession={activeSessions.has(p.id)}
                onOpen={onOpen}
                onMove={pr => setActiveDialog({ type: 'move', project: pr })}
                onEdit={(pr, e) => { e.stopPropagation(); setActiveDialog({ type: 'edit', project: pr }); }}
                onDelete={pr => setActiveDialog({ type: 'delete', project: pr })} />
            ))}

            {byGroup.map(({ group, items }) => {
              if (items.length === 0 && search !== '') return null;
              return (
                <div key={group.id} style={{ display: 'flex', flexDirection: 'column', gap: 11 }}>
                  <GroupHeader group={group} count={items.length} />
                  {items.map(p => (
                    <ProjectCard key={p.id} project={p} index={idx(p)} online={online} hasActiveSession={activeSessions.has(p.id)}
                      onOpen={onOpen}
                      onMove={pr => setActiveDialog({ type: 'move', project: pr })}
                      onEdit={(pr, e) => { e.stopPropagation(); setActiveDialog({ type: 'edit', project: pr }); }}
                      onDelete={pr => setActiveDialog({ type: 'delete', project: pr })} />
                  ))}
                  {items.length === 0 && (
                    <div style={{ fontSize: 12.5, color: C.textMuted, padding: '0 2px 2px' }}>
                      Пусто — переместите сюда проект через меню «⋯»
                    </div>
                  )}
                </div>
              );
            })}

            {addButton}
          </div>

          {loadState === 'offline' && retryBlock('Сервер недоступен — нет сохранённых данных для офлайн-доступа')}
          {loadState === 'error' && retryBlock('Ошибка загрузки проектов')}
          {loadState === 'ok' && !hasAny && search === '' && (
            <div style={{ textAlign: 'center', padding: '40px 0 0' }}>
              <div style={{ fontFamily: FONT.serif, fontWeight: 700, fontSize: 21, color: C.textHeading, marginBottom: 6 }}>
                Пока нет проектов
              </div>
              <div style={{ fontSize: 14, color: C.textSecondary, lineHeight: 1.5 }}>
                Добавьте первый проект, чтобы начать.
              </div>
            </div>
          )}
          {loadState === 'ok' && !hasAny && search !== '' && emptyBlock(`Ничего не найдено по запросу «${search}»`)}
        </div>
      </div>

      {dialogs}
    </div>
  );
}

// Плитка выбора способа добавления проекта (в диалоге «Добавить проект»)
function AddChoice({ title, subtitle, icon, onClick }: { title: string; subtitle: string; icon: ReactNode; onClick: () => void }) {
  const [hover, setHover] = useState(false);
  return (
    <button
      onClick={onClick}
      onMouseEnter={() => setHover(true)}
      onMouseLeave={() => setHover(false)}
      style={{
        display: 'flex', alignItems: 'center', gap: 13, textAlign: 'left', width: '100%',
        background: hover ? C.bgSelected : C.bgWhite, border: `1px solid ${C.border}`,
        borderRadius: R.xl, padding: '13px 14px', cursor: 'pointer', fontFamily: FONT.sans,
      }}
    >
      <span style={{
        width: 38, height: 38, borderRadius: R.lg, flexShrink: 0, background: C.accentLight, color: C.accent,
        display: 'flex', alignItems: 'center', justifyContent: 'center',
      }}>
        <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
          {icon}
        </svg>
      </span>
      <span style={{ flex: 1, minWidth: 0 }}>
        <span style={{ display: 'block', fontSize: 15, fontWeight: 600, color: C.textHeading }}>{title}</span>
        <span style={{ display: 'block', fontSize: 12.5, color: C.textMuted, marginTop: 2 }}>{subtitle}</span>
      </span>
    </button>
  );
}
