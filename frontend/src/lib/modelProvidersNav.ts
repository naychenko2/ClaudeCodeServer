// Навигация в раздел «Модели и расход» из других мест:
//   - «собрать цепочку»: кнопка в панелях выбора модели ведёт во вкладку «Модели» и сразу
//     начинает черновик новой цепочки;
//   - диплинк #/models (уведомление об обновлении claude CLI) — вкладка «Расход».
// Модалка может быть закрыта (её откроет хост — HubHeader или WorkspacePage) или уже открыта
// на другой вкладке (просто переключится). Флаги-пендинги покрывают гонку «событие пришло
// до маунта». Пендинг вкладки хосты только ПОДСМАТРИВАЮТ (hasPendingOpen), а съедает его
// сама модалка (consumeOpenRequest) — иначе хост сбросил бы вкладку до маунта модалки.

import type { ModelsSpendTab } from '../features/modelsSpend/ModelsSpendModal';

let _openTab: ModelsSpendTab | null = null;
let _draftRequested = false;
const _listeners = new Set<() => void>();

function emit() {
  _listeners.forEach(fn => fn());
}

// Открыть раздел на заданной вкладке
export function requestOpenModelsSpend(tab: ModelsSpendTab): void {
  _openTab = tab;
  emit();
}

// Запросить переход: открыть раздел на вкладке «Модели» и начать новую личную цепочку
export function requestNewPreset(): void {
  _openTab = 'slots';
  _draftRequested = true;
  emit();
}

export function subscribeModelProvidersNav(fn: () => void): () => void {
  _listeners.add(fn);
  return () => { _listeners.delete(fn); };
}

// Хосты модалки при маунте: есть ли запрос на открытие (флаг НЕ сбрасывается)
export function hasPendingOpen(): boolean {
  return _openTab !== null;
}

// Модалка: запрошенная вкладка (одноразово) или null
export function consumeOpenRequest(): ModelsSpendTab | null {
  const v = _openTab;
  _openTab = null;
  return v;
}

// Цепочки (вкладка «Модели»): начать черновик новой цепочки (одноразово).
// ModelsSpendModal должен вызвать это в обработчике подписки и запустить черновик.
export function consumeDraftRequest(): boolean {
  const v = _draftRequested;
  _draftRequested = false;
  return v;
}
