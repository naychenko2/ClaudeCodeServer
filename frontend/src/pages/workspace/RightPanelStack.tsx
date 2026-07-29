// Правая зона нового интерфейса проекта (workspace-cc-panels): вертикальная рельса
// иконок РАБОЧИХ ИНСТРУМЕНТОВ у правого края + открытые панели-карточки.
// Раскладка — ЯВНЫЕ колонки (как в Claude Code Desktop): дефолт «по две на колонку»
// в порядке открытия. Drag-and-drop за шапку: дроп НА панель меняет две панели
// местами (слоты остаются на месте), дроп в плейсхолдер между панелями или в
// разделитель колонок вставляет/выносит — можно получить любое распределение,
// например одну панель в первой колонке и две во второй.
// Панели — «воздушные» скруглённые карточки с зазорами; границы высот тянутся
// невидимыми хендлами в зазорах, ширина колонок — сплиттером слева от зоны.
import { useEffect, useState, type ReactNode } from 'react';
import { ClipboardList, FolderTree, GitCompare, ListTodo, Bot, User, Users, SquareTerminal, MonitorPlay, Network, type LucideIcon } from 'lucide-react';
import type { Session } from '../../types';
import { C, FONT, ISLAND, SHADOW } from '../../lib/design';
import { ICON_STROKE } from '../../components/ui/icons';
import { PanelShell } from '../../components/ui/PanelShell';
import { IslandSplitter } from '../../components/ui/IslandSplitter';
import { useSessionArtifacts } from '../../hooks/useSessionArtifacts';
import { PlanSection } from '../../components/artifacts/PlanSection';
import { AgentsSection } from '../../components/artifacts/AgentsSection';
import { ContextSection } from '../../components/artifacts/ContextSection';
import { panelBadge } from '../../components/artifacts/meta';
import { useWindowWidth } from '../../lib/breakpoints';
import { PanelRail, RAIL_W, RAIL_GAP, type RailItem } from '../../components/ui/PanelRail';
import { PanelDropGuide } from '../../components/ui/PanelDropGuide';
import { usePanelDnd, usePanelRowResize, usePanelWidthDrag } from './panelZone';
import { PanelSlot } from './PanelSlot';
import { wsPanelStack, RIGHT_PANEL_KEYS, type PanelKey, type RightPanelKey, type PanelStack } from './panelStackState';

// Порог планшета: шире — панель в потоке рядом с чатом, уже — drawer поверх
const TABLET_INLINE_MIN = 1000;

// Иконки и заголовки панелей ПРАВОЙ рельсы. Мета левой рельсы — своя,
// в LeftPanelStack.LEFT_PANEL_META.
const PANEL_META: Record<RightPanelKey, { title: string; Icon: LucideIcon }> = {
  plan: { title: 'План', Icon: ClipboardList },
  agents: { title: 'Агенты', Icon: Bot },
  // 'context' — досье персоны-собеседника (память/привязки/recall); отображается «Персона».
  context: { title: 'Персона', Icon: User },
  files: { title: 'Файлы', Icon: FolderTree },
  changes: { title: 'Изменения', Icon: GitCompare },
  tasks: { title: 'Задачи', Icon: ListTodo },
  graph: { title: 'Граф', Icon: Network },
  team: { title: 'Команда', Icon: Users },
  terminal: { title: 'Терминал', Icon: SquareTerminal },
  preview: { title: 'Preview', Icon: MonitorPlay },
};

// Рельса разбита на две группы, разделённые сепаратором. Сверху — инструменты
// ПРОЕКТА (файлы, изменения, задачи, команда, терминал, preview), снизу — панели
// ТЕКУЩЕЙ СЕССИИ (План, Агенты, Персона). Порядок: проектные раньше сессионных.
const PROJECT_RAIL_KEYS: RightPanelKey[] = ['files', 'changes', 'tasks', 'graph', 'team', 'terminal', 'preview'];
const SESSION_RAIL_KEYS: RightPanelKey[] = ['plan', 'agents', 'context'];

const GAP = ISLAND.gap; // зазор между карточками — та самая «воздушность»

interface Props {
  session: Session | null;
  projectId?: string;
  rootPath?: string;
  // Планшет: упрощённый режим — всегда одна панель (эфемерный solo, локальный стейт,
  // десктопная раскладка layout НЕ трогается), без DnD/колонок/сворачивания
  isTablet?: boolean;
  // Телефон: тот же компактный режим, что и планшет (одна панель + drawer)
  isMobile?: boolean;
  // Только сессионная группа (План/Агенты/Персона) — для раздела «Чаты» и мобилки:
  // проектные инструменты не рендерятся, пустая рельса скрывается целиком
  sessionOnly?: boolean;
  // Инстанс стора раскладки: воркспейс и «Чаты» держат НЕЗАВИСИМЫЕ раскладки
  // (по умолчанию — воркспейсный, см. panelStackState.createPanelStack)
  panelStack?: { use: () => PanelStack };
  // Терминал и Preview доступны только при включённых инструментах проекта
  toolsEnabled?: boolean;
  // Готовый контент панелек (кроме Плана — он собирается здесь из артефактов сессии).
  // Строится в WorkspacePage, где живут состояние и обработчики этих инструментов.
  // В sessionOnly не нужен — проектных панелей там нет.
  panels?: Partial<Record<Exclude<RightPanelKey, 'plan'>, ReactNode>>;
  // Контролы в шапку карточки (слева от кнопки закрытия) — напр. переключатель
  // видов задач. Собираются в WorkspacePage, состояние живёт там же.
  panelHeaderExtras?: Partial<Record<RightPanelKey, ReactNode>>;
  // Числа-кружки на кнопках ПРОЕКТА (changes/tasks/terminal/preview) — считаются в
  // WorkspacePage (там живут данные git/задач/терминалов/сервисов). Сессионные кнопки
  // свои числа берут из артефактов сессии (railBadgeCount), не отсюда.
  railCounts?: Partial<Record<RightPanelKey, number>>;
  // Хук на ЯВНУЮ активацию панели кликом по иконке рельсы (панель в результате
  // открылась). Только клик: восстановление раскладки из localStorage его не дёргает.
  // Сейчас используется графом — открыть свой документ в центре вместе с панелью.
  onPanelOpen?: (k: RightPanelKey) => void;
}

// Направляющие мест вставки при DnD — общий примитив PanelDropGuide
// (axis='x' между колонками, axis='y' между панелями колонки).

export function RightPanelStack({ session, projectId, rootPath, isTablet, isMobile, sessionOnly, panelStack, toolsEnabled, panels = {}, panelHeaderExtras, railCounts, onPanelOpen }: Props) {
  // Инстанс стора раскладки: оба объявлены на уровне модуля, поэтому вызов хука
  // безусловный и стабильный между рендерами (проп не меняется по ходу жизни экрана)
  const usePanels = (panelStack ?? wsPanelStack).use;
  const { layout, weights, width, mode, toggle, close, collapsed, toggleCollapsed, setWeights, setWidth, swapWith, moveToNewColumn, moveAt, setMode } = usePanels();
  const windowWidth = useWindowWidth();
  // Компактный режим (планшет и телефон): одна панель + drawer, без колонок/DnD/solo
  const compact = !!isTablet || !!isMobile;
  // Планшет: до ДВУХ панелей стеком в одной колонке; выбор локальный эфемерный —
  // десктопный layout не трогаем. Третья открытая вытесняет самую старую (FIFO).
  const [tabletPanels, setTabletPanels] = useState<RightPanelKey[]>([]);
  const tabletInline = windowWidth >= TABLET_INLINE_MIN;
  const sessionId = session?.id ?? null;
  // Артефакты сессии питают сессионную группу рельсы: План, Чек-лист (todos), Агенты
  // (бейджи + содержимое панелек). Персона (context) данные тянет сама через ContextSection.
  const artifacts = useSessionArtifacts(sessionId, projectId, rootPath ?? '', null);
  const plansCount = artifacts.plans.length;
  // Опции расчёта видимости/бейджей сессионных кнопок (единый источник — panelBadge из meta).
  // executingTask=false: в рельсе artifacts считаются без заголовка задачи-исполнителя.
  const badgeOpts = { executingTask: false, personaId: session?.personaId ?? null, isChat: !projectId };

  // Отбор ключей правой рельсы из общей раскладки стора: чужие (левые) ключи
  // отбрасываются здесь же — предикат сужает тип, поэтому ниже по коду везде
  // RightPanelKey и PANEL_META не нужны заглушки.
  // Терминал/Preview дополнительно скрыты при выключенных инструментах проекта.
  const keyAvailable = (k: PanelKey): k is RightPanelKey =>
    (RIGHT_PANEL_KEYS as readonly string[]).includes(k)
    && ((k !== 'terminal' && k !== 'preview') || !!toolsEnabled);
  const soloMode = mode === 'solo';
  // Состояние ЕДИНОЕ для обоих режимов: в solo layout содержит максимум одну
  // панель (toggle заменяет её), поэтому рендер одинаковый.
  // На планшете колонки из layout не рендерятся — там свой стек до двух панелей.
  const columns = compact ? [] : layout.map(col => col.filter(keyAvailable)).filter(col => col.length > 0);
  const tabletKeys = compact ? tabletPanels.filter(keyAvailable) : [];
  const openKeys = compact ? tabletKeys : columns.flat();

  // Видимость иконки на рельсе. Сессионные кнопки показываются ТОЛЬКО когда есть что
  // открывать (План — если был план, Агенты — если есть контент, Персона — если
  // собеседник-персона): иначе иконка скрыта целиком (а не дизейблится), вместе с ней
  // прячется и разделитель групп. Единый расчёт — panelBadge из meta.
  // Объявлено до расчёта ширины зоны: от него зависит скрытие пустой сессионной рельсы.
  const railKeyVisible = (k: RightPanelKey): boolean => {
    if (!keyAvailable(k)) return false;
    if (k === 'plan') return plansCount > 0 || openKeys.includes(k);
    if (k === 'agents' || k === 'context') {
      return panelBadge(k, artifacts, badgeOpts).visible || openKeys.includes(k);
    }
    return true;
  };

  // Режим sessionOnly без контента: рельсу не рисуем вовсе, чтобы у чата не торчала
  // пустая полоса. Ширина зоны при этом 0 — иначе FAB AI-хаба уедет под невидимую рельсу.
  const railHidden = !!sessionOnly && !SESSION_RAIL_KEYS.some(railKeyVisible) && openKeys.length === 0;

  // Сдвиг FAB AI-хаба к зоне чата: правую кромку занимают рельса и панели —
  // пробрасываем их суммарную ширину в глобальную переменную (читает AiLauncher).
  // Drawer на планшете не считаем — он overlay и живёт поверх контента сам.
  // Позиция меняется МГНОВЕННО (переменная не анимируется, см. index.css) —
  // кнопка просто оказывается на новом месте, без движения и миганий.
  // Слагаемые считаются ПО РАЗМЕТКЕ, слева направо: ресайз-сплиттер зоны
  // (RAIL_GAP) + сама зона (у каждой колонки свой ColumnSep шириной GAP) + рельса.
  // При закрытых панелях зоны в потоке нет, а своего зазора у рельсы нет
  // (gapToCenter=0) — остаётся только её ширина.
  const rightZoneW = railHidden ? 0 : RAIL_W + (compact
    ? (tabletKeys.length > 0 && tabletInline ? RAIL_GAP + width + GAP * 2 : 0)
    : (columns.length > 0 ? RAIL_GAP + columns.length * (width + GAP) : 0));
  useEffect(() => {
    document.documentElement.style.setProperty('--cc-fab-right', `${rightZoneW + 20}px`);
    return () => { document.documentElement.style.removeProperty('--cc-fab-right'); };
  }, [rightZoneW]);

  // Флеш «панель уже открыта»: внешние кнопки (git-бар над композером) шлют
  // cc-panel-flash, карточка на мгновение обводится акцентом. Счётчик n нужен,
  // чтобы повторный клик по той же панели перезапускал таймер.
  const [flash, setFlash] = useState<{ key: RightPanelKey; n: number } | null>(null);
  useEffect(() => {
    const onFlash = (e: Event) => {
      const key = (e as CustomEvent<{ key?: RightPanelKey }>).detail?.key;
      if (key) setFlash(cur => ({ key, n: (cur?.n ?? 0) + 1 }));
    };
    window.addEventListener('cc-panel-flash', onFlash);
    return () => window.removeEventListener('cc-panel-flash', onFlash);
  }, []);
  useEffect(() => {
    if (!flash) return;
    // Снимаем класс сразу по окончании анимации (0.55s в index.css) — иначе
    // повторный клик по кнопке не перезапустил бы вспышку
    const id = setTimeout(() => setFlash(null), 600);
    return () => clearTimeout(id);
  }, [flash]);

  // Ресайз: сплиттер ширины зоны и хендлы высот между панелями — общая с левой
  // рельсой механика (метка активного хендла — 'ci:ri' либо 'tablet')
  const { panelRefs, rowDragging, handleRowDrag } = usePanelRowResize<RightPanelKey>(weights, setWeights);
  // Drag-and-drop перестановки: какая панель тащится, над какой висит,
  // и над каким разделителем колонок (дроп туда = вынос в новую колонку)
  // Позиции вставки под курсором: разделитель колонок (индекс) и горизонтальный
  // плейсхолдер ('ci:ri'). Сбрасываются вместе с DnD — через onEnd хука.
  const [dndOverSep, setDndOverSep] = useState<number | null>(null);
  const [dndOverRow, setDndOverRow] = useState<string | null>(null);
  const dnd = usePanelDnd<RightPanelKey>({
    enabled: !soloMode && !compact,
    onSwap: swapWith,
    onEnd: () => { setDndOverSep(null); setDndOverRow(null); },
  });

  // Ширина зоны: тянем влево — колонки растут; width хранится на ОДНУ колонку,
  // поэтому сдвиг курсора делится на их число
  const { dragging: widthDragging, onPointerDown: handleWidthDrag } =
    usePanelWidthDrag(width, setWidth, 'right', columns.length);

  // Пустой стейт панельки (когда открыта, но контента ещё нет)
  const emptyPanel = (text: string): ReactNode => (
    <div style={{ padding: '20px 14px', fontFamily: FONT.sans, fontSize: 12.5, color: C.textMuted, textAlign: 'center' }}>
      {text}
    </div>
  );

  const panelContent = (k: RightPanelKey): ReactNode => {
    if (k === 'plan') {
      return plansCount > 0
        ? <PlanSection plans={artifacts.plans} projectId={projectId} />
        : emptyPanel('План появится после ExitPlanMode в чате');
    }
    if (k === 'agents') {
      return <AgentsSection agents={artifacts.agents} workflows={artifacts.workflows} />;
    }
    if (k === 'context') {
      const pid = session?.personaId;
      return pid
        ? <ContextSection personaId={pid} sessionId={sessionId} />
        : emptyPanel('Доступно в чате с персоной');
    }
    return panels[k] ?? null;
  };

  // Панелька в раскладке колонок
  const renderPanel = (k: RightPanelKey) => {
    return (
      <PanelSlot
        key={k}
        weight={weights[k]}
        resizing={rowDragging != null}
        slotRef={el => { panelRefs.current[k] = el; }}
      >
        <PanelShell
          icon={(() => { const I = PANEL_META[k].Icon; return <I size={15} strokeWidth={ICON_STROKE} color={C.textSecondary} style={{ flexShrink: 0 }} />; })()}
          title={PANEL_META[k].title}
          badge={
            k === 'plan'
              ? (plansCount > 1 ? `${plansCount}` : null)
              : (k === 'agents' || k === 'context')
                ? panelBadge(k, artifacts, badgeOpts).badge
                : null
          }
          headerExtras={panelHeaderExtras?.[k]}
          onClose={() => { if (compact) setTabletPanels(cur => cur.filter(x => x !== k)); else close(k); }}
          flash={flash?.key === k}
          {...dnd.panelProps(k)}
        >
          {panelContent(k)}
        </PanelShell>
      </PanelSlot>
    );
  };

  // Число в кружке над иконкой кнопки. Сессионные — «сколько требует внимания» (не «всего»):
  // План — неодобренные (status ≠ approved), Чек-лист — не закрытые (≠ completed),
  // Агенты — открытые (running); Персона счётчика не имеет. Проектные (changes/tasks/
  // terminal/preview) берут готовое число из railCounts (считается в WorkspacePage).
  // 0 → кружок не рисуем.
  const railBadgeCount = (k: RightPanelKey): number | null => {
    let n: number;
    if (k === 'plan') n = artifacts.plans.filter(p => p.status !== 'approved').length;
    else if (k === 'agents') n = [...artifacts.agents, ...artifacts.workflows.flatMap(w => w.agents)]
      .filter(a => a.status === 'running').length;
    else if (k === 'changes' || k === 'tasks' || k === 'terminal' || k === 'preview') n = railCounts?.[k] ?? 0;
    else return null; // context (Персона), files — без кружка
    return n > 0 ? n : null;
  };

  // Иконки одной группы рельсы: скрытые (railKeyVisible) отсеиваются здесь же —
  // пустая группа не рисуется вовсе, вместе со своим разделителем.
  const railGroup = (keys: RightPanelKey[]): RailItem[] => keys.filter(railKeyVisible).map(k => ({
    key: k,
    title: PANEL_META[k].title,
    Icon: PANEL_META[k].Icon,
    active: openKeys.includes(k),
    badge: railBadgeCount(k),
    onClick: () => {
      const isOpen = openKeys.includes(k);
      if (compact) {
        // До двух панелей: третья вытесняет самую старую (FIFO)
        setTabletPanels(cur => cur.includes(k) ? cur.filter(x => x !== k) : [...cur, k].slice(-2));
      } else toggle(k);
      // Панель в результате клика ОТКРЫЛАСЬ (была скрыта; в solo toggle — радио,
      // закрытой считается и вытесняемая) — сообщаем подписчику (граф и т.п.)
      if (!isOpen) onPanelOpen?.(k);
    },
  }));

  return (
    <>
      {/* Планшет: стек до двух панелей — в потоке на широком экране, drawer поверх
          на узком; между двумя панелями — хендл ресайза высот */}
      {compact && tabletKeys.length > 0 && (() => {
        const stack = (
          // overflow visible — тени панелей-островов не должны срезаться обёрткой
          <div style={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column' }}>
            {tabletKeys.map((k, ri) => (
              <div key={k} style={{ display: 'contents' }}>
                {ri > 0 && (
                  <IslandSplitter orientation="h" active={rowDragging === 'tablet'} onMouseDown={handleRowDrag(tabletKeys[ri - 1], k, 'tablet')} gap={GAP} />
                )}
                {renderPanel(k)}
              </div>
            ))}
          </div>
        );
        return tabletInline ? (
          <>
            <IslandSplitter orientation="v" active={widthDragging} onMouseDown={handleWidthDrag} gap={RAIL_GAP} />
            <div style={{ width: width + GAP * 2, flexShrink: 0, display: 'flex', padding: `0 ${GAP}px`, boxSizing: 'border-box' }}>
              {stack}
            </div>
          </>
        ) : (
          <>
            <div onClick={() => setTabletPanels([])} style={{ position: 'absolute', inset: 0, zIndex: 14, background: C.overlay }} />
            <div style={{ position: 'absolute', top: GAP, right: RAIL_W + GAP, bottom: GAP, zIndex: 15, width: 'min(85vw, 380px)', display: 'flex', flexDirection: 'column', boxShadow: SHADOW.modal }}>
              {stack}
            </div>
          </>
        );
      })()}

      {/* Зона панелей-карточек (не рендерится, когда открывать нечего).
          Горизонтальные зазоры — явные ColumnSep: в покое пустые, при drag-and-drop
          превращаются в дроп-зоны выноса панели в новую колонку. */}
      {columns.length > 0 && (
        <>
          <IslandSplitter orientation="v" active={widthDragging} onMouseDown={handleWidthDrag} gap={RAIL_GAP} />
          <div style={{
            // В покое крайний ЛЕВЫЙ ColumnSep не рендерится (зазор от центра уже даёт
            // ресайз-сплиттер) — сепараторов columns.length; при DnD появляется и он,
            // но нулевой ширины. Ни ширина зоны, ни размеры панелей при DnD НЕ меняются:
            // дроп-зоны сепараторов — absolute-оверлеи, места в потоке не занимают.
            width: columns.length * (width + GAP),
            // Вертикальные отступы зоны даёт холст DesktopWorkspace (padding GAP).
            // overflow visible — иначе зона срезала бы тени крайних панелей-островов
            flexShrink: 0, display: 'flex',
            boxSizing: 'border-box',
            transition: widthDragging ? 'none' : 'width 0.15s ease-out',
          }}>
            {columns.map((col, ci) => (
              <div key={ci} style={{ display: 'contents' }}>
                {/* Крайний левый сеп (ci=0) — только как дроп-зона при DnD: в потоке
                    он нулевой, зазор от центра уже даёт ресайз-сплиттер зоны */}
                <PanelDropGuide
                  axis="x"
                  dndActive={dnd.active}
                  base={ci > 0 ? GAP : 0}
                  edge={ci === 0 ? 'start' : undefined}
                  over={dndOverSep === ci}
                  onDragOver={e => { if (dnd.from) { e.preventDefault(); e.dataTransfer.dropEffect = 'move'; setDndOverSep(ci); } }}
                  onDragLeave={() => setDndOverSep(cur => (cur === ci ? null : cur))}
                  onDrop={e => { e.preventDefault(); if (dnd.from) moveToNewColumn(dnd.from, ci); dnd.end(); }}
                />
                <div style={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column' }}>
                  {(() => {
                    // Горизонтальный плейсхолдер вставки на позицию ri колонки ci.
                    // base — место в потоке: по краям колонки 0 (в покое их нет),
                    // между панелями GAP (подменяет хендл ресайза той же высоты)
                    const rowSep = (ri: number, base = 0, edge?: 'start' | 'end') => (
                      <PanelDropGuide
                        axis="y"
                        key={`sep-${ri}`}
                        dndActive={dnd.active}
                        base={base}
                        edge={edge}
                        over={dndOverRow === `${ci}:${ri}`}
                        onDragOver={e => { if (dnd.from) { e.preventDefault(); e.dataTransfer.dropEffect = 'move'; setDndOverRow(`${ci}:${ri}`); } }}
                        onDragLeave={() => setDndOverRow(cur => (cur === `${ci}:${ri}` ? null : cur))}
                        onDrop={e => { e.preventDefault(); if (dnd.from) moveAt(dnd.from, ci, ri); dnd.end(); }}
                      />
                    );
                    return (
                      <>
                        {rowSep(0, 0, 'start')}
                        {col.map((k, ri) => (
                          <div key={k} style={{ display: 'contents' }}>
                            {ri > 0 && (
                              dnd.active
                                ? rowSep(ri, GAP)
                                : <IslandSplitter orientation="h" active={rowDragging === `${ci}:${ri}`} onMouseDown={handleRowDrag(col[ri - 1], k, `${ci}:${ri}`)} gap={GAP} />
                            )}
                            {renderPanel(k)}
                          </div>
                        ))}
                        {rowSep(col.length, 0, 'end')}
                      </>
                    );
                  })()}
                </div>
              </div>
            ))}
            <PanelDropGuide
              axis="x"
              dndActive={dnd.active}
              base={RAIL_GAP}
              edge="end"
              over={dndOverSep === columns.length}
              onDragOver={e => { if (dnd.from) { e.preventDefault(); e.dataTransfer.dropEffect = 'move'; setDndOverSep(columns.length); } }}
              onDragLeave={() => setDndOverSep(cur => (cur === columns.length ? null : cur))}
              onDrop={e => { e.preventDefault(); if (dnd.from) moveToNewColumn(dnd.from, columns.length); dnd.end(); }}
            />
          </div>
        </>
      )}

      {/* Рельса иконок. Две группы: сверху инструменты ПРОЕКТА (в sessionOnly их
          нет — проекта там либо нет вовсе, либо инструменты живут слева), снизу
          панели ТЕКУЩЕЙ СЕССИИ. Разделитель между ними PanelRail рисует сам и
          убирает вместе с пустой группой (напр. Плана без планов).
          Зазор до центра — весь в крайнем ColumnSep зоны, поэтому своего у рельсы
          нет (gapToCenter=0): при закрытых панелях центр примыкает к ней вплотную.
          Тумблер режима и «свернуть все» в компактном режиме не нужны — там
          панель всегда одна и закрывается своей же иконкой. */}
      <PanelRail
        side="right"
        visible={!railHidden}
        gapToCenter={0}
        groups={[
          sessionOnly ? [] : railGroup(PROJECT_RAIL_KEYS),
          railGroup(SESSION_RAIL_KEYS),
        ]}
        modeToggle={compact ? undefined : {
          soloMode,
          onToggle: () => setMode(soloMode ? 'multi' : 'solo'),
        }}
        collapse={compact ? undefined : {
          collapsed,
          disabled: openKeys.length === 0 && !collapsed,
          onToggle: toggleCollapsed,
        }}
      />
    </>
  );
}
