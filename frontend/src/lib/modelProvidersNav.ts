// Навигация «собрать цепочку»: кнопка в панелях выбора модели ведёт во вкладку
// «Пресеты» раздела «Поставщики моделей» и сразу начинает черновик нового пресета
// (спека, расхождение п.2 — вместо инлайн-цепочки «только для этого места»).
// Модалка может быть закрыта (её откроет HubHeader) или уже открыта на другой вкладке
// (просто переключится). Флаги-пендинги покрывают гонку «событие пришло до маунта».

let _openRequested = false;
let _draftRequested = false;
const _listeners = new Set<() => void>();

function emit() {
  _listeners.forEach(fn => fn());
}

// Запросить переход: открыть раздел на вкладке «Пресеты» и начать новый личный пресет
export function requestNewPreset(): void {
  _openRequested = true;
  _draftRequested = true;
  emit();
}

export function subscribeModelProvidersNav(fn: () => void): () => void {
  _listeners.add(fn);
  return () => { _listeners.delete(fn); };
}

// HubHeader: открыть модалку, если ещё не открыта (вызывает из слушателя и при маунте)
export function consumeOpenRequest(): boolean {
  const v = _openRequested;
  _openRequested = false;
  return v;
}

// PresetsTab: начать черновик нового пресета (одноразово)
export function consumeDraftRequest(): boolean {
  const v = _draftRequested;
  _draftRequested = false;
  return v;
}
