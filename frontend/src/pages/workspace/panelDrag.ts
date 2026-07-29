// Активное перетаскивание панели — состояние, ОБЩЕЕ для обеих зон.
//
// Зачем стор, а не useState внутри зоны: панель тащат из одной рельсы в другую,
// и зона-приёмник обязана знать, что над ней что-то тащат, — иначе её
// направляющие мест вставки просто не отрисуются (они гейтятся признаком
// «drag идёт»). Прочитать ключ панели из dataTransfer на dragover нельзя:
// браузер отдаёт данные только в drop. Отсюда разделяемое состояние.
//
// Позиция под курсором (over) тоже живёт здесь: при переносе между зонами
// подсветку показывает ПРИНИМАЮЩАЯ зона, а завершение drag'а должно гасить её
// в обеих сразу. Локальный стейт пришлось бы сбрасывать эффектом в каждой зоне,
// а setState в эффекте здесь запрещён линтом (react-hooks/set-state-in-effect).
import { useSyncExternalStore } from 'react';
import type { PanelKey, Zone } from './panelCatalog';

// tag — метка места под курсором внутри зоны, уникальная в её пределах:
// 'panel:{key}' — карточка (дроп = обмен местами),
// 'row:{ci}:{ri}' — направляющая между панелями колонки,
// 'col:{i}' — направляющая между колонками (дроп = вынос в новую колонку).
export interface PanelDragState {
  from: PanelKey | null;
  fromZone: Zone | null;
  over: { zone: Zone; tag: string } | null;
}

const IDLE: PanelDragState = { from: null, fromZone: null, over: null };

let _state: PanelDragState = IDLE;
const listeners = new Set<() => void>();

function emit() { listeners.forEach(l => l()); }
function subscribe(l: () => void) { listeners.add(l); return () => { listeners.delete(l); }; }

export function startPanelDrag(from: PanelKey, fromZone: Zone) {
  _state = { from, fromZone, over: null };
  emit();
}

// Курсор вошёл в место вставки / ушёл из него (tag === null). Уход гасит
// подсветку только если она принадлежит этому же месту: dragover соседа
// приходит раньше dragleave предыдущего, и слепой сброс мигал бы.
export function setPanelDragOver(zone: Zone, tag: string | null) {
  if (_state.from === null) return;
  if (tag === null) {
    if (!_state.over || _state.over.zone !== zone) return;
    _state = { ..._state, over: null };
  } else {
    if (_state.over?.zone === zone && _state.over.tag === tag) return;
    _state = { ..._state, over: { zone, tag } };
  }
  emit();
}

// Гасит подсветку конкретного места (dragleave): сбрасываем, только если под
// курсором всё ещё оно.
export function clearPanelDragOver(zone: Zone, tag: string) {
  if (_state.over?.zone === zone && _state.over.tag === tag) {
    _state = { ..._state, over: null };
    emit();
  }
}

export function endPanelDrag() {
  if (_state === IDLE) return;
  _state = IDLE;
  emit();
}

export function usePanelDragState(): PanelDragState {
  return useSyncExternalStore(subscribe, () => _state);
}
