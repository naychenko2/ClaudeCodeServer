import { useRef, useState, type HTMLAttributes, type PointerEvent as ReactPointerEvent } from 'react';
import { startPointerDrag } from '../../lib/pointerDrag';
import { PANEL_MIN_H } from './panelStackState';

// Механика зоны панелей, общая для левой и правой рельс: перетаскивание панелей,
// ресайз ширины зоны и высот внутри колонки.
//
// Зоны зеркальны, но не одинаковы (слева одна стопка, справа колонки и планшетный
// режим), поэтому здесь живёт только то, что у них совпадает ПОВЕДЕНИЕМ. Различия
// зон задаются параметрами — иначе правка DnD требовала бы синхронных изменений
// в двух файлах, а это ровно то, из-за чего рельсы и направляющие когда-то
// разъехались.

// ---------- перетаскивание панелей ----------

// Состояние DnD и готовый набор пропсов для PanelShell.
//
// Хук держит только то, что у зон общее: какая панель тащится и над какой висит.
// Позиции вставки (индекс в стопке слева, пара «колонка:строка» справа) остаются
// в самой зоне — их типы разные; чтобы они сбрасывались вместе с DnD, зона отдаёт
// свой сброс через onEnd.
export function usePanelDnd<K extends string>({ enabled, onSwap, onEnd }: {
  // false — шапка не таскается (одна панель, solo, компактный режим)
  enabled: boolean;
  // Дроп ОДНОЙ панели на другую: они меняются местами
  onSwap: (from: K, to: K) => void;
  // Сброс позиционных состояний зоны — вызывается при любом завершении drag'а
  onEnd?: () => void;
}) {
  const [from, setFrom] = useState<K | null>(null);
  const [over, setOver] = useState<K | null>(null);

  const end = () => {
    setFrom(null);
    setOver(null);
    onEnd?.();
  };

  // Пропсы одной панели: подсветка источника/цели + обработчики дропа НА неё.
  // Перетаскивание начинается с шапки (headerProps), а принимает дроп вся
  // карточка (rootProps) — попасть в неё проще, чем в 40px шапки.
  const panelProps = (k: K): {
    draggable: boolean;
    dragged: boolean;
    dropTarget: boolean;
    rootProps: HTMLAttributes<HTMLDivElement>;
    // draggable сужен до boolean — так его объявляет PanelShell
    headerProps: HTMLAttributes<HTMLDivElement> & { draggable?: boolean };
  } => ({
    draggable: enabled,
    dragged: from === k,
    dropTarget: over === k && from !== null && from !== k,
    rootProps: {
      onDragOver: e => { if (from && from !== k) { e.preventDefault(); e.dataTransfer.dropEffect = 'move'; setOver(k); } },
      onDragLeave: () => { setOver(cur => (cur === k ? null : cur)); },
      onDrop: e => { e.preventDefault(); if (from && from !== k) onSwap(from, k); end(); },
    },
    headerProps: {
      onDragStart: e => { setFrom(k); e.dataTransfer.effectAllowed = 'move'; e.dataTransfer.setData('text/plain', k); },
      onDragEnd: end,
    },
  });

  return { from, active: from !== null, end, panelProps };
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

// Слот панели (обёртка с весом и клампом высоты) — соседний PanelSlot.tsx:
// в одном файле с хуками компонент держать нельзя, это ломает fast refresh.
