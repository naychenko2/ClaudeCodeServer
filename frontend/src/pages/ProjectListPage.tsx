import { useEffect, useRef, useState } from 'react';
import { ArrowDownAZ, Clock, List, Menu as MenuIcon, Plus, Search } from 'lucide-react';
import type { Project, ProjectGroup, Session, AuthState } from '../types';
import { api } from '../lib/api';
import { useOnline } from '../hooks/useOnline';
import { OfflineError } from '../lib/offline';
import { C, R, FONT, CHAT_MAX_W } from '../lib/design';
import { useSidebarDrag } from '../lib/sidebarWidth';
import { MOBILE_MAX } from '../lib/breakpoints';
import { Button, IconButton, IslandScaffold } from '../components/ui';
import { ICON_SIZE } from '../components/ui/icons';
import { PillSwitch } from '../components/Toolbar';
import type { HubTabValue } from '../components/HubTabs';
import { HubHeader } from '../components/HubHeader';
import { ProjectCard } from '../features/projects/ProjectCard';
import { ProjectRow } from '../features/projects/ProjectRow';
import { GroupHeader } from '../features/projects/GroupHeader';
import { ProjectSidebar } from '../features/projects/ProjectSidebar';
import type { ProjectView } from '../features/projects/ProjectSidebar';
import { AddProjectDialog } from '../features/projects/dialogs/AddProjectDialog';
import { EditDialog } from '../features/projects/dialogs/EditDialog';
import { DeleteDialog } from '../features/projects/dialogs/DeleteDialog';
import { MoveToGroupDialog } from '../features/projects/dialogs/MoveToGroupDialog';
import { GroupManagerDialog } from '../features/projects/dialogs/GroupManagerDialog';

type ActiveDialog =
  | { type: 'add' }
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
  onHubTab: (t: HubTabValue) => void;
}

const ACTIVE_STATUSES = new Set(['starting', 'working', 'active', 'waiting']);

// Двухпанельный лейаут включается на широких экранах (планшет/десктоп).
// Порог единый со всеми разделами (см. MOBILE_MAX): двухпанель с MOBILE_MAX+1,
// чтобы на раскладных экранах (Galaxy Fold в развёрнутом виде) сайдбар не пропадал.
function useWide(bp = MOBILE_MAX + 1) {
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

// Ширина элемента (для решения о числе колонок карточек)
function useMeasuredWidth<T extends HTMLElement>() {
  const ref = useRef<T>(null);
  const [width, setWidth] = useState(0);
  useEffect(() => {
    const el = ref.current;
    if (!el || typeof ResizeObserver === 'undefined') return;
    const ro = new ResizeObserver(entries => { for (const e of entries) setWidth(e.contentRect.width); });
    ro.observe(el);
    return () => ro.disconnect();
  }, []);
  return [ref, width] as const;
}

export function ProjectListPage({ onOpen, onLogout, auth, onHubTab }: Props) {
  const online = useOnline();
  const wide = useWide();
  const [listRef, listW] = useMeasuredWidth<HTMLDivElement>();
  const twoCol = listW >= 760;   // две колонки, когда панель достаточно широкая
  const [projects, setProjects] = useState<Project[]>([]);
  const [groups, setGroups] = useState<ProjectGroup[]>([]);
  const [activeSessions, setActiveSessions] = useState<Set<string>>(new Set());
  const [search, setSearch] = useState('');
  const [view, setView] = useState<ProjectView>('all');
  const [sortMode, setSortMode] = useState<SortMode>('activity');
  const [activeDialog, setActiveDialog] = useState<ActiveDialog>(null);
  const [loadState, setLoadState] = useState<'loading' | 'ok' | 'offline' | 'error'>('loading');
  const [retryKey, setRetryKey] = useState(0);

  // Сайдбар: общая ширина + режим (закреплён / свёрнут), как в чатах и воркспейсе.
  // Сворачивание — кнопкой на сплиттере, разворот — гамбургером обратно в поток.
  const { width: sidebarWidth, dragging: draggingSplitter, startDrag: handleSidebarSplitterMouseDown } = useSidebarDrag();
  const [sidebarMode, setSidebarMode] = useState<'pinned' | 'collapsed'>(() =>
    localStorage.getItem('cc_projects_sidebar_mode') === 'collapsed' ? 'collapsed' : 'pinned');
  useEffect(() => { localStorage.setItem('cc_projects_sidebar_mode', sidebarMode); }, [sidebarMode]);

  // Кнопка «Новый проект» в палитре проектов: переход сюда с флагом в sessionStorage —
  // открываем диалог создания сразу после монтирования
  useEffect(() => {
    if (sessionStorage.getItem('cc_pending_new_project')) {
      sessionStorage.removeItem('cc_pending_new_project');
      setActiveDialog({ type: 'add' });
    }
  }, []);

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
  const UNGROUPED_COLOR = C.textMuted;
  let sections: Section[] = [];
  let title = 'Проекты';
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
      {activeDialog?.type === 'add' && (
        <AddProjectDialog
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
          onIconUpdated={upsertProject}
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
  // Пустое состояние проектов: заголовок + подсказка + hero-CTA (единый стиль с чатами/файлами/логином)
  const projectsEmptyHero = () => (
    <div style={{ textAlign: 'center', padding: '44px 0 0', display: 'flex', flexDirection: 'column', alignItems: 'center', gap: 6 }}>
      <div style={{ fontFamily: FONT.serif, fontWeight: 700, fontSize: 21, color: C.textHeading }}>Пока нет проектов</div>
      <div style={{ fontSize: 14, color: C.textSecondary, lineHeight: 1.5, marginBottom: 8 }}>Добавьте первый проект, чтобы начать.</div>
      {online && (
        <Button variant="primary" size="md" glow onClick={() => setActiveDialog({ type: 'add' })}
          leftIcon={<Plus size={ICON_SIZE.sm} strokeWidth={2} />}>
          Добавить проект
        </Button>
      )}
    </div>
  );
  const retryBlock = (msg: string) => (
    <div style={{ textAlign: 'center', padding: '48px 0 0' }}>
      <div style={{ color: C.textMuted, fontSize: 14, marginBottom: 12 }}>{msg}</div>
      <button onClick={() => setRetryKey(k => k + 1)} style={{ fontSize: 13, color: C.accent, background: 'none', border: 'none', cursor: 'pointer', textDecoration: 'underline', fontFamily: 'inherit' }}>
        Повторить
      </button>
    </div>
  );

  // ===== Десктоп/планшет: две панели =====
  if (wide) {
    return (
      <div style={{ height: '100dvh', background: C.bgMain, fontFamily: FONT.sans, display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
        <HubHeader value="projects" onTab={onHubTab} auth={auth!} onLogout={onLogout} />
        <div style={{ flex: 1, minHeight: 0 }}>
          <IslandScaffold
            sidebarOpen={sidebarMode === 'pinned'}
            sidebar={
              <ProjectSidebar
                view={view}
                onSelect={setView}
                total={filtered.length}
                groups={byGroup.map(({ group, items }) => ({ group, count: items.length }))}
                sleepingCount={ungrouped.length}
                onManageGroups={() => setActiveDialog({ type: 'groups' })}
              />
            }
            sidebarWidth={sidebarWidth}
            sidebarDragging={draggingSplitter}
            onSidebarDrag={handleSidebarSplitterMouseDown}
            onSidebarCollapse={() => setSidebarMode('collapsed')}
            centerBare
            center={
          // Центр без острова, шириной как контент чата (CHAT_MAX_W по центру)
          <main style={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', background: C.bgMain, width: '100%', maxWidth: CHAT_MAX_W, margin: '0 auto' }}>
            {/* Шапка панели: заголовок + сортировка + Проект */}
            <div style={{ flexShrink: 0, padding: '20px 26px 14px', display: 'flex', alignItems: 'center', gap: 12 }}>
              {sidebarMode === 'collapsed' && (
                <IconButton onClick={() => setSidebarMode('pinned')} title="Показать панель" size="md" variant="soft" style={{ marginLeft: -4 }}>
                  <MenuIcon size={ICON_SIZE.sm} strokeWidth={2} />
                </IconButton>
              )}
              {/* Заголовок раздела — единый стиль с «Календарём» (serif 28 / 500) */}
              <div style={{ flex: 1, minWidth: 0, fontFamily: FONT.serif, fontSize: 28, fontWeight: 500, color: C.textHeading, letterSpacing: '-0.01em', whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
                {title}
              </div>
              {/* Сортировка — pill-переключатель с иконками, как виды в «Календаре» */}
              <PillSwitch<SortMode>
                value={sortMode}
                onChange={setSortMode}
                options={[
                  { value: 'activity', label: 'По активности', icon: <Clock size={ICON_SIZE.xs} strokeWidth={2} style={{ flexShrink: 0 }} /> },
                  { value: 'name', label: 'По названию', icon: <ArrowDownAZ size={ICON_SIZE.xs} strokeWidth={2} style={{ flexShrink: 0 }} /> },
                ]}
              />
              {online && (
                <Button
                  variant="primary" size="md" glow
                  onClick={() => setActiveDialog({ type: 'add' })}
                  leftIcon={<Plus size={ICON_SIZE.sm} strokeWidth={2} />}
                >
                  Проект
                </Button>
              )}
            </div>

            {/* Список секций */}
            <div ref={listRef} style={{ flex: 1, minHeight: 0, overflowY: 'auto', padding: '0 26px 18px' }}>
              {loadState === 'offline' && retryBlock('Сервер недоступен — нет сохранённых данных для офлайн-доступа')}
              {loadState === 'error' && retryBlock('Ошибка загрузки проектов')}
              {loadState === 'ok' && !hasAny && (search
                ? emptyBlock(`Ничего не найдено по запросу «${search}»`)
                : projectsEmptyHero())}

              {loadState === 'ok' && sections.map(sec => (
                <div key={sec.key} style={{ marginBottom: 20 }}>
                  <div style={{ display: 'flex', alignItems: 'center', gap: 10, marginBottom: 9 }}>
                    <span style={{ width: 5, height: 18, borderRadius: 2, background: sec.color || UNGROUPED_COLOR, flexShrink: 0 }} />
                    <span style={{ fontSize: 14, fontWeight: 700, color: C.textPrimary }}>{sec.name}</span>
                    <span style={{ fontSize: 11.5, color: C.textMuted }}>{sec.items.length}</span>
                    <div style={{ flex: 1, height: 1, background: C.divider }} />
                  </div>
                  <div style={twoCol
                    // minmax(0,1fr): длинный nowrap-путь в карточке не распирает колонки за экран
                    ? { display: 'grid', gridTemplateColumns: 'repeat(2, minmax(0, 1fr))', gap: 10 }
                    : { display: 'flex', flexDirection: 'column', gap: 8 }}>
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
            </div>
          </main>
            }
          />
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
              <Search size={ICON_SIZE.sm} strokeWidth={2} />
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
              <List size={ICON_SIZE.md} strokeWidth={2} />
            </button>
          )}
          {online && (
            <button
              onClick={() => setActiveDialog({ type: 'add' })}
              title="Добавить проект"
              style={{
                flexShrink: 0, width: 44, height: 44, display: 'flex', alignItems: 'center', justifyContent: 'center',
                background: C.accent, color: C.onAccent, border: 'none', borderRadius: R.xl, cursor: 'pointer',
              }}
            >
              <Plus size={ICON_SIZE.md} strokeWidth={2} />
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
          </div>

          {loadState === 'offline' && retryBlock('Сервер недоступен — нет сохранённых данных для офлайн-доступа')}
          {loadState === 'error' && retryBlock('Ошибка загрузки проектов')}
          {loadState === 'ok' && !hasAny && search === '' && projectsEmptyHero()}
          {loadState === 'ok' && !hasAny && search !== '' && emptyBlock(`Ничего не найдено по запросу «${search}»`)}
        </div>
      </div>

      {dialogs}
    </div>
  );
}

