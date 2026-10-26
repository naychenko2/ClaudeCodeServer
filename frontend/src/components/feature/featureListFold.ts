// Свёртка длинных списков карточек (GC-F4, «Гант на Хронологии»).
//
// Чистые функции над КАРТОЧКАМИ (ProdCard-подобными), не над сырыми задачами:
// в карточке есть состояние шага ('done' / 'run' / 'wait' / 'queue' / 'withdrawn')
// и признак «шаг плана без задачи» (planned). Готовым считается шаг в состоянии
// 'done' (корзина accepted) — состояние карточки, а не корзина бэка.
//
// Правила свёртки:
//   • foldProd — незакрытые шаги видны всегда; из готовых видны только последние
//     три (хвост ленты), остальные уходят в hiddenDone;
//   • раскрытая карточка (expandedKey) не прячется никогда, даже если она старая
//     готовая;
//   • showAll — показать всё (hiddenDone/hiddenEarly пусты);
//   • порядок видимых — исходный порядок входа (сортировки по датам нет).
//
// Про двойной счёт хвостов. На живой фиче волны 5 хвосты после мержа
// (run.postMergeTails) ПЕРЕСЕКАЮТСЯ с шагами стадий: их id входят и в
// run.stages[].taskIds, и в списки run.qa/run.cut/run.steps (закреплено тестом
// бэка FeaturePostMergeTailsTests.cs:112-116 — открытый хвост-шаг держит стадию
// active). Если вызывающий склеит шаги стадии и хвосты в один список, наивная
// свёртка посчитает такой шаг дважды. Поэтому обе функции схлопывают элементы с
// одинаковым ключом шага: ПЕРВОЕ вхождение побеждает, второе отбрасывается
// (порядок исходный). Итог: шаг, показанный отдельной строкой «хвосты после
// мержа», не удваивается в списке стадии.
//
// Функции чистые: вход не мутируется, на вход возвращаются новые массивы.

/** Минимальный контракт элемента свёртки. ProdCard удовлетворяет ему как есть. */
export interface Foldable {
  /** Ключ шага: у ProdCard — stepRef. */
  stepRef?: string | null;
  /** Идентификатор задачи — запасной ключ, если stepRef пуст. */
  id?: string | null;
  /** Состояние шага: 'done' / 'run' / 'wait' / 'queue' / 'withdrawn'. */
  state?: string | null;
}

/** Сколько последних готовых карточек остаётся видимыми в ленте. */
export const PROD_DONE_TAIL = 3;

/** Состояния, которые считаются «готовым шагом». */
const DONE_STATES = new Set(['done', 'accepted']);

// Ключ шага в сравнимом виде: без краёв и регистра (как на бэке, StepKeyComparer:
// «Б1» и «б1» — один шаг). Пустой ключ — не ключ: такие элементы между собой
// НЕ схлопываются (иначе два безымянных шага схлопнутся в один).
function normKey(raw: string | null | undefined): string {
  const s = (raw ?? '').trim().toLowerCase();
  return s;
}

// Ключ элемента: сначала stepRef, при пустом — id. Пусто — значит «без ключа».
function keyOf(item: Foldable): string {
  return normKey(item.stepRef) || normKey(item.id);
}

// Готов ли шаг: состояние карточки 'done' (корзина accepted) — или 'accepted',
// если карточка пришла из другой формы свёртки.
function isDone(item: Foldable): boolean {
  return DONE_STATES.has((item.state ?? '').trim().toLowerCase());
}

// Схлопывание элементов с одинаковым ключом: первое вхождение побеждает.
// Порядок исходный. Элементы без ключа не схлопываются.
function dedupe<T extends Foldable>(items: T[]): T[] {
  const seen = new Set<string>();
  const out: T[] = [];
  for (const it of items) {
    const k = keyOf(it);
    if (k !== '') {
      if (seen.has(k)) continue;
      seen.add(k);
    }
    out.push(it);
  }
  return out;
}

// Результат свёртки ленты карточек.
export interface ProdFold {
  /** Карточки к показу — в исходном порядке. */
  visible: Foldable[];
  /** Готовые карточки, убранные из ленты (в исходном порядке). */
  hiddenDone: Foldable[];
}

/**
 * Свёртка ленты шагов: незакрытые + последние готовые + раскрытая карточка.
 *
 * @param cards карточки в исходном порядке (порядок видимых сохраняется);
 * @param expandedKey ключ раскрытой карточки — она видна всегда; null — ничего не раскрыто;
 * @param showAll показать всё (без свёртки).
 */
export function foldProd<T extends Foldable>(
  cards: T[],
  expandedKey: string | null,
  showAll: boolean,
): { visible: T[]; hiddenDone: T[] } {
  const source = dedupe(cards ?? []);
  if (showAll) return { visible: [...source], hiddenDone: [] };

  const expanded = normKey(expandedKey);
  // Готовые: сначала появляющиеся в ленте — хвост ленты (последние PROD_DONE_TAIL),
  // затем исключение раскрытой карточки: раскрытая готовая тоже видна.
  const doneIndexes: number[] = [];
  for (let i = 0; i < source.length; i++) {
    if (isDone(source[i]!)) doneIndexes.push(i);
  }
  const tail = new Set(doneIndexes.slice(-PROD_DONE_TAIL));

  const visible: T[] = [];
  const hiddenDone: T[] = [];
  for (let i = 0; i < source.length; i++) {
    const card = source[i]!;
    if (!isDone(card)) {
      visible.push(card);
      continue;
    }
    if (tail.has(i) || (expanded !== '' && keyOf(card) === expanded)) visible.push(card);
    else hiddenDone.push(card);
  }
  return { visible, hiddenDone };
}

/** Результат свёртки общего списка: показываем хвост, ранние прячем. */
export interface ListFold<T> {
  /** Элементы к показу — в исходном порядке. */
  visible: T[];
  /** Ранние элементы, убранные из списка (в исходном порядке). */
  hiddenEarly: T[];
}

/**
 * Свёртка общего списка: видны последние n элементов плюс раскрытый.
 *
 * @param items элементы в исходном порядке;
 * @param expandedKey ключ раскрытого элемента — он виден всегда; null — ничего не раскрыто;
 * @param showAll показать всё (без свёртки);
 * @param n сколько последних элементов остаётся видимыми.
 */
export function foldLast<T extends Foldable>(
  items: T[],
  expandedKey: string | null,
  showAll: boolean,
  n: number = 10,
): { visible: T[]; hiddenEarly: T[] } {
  const source = dedupe(items ?? []);
  if (showAll) return { visible: [...source], hiddenEarly: [] };

  const expanded = normKey(expandedKey);
  const keep = new Set<number>();
  const start = Math.max(0, source.length - Math.max(0, n));
  for (let i = start; i < source.length; i++) keep.add(i);
  if (expanded !== '') {
    for (let i = 0; i < source.length; i++) {
      if (keyOf(source[i]!) === expanded) keep.add(i);
    }
  }

  const visible: T[] = [];
  const hiddenEarly: T[] = [];
  for (let i = 0; i < source.length; i++) {
    if (keep.has(i)) visible.push(source[i]!);
    else hiddenEarly.push(source[i]!);
  }
  return { visible, hiddenEarly };
}
