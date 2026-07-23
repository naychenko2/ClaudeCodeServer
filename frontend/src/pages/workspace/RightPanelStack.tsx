// Правая зона нового интерфейса проекта (workspace-cc-panels): вертикальная рельса
// иконок РАБОЧИХ ИНСТРУМЕНТОВ у правого края + открытые панели-карточки.
// Раскладка — ЯВНЫЕ колонки (как в Claude Code Desktop): дефолт «по две на колонку»
// в порядке открытия, drag-and-drop за шапку переносит панель в колонку цели
// (вставка перед ней) — можно получить любое распределение, например одну панель
// в первой колонке и две во второй. У каждой панельки есть режим fullscreen —
// карточка разворачивается на всю рабочую область (кроме рельсы).
// Панели — «воздушные» скруглённые карточки с зазорами; границы высот тянутся
// невидимыми хендлами в зазорах, ширина колонок — сплиттером слева от зоны.
import { useEffect, useRef, useState, type ReactNode, type DragEvent, type PointerEvent as ReactPointerEvent } from 'react';
import { X, Maximize2, Minimize2, Columns2, Square, ChevronsRight, ChevronsLeft, ClipboardList, FolderTree, GitCompare, ListTodo, Users, SquareTerminal, MonitorPlay, type LucideIcon } from 'lucide-react';
import type { Session } from '../../types';
import { C, FONT, R, SHADOW } from '../../lib/design';
import { ICON_STROKE } from '../../components/ui/icons';
import { Splitter } from '../../components/ui/Splitter';
import { ToolbarIconButton } from '../../components/Toolbar';
import { useSessionArtifacts } from '../../hooks/useSessionArtifacts';
import { PlanSection } from '../../components/artifacts/PlanSection';
import { useWindowWidth } from '../../lib/breakpoints';
import { usePanelStack, PANEL_MIN_H, RAIL_W, type PanelKey } from './panelStackState';

// Порог планшета: шире — панель в потоке рядом с чатом, уже — drawer поверх
const TABLET_INLINE_MIN = 1000;

// Иконки и заголовки панелей рельсы
const PANEL_META: Record<PanelKey, { title: string; Icon: LucideIcon }> = {
  plan: { title: 'План', Icon: ClipboardList },
  files: { title: 'Файлы', Icon: FolderTree },
  changes: { title: 'Изменения', Icon: GitCompare },
  tasks: { title: 'Задачи', Icon: ListTodo },
  team: { title: 'Команда', Icon: Users },
  terminal: { title: 'Терминал', Icon: SquareTerminal },
  preview: { title: 'Preview', Icon: MonitorPlay },
};

// Рельса разбита на две группы, разделённые сепаратором. Сверху — инструменты
// ПРОЕКТА (файлы, изменения, задачи, команда, терминал, preview), снизу — панели
// ТЕКУЩЕЙ СЕССИИ (пока только План). Порядок: проектные раньше сессионных.
const PROJECT_RAIL_KEYS: PanelKey[] = ['files', 'changes', 'tasks', 'team', 'terminal', 'preview'];
const SESSION_RAIL_KEYS: PanelKey[] = ['plan'];

const GAP = 8; // зазор между карточками — та самая «воздушность»

interface Props {
  session: Session | null;
  projectId?: string;
  rootPath?: string;
  // Планшет: упрощённый режим — всегда одна панель (эфемерный solo, локальный стейт,
  // десктопная раскладка layout НЕ трогается), без DnD/колонок/сворачивания
  isTablet?: boolean;
  // Терминал и Preview доступны только при включённых инструментах проекта
  toolsEnabled?: boolean;
  // Готовый контент панелек (кроме Плана — он собирается здесь из артефактов сессии).
  // Строится в WorkspacePage, где живут состояние и обработчики этих инструментов.
  panels: Partial<Record<Exclude<PanelKey, 'plan'>, ReactNode>>;
  // Контролы в шапку карточки (слева от fullscreen/close) — напр. переключатель
  // видов задач. Собираются в WorkspacePage, состояние живёт там же.
  panelHeaderExtras?: Partial<Record<PanelKey, ReactNode>>;
}

// Вертикальный разделитель между колонками (и по краям зоны): в покое — пустой
// зазор GAP, во время drag-and-drop панели — дроп-зона для выноса в НОВУЮ колонку
// (расширяется, пунктирная направляющая; при наведении — акцентная)
function ColumnSep({ dndActive, over, onDragOver, onDragLeave, onDrop }: {
  dndActive: boolean;
  over: boolean;
  onDragOver: (e: DragEvent) => void;
  onDragLeave: () => void;
  onDrop: (e: DragEvent) => void;
}) {
  return (
    <div
      onDragOver={onDragOver}
      onDragLeave={onDragLeave}
      onDrop={onDrop}
      style={{
        width: dndActive ? 22 : GAP, flexShrink: 0, alignSelf: 'stretch',
        display: 'flex', alignItems: 'stretch', justifyContent: 'center',
        transition: 'width 0.1s ease-out', padding: dndActive ? '2px 0' : 0, boxSizing: 'border-box',
      }}
    >
      {dndActive && (
        <div style={{
          width: 2, borderRadius: 2, margin: '0 auto',
          background: over ? C.accent : 'transparent',
          borderLeft: over ? 'none' : `1px dashed ${C.textMuted}`,
          opacity: over ? 1 : 0.5,
          transition: 'background 0.12s ease, opacity 0.12s ease',
        }} />
      )}
    </div>
  );
}

// Горизонтальный плейсхолдер вставки внутри колонки (над/между/под панелями):
// появляется только во время drag-and-drop — пунктирная направляющая,
// при наведении — акцентная (парный к вертикальному ColumnSep)
function RowSep({ dndActive, over, onDragOver, onDragLeave, onDrop }: {
  dndActive: boolean;
  over: boolean;
  onDragOver: (e: DragEvent) => void;
  onDragLeave: () => void;
  onDrop: (e: DragEvent) => void;
}) {
  return (
    <div
      onDragOver={onDragOver}
      onDragLeave={onDragLeave}
      onDrop={onDrop}
      style={{
        height: dndActive ? 18 : 0, flexShrink: 0,
        display: 'flex', alignItems: 'center', justifyContent: 'stretch',
        transition: 'height 0.1s ease-out', padding: dndActive ? '0 2px' : 0, boxSizing: 'border-box',
        overflow: 'hidden',
      }}
    >
      {dndActive && (
        <div style={{
          height: 2, borderRadius: 2, flex: 1,
          background: over ? C.accent : 'transparent',
          borderTop: over ? 'none' : `1px dashed ${C.textMuted}`,
          opacity: over ? 1 : 0.5,
          transition: 'background 0.12s ease, opacity 0.12s ease', boxSizing: 'border-box',
        }} />
      )}
    </div>
  );
}

// Невидимый хендл ресайза высот в зазоре между карточками колонки:
// в покое пустой, на hover/drag показывает короткий grip по центру
function GapHandle({ active, onPointerDown }: { active: boolean; onPointerDown: (e: ReactPointerEvent) => void }) {
  const [hover, setHover] = useState(false);
  const show = hover || active;
  return (
    <div
      onPointerDown={onPointerDown}
      onMouseEnter={() => setHover(true)}
      onMouseLeave={() => setHover(false)}
      style={{
        height: GAP, flexShrink: 0, cursor: 'row-resize', touchAction: 'none',
        display: 'flex', alignItems: 'center', justifyContent: 'center',
      }}
    >
      <div style={{
        width: 34, height: 4, borderRadius: 3, background: C.accent,
        opacity: show ? 1 : 0, transition: 'opacity 0.15s ease', pointerEvents: 'none',
        display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 3,
      }}>
        {[0, 1, 2].map(i => <span key={i} style={{ width: 2, height: 2, borderRadius: '50%', background: C.onAccent }} />)}
      </div>
    </div>
  );
}

// Карточка панельки: скруглённая, с шапкой 40px (drag-хендл для перестановки),
// кнопками fullscreen и закрытия
function PanelShell({ k, badge, headerExtras, fullscreen, canDrag, onToggleFullscreen, onClose, dragged, dropTarget, onDragStart, onDragEnd, onDragOver, onDragLeave, onDrop, children }: {
  k: PanelKey;
  badge: string | null;
  // Кастомные контролы в шапке (напр. переключатель видов задач) — слева от кнопок
  headerExtras?: ReactNode;
  fullscreen: boolean;
  // Solo-режим: одна панель без перетаскивания — шапка не drag-хендл
  canDrag: boolean;
  onToggleFullscreen: () => void;
  onClose: () => void;
  dragged: boolean;
  dropTarget: boolean;
  onDragStart: (e: DragEvent) => void;
  onDragEnd: () => void;
  onDragOver: (e: DragEvent) => void;
  onDragLeave: () => void;
  onDrop: (e: DragEvent) => void;
  children: ReactNode;
}) {
  const { title, Icon } = PANEL_META[k];
  // Плавное появление карточки при открытии/переносе: лёгкий fade + подъём
  const [mounted, setMounted] = useState(false);
  useEffect(() => {
    const id = requestAnimationFrame(() => setMounted(true));
    return () => cancelAnimationFrame(id);
  }, []);
  const iconBtn = (onClick: () => void, btnTitle: string, icon: ReactNode) => (
    <button
      onClick={onClick}
      title={btnTitle}
      style={{
        width: 26, height: 26, border: 'none', borderRadius: R.sm, background: 'transparent',
        cursor: 'pointer', display: 'flex', alignItems: 'center', justifyContent: 'center',
        color: C.textMuted, flexShrink: 0,
      }}
    >
      {icon}
    </button>
  );
  return (
    <div
      onDragOver={onDragOver}
      onDragLeave={onDragLeave}
      onDrop={onDrop}
      style={{
        flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column', overflow: 'hidden',
        background: C.bgPanel, border: `1px solid ${dropTarget ? C.accent : C.borderLight}`,
        borderRadius: R.xxl, boxShadow: dropTarget ? `0 0 0 1px ${C.accent}` : fullscreen ? SHADOW.modal : SHADOW.card,
        opacity: dragged ? 0.5 : mounted ? 1 : 0,
        transform: mounted ? 'translateY(0) scale(1)' : 'translateY(5px) scale(0.99)',
        transition: 'border-color 0.1s, box-shadow 0.1s, opacity 0.12s ease-out, transform 0.12s ease-out',
      }}
    >
      <div
        draggable={canDrag && !fullscreen}
        onDragStart={onDragStart}
        onDragEnd={onDragEnd}
        title={canDrag && !fullscreen ? 'Перетащите, чтобы поменять панели местами' : undefined}
        style={{
          flexShrink: 0, height: 40, display: 'flex', alignItems: 'center', gap: 7,
          padding: '0 6px 0 12px', borderBottom: `1px solid ${C.border}`,
          // Шапка чуть утоплена относительно тела карточки — читается как заголовочная зона
          background: C.bgInset,
          cursor: canDrag && !fullscreen ? 'grab' : 'default',
        }}
      >
        <Icon size={15} strokeWidth={ICON_STROKE} color={C.textSecondary} style={{ flexShrink: 0 }} />
        <span style={{ fontFamily: FONT.sans, fontSize: 13, fontWeight: 600, color: C.textHeading, flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
          {title}
        </span>
        {badge && (
          <span style={{
            flexShrink: 0, fontFamily: FONT.mono, fontSize: 10.5, fontWeight: 600,
            padding: '2px 7px', borderRadius: R.sm, color: C.textSecondary, background: C.bgInset,
          }}>
            {badge}
          </span>
        )}
        {/* Контролы шапки (переключатель видов и т.п.): draggable=false, чтобы взаимодействие
            с ними не инициировало перетаскивание карточки за шапку */}
        {headerExtras && (
          <span
            draggable={false}
            onDragStart={e => e.preventDefault()}
            style={{ flexShrink: 0, display: 'flex', alignItems: 'center' }}
          >
            {headerExtras}
          </span>
        )}
        {iconBtn(onToggleFullscreen, fullscreen ? 'Свернуть к раскладке' : 'Развернуть на всю область',
          fullscreen ? <Minimize2 size={13} strokeWidth={ICON_STROKE} /> : <Maximize2 size={13} strokeWidth={ICON_STROKE} />)}
        {iconBtn(onClose, 'Скрыть панель', <X size={14} strokeWidth={ICON_STROKE} />)}
      </div>
      <div style={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
        {children}
      </div>
    </div>
  );
}

export function RightPanelStack({ session, projectId, rootPath, isTablet, toolsEnabled, panels, panelHeaderExtras }: Props) {
  const { layout, weights, width, fullscreen, mode, toggle, close, collapsed, toggleCollapsed, setWeights, setWidth, moveTo, moveToNewColumn, moveAt, setFullscreen, setMode } = usePanelStack();
  const windowWidth = useWindowWidth();
  // Планшет: до ДВУХ панелей стеком в одной колонке; выбор локальный эфемерный —
  // десктопный layout не трогаем. Третья открытая вытесняет самую старую (FIFO).
  const [tabletPanels, setTabletPanels] = useState<PanelKey[]>([]);
  const tabletInline = windowWidth >= TABLET_INLINE_MIN;
  const sessionId = session?.id ?? null;
  // Артефакты сессии нужны только Плану (бейдж на рельсе + содержимое панельки)
  const artifacts = useSessionArtifacts(sessionId, projectId, rootPath ?? '', null);
  const plansCount = artifacts.plans.length;

  // Терминал/Preview скрыты при выключенных инструментах проекта
  const keyAvailable = (k: PanelKey): boolean =>
    (k !== 'terminal' && k !== 'preview') || !!toolsEnabled;
  const soloMode = mode === 'solo';
  // Состояние ЕДИНОЕ для обоих режимов: в solo layout содержит максимум одну
  // панель (toggle заменяет её), поэтому рендер одинаковый.
  // На планшете колонки из layout не рендерятся — там свой стек до двух панелей.
  const columns = isTablet ? [] : layout.map(col => col.filter(keyAvailable)).filter(col => col.length > 0);
  const tabletKeys = isTablet ? tabletPanels.filter(keyAvailable) : [];
  const openKeys = isTablet ? tabletKeys : columns.flat();
  const fsKey = fullscreen && openKeys.includes(fullscreen) ? fullscreen : null;

  // Сдвиг FAB AI-хаба к зоне чата: правую кромку занимают рельса и панели —
  // пробрасываем их суммарную ширину в глобальную переменную (читает AiLauncher).
  // Drawer на планшете не считаем — он overlay и живёт поверх контента сам.
  // Позиция меняется МГНОВЕННО (переменная не анимируется, см. index.css) —
  // кнопка просто оказывается на новом месте, без движения и миганий.
  const rightZoneW = RAIL_W + (isTablet
    ? (tabletKeys.length > 0 && tabletInline ? width + GAP * 2 : 0)
    : (columns.length > 0 ? columns.length * width + GAP * (columns.length + 1) : 0));
  useEffect(() => {
    document.documentElement.style.setProperty('--cc-fab-right', `${rightZoneW + 20}px`);
    return () => { document.documentElement.style.removeProperty('--cc-fab-right'); };
  }, [rightZoneW]);

  // Подсветка активного ресайза: 'width' — сплиттер зоны, 'ci:ri' — хендл высот
  const [dragging, setDragging] = useState<'width' | string | null>(null);
  // Drag-and-drop перестановки: какая панель тащится, над какой висит,
  // и над каким разделителем колонок (дроп туда = вынос в новую колонку)
  const [dndFrom, setDndFrom] = useState<PanelKey | null>(null);
  const [dndOver, setDndOver] = useState<PanelKey | null>(null);
  const [dndOverSep, setDndOverSep] = useState<number | null>(null);
  // Горизонтальный плейсхолдер под курсором: 'ci:ri'
  const [dndOverRow, setDndOverRow] = useState<string | null>(null);
  const panelRefs = useRef<Partial<Record<PanelKey, HTMLDivElement | null>>>({});

  // Drag ширины зоны: тянем влево — колонки растут; width хранится на ОДНУ колонку
  const handleWidthDrag = (e: ReactPointerEvent) => {
    e.preventDefault();
    const startX = e.clientX;
    const startW = width;
    const nCols = Math.max(1, columns.length);
    const onMove = (ev: PointerEvent) => { setWidth(startW - (ev.clientX - startX) / nCols); };
    const onUp = () => {
      document.removeEventListener('pointermove', onMove);
      document.removeEventListener('pointerup', onUp);
      document.body.style.cursor = '';
      document.body.style.userSelect = '';
      setDragging(null);
    };
    document.body.style.cursor = 'col-resize';
    document.body.style.userSelect = 'none';
    document.addEventListener('pointermove', onMove);
    document.addEventListener('pointerup', onUp);
    setDragging('width');
  };

  // Drag хендла высот между соседними панелями aKey/bKey (в колонке десктопа
  // или в планшетном стеке): пиксельные высоты пары на старте → пересчёт весов
  // с клампом PANEL_MIN_H. tag — метка для подсветки активного хендла.
  const handleRowDrag = (aKey: PanelKey, bKey: PanelKey, tag: string) => (e: ReactPointerEvent) => {
    e.preventDefault();
    const aEl = panelRefs.current[aKey];
    const bEl = panelRefs.current[bKey];
    if (!aEl || !bEl) return;
    const startY = e.clientY;
    const ha = aEl.getBoundingClientRect().height;
    const hb = bEl.getBoundingClientRect().height;
    const wa = weights[aKey] ?? 1;
    const wb = weights[bKey] ?? 1;
    const onMove = (ev: PointerEvent) => {
      const dy = ev.clientY - startY;
      const haNext = Math.max(PANEL_MIN_H, Math.min(ha + hb - PANEL_MIN_H, ha + dy));
      const waNext = (wa + wb) * (haNext / (ha + hb));
      setWeights({ [aKey]: waNext, [bKey]: (wa + wb) - waNext });
    };
    const onUp = () => {
      document.removeEventListener('pointermove', onMove);
      document.removeEventListener('pointerup', onUp);
      document.body.style.cursor = '';
      document.body.style.userSelect = '';
      setDragging(null);
    };
    document.body.style.cursor = 'row-resize';
    document.body.style.userSelect = 'none';
    document.addEventListener('pointermove', onMove);
    document.addEventListener('pointerup', onUp);
    setDragging(tag);
  };

  const panelContent = (k: PanelKey): ReactNode => {
    if (k === 'plan') {
      return plansCount > 0
        ? <PlanSection plans={artifacts.plans} projectId={projectId} />
        : (
          <div style={{ padding: '20px 14px', fontFamily: FONT.sans, fontSize: 12.5, color: C.textMuted, textAlign: 'center' }}>
            План появится после ExitPlanMode в чате
          </div>
        );
    }
    return panels[k] ?? null;
  };

  // Панелька в раскладке или fullscreen-оверлеем. Контент НЕ дублируется:
  // в fullscreen тот же wrapper вырывается absolute'ом из потока (см. стиль ниже) —
  // важно для терминала: xterm остаётся смонтированным, буфер не теряется.
  // Absolute позиционируется от body-div WorkspacePage (position:relative), рельса не перекрывается.
  const renderPanel = (k: PanelKey) => {
    const isFs = fsKey === k;
    return (
      <div
        key={k}
        ref={el => { panelRefs.current[k] = el; }}
        style={isFs
          ? { position: 'absolute', top: GAP, left: GAP, right: RAIL_W + GAP, bottom: GAP, zIndex: 15, display: 'flex', flexDirection: 'column', overflow: 'hidden' }
          : {
              flex: `${weights[k] ?? 1} 1 0`, minHeight: PANEL_MIN_H, display: 'flex', flexDirection: 'column', overflow: 'hidden',
              // Быстрое перераспределение высот при открытии/закрытии соседей;
              // во время ручного drag хендла — без transition, чтобы не отставать от курсора
              transition: dragging == null ? 'flex-grow 0.15s ease-out' : 'none',
            }}
      >
        <PanelShell
          k={k}
          badge={k === 'plan' && plansCount > 1 ? `${plansCount}` : null}
          headerExtras={panelHeaderExtras?.[k]}
          fullscreen={isFs}
          canDrag={!soloMode && !isTablet}
          onToggleFullscreen={() => setFullscreen(isFs ? null : k)}
          onClose={() => { if (isTablet) setTabletPanels(cur => cur.filter(x => x !== k)); else close(k); }}
          dragged={dndFrom === k}
          dropTarget={dndOver === k && dndFrom !== null && dndFrom !== k}
          onDragStart={e => { setDndFrom(k); e.dataTransfer.effectAllowed = 'move'; e.dataTransfer.setData('text/plain', k); }}
          onDragEnd={() => { setDndFrom(null); setDndOver(null); setDndOverSep(null); setDndOverRow(null); }}
          onDragOver={e => { if (dndFrom && dndFrom !== k) { e.preventDefault(); e.dataTransfer.dropEffect = 'move'; setDndOver(k); } }}
          onDragLeave={() => { setDndOver(cur => (cur === k ? null : cur)); }}
          onDrop={e => { e.preventDefault(); if (dndFrom && dndFrom !== k) moveTo(dndFrom, k); setDndFrom(null); setDndOver(null); }}
        >
          {panelContent(k)}
        </PanelShell>
      </div>
    );
  };

  // Видимость иконки на рельсе. Сессионные кнопки (пока только План) показываются
  // ТОЛЬКО когда есть что открывать: без планов иконка Плана скрыта целиком
  // (а не дизейблится) — вместе с ней прячется и разделитель групп.
  const railKeyVisible = (k: PanelKey): boolean => {
    if (!keyAvailable(k)) return false;
    if (k === 'plan') return plansCount > 0 || openKeys.includes(k);
    return true;
  };

  // Одна иконка рельсы (используется обеими группами: проектной и сессионной).
  const renderRailIcon = (k: PanelKey): ReactNode => {
    if (!railKeyVisible(k)) return null;
    const isOpen = openKeys.includes(k);
    const { title, Icon } = PANEL_META[k];
    return (
      <div key={k}>
        <ToolbarIconButton
          onClick={() => {
            if (isTablet) {
              setFullscreen(null);
              // До двух панелей: третья вытесняет самую старую (FIFO)
              setTabletPanels(cur => cur.includes(k) ? cur.filter(x => x !== k) : [...cur, k].slice(-2));
            } else toggle(k);
          }}
          title={title} active={isOpen}>
          <div style={{ position: 'relative', display: 'flex' }}>
            <Icon size={17} strokeWidth={ICON_STROKE} />
            {k === 'plan' && plansCount > 0 && (
              <span style={{
                position: 'absolute', top: -6, right: -7, minWidth: 14, height: 14, padding: '0 3px',
                borderRadius: 7, background: C.accent, color: C.onAccent,
                fontFamily: FONT.sans, fontSize: 9, fontWeight: 700, lineHeight: '14px', textAlign: 'center',
              }}>
                {plansCount}
              </span>
            )}
          </div>
        </ToolbarIconButton>
      </div>
    );
  };

  return (
    <>
      {/* Планшет: стек до двух панелей — в потоке на широком экране, drawer поверх
          на узком; между двумя панелями — хендл ресайза высот */}
      {isTablet && tabletKeys.length > 0 && (() => {
        const stack = (
          <div style={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
            {tabletKeys.map((k, ri) => (
              <div key={k} style={{ display: 'contents' }}>
                {ri > 0 && fsKey !== k && fsKey !== tabletKeys[ri - 1] && (
                  <GapHandle active={dragging === 'tablet'} onPointerDown={handleRowDrag(tabletKeys[ri - 1], k, 'tablet')} />
                )}
                {renderPanel(k)}
              </div>
            ))}
          </div>
        );
        return tabletInline ? (
          <>
            <Splitter orientation="v" active={dragging === 'width'} onMouseDown={handleWidthDrag} />
            <div style={{ width: width + GAP * 2, flexShrink: 0, display: 'flex', padding: GAP, boxSizing: 'border-box' }}>
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
          <Splitter orientation="v" active={dragging === 'width'} onMouseDown={handleWidthDrag} />
          <div style={{
            width: columns.length * width + (dndFrom ? 22 : GAP) * (columns.length + 1),
            flexShrink: 0, display: 'flex', padding: `${GAP}px 0`,
            overflow: 'hidden', boxSizing: 'border-box',
            transition: dragging === 'width' ? 'none' : 'width 0.15s ease-out',
          }}>
            {columns.map((col, ci) => (
              <div key={ci} style={{ display: 'contents' }}>
                <ColumnSep
                  dndActive={dndFrom !== null}
                  over={dndOverSep === ci}
                  onDragOver={e => { if (dndFrom) { e.preventDefault(); e.dataTransfer.dropEffect = 'move'; setDndOverSep(ci); } }}
                  onDragLeave={() => setDndOverSep(cur => (cur === ci ? null : cur))}
                  onDrop={e => { e.preventDefault(); if (dndFrom) moveToNewColumn(dndFrom, ci); setDndFrom(null); setDndOver(null); setDndOverSep(null); }}
                />
                <div style={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
                  {(() => {
                    // Горизонтальный плейсхолдер вставки на позицию ri колонки ci
                    const rowSep = (ri: number) => (
                      <RowSep
                        key={`sep-${ri}`}
                        dndActive={dndFrom !== null}
                        over={dndOverRow === `${ci}:${ri}`}
                        onDragOver={e => { if (dndFrom) { e.preventDefault(); e.dataTransfer.dropEffect = 'move'; setDndOverRow(`${ci}:${ri}`); } }}
                        onDragLeave={() => setDndOverRow(cur => (cur === `${ci}:${ri}` ? null : cur))}
                        onDrop={e => { e.preventDefault(); if (dndFrom) moveAt(dndFrom, ci, ri); setDndFrom(null); setDndOver(null); setDndOverSep(null); setDndOverRow(null); }}
                      />
                    );
                    return (
                      <>
                        {dndFrom !== null && rowSep(0)}
                        {col.map((k, ri) => (
                          <div key={k} style={{ display: 'contents' }}>
                            {ri > 0 && fsKey !== k && fsKey !== col[ri - 1] && (
                              dndFrom !== null
                                ? rowSep(ri)
                                : <GapHandle active={dragging === `${ci}:${ri}`} onPointerDown={handleRowDrag(col[ri - 1], k, `${ci}:${ri}`)} />
                            )}
                            {renderPanel(k)}
                          </div>
                        ))}
                        {dndFrom !== null && rowSep(col.length)}
                      </>
                    );
                  })()}
                </div>
              </div>
            ))}
            <ColumnSep
              dndActive={dndFrom !== null}
              over={dndOverSep === columns.length}
              onDragOver={e => { if (dndFrom) { e.preventDefault(); e.dataTransfer.dropEffect = 'move'; setDndOverSep(columns.length); } }}
              onDragLeave={() => setDndOverSep(cur => (cur === columns.length ? null : cur))}
              onDrop={e => { e.preventDefault(); if (dndFrom) moveToNewColumn(dndFrom, columns.length); setDndFrom(null); setDndOver(null); setDndOverSep(null); }}
            />
          </div>
        </>
      )}

      {/* Рельса иконок — видна всегда. Высота ПО КОНТЕНТУ (alignSelf: flex-start),
          поэтому низ идёт сразу под последней иконкой, сколько бы их ни было.
          Низ прямой горизонтальный, скруглён только нижне-левый угол; правый угол
          прямой (примыкает к краю окна). Ниже рельсы проступает фон рабочей области. */}
      <div style={{
        width: RAIL_W, flexShrink: 0, alignSelf: 'flex-start',
        display: 'flex', flexDirection: 'column', alignItems: 'center',
        gap: 6, paddingTop: 8, paddingBottom: 16, background: C.bgPanel,
        borderLeft: `1px solid ${C.border}`, borderBottom: `1px solid ${C.border}`,
        borderBottomLeftRadius: 26, borderBottomRightRadius: 0,
        boxSizing: 'border-box', overflow: 'hidden',
      }}>
        {/* Переключатель режима зоны: раскладка колонками (дефолт) ↔ одна панель.
            На планшете скрыт — там всегда одна панель */}
        {!isTablet && (
          <>
            <ToolbarIconButton
              onClick={() => setMode(soloMode ? 'multi' : 'solo')}
              title={soloMode ? 'Одна панель — нажмите для раскладки колонками' : 'Раскладка колонками — нажмите для режима одной панели'}
            >
              {soloMode
                ? <Square size={15} strokeWidth={ICON_STROKE} />
                : <Columns2 size={15} strokeWidth={ICON_STROKE} />}
            </ToolbarIconButton>
            <div style={{ width: 22, height: 1, background: C.border, flexShrink: 0, margin: '1px 0 2px' }} />
          </>
        )}
        {/* Инструменты ПРОЕКТА (первыми) */}
        {PROJECT_RAIL_KEYS.map(renderRailIcon)}
        {/* Разделитель групп: проектные ↔ сессионные. Прячется, когда сессионных
            кнопок нет (напр. Плана без планов) — по railKeyVisible, не keyAvailable */}
        {PROJECT_RAIL_KEYS.some(railKeyVisible) && SESSION_RAIL_KEYS.some(railKeyVisible) && (
          <div style={{ width: 22, height: 1, background: C.border, flexShrink: 0, margin: '2px 0' }} />
        )}
        {/* Панели ТЕКУЩЕЙ СЕССИИ (после проектных) — пока только План */}
        {SESSION_RAIL_KEYS.map(renderRailIcon)}
        {/* Под иконками панелей, через сепаратор: свернуть все / вернуть набор как был.
            На планшете скрыта — панель одна, закрывается своей же иконкой */}
        {!isTablet && (
          <>
            <div style={{ width: 22, height: 1, background: C.border, flexShrink: 0, margin: '2px 0 1px' }} />
            {(() => {
              const collapseDisabled = openKeys.length === 0 && !collapsed;
              return (
                <div style={{ opacity: collapseDisabled ? 0.3 : 1 }}>
                  <ToolbarIconButton
                    onClick={toggleCollapsed}
                    disabled={collapseDisabled}
                    title={collapsed ? 'Открыть свёрнутые панели' : 'Свернуть все панели'}
                  >
                    <div style={{ display: 'flex', color: collapseDisabled ? C.textMuted : undefined }}>
                      {collapsed
                        ? <ChevronsLeft size={16} strokeWidth={ICON_STROKE} />
                        : <ChevronsRight size={16} strokeWidth={ICON_STROKE} />}
                    </div>
                  </ToolbarIconButton>
                </div>
              );
            })()}
          </>
        )}
      </div>
    </>
  );
}
