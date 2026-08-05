import { useRef, useState, type HTMLAttributes, type PointerEvent as ReactPointerEvent } from 'react';
import { startPointerDrag } from '../../lib/pointerDrag';
import { COL_MIN, PANEL_MIN_H } from './panelStackState';
import type { PanelKey, Zone } from './panelCatalog';
import { clearPanelDragOver, endPanelDrag, markPanelMoved, setPanelDragOver, startPanelDrag, usePanelDragState } from './panelDrag';

// Механика зоны панелей, общая для левой и правой рельс: перетаскивание панелей,
// ресайз ширины зоны и высот внутри колонки.
//
// Зоны зеркальны, но не одинаковы (справа есть планшетный режим и сессионная
// группа), поэтому здесь живёт только то, что у них совпадает ПОВЕДЕНИЕМ. Различия
// зон задаются параметрами — иначе правка DnD требовала бы синхронных изменений
// в двух файлах, а это ровно то, из-за чего рельсы и направляющие когда-то
// разъехались.

// ---------- перетаскивание панелей ----------

// Готовый набор пропсов для PanelShell поверх ОБЩЕГО состояния перетаскивания
// (panelDrag): какая панель тащится, из какой зоны и над каким местом висит.
//
// Состояние общее, потому что панель переносится между рельсами: зона-приёмник
// должна видеть чужой drag, иначе её направляющие не появятся. Каждая зона
// смотрит на него через свой экземпляр хука и сравнивает позиции со своими
// метками мест (tag).
export function usePanelDnd({ zone, enabled, accepts, onSwap }: {
  // Зона, в которой живёт этот экземпляр хука
  zone: Zone;
  // false — шапка не таскается (компактный режим)
  enabled: boolean;
  // Может ли ЭТА зона принять такую панель. Экраны отличаются набором панелей, и
  // дроп ключа, который здесь некому нарисовать, оставлял бы панель невидимой:
  // в родной зоне её уже нет, а тут она не рисуется.
  accepts?: (k: PanelKey) => boolean;
  // Дроп ОДНОЙ панели на другую: они меняются местами (в т.ч. через границу зон)
  onSwap: (from: PanelKey, to: PanelKey) => void;
}) {
  const { from, fromZone, over, fromTucked, moved } = usePanelDragState();
  // Панель тащат И эта зона её принимает. Дальше по коду ориентируемся на неё:
  // непринимаемый дроп ведёт себя как «перетаскивания нет» — ни направляющих, ни
  // подсветки, ни dropEffect (курсор сам покажет запрет).
  const incoming = from !== null && (accepts?.(from) ?? true) ? from : null;

  // Место под курсором принадлежит ЭТОЙ зоне и имеет такую метку
  const isOver = (tag: string) => over?.zone === zone && over.tag === tag;

  // Пропсы места вставки (направляющей): подсветка + обработчики дропа.
  // dropTag — метка места внутри зоны ('row:ci:ri', 'col:i').
  const guideProps = (tag: string, onDropAt: (from: PanelKey) => void) => ({
    over: isOver(tag),
    onDragOver: (e: { preventDefault: () => void; dataTransfer: DataTransfer }) => {
      if (!incoming) return;
      e.preventDefault();
      e.dataTransfer.dropEffect = 'move';
      setPanelDragOver(zone, tag);
    },
    onDragLeave: () => clearPanelDragOver(zone, tag),
    onDrop: (e: { preventDefault: () => void }) => {
      e.preventDefault();
      // Метку ставим ДО перестройки раскладки: рендер с новыми колонками должен
      // застать её, иначе непричастные панели успеют мигнуть появлением
      if (incoming) { markPanelMoved(incoming); onDropAt(incoming); }
      endPanelDrag();
    },
  });

  // Что делает элемент «ручкой» панели: шапка карточки, иконка в рельсе и строка
  // ящика («…»). Из рельсы тащат ЗАКРЫТУЮ панель — так её можно сразу поставить в
  // нужное место, а не открывать кликом туда, куда решит раскладка.
  // tucked — ручка живёт в ящике: дроп в раскладку тогда ещё и вернёт кнопку на
  // рельсу (см. fromTucked в panelDrag).
  const dragSourceProps = (k: PanelKey, opts?: { tucked?: boolean }): HTMLAttributes<HTMLElement> & { draggable?: boolean } => ({
    draggable: true,
    onDragStart: e => {
      // dataTransfer живёт только внутри обработчика — заполняем сразу
      e.dataTransfer.effectAllowed = 'move';
      e.dataTransfer.setData('text/plain', k);
      // А состояние перетаскивания поднимаем СЛЕДУЮЩИМ кадром. Оно перестраивает
      // обе зоны прямо под захваченным элементом (у рельсы появляется мишень с
      // оверлеем поверх столбца иконок), и браузер на такую перестройку жест
      // отменяет: dragstart приходит, следом сразу dragend, без единого события
      // drag. Заметно это было только на иконках рельсы — карточку панели тащат
      // за шапку, а её DOM оверлей рельсы не трогает. Кадр задержки не виден:
      // человек в этот момент ещё только сдвигает курсор.
      requestAnimationFrame(() => startPanelDrag(k, zone, opts?.tucked));
    },
    onDragEnd: endPanelDrag,
  });

  // Пропсы одной панели: подсветка источника/цели + обработчики дропа НА неё.
  // Перетаскивание начинается с шапки (headerProps), а принимает дроп вся
  // карточка (rootProps) — попасть в неё проще, чем в 40px шапки.
  const panelProps = (k: PanelKey): {
    draggable: boolean;
    dragged: boolean;
    dropTarget: boolean;
    rootProps: HTMLAttributes<HTMLDivElement>;
    // draggable сужен до boolean — так его объявляет PanelShell
    headerProps: HTMLAttributes<HTMLDivElement> & { draggable?: boolean };
  } => {
    const tag = `panel:${k}`;
    return {
      draggable: enabled,
      dragged: from === k,
      dropTarget: isOver(tag) && incoming !== null && incoming !== k,
      rootProps: {
        onDragOver: e => { if (incoming && incoming !== k) { e.preventDefault(); e.dataTransfer.dropEffect = 'move'; setPanelDragOver(zone, tag); } },
        onDragLeave: () => clearPanelDragOver(zone, tag),
        onDrop: e => {
          e.preventDefault();
          if (incoming && incoming !== k) { markPanelMoved(incoming); onSwap(incoming, k); }
          endPanelDrag();
        },
      },
      headerProps: dragSourceProps(k),
    };
  };

  // active — панель тащат где-то на экране (нужно источнику: подсветить себя,
  // подменить хендлы ресайза направляющими). accepting — тащат панель, которую
  // ЭТА зона готова принять: по ней решается, показывать ли места вставки.
  return {
    from, fromZone, fromTucked, active: from !== null, accepting: incoming !== null,
    // Панель, которую только что перенесли: анимацию появления в перестроенной
    // раскладке получает ТОЛЬКО она (см. markPanelMoved)
    moved,
    end: endPanelDrag, isOver, guideProps, panelProps, dragSourceProps,
  };
}

// ---------- ресайз ширины зоны ----------

// Сплиттер ширины. Зоны растут в разные стороны: правая — влево от кромки окна,
// левая — вправо, отсюда знак. Ширина хранится на ОДНУ колонку, поэтому справа
// сдвиг курсора делится на их число (слева колонка одна, делитель 1).
export function usePanelWidthDrag(
  width: number,
  setWidth: (n: number) => void,
  side: 'left' | 'right',
  columns = 1,
) {
  const [dragging, setDragging] = useState(false);

  const onPointerDown = (e: ReactPointerEvent) => {
    e.preventDefault();
    const startX = e.clientX;
    const startW = width;
    const n = Math.max(1, columns);
    const sign = side === 'left' ? 1 : -1;
    setDragging(true);
    startPointerDrag(
      ev => setWidth(startW + sign * (ev.clientX - startX) / n),
      { onEnd: () => setDragging(false) },
    );
  };

  return { dragging, onPointerDown };
}

// ---------- ресайз высот внутри колонки ----------

// Граница между двумя соседними панелями.
//
// Вес панели — это высота её СЛОТА (flex-grow), а не пиксели: при открытии и
// закрытии соседей раскладка пересчитывается сама. Поэтому drag берёт пиксельные
// высоты пары на старте, а сохраняет пересчитанные веса, деля между парой их
// общую сумму — соседние слоты при этом не шевелятся.
export function usePanelRowResize<K extends string>(
  weights: Partial<Record<K, number>>,
  setWeights: (next: Partial<Record<K, number>>) => void,
) {
  // Живые узлы панелей — по ним берутся фактические высоты на старте drag'а
  const panelRefs = useRef<Partial<Record<K, HTMLDivElement | null>>>({});
  // Метка активного хендла ('ci:ri', 'tablet', …) — ею подсвечивается grip
  const [rowDragging, setRowDragging] = useState<string | null>(null);

  const handleRowDrag = (aKey: K, bKey: K, tag: string) => (e: ReactPointerEvent) => {
    e.preventDefault();
    const aEl = panelRefs.current[aKey];
    const bEl = panelRefs.current[bKey];
    if (!aEl || !bEl) return;
    const startY = e.clientY;
    const ha = aEl.getBoundingClientRect().height;
    const hb = bEl.getBoundingClientRect().height;
    const wa = weights[aKey] ?? 1;
    const wb = weights[bKey] ?? 1;
    setRowDragging(tag);
    startPointerDrag(
      ev => {
        const dy = ev.clientY - startY;
        // Кламп PANEL_MIN_H с обеих сторон: ни одна из пары не схлопывается
        const haNext = Math.max(PANEL_MIN_H, Math.min(ha + hb - PANEL_MIN_H, ha + dy));
        const waNext = (wa + wb) * (haNext / (ha + hb));
        setWeights({ [aKey]: waNext, [bKey]: (wa + wb) - waNext } as Partial<Record<K, number>>);
      },
      { cursor: 'row-resize', onEnd: () => setRowDragging(null) },
    );
  };

  return { panelRefs, rowDragging, handleRowDrag };
}

// ---------- ресайз ширины между колонками ----------

// Граница между двумя соседними КОЛОНКАМИ зоны. Как и ресайз высот, работает с
// долями (colFlex), а не пикселями: колонки делят общую ширину зоны, и перетянутая
// граница переносит долю от одной колонки к другой, сумма пары сохраняется —
// соседние колонки не шевелятся, общий масштаб зоны (width) не трогается.
//
// Живые ширины пары берём на старте из DOM-узлов колонок (colRefs) — так же, как
// ресайз высот берёт высоты панелей.
export function usePanelColResize(
  colFlex: number[],
  setColFlex: (next: number[]) => void,
) {
  const colRefs = useRef<Record<number, HTMLDivElement | null>>({});
  const [colDragging, setColDragging] = useState<number | null>(null);

  // aCi/bCi — РЕАЛЬНЫЕ индексы колонок в layout (по ним же ключ colFlex).
  const handleColDrag = (aCi: number, bCi: number, sign: 1 | -1) => (e: ReactPointerEvent) => {
    e.preventDefault();
    const aEl = colRefs.current[aCi];
    const bEl = colRefs.current[bCi];
    if (!aEl || !bEl) return;
    const startX = e.clientX;
    const wa = aEl.getBoundingClientRect().width;
    const wb = bEl.getBoundingClientRect().width;
    const fa = colFlex[aCi] ?? 1;
    const fb = colFlex[bCi] ?? 1;
    setColDragging(aCi);
    startPointerDrag(
      ev => {
        // sign разворачивает жест по стороне: у правой зоны колонки растут влево,
        // поэтому движение курсора вправо ужимает левую колонку пары
        const dx = (ev.clientX - startX) * sign;
        // Минимум колонки — COL_MIN в пикселях, переведённый в долю через масштаб
        // «доля/пиксель» пары (fa+fb на wa+wb)
        const perPx = (fa + fb) / (wa + wb);
        const minShare = COL_MIN * perPx;
        const total = fa + fb;
        const faNext = Math.max(minShare, Math.min(total - minShare, (wa + dx) * perPx));
        const next = [...colFlex];
        next[aCi] = faNext;
        next[bCi] = total - faNext;
        setColFlex(next);
      },
      { cursor: 'col-resize', onEnd: () => setColDragging(null) },
    );
  };

  return { colRefs, colDragging, handleColDrag };
}

// Слот панели (обёртка с весом и клампом высоты) — соседний PanelSlot.tsx:
// в одном файле с хуками компонент держать нельзя, это ломает fast refresh.
// Сама зона — PanelZone.tsx; имена файлов не должны различаться только регистром,
// иначе сборка спотыкается о регистронезависимую файловую систему.
