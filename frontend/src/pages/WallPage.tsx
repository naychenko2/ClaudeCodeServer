// «Стена» (фича wall): 2-5 чатов из РАЗНЫХ проектов рядом колонками — параллельное
// ведение нескольких сессий. Вход — из воркспейса (док стены; вкладки в таббаре нет).
// Слева рельса набора (WallRail: выход, цифровые монеты, «+»), в центре колонки
// (WallColumn: полоса-ярлык + штатный чат), справа рельса панелей фокусного чата и
// его проекта (WallPanelRail: hover=peek, клик=закрепление). Результаты кликов из
// панелей приземляются в ОВЕРЛЕЙ поверх колонок (WallOverlay: файл, коммит, задача,
// персона, командный центр, граф, превью сервиса). Состав набора живёт на бэке
// (/api/me/wall, wallStore), фокус/оверлей/геометрия — эфемерные.
import { useEffect, useMemo, useState } from 'react';
import { LayoutGrid, Plus } from 'lucide-react';
import type { AuthState, Session } from '../types';
import { C, FONT, FS, ISLAND } from '../lib/design';
import { useWindowWidth, TABLET_MAX } from '../lib/breakpoints';
import { useFeature, FLAGS } from '../lib/featureFlags';
import { api } from '../lib/api';
import { HubHeader } from '../components/HubHeader';
import { CanvasBackdrop } from '../components/ui/CanvasBackdrop';
import { Button } from '../components/ui';
import { ICON_SIZE } from '../components/ui/icons';
import type { HubTabValue } from '../components/HubTabs';
import type { PanelKey } from './workspace/panelCatalog';
import { FileViewer } from '../components/FileViewer';
import { GitCommitView } from '../components/GitCommitView';
import { TaskDetailsPane } from '../features/tasks/TaskDetailsPane';
import { useWallState, getWallState, initWall, slotCount, addChatSafe, focusChat } from '../features/wall/wallStore';
import { WallRail } from '../features/wall/WallRail';
import { WallColumn } from '../features/wall/WallColumn';
import { WallPicker } from '../features/wall/WallPicker';
import { WallPanelRail } from '../features/wall/WallPanelRail';
import { WallOverlay } from '../features/wall/WallOverlay';
import { buildWallProjectPanels, WallPreviewOverlayBody, type WallOverlayTarget } from '../features/wall/useWallProjectPanels';
import { ProjectPersonaPane } from '../features/personas/ProjectPersonasPanel';
import { TeamCommandCenter } from '../features/personas/TeamCommandCenter';
import { CodeGraphDocument } from '../features/codegraph/CodeGraphDocument';

interface Props {
  auth: AuthState;
  onLogout: () => void;
  onHubTab: (t: HubTabValue) => void;
}

export function WallPage({ auth, onLogout, onHubTab }: Props) {
  const wallOn = useFeature(FLAGS.wall);
  const w = useWindowWidth();
  const { loaded, chats, projects, focusId } = useWallState();
  const [pickerOpen, setPickerOpen] = useState(false);
  // Закреплённая панель фокусного чата (рисует WallPanelRail)
  const [pinned, setPinned] = useState<PanelKey | null>(null);
  // Оверлей-приземление кликов из панелей; относится к ФОКУСНОМУ проекту
  const [overlay, setOverlay] = useState<WallOverlayTarget | null>(null);

  // ЕДИНЫЙ гейт деградации — в рендере, а не в resize-обработчике: покрывает и сжатие
  // окна на открытой стене, и старт по хешу #/wall на узком экране, и выключенный флаг.
  const narrow = w <= TABLET_MAX;
  const active = wallOn && !narrow;

  // Снимок и SignalR-группы — только когда стена реально работает
  useEffect(() => { if (active) initWall(auth.id ?? undefined); }, [auth.id, active]);

  const slots = slotCount(w);
  const visible = chats.slice(0, slots);
  const focused: Session | null = visible.find(c => c.id === focusId) ?? visible[0] ?? null;
  const focusedProject = focused?.projectId ? projects.get(focused.projectId) : undefined;

  // Смена фокуса закрывает оверлей: контент чужого проекта поверх нового фокуса — враньё
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
    // eslint-disable-next-line react-hooks/exhaustive-deps -- openChatOnWall стабилен по смыслу (замыкание на сторе)
    [focusedProject, overlay],
  );

  if (!active) {
    return (
      <div style={{ height: '100dvh', background: C.bgMain, fontFamily: FONT.sans, display: 'flex', flexDirection: 'column', overflow: 'hidden', position: 'relative', isolation: 'isolate' }}>
        <CanvasBackdrop />
        <HubHeader value="wall" onTab={onHubTab} auth={auth} onLogout={onLogout} />
        <div style={{ flex: 1, display: 'flex', alignItems: 'center', justifyContent: 'center', padding: 24 }}>
          <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', textAlign: 'center', maxWidth: 380, gap: 10 }}>
            <div style={{ width: 56, height: 56, borderRadius: 16, background: C.bgPanel, color: C.accent, display: 'flex', alignItems: 'center', justifyContent: 'center' }}>
              <LayoutGrid size={ICON_SIZE.xl} strokeWidth={2} />
            </div>
            <div style={{ fontFamily: FONT.serif, fontWeight: 500, fontSize: 20, color: C.textHeading }}>
              {wallOn ? 'Стене нужен широкий экран' : 'Стена выключена'}
            </div>
            <div style={{ fontSize: FS.sm, color: C.textSecondary, lineHeight: 1.55 }}>
              {wallOn
                ? 'Колонкам чатов не хватает места. Откройте окно шире 1200px или вернитесь к проектам.'
                : 'Включите «Стену» в экспериментальных функциях (меню аватара).'}
            </div>
            <Button variant="secondary" size="md" onClick={() => onHubTab('projects')} style={{ marginTop: 8 }}>
              К проектам
            </Button>
          </div>
        </div>
      </div>
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
    <div style={{ height: '100dvh', background: C.bgMain, fontFamily: FONT.sans, display: 'flex', flexDirection: 'column', overflow: 'hidden', position: 'relative', isolation: 'isolate' }}>
      <CanvasBackdrop />
      <HubHeader value="wall" onTab={onHubTab} auth={auth} onLogout={onLogout} />

      {/* position: relative — якорь оверлея-лайтбокса (WallOverlay absolute inset) */}
      <div style={{ flex: 1, minHeight: 0, display: 'flex', position: 'relative', padding: `${ISLAND.gap}px 0 ${ISLAND.pad}px 0` }}>
        {/* Рельса набора у левого края (капсула по контенту) */}
        <WallRail slots={slots} onOpenPicker={() => setPickerOpen(true)} onExit={() => onHubTab('projects')} />

        {/* Колонки чатов */}
        <div style={{ flex: 1, minWidth: 0, display: 'flex', gap: ISLAND.gap, margin: `0 ${ISLAND.centerGap}px` }}>
          {!loaded ? null : visible.length === 0 ? (
            <div style={{ flex: 1, display: 'flex', alignItems: 'center', justifyContent: 'center' }}>
              <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', textAlign: 'center', maxWidth: 400, gap: 10 }}>
                <div style={{ width: 56, height: 56, borderRadius: 16, background: C.bgPanel, color: C.accent, display: 'flex', alignItems: 'center', justifyContent: 'center' }}>
                  <LayoutGrid size={ICON_SIZE.xl} strokeWidth={2} />
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
            visible.map(s => {
              const proj = s.projectId ? (projects.get(s.projectId) ?? null) : undefined;
              return (
                <WallColumn
                  key={s.id}
                  session={s}
                  project={proj}
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

        {/* Рельса панелей фокуса: полновысотная обёртка — якорь peek/закрепа */}
        <div style={{ position: 'relative', display: 'flex', flexShrink: 0 }}>
          <WallPanelRail session={focused} project={focusedProject} projectPanels={projectPanels} pinned={pinned} onPin={setPinned} />
        </div>

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
    </div>
  );
}
