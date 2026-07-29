// Зона панелей — рельса иконок у края окна плюс открытые панели-карточки.
//
// ОДИН компонент на обе стороны. Раньше это были LeftPanelStack и
// RightPanelStack — два почти дословных файла, которые расходились при каждой
// правке геометрии (левая долго жила без колонок, правая — с иной формулой
// ширины и своей подсказкой режима). Различия сторон теперь параметры:
//
//   side       — геометрия: рельса слева и рост панелей вправо, либо зеркально;
//   compact    — планшет/телефон: одна-две панели стеком, drawer поверх на узком;
//   sessionOnly— экран без проекта: доступны только чаты и панели сессии;
//   session*   — контент, видимость и бейджи панелей текущей сессии (useSessionPanels).
//
// Раскладка — ЯВНЫЕ колонки: дефолт «по две на колонку» в порядке открытия,
// drag-and-drop за шапку даёт любое распределение, В ТОМ ЧИСЛЕ перенос панели
// в соседнюю зону (состояние перетаскивания общее — panelDrag).
//
// Панели — «воздушные» скруглённые карточки с зазорами; границы высот тянутся
// невидимыми хендлами в зазорах, ширина колонок — сплиттером со стороны центра.
import { Fragment, useEffect, useState, type ReactNode } from 'react';
import { C, ISLAND, SHADOW, PANEL_ANIM } from '../../lib/design';
import { ICON_STROKE } from '../../components/ui/icons';
import { PanelShell } from '../../components/ui/PanelShell';
import { PanelRail, RAIL_W, RAIL_GAP, type RailItem } from '../../components/ui/PanelRail';
import { PanelDropGuide } from '../../components/ui/PanelDropGuide';
import { IslandSplitter } from '../../components/ui/IslandSplitter';
import { useWindowWidth } from '../../lib/breakpoints';
import {
  PANEL_META, PANEL_KEYS, PROJECT_KEYS, SESSION_KEYS, TOOLS_KEYS, WORKSPACE_KEYS,
  type PanelKey, type Zone,
} from './panelCatalog';
import { wsPanels, homeOf, isZoneCollapsed, zoneOf, type PanelZonesStore } from './panelStackState';
import { usePanelDnd, usePanelRowResize, usePanelWidthDrag } from './zoneGestures';
import { PanelSlot } from './PanelSlot';
import type { SessionPanels } from './useSessionPanels';

const GAP = ISLAND.gap; // зазор между карточками — та самая «воздушность»

// Порог планшета: шире — панель в потоке рядом с чатом, уже — drawer поверх
const TABLET_INLINE_MIN = 1000;

// Ширина полосы-приёмника, которую пустая зона показывает на время перетаскивания.
// Здесь раскладка намеренно «дышит»: у пустой зоны нет ни одной панели, рядом с
// которой можно было бы встать невидимым оверлеем, а целиться в невидимую кромку
// окна — мучение. Полоса живёт только пока панель тащат.
const EMPTY_DROP_W = 28;

interface Props {
  // Сторона окна, к которой прижата зона
  side: Zone;
  // Готовый контент ВСЕХ панелей экрана — обе зоны получают один и тот же набор
  // и рисуют из него то, что лежит именно в них
  panels: Partial<Record<PanelKey, ReactNode>>;
  // Контролы в шапку карточки (слева от кнопки закрытия) — напр. переключатель видов задач
  panelHeaderExtras?: Partial<Record<PanelKey, ReactNode>>;
  // Числа-кружки на иконках рельсы (changes/tasks/terminal/preview/chats).
  // Сессионные свои числа берут из sessionPanels.
  railCounts?: Partial<Record<PanelKey, number>>;
  // Инстанс стора зон: каждый экран держит НЕЗАВИСИМУЮ раскладку
  panelStack?: PanelZonesStore;
  // Какие панели вообще доступны на этом экране. У воркспейса это инструменты
  // проекта и сессии, у раздела хаба — его собственные. Раньше вместо набора был
  // флаг sessionOnly, и добавить экран с другим составом было нечем.
  allowedKeys?: readonly PanelKey[];
  // Рельсу целиком прячем, когда показывать нечего (у чата без артефактов иначе
  // торчала бы пустая полоса)
  hideWhenEmpty?: boolean;
  // Терминал и Preview доступны только при включённых инструментах проекта
  toolsEnabled?: boolean;
  // Планшет/телефон: одна-две панели, drawer поверх на узком экране, без DnD и колонок
  compact?: boolean;
  // Панели текущей сессии (План/Агенты/Персона) — контент, видимость, бейджи
  sessionPanels?: SessionPanels;
  // Хук на ЯВНУЮ активацию панели кликом по иконке рельсы (панель открылась).
  // Только клик: восстановление раскладки из localStorage его не дёргает.
  onPanelOpen?: (k: PanelKey) => void;
}

export function PanelZone({
  side, panels, panelHeaderExtras, railCounts, panelStack,
  allowedKeys = WORKSPACE_KEYS, hideWhenEmpty, toolsEnabled, compact, sessionPanels, onPanelOpen,
}: Props) {
  const usePanels = (panelStack ?? wsPanels).use;
  const { zones, toggle, setMode, setWidth, setWeights, toggleCollapsed, swapWith, moveAt, moveToNewColumn } = usePanels();
  const zoneState = zones[side];
  const { layout, mode, width } = zoneState;
  const windowWidth = useWindowWidth();
  const isLeft = side === 'left';

  const { panelRefs, rowDragging, handleRowDrag } = usePanelRowResize<PanelKey>(zones.weights, setWeights);

  // Компактный режим: до ДВУХ панелей стеком; выбор локальный эфемерный —
  // раскладка зоны не трогается. Третья открытая вытесняет самую старую (FIFO).
  const [tabletPanels, setTabletPanels] = useState<PanelKey[]>([]);
  const tabletInline = windowWidth >= TABLET_INLINE_MIN;

  // Панель доступна на этом экране: есть контент (у сессионных он всегда есть),
  // экран не sessionOnly либо ключ из разрешённых там, инструменты включены.
  const keyAvailable = (k: PanelKey): boolean => {
    if (!allowedKeys.includes(k)) return false;
    if (TOOLS_KEYS.includes(k) && !toolsEnabled) return false;
    return content(k) != null;
  };

  // Контент панели: сессионные собирает useSessionPanels, остальные приходят пропом
  function content(k: PanelKey): ReactNode {
    if (SESSION_KEYS.includes(k)) return sessionPanels?.content[k] ?? null;
    return panels[k] ?? null;
  }

  const soloMode = mode === 'solo';

  // Видимые колонки. Вместе с ключами колонка несёт свой ИСХОДНЫЙ индекс в
  // layout: недоступные панели отсеиваются, поэтому видимые координаты
  // (колонка, строка) не совпадают с настоящими, а moveAt/moveToNewColumn
  // работают именно с настоящими.
  const columns = compact ? [] : layout
    .map((col, ci) => ({ ci, keys: col.filter(keyAvailable) }))
    .filter(c => c.keys.length > 0);
  const tabletKeys = compact ? tabletPanels.filter(keyAvailable) : [];
  const openKeys = compact ? tabletKeys : columns.flatMap(c => c.keys);

  // Иконка панели живёт в ТОЙ зоне, где панель лежит; закрытая — в домашней.
  // Отсюда «иконка едет вместе с панелью», а закрытие возвращает её домой.
  const railKeyVisible = (k: PanelKey): boolean => {
    if (!keyAvailable(k)) return false;
    // Где панель сейчас лежит (null — закрыта). В компактном режиме раскладка
    // зоны не участвует: там свой эфемерный стек.
    const at = compact ? (tabletKeys.includes(k) ? side : null) : zoneOf(zones, k);
    // Открыта в соседней зоне — её иконка сейчас там
    if (at !== null && at !== side) return false;
    // Закрыта — иконка ждёт там, где панель лежала в последний раз (а до первого
    // открытия — в домашней зоне из реестра)
    if (at === null && homeOf(zones, k) !== side) return false;
    // Сессионные показываются только когда есть что открывать
    if (SESSION_KEYS.includes(k) && sessionPanels) return sessionPanels.visible(k, openKeys.includes(k));
    return true;
  };

  // Показывать нечего: рельсу не рисуем вовсе, чтобы у контента не торчала пустая
  // полоса. Ширина зоны при этом 0 — иначе FAB AI-хаба уедет под невидимую рельсу.
  const availableKeys = PANEL_KEYS.filter(railKeyVisible);
  const railHidden = !!hideWhenEmpty && availableKeys.length === 0 && openKeys.length === 0;

  // === ВИДИМОСТЬ РЕЛЬСЫ ===
  // Рельса стоит, пока зоне есть что показать: даже единственная открытая панель
  // не прячет её. Раньше в этом случае рельса убиралась (панель, мол, сама себя
  // называет), но тогда закрыть панель было нечем, кроме крестика в шапке, и
  // край окна дёргался при каждом открытии.
  const showRail = !railHidden;
  // Управлять раскладкой при единственной доступной панели нечем: тумблер режима
  // и «свернуть все» в этом случае не рисуем.
  const singlePanelMode = availableKeys.length === 1 && !compact;

  const dnd = usePanelDnd({
    zone: side,
    // Тащить есть смысл, когда переносить есть куда: соседняя зона всегда рядом,
    // поэтому хватает одной открытой панели. В solo и компактном режиме — нет.
    enabled: openKeys.length > 0 && !soloMode && !compact,
    onSwap: swapWith,
  });

  // Ширина зоны: колонки по width плюс зазоры МЕЖДУ ними (крайние направляющие
  // в покое нулевые: зазор к рельсе даёт отдельная прокладка, к центру — сплиттер)
  const zoneW = columns.length > 0 ? columns.length * width + (columns.length - 1) * GAP : 0;
  // Ширина зоны: тянем от кромки окна — колонки растут; width хранится на ОДНУ
  // колонку, поэтому сдвиг курсора делится на их число
  const { dragging: widthDragging, onPointerDown: handleWidthDrag } =
    usePanelWidthDrag(width, n => setWidth(side, n), side, columns.length);

  // Сдвиг FAB AI-хаба: кромку занимают рельса и панели — пробрасываем их суммарную
  // ширину в глобальную переменную (её читает AiLauncher). Слагаемые считаются ПО
  // РАЗМЕТКЕ: рельса + зазор до панелей + сама зона + её ресайз-сплиттер.
  // Drawer компактного режима не считаем — он overlay и живёт поверх контента сам.
  const zoneEdgeW = railHidden ? 0 : RAIL_W + RAIL_GAP + (compact
    ? (tabletKeys.length > 0 && tabletInline ? width + GAP * 2 : 0)
    : (columns.length > 0 ? zoneW + RAIL_GAP : 0));
  useEffect(() => {
    const prop = isLeft ? '--cc-fab-left' : '--cc-fab-right';
    document.documentElement.style.setProperty(prop, `${zoneEdgeW + 20}px`);
    return () => { document.documentElement.style.removeProperty(prop); };
  }, [zoneEdgeW, isLeft]);

  // Флеш «панель уже открыта»: внешние кнопки (git-бар над композером) шлют
  // cc-panel-flash, карточка на мгновение обводится акцентом. Счётчик n нужен,
  // чтобы повторный клик по той же панели перезапускал таймер. Слушают ОБЕ зоны —
  // панель могла переехать в любую из них.
  const [flash, setFlash] = useState<{ key: PanelKey; n: number } | null>(null);
  useEffect(() => {
    const onFlash = (e: Event) => {
      const key = (e as CustomEvent<{ key?: PanelKey }>).detail?.key;
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

  // Панель тащат из соседней зоны — эта обязана показаться, даже когда пуста:
  // иначе, утащив отсюда последнюю панель, вернуть её перетаскиванием было бы
  // некуда (осталась бы только дорога через закрытие панели).
  const acceptsForeign = dnd.active && dnd.fromZone !== side && !compact;

  // Ранний return — ПОСЛЕ всех хуков (useSyncExternalStore, useEffect выше).
  // Ни одной доступной панели и ничего не открыто — зоны на экране нет вовсе,
  // иначе у контента торчала бы пустая полоса рельсы.
  if (availableKeys.length === 0 && openKeys.length === 0 && !acceptsForeign) return null;

  // Настоящий индекс СТРОКИ в layout по видимой позиции ri колонки: пропущенные
  // (недоступные) панели сдвигают нумерацию, а moveAt ждёт индекс в исходной
  // колонке. Позиция за последней видимой панелью = конец настоящей колонки.
  const layoutRowFor = (col: { ci: number; keys: PanelKey[] }, ri: number): number => {
    const real = layout[col.ci] ?? [];
    if (ri >= col.keys.length) return real.length;
    const at = real.indexOf(col.keys[ri]);
    return at >= 0 ? at : real.length;
  };

  // Настоящий индекс РАЗДЕЛИТЕЛЯ колонок по видимому: разделитель перед видимой
  // колонкой — это её же исходный индекс, крайний — конец layout.
  const layoutSepFor = (vi: number): number => (vi < columns.length ? columns[vi].ci : layout.length);

  // Направляющая места вставки в колонку. base — место в потоке: по краям колонки
  // 0 (в покое их нет), между панелями GAP — там направляющая подменяет хендл
  // ресайза той же высоты.
  const rowGuide = (col: { ci: number; keys: PanelKey[] }, vi: number, ri: number, base = 0, edge?: 'start' | 'end') => (
    <PanelDropGuide
      axis="y"
      key={`row-${vi}-${ri}`}
      dndActive={dnd.active}
      base={base}
      edge={edge}
      {...dnd.guideProps(`row:${vi}:${ri}`, from => moveAt(from, side, col.ci, layoutRowFor(col, ri)))}
    />
  );

  // Направляющая между колонками: дроп сюда выносит панель в НОВУЮ колонку
  const colGuide = (vi: number, base = 0, edge?: 'start' | 'end') => (
    <PanelDropGuide
      axis="x"
      key={`col-${vi}`}
      dndActive={dnd.active}
      base={base}
      edge={edge}
      {...dnd.guideProps(`col:${vi}`, from => moveToNewColumn(from, side, layoutSepFor(vi)))}
    />
  );

  // Иконки одной группы рельсы: скрытые отсеиваются здесь же — пустая группа не
  // рисуется вовсе, вместе со своим разделителем (это делает PanelRail).
  const railGroup = (keys: readonly PanelKey[]): RailItem[] => keys.filter(railKeyVisible).map(k => ({
    key: k,
    title: PANEL_META[k].title,
    Icon: PANEL_META[k].Icon,
    active: openKeys.includes(k),
    badge: sessionPanels?.railBadge(k) ?? railCounts?.[k] ?? null,
    onClick: () => {
      const isOpen = openKeys.includes(k);
      if (compact) {
        // До двух панелей: третья вытесняет самую старую (FIFO)
        setTabletPanels(cur => cur.includes(k) ? cur.filter(x => x !== k) : [...cur, k].slice(-2));
      } else toggle(side, k);
      // Панель в результате клика ОТКРЫЛАСЬ — сообщаем подписчику (граф и т.п.)
      if (!isOpen) onPanelOpen?.(k);
    },
  }));

  // Карточка панели. multiInCol=false — высота по контенту: короткий список чатов
  // не должен растягиваться на весь экран. Как только в колонке две панели и
  // больше, высоту делят веса слотов — тогда между ними и появляется хендл ресайза.
  const renderPanel = (k: PanelKey, multiInCol: boolean): ReactNode => {
    const { title, Icon } = PANEL_META[k];
    const shell = (
      <PanelShell
        icon={<Icon size={15} strokeWidth={ICON_STROKE} color={C.textSecondary} style={{ flexShrink: 0 }} />}
        title={title}
        badge={sessionPanels?.headerBadge(k) ?? null}
        headerExtras={panelHeaderExtras?.[k]}
        // Крестик в шапке остаётся только в компактном режиме: там панель —
        // drawer поверх контента, а hover, которым десктоп подменяет иконку
        // рельсы крестиком, на тач-экране не существует.
        onClose={compact ? () => setTabletPanels(cur => cur.filter(x => x !== k)) : undefined}
        fill={multiInCol}
        flash={flash?.key === k}
        slideDirection={isLeft ? 'left' : 'up'}
        {...dnd.panelProps(k)}
      >
        {content(k)}
      </PanelShell>
    );
    if (!multiInCol) return shell;
    return (
      <PanelSlot
        weight={zones.weights[k]}
        resizing={rowDragging != null}
        slotRef={el => { panelRefs.current[k] = el; }}
      >
        {shell}
      </PanelSlot>
    );
  };

  const rail = (
    <PanelRail
      side={side}
      visible={showRail}
      // Две группы: инструменты ПРОЕКТА и панели ТЕКУЩЕЙ СЕССИИ. Разделитель между
      // ними PanelRail рисует сам и убирает вместе с пустой группой.
      groups={[railGroup(PROJECT_KEYS), railGroup(SESSION_KEYS)]}
      // Свой зазор до центра нужен только при закрытых панелях: иначе его даёт
      // прокладка перед зоной
      gapToCenter={openKeys.length === 0 ? RAIL_GAP : 0}
      // Тумблер режима и «свернуть все» в компактном и однопанельном режимах не
      // нужны — там управлять нечем
      modeToggle={compact || singlePanelMode ? undefined : {
        soloMode,
        onToggle: () => setMode(side, soloMode ? 'multi' : 'solo'),
      }}
      collapse={compact || singlePanelMode ? undefined : {
        collapsed: isZoneCollapsed(zoneState),
        disabled: openKeys.length === 0 && !isZoneCollapsed(zoneState),
        onToggle: () => toggleCollapsed(side),
      }}
    />
  );

  // Прокладка между рельсой и панелями зоны
  const railGapBox = <div style={{ width: RAIL_GAP, flexShrink: 0 }} />;

  const splitter = <IslandSplitter orientation="v" active={widthDragging} onMouseDown={handleWidthDrag} gap={RAIL_GAP} />;

  // Колонки зоны. Крайние направляющие в потоке нулевые: зазоры уже дают
  // прокладка у рельсы и сплиттер у центра. Дроп-зоны направляющих —
  // absolute-оверлеи, поэтому при DnD раскладка не «дышит».
  const zoneBody = (
    <div style={{
      width: zoneW,
      flexShrink: 0,
      display: 'flex',
      boxSizing: 'border-box',
      // Тени панелей-островов не должны срезаться обёрткой
      overflow: 'visible',
      transition: widthDragging ? 'none' : `width ${PANEL_ANIM}`,
    }}>
      {columns.map((col, vi) => (
        <Fragment key={col.ci}>
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
      {colGuide(columns.length, 0, 'end')}
    </div>
  );

  // Компактный режим: стек до двух панелей — в потоке на широком экране,
  // drawer поверх на узком; между двумя панелями — хендл ресайза высот
  const compactBody = (() => {
    if (!compact || tabletKeys.length === 0) return null;
    const stack = (
      <div style={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column' }}>
        {tabletKeys.map((k, ri) => (
          <Fragment key={k}>
            {ri > 0 && (
              <IslandSplitter orientation="h" active={rowDragging === 'tablet'} onMouseDown={handleRowDrag(tabletKeys[ri - 1], k, 'tablet')} gap={GAP} />
            )}
            {renderPanel(k, tabletKeys.length > 1)}
          </Fragment>
        ))}
      </div>
    );
    if (tabletInline) {
      return (
        <>
          {splitter}
          <div style={{ width: width + GAP * 2, flexShrink: 0, display: 'flex', padding: `0 ${GAP}px`, boxSizing: 'border-box' }}>
            {stack}
          </div>
        </>
      );
    }
    return (
      <>
        <div onClick={() => setTabletPanels([])} style={{ position: 'absolute', inset: 0, zIndex: 14, background: C.overlay }} />
        <div style={{
          position: 'absolute', top: GAP, bottom: GAP, zIndex: 15,
          ...(isLeft ? { left: RAIL_W + GAP } : { right: RAIL_W + GAP }),
          width: 'min(85vw, 380px)', display: 'flex', flexDirection: 'column', boxShadow: SHADOW.modal,
        }}>
          {stack}
        </div>
      </>
    );
  })();

  // Пустая зона на время перетаскивания из соседней: показываем единственную
  // направляющую — дроп сюда создаёт первую колонку.
  const emptyDropZone = (
    <div style={{ display: 'flex', flexShrink: 0 }}>
      {colGuide(0, EMPTY_DROP_W, isLeft ? 'start' : 'end')}
    </div>
  );

  // Порядок элементов по стороне: у левой зоны рельса первая и панели растут
  // вправо, у правой — зеркально.
  const body = compact ? compactBody : (
    columns.length > 0
      ? (isLeft ? <>{railGapBox}{zoneBody}{splitter}</> : <>{splitter}{zoneBody}{railGapBox}</>)
      : (acceptsForeign ? emptyDropZone : null)
  );

  return isLeft ? <>{rail}{body}</> : <>{body}{rail}</>;
}
