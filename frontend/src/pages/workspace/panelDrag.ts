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
  // Кнопку тащат ИЗ ЯЩИКА рельсы (меню «…»), а не из самой рельсы или шапки панели.
  // По этому признаку дроп в раскладку не просто открывает панель, а ещё и
  // возвращает её кнопку на рельсу: перетаскивание из меню и есть жест возврата.
  // Открытую спрятанную панель (её кнопка временно стоит в рельсе) можно двигать
  // по раскладке как обычно — это перестановка, ящик она не трогает.
  fromTucked: boolean;
  // Панель, которую ТОЛЬКО ЧТО переставили дропом. Живёт пару кадров и нужна
  // обеим зонам сразу — см. markPanelMoved.
  moved: PanelKey | null;
}

const IDLE: PanelDragState = { from: null, fromZone: null, over: null, fromTucked: false, moved: null };

let _state: PanelDragState = IDLE;
const listeners = new Set<() => void>();

function emit() { listeners.forEach(l => l()); }
function subscribe(l: () => void) { listeners.add(l); return () => { listeners.delete(l); }; }

export function startPanelDrag(from: PanelKey, fromZone: Zone, fromTucked = false) {
  _state = { from, fromZone, over: null, fromTucked, moved: null };
  emit();
}

// Метка «эту панель только что переставили». Пока она держится, ОСТАЛЬНЫЕ панели
// рисуются без анимации появления: перенос перестраивает колонки, React
// перемонтирует карточки соседей — и они, стоя на своих местах, мигали бы
// «прилётом» вместе с той единственной, что действительно переехала.
//
// Метка снимается через кадр после перерисовки: к этому моменту карточки уже
// смонтированы, и возвращённая анимация им ничего не двигает. Живёт в общем
// сторе, потому что перестраиваются ОБЕ зоны — и та, откуда панель ушла, тоже.
export function markPanelMoved(k: PanelKey) {
  _state = { ..._state, moved: k };
  emit();
  requestAnimationFrame(() => requestAnimationFrame(() => {
    if (_state.moved !== k) return;
    _state = { ..._state, moved: null };
    emit();
  }));
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
  // moved переживает конец перетаскивания: dragend приходит ПОСЛЕ drop, и
  // сброс в IDLE стёр бы метку раньше, чем зоны успели перерисоваться.
  _state = { ...IDLE, moved: _state.moved };
  emit();
}

export function usePanelDragState(): PanelDragState {
  return useSyncExternalStore(subscribe, () => _state);
}
