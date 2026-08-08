// Панели ПРОЕКТА для стены: контент собирается для ФОКУСНОГО проекта (смена
// фокуса перезаполняет, как у сессионных панелей), а клики-результаты
// приземляются в ОВЕРЛЕЙ стены (openOverlay) — центра, как в воркспейсе, тут нет.
//
// Состав — явная константа WALL_PROJECT_KEYS (НЕ PROJECT_KEYS из panelCatalog:
// тот включает chats и панели разделов хаба). Компоненты панелей — те же, что в
// воркспейсе; сюда передаётся только необходимый минимум пропсов, воркспейсная
// обвязка (знания, attach-to-chat, доска) намеренно не тянется.
import { useEffect, type ReactNode } from 'react';
import type { Project, Task } from '../../types';
import { PreviewView } from '../../components/preview/PreviewView';
import type { PanelKey } from '../../pages/workspace/panelCatalog';
import { showToast } from '../../lib/toast';
import { FileExplorer } from '../../components/FileExplorer';
import { DocsPanel } from '../../pages/workspace/DocsPanel';
import { GitChangesRail } from '../../components/GitChangesRail';
import { TasksPanel } from '../../features/tasks/TasksPanel';
import { ProjectPersonasPanel } from '../../features/personas/ProjectPersonasPanel';
import { CodeGraphPanel } from '../../features/codegraph/CodeGraphPanel';
import { TerminalPanelContent, PreviewPanelContent } from '../../pages/workspace/panels';
import { useProjectTerminals } from '../../hooks/useProjectTerminals';
import { useProjectServices } from '../../hooks/useProjectServices';

// Панели проекта, доступные на стене (порядок = порядок иконок в рельсе)
export const WALL_PROJECT_KEYS: readonly PanelKey[] = ['files', 'docs', 'changes', 'tasks', 'graph', 'team'];

// Куда приземлился клик из панели (стейт оверлея держит WallPage)
export type WallOverlayTarget =
  | { kind: 'file'; path: string; diffMode?: boolean; gitStagePath?: string }
  | { kind: 'commit'; sha: string }
  | { kind: 'task'; task: Task }
  | { kind: 'persona'; personaId: string | null; creating?: boolean }
  | { kind: 'teamCenter' }
  | { kind: 'graph' }
  | { kind: 'preview'; serviceId: string };

export interface WallPanelsDeps {
  openOverlay: (t: WallOverlayTarget) => void;
  // Открыть чат колонкой на стене (задача/командный центр ссылаются на сессии)
  openChatOnWall: (sessionId: string) => void;
  // Выбранная задача (подсветка в списке, пока открыт её оверлей)
  selectedTaskId: string | null;
  // Открыт ли оверлей графа (панель графа подсвечивает состояние документа)
  graphOverlayOpen: boolean;
}

// Контент списочных панелей проекта. Хуком не является (чистая сборка JSX) —
// но имя оставлено «use»-стилем каталога воркспейса намеренно не повторять.
export function buildWallProjectPanels(
  project: Project | undefined,
  deps: WallPanelsDeps,
): Partial<Record<PanelKey, ReactNode>> {
  if (!project) return {};
  const { openOverlay } = deps;

  // key={project.id} на КАЖДОЙ панели: закреплённая карточка при смене фокуса между
  // проектами не перемонтируется сама (тип и позиция те же), а панели держат
  // per-project состояние в useState-инициализаторах (DocsPanel читает
  // localStorage-ключи проекта только на маунте — без ключа свёрнутые папки
  // проекта A применялись бы к проекту B и портили его персист при записи)
  return {
    files: (
      <FileExplorer
        key={project.id}
        project={project}
        activeFilePath={null}
        isMobile={false}
        onOpenFile={path => openOverlay({ kind: 'file', path })}
      />
    ),
    docs: (
      <DocsPanel
        key={project.id}
        project={project}
        onOpenFile={path => openOverlay({ kind: 'file', path })}
        // На стене цель прикрепления неоднозначна (несколько чатов) — честная
        // подсказка вместо молчаливого no-op; полный сценарий — через zoom
        onAttachToChat={() => showToast('Стена', 'Прикрепление к чату доступно в полном виде проекта')}
        activeFilePath={null}
        onCloseFile={() => {}}
      />
    ),
    changes: (
      <GitChangesRail
        key={project.id}
        project={project}
        onOpenDiff={(path, staged) => openOverlay({ kind: 'file', path, diffMode: true, gitStagePath: staged ? undefined : path })}
        onOpenFile={path => openOverlay({ kind: 'file', path })}
        onOpenCommit={sha => openOverlay({ kind: 'commit', sha })}
        activeFilePath={null}
        activeCommitSha={null}
      />
    ),
    tasks: (
      <TasksPanel
        key={project.id}
        project={project}
        selectedTaskId={deps.selectedTaskId}
        onSelect={task => openOverlay({ kind: 'task', task })}
        isMobile={false}
      />
    ),
    team: (
      <ProjectPersonasPanel
        key={project.id}
        project={project}
        selectedId={null}
        onSelect={id => { if (id) openOverlay({ kind: 'persona', personaId: id }); }}
        onNew={() => openOverlay({ kind: 'persona', personaId: null, creating: true })}
        onShowTeam={() => openOverlay({ kind: 'teamCenter' })}
      />
    ),
    graph: (
      <CodeGraphPanel
        key={project.id}
        projectId={project.id}
        graphOpen={deps.graphOverlayOpen}
        onEnsureGraphOpen={() => openOverlay({ kind: 'graph' })}
        onOpenFile={path => openOverlay({ kind: 'file', path })}
        onBuild={() => openOverlay({ kind: 'graph' })}
      />
    ),
    // Терминал и Сервисы: live-состояние в хуках внутри компонентов-обёрток,
    // panels сами по себе — чистый JSX. По дефолту их кнопки лежат в ящике рельсы
    // (defaultTucked у wsPanels), но контент подаём всегда — достанут из «…».
    terminal: <WallTerminalPanel key={project.id} projectId={project.id} />,
    preview: <WallServicesPanel key={project.id} projectId={project.id} onOpenPreview={id => openOverlay({ kind: 'preview', serviceId: id })} />,
  };
}

// Панель «Терминал» на стене: те же хуки, что у воркспейса. Живой xterm живёт в
// карточке (peek у этой панели отключён — см. WallPanelRail). onActivity глушим:
// индикатор занятости терминала — воркспейсная механика.
function WallTerminalPanel({ projectId }: { projectId: string }) {
  const t = useProjectTerminals(projectId);
  return (
    <TerminalPanelContent
      terminals={t.terminals}
      activeTerminalId={t.activeTerminalId}
      onSelect={t.setActiveTerminalId}
      onCreate={t.create}
      onStop={t.stop}
      onActivity={() => {}}
    />
  );
}

// Панель «Сервисы» на стене: выбор запущенного сервиса открывает оверлей PreviewView
function WallServicesPanel({ projectId, onOpenPreview }: { projectId: string; onOpenPreview: (serviceId: string) => void }) {
  const s = useProjectServices(projectId);
  return (
    <PreviewPanelContent
      projectId={projectId}
      services={s.services}
      activePreviewId={s.activePreviewId}
      // Оверлей открываем только после назначения активного сервиса на бэкенде —
      // иначе его iframe уедет в прокси раньше и получит «Dev-сервер не запущен»
      onSelect={async id => { await s.activate(id); if (id) onOpenPreview(id); }}
      onStart={s.start}
      onStop={s.stop}
      onRefresh={s.refresh}
    />
  );
}

// Live-сервисы фокусного проекта нужны и ОВЕРЛЕЮ превью (PreviewView требует объект
// ProjectService и их список) — отдельный компонент с тем же хуком, монтируется
// только когда оверлей открыт.
export function WallPreviewOverlayBody({ projectId, serviceId, onClose }: {
  projectId: string; serviceId: string; onClose: () => void;
}) {
  const s = useProjectServices(projectId);
  // Снимок сервисов грузится лениво — до его приезда рисовать нечего
  const { refresh } = s;
  useEffect(() => { void refresh(); }, [refresh]);
  const svc = s.services.find(x => x.id === serviceId);
  if (!svc) return null;
  return <PreviewView service={svc} projectId={projectId} onStop={s.stop} onClose={onClose} services={s.services} />;
}
