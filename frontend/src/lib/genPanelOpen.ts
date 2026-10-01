// Какая панель генерации («Картинки», «Звук») сейчас на экране и в каком виде. Каркас
// GenerationPanel отмечается здесь, пока смонтирован: по этому признаку клик по карточке
// в ленте переключает открытую панель, а закрытую не открывает
// (docs/mockups/image-editor-v4-panel-proposal.md, «Панель следует за выбором»).
//
// Модуль без зависимостей: его читает revealWorkspacePanel в registryCore.

// column — колонка в зоне панелей; sheet / peek — шторка телефона, поднята / опущена
export type GenPanelOpenView = 'column' | 'sheet' | 'peek';
export interface GenPanelOpen { key: string; view: GenPanelOpenView }

// Список, а не одно значение: при переключении новая панель монтируется раньше, чем
// уходит прежняя, и снятие прежней не должно стереть новую. Свежая — в конце
const _open: { id: number; key: string; view: GenPanelOpenView }[] = [];
let _seq = 0;

// Панель на экране; возвращает снятие. Вид обновляется повторной отметкой
export function holdGenPanelOpen(key: string, view: GenPanelOpenView): () => void {
  const id = ++_seq;
  _open.push({ id, key, view });
  return () => {
    const i = _open.findIndex(x => x.id === id);
    if (i >= 0) _open.splice(i, 1);
  };
}

export function openGenPanel(): GenPanelOpen | null {
  const last = _open[_open.length - 1];
  return last ? { key: last.key, view: last.view } : null;
}

// Переключение «по выбору» с опущенной шторки: новая шторка рождается тоже опущенной.
// Признак живёт до следующего показа и недолго — шторку, открытую потом руками, он не трогает
const CARRY_MS = 1500;
let _carry: { key: string; at: number } | null = null;

export function noteReveal(key: string, peek: boolean) {
  _carry = peek ? { key, at: Date.now() } : null;
}

export function followPeeked(key: string): boolean {
  return !!_carry && _carry.key === key && Date.now() - _carry.at < CARRY_MS;
}

export function __resetGenPanelOpen() {
  _open.length = 0;
  _carry = null;
}
