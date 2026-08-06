// «Стена»: 2-5 чатов из РАЗНЫХ проектов рядом колонками — параллельное ведение
// нескольких сессий. Вход — из воркспейса (док стены; вкладки в таббаре нет).
//
// Обвязка — ШТАТНАЯ, как в воркспейсе: те же зоны панелей (PanelZone слева и справа)
// на ОБЩЕМ сторе раскладки (wsPanels — PanelZone берёт его по умолчанию), поэтому
// панель, перетащенная в воркспейсе в левую зону, и здесь окажется слева; работают
// тумблер режима зоны и «свернуть все». Под левой рельсой — те же доки: проекты и
// стена. Своих кнопок-чатов у рельсы нет: набором управляют панель «Чаты»
// (перетаскивание карточки на док / пункт «На стену»), перетаскивание самих колонок
// и крестик в ярлыке колонки.
//
// Центр — колонки чатов (WallColumn: полоса-ярлык + штатный чат). Результаты кликов
// из панелей приземляются в ОВЕРЛЕЙ поверх колонок (WallOverlay: файл, коммит,
// задача, персона, командный центр, граф, превью сервиса). Состав набора живёт на
// бэке (/api/me/wall, wallStore), фокус/оверлей/геометрия — эфемерные.
import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { Columns3, Plus } from 'lucide-react';
import type { AuthState, Project, Session } from '../types';
import { C, FONT, FS, ISLAND } from '../lib/design';
import { useWindowWidth, MOBILE_MAX } from '../lib/breakpoints';
import { setWallActive } from '../lib/wallMode';
import { api } from '../lib/api';
import { HubHeader } from '../components/HubHeader';
import { PageCanvas } from '../components/ui/PageCanvas';
import { Button } from '../components/ui';
import { ICON_SIZE } from '../components/ui/icons';
import type { HubTabValue } from '../components/HubTabs';
import type { PanelKey } from './workspace/panelCatalog';
import { FileViewer } from '../components/FileViewer';
import { GitCommitView } from '../components/GitCommitView';
import { TaskDetailsPane } from '../features/tasks/TaskDetailsPane';
import { EditDialog } from '../features/projects/dialogs/EditDialog';
import { PanelZone } from './workspace/PanelZone';
import { wsPanels, isZoneCollapsed } from './workspace/panelStackState';
import type { Zone } from './workspace/panelCatalog';
import { useSessionPanels } from './workspace/useSessionPanels';
import { SessionList } from '../components/SessionList';
import { ProjectRail } from '../features/projects/ProjectRail';
import { useWallState, getWallState, initWall, slotCount, addChatSafe, focusChat, updateProject } from '../features/wall/wallStore';
import { WallColumn } from '../features/wall/WallColumn';
import { WallPicker } from '../features/wall/WallPicker';
import { WallDock } from '../features/wall/WallDock';
import { WallOverlay } from '../features/wall/WallOverlay';
import { buildWallProjectPanels, WallPreviewOverlayBody, type WallOverlayTarget } from '../features/wall/useWallProjectPanels';
import { ProjectPersonaPane } from '../features/personas/ProjectPersonasPanel';
import { TeamCommandCenter } from '../features/personas/TeamCommandCenter';
import { CodeGraphDocument } from '../features/codegraph/CodeGraphDocument';

interface Props {
  auth: AuthState;
  onLogout: () => void;
  onHubTab: (t: HubTabValue) => void;
  // Выход со стены её собственной кнопкой — возврат в проект, из которого вошли
  // (гасит режим и восстанавливает «спящий» воркспейс; App.exitWall)
  onExitWall: () => void;
}

export function WallPage({ auth, onLogout, onHubTab, onExitWall }: Props) {
  const w = useWindowWidth();
  const { loaded, chats, projects, focusId } = useWallState();
  const [pickerOpen, setPickerOpen] = useState(false);
  // Оверлей-приземление кликов из панелей; относится к ФОКУСНОМУ проекту
  const [overlay, setOverlay] = useState<WallOverlayTarget | null>(null);
  // Проект, открытый в диалоге настроек (шестерёнка в хлебной крошке)
  const [editProject, setEditProject] = useState<Project | null>(null);
  // Общая с воркспейсом раскладка зон — нужна, чтобы прятать панели на входе
  const { zones, toggleCollapsed } = wsPanels.use();

  // ЕДИНЫЙ гейт деградации — в рендере, а не в resize-обработчике: покрывает и
  // сжатие окна на открытой стене, и старт по хешу #/wall на узком экране.
  // Планшету стена доступна: колонок туда влезает одна-две, и это уже работает —
  // отсекаем только телефон, где на колонку не остаётся места вовсе.
  // Панели ВСЕГДА всплывают поверх колонок (floating), в том числе на планшете:
  // компактный режим зон там ставил бы панель в поток и отжимал единственную
  // колонку — ровно то, ради чего плавающий режим и заводился
  const active = w > MOBILE_MAX;

  // Снимок и SignalR-группы — только когда стена реально работает
  useEffect(() => { if (active) initWall(auth.id ?? undefined); }, [auth.id, active]);

  // Пока режим открыт — помним его: возврат во вкладку «Проекты» из других разделов
  // приведёт обратно сюда (снимает флаг только явный выход «К проектам»)
  useEffect(() => { if (active) setWallActive(true); }, [active]);

  // Вход на стену прячет панели, выход — возвращает их как были. Раскладка общая с
  // воркспейсом, поэтому «прячем» = СВОРАЧИВАЕМ зону (состав уезжает в stash и
  // возвращается кнопкой рельсы), а не закрываем панели. Разворачиваем на выходе
  // только те зоны, которые свернули сами: если человек уже на стене раскрыл панель
  // руками, его выбор трогать нельзя.
  const collapsedByWall = useRef<Zone[]>([]);
  useEffect(() => {
    if (!active) return;
    const mine: Zone[] = [];
    for (const side of ['left', 'right'] as const) {
      if (!isZoneCollapsed(zones[side]) && zones[side].layout.flat().length > 0) {
        toggleCollapsed(side);
        mine.push(side);
      }
    }
    collapsedByWall.current = mine;
    return () => {
      for (const side of collapsedByWall.current) toggleCollapsed(side);
      collapsedByWall.current = [];
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps -- снимок раскладки нужен только на входе/выходе
  }, [active]);


  const slots = slotCount(w);
  const visible = chats.slice(0, slots);
  const focused: Session | null = visible.find(c => c.id === focusId) ?? visible[0] ?? null;
  const focusedProject = focused?.projectId ? projects.get(focused.projectId) : undefined;

  // Смена фокуса закрывает оверлей: контент чужого проекта поверх нового фокуса — враньё
  // eslint-disable-next-line react-hooks/set-state-in-effect -- закрытие оверлея при смене фокусного чата
  useEffect(() => { setOverlay(null); }, [focused?.id]);

  // Открыть чат колонкой по id (ссылки из задач/командного центра): резолв через
  // api.chats.get (отдаёт любой чат владельца), в наборе — фокус, нет — добавление
  const openChatOnWall = (sessionId: string) => {
    setOverlay(null);
    // Состав — из стора, не из замыкания: memo панелей пересобирается реже, чем
    // меняется набор, и по стейл-списку свежедобавленный чат «не находился» —
    // addChat дедупил его молча, не ставя фокус
    const inSet = getWallState().chats.some(c => c.id === sessionId);
    if (inSet) { focusChat(sessionId); return; }
    void api.chats.get(sessionId).then(s => addChatSafe(s)).catch(() => {});
  };

  // Панели фокусного проекта; пересобираются при смене фокуса
  const projectPanels = useMemo(
    () => buildWallProjectPanels(focusedProject, {
      openOverlay: setOverlay,
      openChatOnWall,
      selectedTaskId: overlay?.kind === 'task' ? overlay.task.id : null,
      graphOverlayOpen: overlay?.kind === 'graph',
    }),
    [focusedProject, overlay],
  );

  // Панели сессии фокусного чата — их зоны подмешивают сами (как в воркспейсе)
  const sessionPanels = useSessionPanels(focused, focusedProject?.id, focusedProject?.rootPath);

  // Панель «Чаты» — список чатов ФОКУСНОГО проекта, как в воркспейсе: оттуда
  // карточки перетаскиваются на док стены, а пункт меню «На стену» добавляет их
  // без перетаскивания. Своих кнопок-чатов у рельсы стены больше нет.
  const zonePanels: Partial<Record<PanelKey, ReactNode>> = useMemo(() => ({
    ...projectPanels,
    chats: focusedProject ? (
      <div style={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column', background: C.bgWhite }}>
        <SessionList
          project={focusedProject}
          activeSession={focused}
          onSelect={s => openChatOnWall(s.id)}
          isMobile={false}
          onAddToWall={s => { void addChatSafe(s); }}
        />
      </div>
    ) : undefined,
  }), [projectPanels, focusedProject, focused]);

  if (!active) {
    return (
      <PageCanvas>
        <HubHeader value="wall" onTab={onHubTab} auth={auth} onLogout={onLogout} />
        <div style={{ flex: 1, display: 'flex', alignItems: 'center', justifyContent: 'center', padding: 24 }}>
          <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', textAlign: 'center', maxWidth: 380, gap: 10 }}>
            <div style={{ width: 56, height: 56, borderRadius: 16, background: C.bgPanel, color: C.accent, display: 'flex', alignItems: 'center', justifyContent: 'center' }}>
              <Columns3 size={ICON_SIZE.xl} strokeWidth={2} />
            </div>
            <div style={{ fontFamily: FONT.serif, fontWeight: 500, fontSize: 20, color: C.textHeading }}>
              Стене нужен экран пошире
            </div>
            <div style={{ fontSize: FS.sm, color: C.textSecondary, lineHeight: 1.55 }}>
              На телефоне колонки чатов не помещаются. Откройте стену на планшете или компьютере — или вернитесь к проектам.
            </div>
            <Button variant="secondary" size="md" onClick={() => onHubTab('projects')} style={{ marginTop: 8 }}>
              К проектам
            </Button>
          </div>
        </div>
      </PageCanvas>
    );
  }

  // Zoom: проектный чат — канал cc_pending_session + cc-open-session (приёмник
  // WorkspacePage); внепроектный — раздел «Чаты» с активным чатом (cc_open_chat).
  const zoom = (s: Session) => {
    if (s.projectId) {
      const proj = projects.get(s.projectId);
      if (!proj) return;
      sessionStorage.setItem('cc_pending_session', JSON.stringify(s));
      window.dispatchEvent(new CustomEvent('cc-open-session', { detail: { project: proj } }));
    } else {
      localStorage.setItem('cc_open_chat', s.id);
      onHubTab('chats');
    }
  };

  return (
    <PageCanvas>
      {/* Хлебная крошка у логотипа — проект ФОКУСНОЙ колонки (на стене чаты разных
          проектов, и «где я» отвечает та колонка, в которой сейчас работают) вместе
          с кнопкой его настроек — тот же диалог, что в воркспейсе */}
      <HubHeader
        value="wall" onTab={onHubTab} auth={auth} onLogout={onLogout}
        project={focusedProject}
        onOpenProjectSettings={focusedProject ? () => setEditProject(focusedProject) : undefined}
      />

      {/* position: relative — якорь оверлея-лайтбокса (WallOverlay absolute inset) */}
      <div style={{ flex: 1, minHeight: 0, display: 'flex', position: 'relative', padding: `${ISLAND.gap}px 0 ${ISLAND.pad}px 0` }}>
        {/* Левая зона панелей — ШТАТНАЯ и с той же раскладкой, что у воркспейса:
            PanelZone без пропа panelStack берёт общий стор wsPanels, поэтому
            панель, перетащенная в воркспейсе влево, и здесь окажется слева.
            Под рельсой — те же два дока: проекты и стена. */}
        <PanelZone
          side="left"
          floating
          panels={zonePanels}
          sessionPanels={sessionPanels}
          railFooter={
            // flex: 1 — обёртка обязана забрать всю высоту под рельсой: по ней док
            // проектов считает, сколько иконок показать (иначе все уезжают под лупу)
            <div style={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column' }}>
              {/* Док проектов — ВСЕГДА, даже на пустой стене: с него и начинают
                  («открыть проект» и «собрать стену» — соседние действия). Активного
                  проекта без колонок нет, и подсвечивать в ряду просто нечего */}
              <ProjectRail
                project={focusedProject}
                onOpenSettings={() => { if (focusedProject) setEditProject(focusedProject); }}
              />
              <WallDock onExit={onExitWall} slots={slots} />
            </div>
          }
        />

        {/* Колонки чатов */}
        <div style={{ flex: 1, minWidth: 0, display: 'flex', gap: ISLAND.gap, margin: `0 ${ISLAND.centerGap}px` }}>
          {!loaded ? null : visible.length === 0 ? (
            <div style={{ flex: 1, display: 'flex', alignItems: 'center', justifyContent: 'center' }}>
              <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', textAlign: 'center', maxWidth: 400, gap: 10 }}>
                <div style={{ width: 56, height: 56, borderRadius: 16, background: C.bgPanel, color: C.accent, display: 'flex', alignItems: 'center', justifyContent: 'center' }}>
                  <Columns3 size={ICON_SIZE.xl} strokeWidth={2} />
                </div>
                <div style={{ fontFamily: FONT.serif, fontWeight: 500, fontSize: 22, color: C.textHeading }}>
                  Соберите свою стену
                </div>
                <div style={{ fontSize: 13.5, color: C.textSecondary, lineHeight: 1.55, maxWidth: 360 }}>
                  Добавьте чаты из разных проектов — они встанут рядом колонками, и вы сможете вести несколько разговоров параллельно.
                </div>
                <Button variant="primary" size="md" glow onClick={() => setPickerOpen(true)} style={{ marginTop: 10 }} leftIcon={<Plus size={ICON_SIZE.sm} strokeWidth={2} />}>
                  Добавить чат
                </Button>
              </div>
            </div>
          ) : (
            visible.map((s, i) => {
              const proj = s.projectId ? (projects.get(s.projectId) ?? null) : undefined;
              return (
                <WallColumn
                  key={s.id}
                  session={s}
                  project={proj}
                  index={i}
                  focused={focused?.id === s.id}
                  onZoom={() => zoom(s)}
                  // Клик по файлу в ленте → оверлей; только у колонок с проектом
                  // (FileViewer требует project; оверлей относится к фокусу, а клик
                  // в колонке фокусирует её capture-фазой раньше обработчика)
                  onOpenFile={proj ? path => setOverlay({ kind: 'file', path }) : undefined}
                />
              );
            })
          )}
        </div>

        {/* Правая зона — тот же общий стор раскладки (wsPanels), тоже всплывающая */}
        <PanelZone
          side="right"
          floating
          panels={zonePanels}
          sessionPanels={sessionPanels}
        />

        {/* Оверлей-приземление (файл/коммит/задача фокусного проекта) */}
        {overlay && focusedProject && (
          <WallOverlay onClose={() => setOverlay(null)}>
            {overlay.kind === 'file' && (
              <FileViewer
                project={focusedProject}
                filePath={overlay.path}
                onClose={() => setOverlay(null)}
                initialTab={overlay.diffMode ? 'diff' : undefined}
                gitStagePath={overlay.gitStagePath}
                fullscreen
              />
            )}
            {overlay.kind === 'commit' && (
              <GitCommitView project={focusedProject} sha={overlay.sha} onClose={() => setOverlay(null)} />
            )}
            {overlay.kind === 'task' && (
              <TaskDetailsPane
                key={overlay.task.id}
                task={overlay.task}
                project={focusedProject}
                onOpenSession={openChatOnWall}
                onOpenFile={path => setOverlay({ kind: 'file', path })}
                onClose={() => setOverlay(null)}
                onDeleted={() => setOverlay(null)}
              />
            )}
            {overlay.kind === 'persona' && (
              <ProjectPersonaPane
                project={focusedProject}
                personaId={overlay.creating ? null : overlay.personaId}
                creating={!!overlay.creating}
                // «Поговорить» с персоной со стены = её чат встаёт колонкой
                onOpenChat={(s: Session) => { setOverlay(null); void addChatSafe(s); }}
                onSelectPersona={(id: string) => setOverlay({ kind: 'persona', personaId: id })}
                onCleared={() => setOverlay(null)}
                onClose={() => setOverlay(null)}
              />
            )}
            {overlay.kind === 'teamCenter' && (
              <TeamCommandCenter
                project={focusedProject}
                onOpenPersona={id => setOverlay({ kind: 'persona', personaId: id })}
                onNewPersona={() => setOverlay({ kind: 'persona', personaId: null, creating: true })}
                onOpenSession={s => { setOverlay(null); void addChatSafe(s); }}
                onOpenSessionById={openChatOnWall}
                onClose={() => setOverlay(null)}
              />
            )}
            {overlay.kind === 'graph' && (
              <CodeGraphDocument
                projectId={focusedProject.id}
                isMobile={false}
                onClose={() => setOverlay(null)}
                onOpenFile={path => setOverlay({ kind: 'file', path })}
                onBuild={() => {}}
              />
            )}
            {overlay.kind === 'preview' && (
              <WallPreviewOverlayBody
                projectId={focusedProject.id}
                serviceId={overlay.serviceId}
                onClose={() => setOverlay(null)}
              />
            )}
          </WallOverlay>
        )}
      </div>

      {pickerOpen && <WallPicker onClose={() => setPickerOpen(false)} />}

      {/* Настройки проекта из крошки. Обновлённый проект кладём в стор стены —
          иконка и имя фокусной колонки должны обновиться на месте */}
      {editProject && (
        <EditDialog
          project={editProject}
          onSuccess={updated => { updateProject(updated); setEditProject(null); }}
          onIconUpdated={updateProject}
          onProjectUpdated={updateProject}
          onClose={() => setEditProject(null)}
        />
      )}
    </PageCanvas>
  );
}
