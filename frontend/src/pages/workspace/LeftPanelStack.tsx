// Левая рельса — зеркало RightPanelStack (минимальная версия).
//
// Рельса иконок СЛЕВА у левого края окна + открытые панели-карточки, растущие
// ВПРАВО от рельсы. Клик по иконке открывает/закрывает панель.
//
// Общее с правой зоной вынесено и НЕ дублируется: сама рельса — PanelRail
// (side="left" разворачивает капсулу и стрелки), направляющие мест вставки —
// PanelDropGuide, ресайз высот — хук usePanelRowResize, подписка на drag —
// startPointerDrag.
//
// Пока НЕ реализовано (план Этап 1.1 — полноценное зеркало):
//   - Multi-колонки (одна колонка, панели стакаются вертикально; инстансы стора
//     созданы с singleColumn, поэтому раскладка не разъезжается по колонкам)
//   - Планшетный drawer
//
// Базовая логика: toggle через стор (wsLeftPanelStack / chatLeftPanelStack),
// панель рендерится как PanelShell, закрывается кнопкой в шапке.
// Ширина панелей тянется сплиттером справа от зоны и живёт в том же сторе
// (width), что и у правой рельсы, — зеркально handleWidthDrag в RightPanelStack.
//
// sessionOnly=true — только chats (для раздела «Чаты» без проекта).
// sessionOnly=false — chats/files/tasks/personas (+ tools если toolsEnabled).
import { Fragment, useEffect, useState, type ReactNode } from 'react';
import { MessageCircle, FolderTree, ListTodo, Users, SquareTerminal, type LucideIcon } from 'lucide-react';
import { C } from '../../lib/design';
import { ICON_STROKE } from '../../components/ui/icons';
import { PanelShell } from '../../components/ui/PanelShell';
import { PanelRail, RAIL_W, RAIL_GAP, type RailItem } from '../../components/ui/PanelRail';
import { PanelDropGuide } from '../../components/ui/PanelDropGuide';
import { IslandSplitter } from '../../components/ui/IslandSplitter';
import { wsLeftPanelStack, type LeftPanelKey, type PanelStack } from './panelStackState';
import { usePanelDnd, usePanelRowResize, usePanelWidthDrag } from './panelZone';
import { PanelSlot } from './PanelSlot';

// Мета панелей левой рельсы: иконка + заголовок для шапки PanelShell и tooltip.
const LEFT_PANEL_META: Record<LeftPanelKey, { title: string; Icon: LucideIcon }> = {
  chats:    { title: 'Чаты',       Icon: MessageCircle },
  files:    { title: 'Файлы',      Icon: FolderTree },
  tasks:    { title: 'Задачи',     Icon: ListTodo },
  personas: { title: 'Команда',    Icon: Users },
  tools:    { title: 'Инструменты', Icon: SquareTerminal },
};

// Группа левых панелей: основные инструменты (всегда видны в воркспейсе)
const WORKSPACE_LEFT_KEYS: LeftPanelKey[] = ['chats', 'files', 'tasks', 'personas'];
// Tools доступен только при toolsEnabled проекта
const TOOLS_KEY: LeftPanelKey = 'tools';

interface Props {
  // Готовый контент панелек — caller (ChatsPage / WorkspacePage) собирает
  panels: Partial<Record<LeftPanelKey, ReactNode>>;
  // Бейджи-числа на иконках (напр. chats.length). Не обязательно.
  railCounts?: Partial<Record<LeftPanelKey, number>>;
  // Инстанс стора раскладки: воркспейс и «Чаты» держат НЕЗАВИСИМЫЕ раскладки
  panelStack?: { use: () => PanelStack };
  // sessionOnly — только chats (для раздела «Чаты»)
  sessionOnly?: boolean;
  // Терминал и Preview в правой рельсе; tools в левой — аналогично по флагу
  toolsEnabled?: boolean;
}

export function LeftPanelStack({ panels, railCounts, panelStack, sessionOnly = false, toolsEnabled = false }: Props) {
  const usePanels = (panelStack ?? wsLeftPanelStack).use;
  const { layout, mode, toggle, close, collapsed, toggleCollapsed, setMode, width, setWidth, weights, setWeights, swapWith, moveAt } = usePanels();
  const { panelRefs, rowDragging, handleRowDrag } = usePanelRowResize<LeftPanelKey>(weights, setWeights);
  // Ширина зоны: тянем ВПРАВО — панели растут (зеркально правой рельсе, где рост
  // идёт влево). Клампы COL_MIN/COL_MAX применяет сам стор.
  const { dragging, onPointerDown: handleWidthDrag } = usePanelWidthDrag(width, setWidth, 'left');

  // Позиция вставки: колонок слева нет, поэтому это просто индекс в стопке
  // (у правой рельсы там пара 'колонка:строка'). Сбрасывается вместе с DnD.
  const [dndOverRow, setDndOverRow] = useState<number | null>(null);

  // Какие иконки показывать в рельсе
  const visibleKeys: LeftPanelKey[] = sessionOnly
    ? ['chats']
    : [...WORKSPACE_LEFT_KEYS, ...(toolsEnabled ? [TOOLS_KEY] : [])];

  // Панели, у которых есть контент (panels[k] != null). Если ни у одной —
  // возвращаем null, рельса не рендерится вовсе.
  const availableKeys = visibleKeys.filter(k => panels[k] != null);

  // Открытые панели — только те, что available.
  const openKeys = layout.flat().filter(k => availableKeys.includes(k as LeftPanelKey)) as LeftPanelKey[];
  const soloMode = mode === 'solo';
  // Делить высоту между слотами и переставлять панели есть смысл только когда
  // их больше одной; в solo открыта ровно одна — переставлять нечего.
  const multiOpen = openKeys.length > 1;
  const dnd = usePanelDnd<LeftPanelKey>({
    enabled: multiOpen && !soloMode,
    onSwap: swapWith,
    onEnd: () => setDndOverRow(null),
  });

  // === ПРАВИЛО СКРЫТИЯ РЕЛЬСЫ ===
  // Если доступна только ОДНА панель (напр. sessionOnly → только chats) и она
  // ОТКРЫТА — рельса не нужна: панель сама показывает заголовок с иконкой.
  // Если панель ЗАКРЫТА — показываем рельсу с 1 иконкой (чтобы открыть обратно).
  // Если доступно >1 панелей — рельса всегда видна.
  const singlePanelMode = availableKeys.length === 1;
  const showRail = !singlePanelMode || openKeys.length === 0;

  // Сдвиг FAB AI-хаба: левая рельса занимает место слева — пробрасываем в CSS-переменную.
  // Слагаемые считаются ПО РАЗМЕТКЕ, слева направо: рельса + зазор до панелей +
  // сама зона + её ресайз-сплиттер. Когда панелей нет, тот же RAIL_GAP даёт
  // gapToCenter самой рельсы, так что зазор в сумме остаётся один.
  const leftZoneW = availableKeys.length === 0
    ? 0
    : RAIL_W + RAIL_GAP + (openKeys.length > 0 ? width + RAIL_GAP : 0);
  useEffect(() => {
    document.documentElement.style.setProperty('--cc-fab-left', `${leftZoneW + 20}px`);
    return () => { document.documentElement.style.removeProperty('--cc-fab-left'); };
  }, [leftZoneW]);

  // Ранний return — ПОСЛЕ всех хуков (useSyncExternalStore в usePanels, useEffect выше).
  // Если ни у одной панели нет контента — не рендерим рельсу вовсе.
  if (availableKeys.length === 0) return null;

  // Позиция вставки в РЕАЛЬНОЙ раскладке. openKeys отфильтрован по наличию
  // контента (panels[k] != null), поэтому индекс в видимой стопке может не
  // совпадать с индексом в layout — moveAt же работает с настоящим.
  const layoutIndexFor = (ri: number): number => {
    const col = layout[0] ?? [];
    if (ri >= openKeys.length) return col.length;
    const at = col.indexOf(openKeys[ri]);
    return at >= 0 ? at : col.length;
  };

  // Направляющая места вставки на позицию ri стопки. Колонка одна (стор левых
  // инстансов создан с singleColumn), поэтому colIdx у moveAt всегда 0.
  // base — место в потоке: по краям стопки 0 (в покое их нет), между панелями
  // RAIL_GAP — там направляющая подменяет хендл ресайза той же высоты.
  const dropGuide = (ri: number, base = 0, edge?: 'start' | 'end') => (
    <PanelDropGuide
      axis="y"
      key={`guide-${ri}`}
      dndActive={dnd.active}
      base={base}
      edge={edge}
      over={dndOverRow === ri}
      onDragOver={e => { if (dnd.from) { e.preventDefault(); e.dataTransfer.dropEffect = 'move'; setDndOverRow(ri); } }}
      onDragLeave={() => setDndOverRow(cur => (cur === ri ? null : cur))}
      onDrop={e => { e.preventDefault(); if (dnd.from) moveAt(dnd.from, 0, layoutIndexFor(ri)); dnd.end(); }}
    />
  );

  // Иконки рельсы — одной группой: в отличие от правой, левая панели по смыслу
  // не делит (там инструменты проекта отделены от панелей текущей сессии).
  const railItems: RailItem[] = availableKeys.map(k => ({
    key: k,
    title: LEFT_PANEL_META[k].title,
    Icon: LEFT_PANEL_META[k].Icon,
    active: openKeys.includes(k),
    badge: railCounts?.[k] ?? null,
    onClick: () => toggle(k),
  }));

  // Одна панель: PanelShell с иконкой/заголовком + контент из props.
  // При ЕДИНСТВЕННОЙ открытой панели высота — по контенту: короткий список чатов
  // не должен растягиваться на весь экран. Как только панелей две и больше,
  // высоту делят веса слотов — тогда между панелями и появляется хендл ресайза.
  const renderPanel = (k: LeftPanelKey): ReactNode => {
    const { title, Icon } = LEFT_PANEL_META[k];
    const shell = (
      <PanelShell
        icon={<Icon size={15} strokeWidth={ICON_STROKE} color={C.textSecondary} style={{ flexShrink: 0 }} />}
        title={title}
        onClose={() => close(k)}
        fill={multiOpen}
        slideDirection="left"
        // Перетаскивать есть смысл только когда соседи есть: в solo и при
        // единственной открытой панели шапка остаётся обычной (см. enabled выше).
        {...dnd.panelProps(k)}
      >
        {panels[k] ?? null}
      </PanelShell>
    );
    if (!multiOpen) return shell;
    return (
      <PanelSlot
        weight={weights[k]}
        resizing={rowDragging != null}
        slotRef={el => { panelRefs.current[k] = el; }}
      >
        {shell}
      </PanelSlot>
    );
  };

  return (
    <>
      {/* Рельса. singlePanelMode (1 доступная панель):
          - панель открыта → visible=false → рельса схлопывается
          - панель закрыта → visible=true → рельса с 1 иконкой
          Мульти-режим (>1 панель): рельса всегда видна. Тумблер режима и
          «свернуть все» в singlePanelMode не нужны — там управлять нечем. */}
      <PanelRail
        side="left"
        visible={showRail}
        groups={[railItems]}
        gapToCenter={openKeys.length === 0 ? RAIL_GAP : 0}
        modeToggle={singlePanelMode ? undefined : {
          soloMode,
          onToggle: () => setMode(soloMode ? 'multi' : 'solo'),
        }}
        collapse={singlePanelMode ? undefined : {
          collapsed,
          disabled: openKeys.length === 0 && !collapsed,
          onToggle: toggleCollapsed,
        }}
      />

      {/* Зона открытых панелей — растёт вправо от рельсы. Колонка одна: панели
          стакаются вертикально, порядок меняется перетаскиванием за шапку. */}
      {openKeys.length > 0 && (
        <>
          {/* Зазор между рельсой и панелями — только когда рельса видна.
              Если рельса скрыта (singlePanelMode + панель открыта) — оставляем
              placeholder (RAIL_W + RAIL_GAP) чтобы панель стояла на том же месте,
              где была бы если бы рельса была видна. Визуальная консистентность:
              панель не «прыгает» при скрытии/показе рельсы. */}
          <div style={{
            width: showRail ? RAIL_GAP : RAIL_W + RAIL_GAP,
            flexShrink: 0, transition: 'width 0.15s ease-out',
          }} />
          <div style={{
            width,
            flexShrink: 0,
            display: 'flex',
            flexDirection: 'column',
            // Тени панелей-островов не должны срезаться обёрткой
            overflow: 'visible',
          }}>
            {dropGuide(0, 0, 'start')}
            {openKeys.map((k, ri) => (
              <Fragment key={k}>
                {/* Между соседними панелями — хендл ресайза высот (тот же grip,
                    что у сплиттера ширины). Он же и есть зазор: отдельный gap
                    колонке не нужен, иначе между панелями было бы вдвое.
                    На время перетаскивания хендл подменяется направляющей той же
                    высоты — раскладка от этого не «дышит». */}
                {ri > 0 && (
                  dnd.active
                    ? dropGuide(ri, RAIL_GAP)
                    : <IslandSplitter
                        orientation="h"
                        active={rowDragging === `row:${ri}`}
                        onMouseDown={handleRowDrag(openKeys[ri - 1], k, `row:${ri}`)}
                        gap={RAIL_GAP}
                      />
                )}
                {renderPanel(k)}
              </Fragment>
            ))}
            {dropGuide(openKeys.length, 0, 'end')}
          </div>
          {/* Сплиттер ширины — справа от зоны панелей (у правой рельсы он слева) */}
          <IslandSplitter orientation="v" active={dragging} onMouseDown={handleWidthDrag} gap={RAIL_GAP} />
        </>
      )}
    </>
  );
}
