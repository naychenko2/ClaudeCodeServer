// Отбор и порядок строк меню «Что обработать? / Что править?» — чистые функции,
// общие для звука и картинок. Каждый раздел сам переводит свои нити в кандидатов
// (id, время последнего изменения) и потом рисует строки GenerationPickMenu.

export interface PickCandidate {
  id: string;
  // Время последнего изменения, мс: по нему «свежие сверху»
  at: number;
  // Строку не показывать вовсе (черновик без файла, нить в работе у другого режима)
  hidden?: boolean;
}

export interface PickOptions {
  // Что правили последним — эта строка получает отметку «правили последней»
  lastId?: string | null;
  // Что уже выбрано: в меню выбора ему не место
  excludeId?: string | null;
  // Потолок строк; лишние — самые старые
  limit?: number;
}

export type Picked<T> = T & { last: boolean };

// Свежие сверху; при равном времени выше тот, кто позже в исходном списке (позже
// добавлен в ленту). Повтор id схлопывается в самую свежую запись
export function pickRows<T extends PickCandidate>(items: readonly T[], opts: PickOptions = {}): Picked<T>[] {
  const best = new Map<string, { item: T; idx: number }>();
  items.forEach((item, idx) => {
    if (item.hidden || item.id === opts.excludeId) return;
    const prev = best.get(item.id);
    if (!prev || item.at > prev.item.at || (item.at === prev.item.at && idx > prev.idx)) best.set(item.id, { item, idx });
  });
  const sorted = [...best.values()].sort((a, b) => b.item.at - a.item.at || b.idx - a.idx);
  const cut = opts.limit != null && opts.limit >= 0 ? sorted.slice(0, opts.limit) : sorted;
  return cut.map(({ item }) => ({ ...item, last: opts.lastId != null && item.id === opts.lastId }));
}
