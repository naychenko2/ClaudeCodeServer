// Левая рельса — зеркало RightPanelStack.
//
// Рельса иконок СЛЕВА у левого края окна + открытые панели-карточки, растущие
// ВПРАВО от рельсы. Клик по иконке открывает/закрывает панель. Раскладка —
// ЯВНЫЕ колонки, как справа: дефолт «по две на колонку» в порядке открытия,
// а drag-and-drop за шапку даёт любое распределение (дроп НА панель меняет две
// местами, дроп в направляющую вставляет в колонку или выносит в новую).
//
// Общее с правой зоной вынесено и НЕ дублируется: сама рельса — PanelRail
// (side="left" разворачивает капсулу и стрелки), направляющие мест вставки —
// PanelDropGuide, механика DnD и ресайза — хуки panelZone, слот высоты —
// PanelSlot.
//
// Пока НЕ реализовано: планшетный drawer (у правой зоны — compact-режим со
// своим стеком tabletPanels).
//
// Базовая логика: toggle через стор (wsLeftPanelStack / chatLeftPanelStack),
// панель рендерится как PanelShell, закрывается кнопкой в шапке.
// Ширина панелей тянется сплиттером справа от зоны и живёт в том же сторе
// (width, на ОДНУ колонку), что и у правой рельсы.
//
// sessionOnly=true — только chats (для раздела «Чаты» без проекта).
// sessionOnly=false — chats/files/tasks/personas (+ tools если toolsEnabled).
import { Fragment, useEffect, useState, type ReactNode } from 'react';
import { MessageCircle, FolderTree, ListTodo, Users, SquareTerminal, type LucideIcon } from 'lucide-react';
import { C, ISLAND } from '../../lib/design';
import { ICON_STROKE } from '../../components/ui/icons';
import { PanelShell } from '../../components/ui/PanelShell';
import { PanelRail, RAIL_W, RAIL_GAP, type RailItem } from '../../components/ui/PanelRail';
import { PanelDropGuide } from '../../components/ui/PanelDropGuide';
import { IslandSplitter } from '../../components/ui/IslandSplitter';
import { wsLeftPanelStack, type LeftPanelKey, type PanelStack } from './panelStackState';
import { usePanelDnd, usePanelRowResize, usePanelWidthDrag } from './panelZone';
import { PanelSlot } from './PanelSlot';

const GAP = ISLAND.gap; // зазор между карточками — та самая «воздушность»

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
  const { layout, mode, toggle, close, collapsed, toggleCollapsed, setMode, width, setWidth, weights, setWeights, swapWith, moveAt, moveToNewColumn } = usePanels();
  const { panelRefs, rowDragging, handleRowDrag } = usePanelRowResize<LeftPanelKey>(weights, setWeights);

  // Позиции вставки под курсором: разделитель колонок (индекс) и горизонтальный
  // плейсхолдер ('ci:ri'). Сбрасываются вместе с DnD — через onEnd хука.
  const [dndOverSep, setDndOverSep] = useState<number | null>(null);
  const [dndOverRow, setDndOverRow] = useState<string | null>(null);

  // Какие иконки показывать в рельсе
  const visibleKeys: LeftPanelKey[] = sessionOnly
    ? ['chats']
    : [...WORKSPACE_LEFT_KEYS, ...(toolsEnabled ? [TOOLS_KEY] : [])];

  // Панели, у которых есть контент (panels[k] != null). Если ни у одной —
  // возвращаем null, рельса не рендерится вовсе.
  const availableKeys = visibleKeys.filter(k => panels[k] != null);

  // Видимые колонки. Вместе с ключами колонка несёт свой ИСХОДНЫЙ индекс в
  // layout: панели без контента отсеиваются, поэтому видимые координаты
  // (колонка, строка) не совпадают с настоящими, а moveAt/moveToNewColumn
  // работают именно с настоящими.
  const columns = layout
    .map((col, ci) => ({ ci, keys: col.filter(k => availableKeys.includes(k as LeftPanelKey)) as LeftPanelKey[] }))
    .filter(c => c.keys.length > 0);
  const openKeys = columns.flatMap(c => c.keys);
  const soloMode = mode === 'solo';
  // Делить высоту между слотами и переставлять панели есть смысл только когда
  // их больше одной; в solo открыта ровно одна — переставлять нечего.
  const multiOpen = openKeys.length > 1;
  const dnd = usePanelDnd<LeftPanelKey>({
    enabled: multiOpen && !soloMode,
    onSwap: swapWith,
    onEnd: () => { setDndOverSep(null); setDndOverRow(null); },
  });

  // Ширина зоны: тянем ВПРАВО — панели растут (зеркально правой рельсе, где рост
  // идёт влево). Ширина хранится на ОДНУ колонку, поэтому сдвиг курсора делится
  // на их число. Клампы COL_MIN/COL_MAX применяет сам стор.
  const { dragging, onPointerDown: handleWidthDrag } = usePanelWidthDrag(width, setWidth, 'left', columns.length);

  // === ПРАВИЛО СКРЫТИЯ РЕЛЬСЫ ===
  // Если доступна только ОДНА панель (напр. sessionOnly → только chats) и она
  // ОТКРЫТА — рельса не нужна: панель сама показывает заголовок с иконкой.
  // Если панель ЗАКРЫТА — показываем рельсу с 1 иконкой (чтобы открыть обратно).
  // Если доступно >1 панелей — рельса всегда видна.
  const singlePanelMode = availableKeys.length === 1;
  const showRail = !singlePanelMode || openKeys.length === 0;

  // Ширина зоны панелей: колонки по width плюс зазоры МЕЖДУ ними (крайние
  // направляющие в покое нулевые, зазор до центра даёт сплиттер ширины).
  const zoneW = columns.length > 0 ? columns.length * width + (columns.length - 1) * GAP : 0;

  // Сдвиг FAB AI-хаба: левая рельса занимает место слева — пробрасываем в CSS-переменную.
  // Слагаемые считаются ПО РАЗМЕТКЕ, слева направо: рельса + зазор до панелей +
  // сама зона + её ресайз-сплиттер. Когда панелей нет, тот же RAIL_GAP даёт
  // gapToCenter самой рельсы, так что зазор в сумме остаётся один.
  const leftZoneW = availableKeys.length === 0
    ? 0
    : RAIL_W + RAIL_GAP + (columns.length > 0 ? zoneW + RAIL_GAP : 0);
  useEffect(() => {
    document.documentElement.style.setProperty('--cc-fab-left', `${leftZoneW + 20}px`);
    return () => { document.documentElement.style.removeProperty('--cc-fab-left'); };
  }, [leftZoneW]);

  // Ранний return — ПОСЛЕ всех хуков (useSyncExternalStore в usePanels, useEffect выше).
  // Если ни у одной панели нет контента — не рендерим рельсу вовсе.
  if (availableKeys.length === 0) return null;

  // Настоящий индекс СТРОКИ в layout по видимой позиции ri колонки: пропущенные
  // (без контента) панели сдвигают нумерацию, а moveAt ждёт индекс в исходной
  // колонке. Позиция за последней видимой панелью = конец настоящей колонки.
  const layoutRowFor = (col: { ci: number; keys: LeftPanelKey[] }, ri: number): number => {
    const real = layout[col.ci] ?? [];
    if (ri >= col.keys.length) return real.length;
    const at = real.indexOf(col.keys[ri]);
    return at >= 0 ? at : real.length;
  };

  // Настоящий индекс РАЗДЕЛИТЕЛЯ колонок по видимому: разделитель перед видимой
  // колонкой — это её же исходный индекс, а крайний правый — конец layout.
  const layoutSepFor = (vi: number): number => (vi < columns.length ? columns[vi].ci : layout.length);

  // Направляющая места вставки в колонку: позиция ri колонки с видимым индексом vi.
  // base — место в потоке: по краям колонки 0 (в покое их нет), между панелями
  // GAP — там направляющая подменяет хендл ресайза той же высоты.
  const rowGuide = (col: { ci: number; keys: LeftPanelKey[] }, vi: number, ri: number, base = 0, edge?: 'start' | 'end') => {
    const tag = `${vi}:${ri}`;
    return (
      <PanelDropGuide
        axis="y"
        key={`guide-${tag}`}
        dndActive={dnd.active}
        base={base}
        edge={edge}
        over={dndOverRow === tag}
        onDragOver={e => { if (dnd.from) { e.preventDefault(); e.dataTransfer.dropEffect = 'move'; setDndOverRow(tag); } }}
        onDragLeave={() => setDndOverRow(cur => (cur === tag ? null : cur))}
        onDrop={e => { e.preventDefault(); if (dnd.from) moveAt(dnd.from, col.ci, layoutRowFor(col, ri)); dnd.end(); }}
      />
    );
  };

  // Направляющая между колонками: дроп сюда выносит панель в НОВУЮ колонку.
  // Крайняя у рельсы (vi=0) и крайняя у центра в покое нулевые — зазоры там уже
  // дают отступ от рельсы и сплиттер ширины.
  const colGuide = (vi: number, base = 0, edge?: 'start' | 'end') => (
    <PanelDropGuide
      axis="x"
      key={`colguide-${vi}`}
      dndActive={dnd.active}
      base={base}
      edge={edge}
      over={dndOverSep === vi}
      onDragOver={e => { if (dnd.from) { e.preventDefault(); e.dataTransfer.dropEffect = 'move'; setDndOverSep(vi); } }}
      onDragLeave={() => setDndOverSep(cur => (cur === vi ? null : cur))}
      onDrop={e => { e.preventDefault(); if (dnd.from) moveToNewColumn(dnd.from, layoutSepFor(vi)); dnd.end(); }}
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
  // При ЕДИНСТВЕННОЙ панели в колонке высота — по контенту: короткий список чатов
  // не должен растягиваться на весь экран. Как только в колонке две панели и
  // больше, высоту делят веса слотов — тогда между ними и появляется хендл ресайза.
  const renderPanel = (k: LeftPanelKey, multiInCol: boolean): ReactNode => {
    const { title, Icon } = LEFT_PANEL_META[k];
    const shell = (
      <PanelShell
        icon={<Icon size={15} strokeWidth={ICON_STROKE} color={C.textSecondary} style={{ flexShrink: 0 }} />}
        title={title}
        onClose={() => close(k)}
        fill={multiInCol}
        slideDirection="left"
        // Перетаскивать есть смысл только когда соседи есть: в solo и при
        // единственной открытой панели шапка остаётся обычной (см. enabled выше).
        {...dnd.panelProps(k)}
      >
        {panels[k] ?? null}
      </PanelShell>
    );
    if (!multiInCol) return shell;
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

      {/* Зона открытых панелей — растёт вправо от рельсы. Колонки идут слева
          направо, внутри колонки панели стакаются вертикально; и то и другое
          перекладывается перетаскиванием за шапку. */}
      {columns.length > 0 && (
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
            // Колонки делят ширину зоны поровну (flex:1), зазоры между ними —
            // сами направляющие. Ни ширина зоны, ни размеры панелей при DnD НЕ
            // меняются: дроп-зоны направляющих — absolute-оверлеи, места в потоке
            // не занимают.
            width: zoneW,
            flexShrink: 0,
            display: 'flex',
            boxSizing: 'border-box',
            // Тени панелей-островов не должны срезаться обёрткой
            overflow: 'visible',
            transition: dragging ? 'none' : 'width 0.15s ease-out',
          }}>
            {columns.map((col, vi) => (
              <Fragment key={col.ci}>
                {/* Крайняя левая направляющая (vi=0) — только дроп-зона: в потоке
                    она нулевая, зазор от рельсы уже дан div'ом выше */}
                {colGuide(vi, vi > 0 ? GAP : 0, vi === 0 ? 'start' : undefined)}
                <div style={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column' }}>
                  {rowGuide(col, vi, 0, 0, 'start')}
                  {col.keys.map((k, ri) => (
                    <Fragment key={k}>
                      {/* Между соседними панелями — хендл ресайза высот (тот же grip,
                          что у сплиттера ширины). Он же и есть зазор: отдельный gap
                          колонке не нужен, иначе между панелями было бы вдвое.
                          На время перетаскивания хендл подменяется направляющей той же
                          высоты — раскладка от этого не «дышит». */}
                      {ri > 0 && (
                        dnd.active
                          ? rowGuide(col, vi, ri, GAP)
                          : <IslandSplitter
                              orientation="h"
                              active={rowDragging === `${vi}:${ri}`}
                              onMouseDown={handleRowDrag(col.keys[ri - 1], k, `${vi}:${ri}`)}
                              gap={GAP}
                            />
                      )}
                      {renderPanel(k, col.keys.length > 1)}
                    </Fragment>
                  ))}
                  {rowGuide(col, vi, col.keys.length, 0, 'end')}
                </div>
              </Fragment>
            ))}
            {/* Крайняя правая направляющая — вынос в новую колонку у центра */}
            {colGuide(columns.length, 0, 'end')}
          </div>
          {/* Сплиттер ширины — справа от зоны панелей (у правой рельсы он слева) */}
          <IslandSplitter orientation="v" active={dragging} onMouseDown={handleWidthDrag} gap={RAIL_GAP} />
        </>
      )}
    </>
  );
}
