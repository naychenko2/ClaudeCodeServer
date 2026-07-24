// Правая зона нового интерфейса проекта (workspace-cc-panels): вертикальная рельса
// иконок РАБОЧИХ ИНСТРУМЕНТОВ у правого края + открытые панели-карточки.
// Раскладка — ЯВНЫЕ колонки (как в Claude Code Desktop): дефолт «по две на колонку»
// в порядке открытия, drag-and-drop за шапку переносит панель в колонку цели
// (вставка перед ней) — можно получить любое распределение, например одну панель
// в первой колонке и две во второй.
// Панели — «воздушные» скруглённые карточки с зазорами; границы высот тянутся
// невидимыми хендлами в зазорах, ширина колонок — сплиттером слева от зоны.
import { useEffect, useRef, useState, type ReactNode, type DragEvent, type PointerEvent as ReactPointerEvent } from 'react';
import { X, Columns2, Square, ChevronsRight, ChevronsLeft, ClipboardList, FolderTree, GitCompare, ListTodo, Bot, User, Users, SquareTerminal, MonitorPlay, type LucideIcon } from 'lucide-react';
import type { Session } from '../../types';
import { C, FONT, ISLAND, R, SHADOW } from '../../lib/design';
import { ICON_STROKE } from '../../components/ui/icons';
import { Island, IslandHeader } from '../../components/ui/Island';
import { IslandSplitter } from '../../components/ui/IslandSplitter';
import { ToolbarIconButton } from '../../components/Toolbar';
import { useSessionArtifacts } from '../../hooks/useSessionArtifacts';
import { PlanSection } from '../../components/artifacts/PlanSection';
import { AgentsSection } from '../../components/artifacts/AgentsSection';
import { ContextSection } from '../../components/artifacts/ContextSection';
import { panelBadge } from '../../components/artifacts/meta';
import { useWindowWidth } from '../../lib/breakpoints';
import { wsPanelStack, PANEL_MIN_H, RAIL_W, type PanelKey, type PanelStack } from './panelStackState';

// Порог планшета: шире — панель в потоке рядом с чатом, уже — drawer поверх
const TABLET_INLINE_MIN = 1000;

// Иконки и заголовки панелей рельсы
const PANEL_META: Record<PanelKey, { title: string; Icon: LucideIcon }> = {
  plan: { title: 'План', Icon: ClipboardList },
  agents: { title: 'Агенты', Icon: Bot },
  // 'context' — досье персоны-собеседника (память/привязки/recall); отображается «Персона».
  context: { title: 'Персона', Icon: User },
  files: { title: 'Файлы', Icon: FolderTree },
  changes: { title: 'Изменения', Icon: GitCompare },
  tasks: { title: 'Задачи', Icon: ListTodo },
  team: { title: 'Команда', Icon: Users },
  terminal: { title: 'Терминал', Icon: SquareTerminal },
  preview: { title: 'Preview', Icon: MonitorPlay },
};

// Рельса разбита на две группы, разделённые сепаратором. Сверху — инструменты
// ПРОЕКТА (файлы, изменения, задачи, команда, терминал, preview), снизу — панели
// ТЕКУЩЕЙ СЕССИИ (План, Агенты, Персона). Порядок: проектные раньше сессионных.
const PROJECT_RAIL_KEYS: PanelKey[] = ['files', 'changes', 'tasks', 'team', 'terminal', 'preview'];
const SESSION_RAIL_KEYS: PanelKey[] = ['plan', 'agents', 'context'];

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
  panels?: Partial<Record<Exclude<PanelKey, 'plan'>, ReactNode>>;
  // Контролы в шапку карточки (слева от кнопки закрытия) — напр. переключатель
  // видов задач. Собираются в WorkspacePage, состояние живёт там же.
  panelHeaderExtras?: Partial<Record<PanelKey, ReactNode>>;
  // Числа-кружки на кнопках ПРОЕКТА (changes/tasks/terminal/preview) — считаются в
  // WorkspacePage (там живут данные git/задач/терминалов/сервисов). Сессионные кнопки
  // свои числа берут из артефактов сессии (railBadgeCount), не отсюда.
  railCounts?: Partial<Record<PanelKey, number>>;
}

// Ширина/высота дроп-зоны сепаратора при перетаскивании (только оверлей, в потоке
// места не занимает — иначе панели ужимались бы на время DnD)
const SEP_HIT = 22;

// Вертикальный разделитель между колонками (и по краям зоны): в потоке всегда
// занимает ровно `base` px (в покое и при DnD — одинаково), а дроп-зона выноса в
// НОВУЮ колонку рисуется absolute-оверлеем поверх зазора: пунктирная направляющая,
// при наведении — акцентная. Так раскладка при перетаскивании не «дышит».
function ColumnSep({ dndActive, over, base = GAP, onDragOver, onDragLeave, onDrop }: {
  dndActive: boolean;
  over: boolean;
  base?: number;
  onDragOver: (e: DragEvent) => void;
  onDragLeave: () => void;
  onDrop: (e: DragEvent) => void;
}) {
  return (
    <div style={{ width: base, flexShrink: 0, alignSelf: 'stretch', position: 'relative' }}>
      {dndActive && (
        <div
          onDragOver={onDragOver}
          onDragLeave={onDragLeave}
          onDrop={onDrop}
          style={{
            position: 'absolute', top: 0, bottom: 0, left: (base - SEP_HIT) / 2, width: SEP_HIT, zIndex: 5,
            display: 'flex', alignItems: 'stretch', justifyContent: 'center',
            padding: '2px 0', boxSizing: 'border-box',
          }}
        >
          <div style={{
            width: 2, borderRadius: 2, margin: '0 auto',
            background: over ? C.accent : 'transparent',
            borderLeft: over ? 'none' : `1px dashed ${C.textMuted}`,
            opacity: over ? 1 : 0.5,
            transition: 'background 0.12s ease, opacity 0.12s ease',
          }} />
        </div>
      )}
    </div>
  );
}

// Горизонтальный плейсхолдер вставки внутри колонки (над/между/под панелями):
// парный к ColumnSep — в потоке занимает `base` px (0 по краям колонки, GAP между
// панелями, где он подменяет хендл ресайза), сама дроп-зона — absolute-оверлей
function RowSep({ dndActive, over, base = 0, onDragOver, onDragLeave, onDrop }: {
  dndActive: boolean;
  over: boolean;
  base?: number;
  onDragOver: (e: DragEvent) => void;
  onDragLeave: () => void;
  onDrop: (e: DragEvent) => void;
}) {
  return (
    <div style={{ height: base, flexShrink: 0, position: 'relative' }}>
      {dndActive && (
        <div
          onDragOver={onDragOver}
          onDragLeave={onDragLeave}
          onDrop={onDrop}
          style={{
            position: 'absolute', left: 0, right: 0, top: (base - SEP_HIT) / 2, height: SEP_HIT, zIndex: 5,
            display: 'flex', alignItems: 'center', justifyContent: 'stretch',
            padding: '0 2px', boxSizing: 'border-box',
          }}
        >
          <div style={{
            height: 2, borderRadius: 2, flex: 1,
            background: over ? C.accent : 'transparent',
            borderTop: over ? 'none' : `1px dashed ${C.textMuted}`,
            opacity: over ? 1 : 0.5,
            transition: 'background 0.12s ease, opacity 0.12s ease', boxSizing: 'border-box',
          }} />
        </div>
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

// Карточка панельки: скруглённая, с шапкой 40px (drag-хендл для перестановки)
// и кнопкой закрытия
function PanelShell({ k, badge, headerExtras, canDrag, onClose, dragged, dropTarget, onDragStart, onDragEnd, onDragOver, onDragLeave, onDrop, children }: {
  k: PanelKey;
  badge: string | null;
  // Кастомные контролы в шапке (напр. переключатель видов задач) — слева от кнопок
  headerExtras?: ReactNode;
  // Solo-режим: одна панель без перетаскивания — шапка не drag-хендл
  canDrag: boolean;
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
    <Island
      rootProps={{ onDragOver, onDragLeave, onDrop }}
      borderColor={dropTarget ? C.accent : ISLAND.border}
      shadow={dropTarget ? `0 0 0 1px ${C.accent}` : ISLAND.shadow}
      style={{
        flex: 1,
        opacity: dragged ? 0.5 : mounted ? 1 : 0,
        transform: mounted ? 'translateY(0) scale(1)' : 'translateY(5px) scale(0.99)',
        transition: 'border-color 0.1s, box-shadow 0.1s, opacity 0.12s ease-out, transform 0.12s ease-out',
      }}
    >
      <IslandHeader
        icon={<Icon size={15} strokeWidth={ICON_STROKE} color={C.textSecondary} style={{ flexShrink: 0 }} />}
        title={title}
        badge={badge}
        headerProps={{
          draggable: canDrag,
          onDragStart,
          onDragEnd,
          title: canDrag ? 'Перетащите, чтобы поменять панели местами' : undefined,
          style: { cursor: canDrag ? 'grab' : 'default' },
        }}
        actions={iconBtn(onClose, 'Скрыть панель', <X size={14} strokeWidth={ICON_STROKE} />)}
      >
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
      </IslandHeader>
      {/* Контентная зона панели — белая: отделяет рабочую область от кремовой шапки
          карточки и от фона страницы с дудл-паттерном */}
      <div style={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column', overflow: 'hidden', background: C.bgWhite }}>
        {children}
      </div>
    </Island>
  );
}

export function RightPanelStack({ session, projectId, rootPath, isTablet, isMobile, sessionOnly, panelStack, toolsEnabled, panels = {}, panelHeaderExtras, railCounts }: Props) {
  // Инстанс стора раскладки: оба объявлены на уровне модуля, поэтому вызов хука
  // безусловный и стабильный между рендерами (проп не меняется по ходу жизни экрана)
  const usePanels = (panelStack ?? wsPanelStack).use;
  const { layout, weights, width, mode, toggle, close, collapsed, toggleCollapsed, setWeights, setWidth, moveTo, moveToNewColumn, moveAt, setMode } = usePanels();
  const windowWidth = useWindowWidth();
  // Компактный режим (планшет и телефон): одна панель + drawer, без колонок/DnD/solo
  const compact = !!isTablet || !!isMobile;
  // Планшет: до ДВУХ панелей стеком в одной колонке; выбор локальный эфемерный —
  // десктопный layout не трогаем. Третья открытая вытесняет самую старую (FIFO).
  const [tabletPanels, setTabletPanels] = useState<PanelKey[]>([]);
  const tabletInline = windowWidth >= TABLET_INLINE_MIN;
  const sessionId = session?.id ?? null;
  // Артефакты сессии питают сессионную группу рельсы: План, Чек-лист (todos), Агенты
  // (бейджи + содержимое панелек). Персона (context) данные тянет сама через ContextSection.
  const artifacts = useSessionArtifacts(sessionId, projectId, rootPath ?? '', null);
  const plansCount = artifacts.plans.length;
  // Опции расчёта видимости/бейджей сессионных кнопок (единый источник — panelBadge из meta).
  // executingTask=false: в рельсе artifacts считаются без заголовка задачи-исполнителя.
  const badgeOpts = { executingTask: false, personaId: session?.personaId ?? null, isChat: !projectId };

  // Терминал/Preview скрыты при выключенных инструментах проекта
  const keyAvailable = (k: PanelKey): boolean =>
    (k !== 'terminal' && k !== 'preview') || !!toolsEnabled;
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
  const railKeyVisible = (k: PanelKey): boolean => {
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
  // Слагаемые зазоров: при открытой зоне — ресайз-сплиттер GAP + межколоночные/крайний
  // правый ColumnSep (GAP на колонку); при закрытой — marginLeft GAP самой рельсы
  const rightZoneW = railHidden ? 0 : RAIL_W + (compact
    ? (tabletKeys.length > 0 && tabletInline ? width + GAP * 3 : GAP)
    : (columns.length > 0 ? columns.length * width + GAP * (columns.length + 1) : GAP));
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

  // Пустой стейт панельки (когда открыта, но контента ещё нет)
  const emptyPanel = (text: string): ReactNode => (
    <div style={{ padding: '20px 14px', fontFamily: FONT.sans, fontSize: 12.5, color: C.textMuted, textAlign: 'center' }}>
      {text}
    </div>
  );

  const panelContent = (k: PanelKey): ReactNode => {
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
  const renderPanel = (k: PanelKey) => {
    return (
      <div
        key={k}
        ref={el => { panelRefs.current[k] = el; }}
        // overflow НЕ hidden: контент клипает сама карточка-остров (PanelShell),
        // а обёртке нельзя — иначе она срезает тень острова (ISLAND.shadow)
        style={{
          flex: `${weights[k] ?? 1} 1 0`, minHeight: PANEL_MIN_H, display: 'flex', flexDirection: 'column', minWidth: 0,
          // Быстрое перераспределение высот при открытии/закрытии соседей;
          // во время ручного drag хендла — без transition, чтобы не отставать от курсора
          transition: dragging == null ? 'flex-grow 0.15s ease-out' : 'none',
        }}
      >
        <PanelShell
          k={k}
          badge={
            k === 'plan'
              ? (plansCount > 1 ? `${plansCount}` : null)
              : (k === 'agents' || k === 'context')
                ? panelBadge(k, artifacts, badgeOpts).badge
                : null
          }
          headerExtras={panelHeaderExtras?.[k]}
          canDrag={!soloMode && !compact}
          onClose={() => { if (compact) setTabletPanels(cur => cur.filter(x => x !== k)); else close(k); }}
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

  // Число в кружке над иконкой кнопки. Сессионные — «сколько требует внимания» (не «всего»):
  // План — неодобренные (status ≠ approved), Чек-лист — не закрытые (≠ completed),
  // Агенты — открытые (running); Персона счётчика не имеет. Проектные (changes/tasks/
  // terminal/preview) берут готовое число из railCounts (считается в WorkspacePage).
  // 0 → кружок не рисуем.
  const railBadgeCount = (k: PanelKey): number | null => {
    let n = 0;
    if (k === 'plan') n = artifacts.plans.filter(p => p.status !== 'approved').length;
    else if (k === 'agents') n = [...artifacts.agents, ...artifacts.workflows.flatMap(w => w.agents)]
      .filter(a => a.status === 'running').length;
    else if (k === 'changes' || k === 'tasks' || k === 'terminal' || k === 'preview') n = railCounts?.[k] ?? 0;
    else return null; // context (Персона), files — без кружка
    return n > 0 ? n : null;
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
            if (compact) {
              // До двух панелей: третья вытесняет самую старую (FIFO)
              setTabletPanels(cur => cur.includes(k) ? cur.filter(x => x !== k) : [...cur, k].slice(-2));
            } else toggle(k);
          }}
          title={title} active={isOpen}>
          <div style={{ position: 'relative', display: 'flex' }}>
            <Icon size={17} strokeWidth={ICON_STROKE} />
            {(() => {
              const rc = railBadgeCount(k);
              return rc ? (
                <span style={{
                  position: 'absolute', top: -6, right: -7, minWidth: 14, height: 14, padding: '0 3px',
                  borderRadius: 7, background: C.accent, color: C.onAccent,
                  fontFamily: FONT.sans, fontSize: 9, fontWeight: 700, lineHeight: '14px', textAlign: 'center',
                }}>
                  {rc}
                </span>
              ) : null;
            })()}
          </div>
        </ToolbarIconButton>
      </div>
    );
  };

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
                  <GapHandle active={dragging === 'tablet'} onPointerDown={handleRowDrag(tabletKeys[ri - 1], k, 'tablet')} />
                )}
                {renderPanel(k)}
              </div>
            ))}
          </div>
        );
        return tabletInline ? (
          <>
            <IslandSplitter orientation="v" active={dragging === 'width'} onMouseDown={handleWidthDrag} />
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
          <IslandSplitter orientation="v" active={dragging === 'width'} onMouseDown={handleWidthDrag} />
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
            transition: dragging === 'width' ? 'none' : 'width 0.15s ease-out',
          }}>
            {columns.map((col, ci) => (
              <div key={ci} style={{ display: 'contents' }}>
                {/* Крайний левый сеп (ci=0) — только как дроп-зона при DnD: в покое
                    зазор от центра уже обеспечен ресайз-сплиттером зоны */}
                {(ci > 0 || dndFrom !== null) && (
                  <ColumnSep
                    dndActive={dndFrom !== null}
                    base={ci > 0 ? GAP : 0}
                    over={dndOverSep === ci}
                    onDragOver={e => { if (dndFrom) { e.preventDefault(); e.dataTransfer.dropEffect = 'move'; setDndOverSep(ci); } }}
                    onDragLeave={() => setDndOverSep(cur => (cur === ci ? null : cur))}
                    onDrop={e => { e.preventDefault(); if (dndFrom) moveToNewColumn(dndFrom, ci); setDndFrom(null); setDndOver(null); setDndOverSep(null); }}
                  />
                )}
                <div style={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column' }}>
                  {(() => {
                    // Горизонтальный плейсхолдер вставки на позицию ri колонки ci.
                    // base — место в потоке: по краям колонки 0 (в покое их нет),
                    // между панелями GAP (подменяет хендл ресайза той же высоты)
                    const rowSep = (ri: number, base = 0) => (
                      <RowSep
                        key={`sep-${ri}`}
                        dndActive={dndFrom !== null}
                        base={base}
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
                            {ri > 0 && (
                              dndFrom !== null
                                ? rowSep(ri, GAP)
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
          Скруглены оба левых угла (капсула у правого края окна); правые углы прямые.
          Когда слева от рельсы ЦЕНТР (панели закрыты / drawer) — зазор GAP, чтобы
          контент не прижимался; при открытой зоне зазор даёт её крайний ColumnSep. */}
      {!railHidden && <div style={{
        width: RAIL_W, flexShrink: 0, alignSelf: 'flex-start',
        display: 'flex', flexDirection: 'column', alignItems: 'center',
        // Тон шапок островов и сайдбаров — единая «оправа» интерфейса
        gap: 6, paddingTop: 12, paddingBottom: 16, background: C.bgMain,
        borderLeft: `1px solid ${C.border}`, borderTop: `1px solid ${C.border}`, borderBottom: `1px solid ${C.border}`,
        borderTopLeftRadius: 26, borderBottomLeftRadius: 26, borderTopRightRadius: 0, borderBottomRightRadius: 0,
        boxSizing: 'border-box', overflow: 'hidden',
        // Рельса — полукапсула-остров у края окна: тень как у остальных островов
        boxShadow: ISLAND.shadow,
        marginLeft: (compact ? !(tabletKeys.length > 0 && tabletInline) : columns.length === 0) ? GAP : 0,
      }}>
        {/* Переключатель режима зоны: раскладка колонками (дефолт) ↔ одна панель.
            В компактном режиме (планшет/телефон) скрыт — там всегда одна панель */}
        {!compact && (
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
        {/* Инструменты ПРОЕКТА (первыми). В sessionOnly (Чаты, мобилка) их нет —
            проекта там либо нет вовсе, либо инструменты живут в левых вкладках */}
        {!sessionOnly && PROJECT_RAIL_KEYS.map(renderRailIcon)}
        {/* Разделитель групп: проектные ↔ сессионные. Прячется, когда сессионных
            кнопок нет (напр. Плана без планов) — по railKeyVisible, не keyAvailable */}
        {!sessionOnly && PROJECT_RAIL_KEYS.some(railKeyVisible) && SESSION_RAIL_KEYS.some(railKeyVisible) && (
          <div style={{ width: 22, height: 1, background: C.border, flexShrink: 0, margin: '2px 0' }} />
        )}
        {/* Панели ТЕКУЩЕЙ СЕССИИ (после проектных): План, Агенты, Персона */}
        {SESSION_RAIL_KEYS.map(renderRailIcon)}
        {/* Под иконками панелей, через сепаратор: свернуть все / вернуть набор как был.
            В компактном режиме скрыта — панель одна, закрывается своей же иконкой */}
        {!compact && (
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
      </div>}
    </>
  );
}
