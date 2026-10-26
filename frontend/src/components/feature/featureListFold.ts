// Свёртка длинных списков карточек (GC-F4, «Гант на Хронологии»; GC-F4R и GC-F4R2 —
// правки по находкам ревью GC-R-F4 и GC-R-F4R).
//
// Чистые функции над КАРТОЧКАМИ (ProdCard-подобными), не над сырыми задачами:
// в карточке есть состояние шага ('done' / 'run' / 'wait' / 'queue' / 'withdrawn')
// и признак «шаг плана без задачи» (planned). Готовым считается только состояние
// карточки 'done'; 'accepted' — имя бэковой корзины (ProdBucket), а не состояния
// карточки (ProdCardState): карточки с таким состоянием не бывает.
//
// Правила свёртки:
//   • foldProd — незакрытые шаги видны всегда; снятый ('withdrawn') не считается
//     незакрытым и не показывается ни в одной ветке, включая showAll (продуктовое
//     решение, GC-R-F4: шаг человек снял — он не будет сделан, показывать его
//     «ещё не сделанным» — вводить в заблуждение); из готовых видны только последние
//     три (хвост ленты), остальные уходят в hiddenDone;
//   • раскрытая карточка (expandedKey) не прячется никогда, даже если она старая
//     готовая;
//   • showAll — тот же набор шагов, что считает свёртка: без дублей и без снятых,
//     но хвост готовых не прячется (GC-F4R2: счётчик «Показать все N» берут из
//     свёртки (visible+hiddenDone), и он обязан сходиться с тем, что откроется);
//   • порядок видимых — исходный порядок входа (сортировки по датам нет);
//   • foldLast — видны последние n элементов, БЕЗ схлопывания: длина входа
//     сохраняется (шаг и его доработка имеют одинаковый stepRef, но это разные
//     задачи — GC-F10, обе должны быть в ленте).
//
// Про двойной счёт хвостов — про все ветки foldProd, включая showAll. На живой фиче волны 5 хвосты
// после мержа (run.postMergeTails) ПЕРЕСЕКАЮТСЯ с шагами стадий: их id входят и в
// run.stages[].taskIds, и в списки run.qa/run.cut/run.steps (закреплено тестом
// бэка FeaturePostMergeTailsTests.cs:112-116 — открытый хвост-шаг держит стадию
// active). Если вызывающий склеит шаги стадии и хвосты в один список, наивная
// свёртка посчитает такой шаг дважды. Поэтому foldProd схлопывает элементы с
// одинаковым ключом шага: ПЕРВОЕ вхождение побеждает, второе отбрасывается
// (порядок исходный). Итог: шаг, показанный отдельной строкой «хвосты после
// мержа», не удваивается в списке стадии. В foldLast схлопывания нет: его контракт
// — «последние n элементов», а одинаковые stepRef там — намеренно разные задачи.
//
// Функции чистые: вход не мутируется, на вход возвращаются новые массивы.

/** Минимальный контракт элемента свёртки. ProdCard удовлетворяет ему как есть. */
export interface Foldable {
  /** Ключ шага: у ProdCard — stepRef. */
  stepRef?: string | null;
  /** Состояние шага: 'done' / 'run' / 'wait' / 'queue' / 'withdrawn'. */
  state?: string | null;
}

/** Сколько последних готовых карточек остаётся видимыми в ленте. */
export const PROD_DONE_TAIL = 3;

/** Состояния, которые считаются «готовым шагом»: только состояние карточки 'done'. */
const DONE_STATES = new Set(['done']);

/** Состояния снятых шагов: не считаются незакрытыми и в свёртке не показываются
 * (продуктовое решение, GC-R-F4). */
const WITHDRAWN_STATES = new Set(['withdrawn']);

// Ключ шага в сравнимом виде: без краёв и регистра. Это ПРИБЛИЖЕНИЕ к бэковому
// OrdinalIgnoreCase (FeatureStepAccounting.cs:32): для «Б1»/«б1» оно совпадает,
// но для символов с неоднозначным регистронезависимым сопоставлением («ẞ»/«ß»,
// «İ») toLowerCase может схлопить то, что бэк различает.
function normKey(raw: string | null | undefined): string {
  const s = (raw ?? '').trim().toLowerCase();
  return s;
}

// Ключ элемента: stepRef. Пусто — значит «без ключа». (Запасной ключ id убран:
// у ProdCard поля id нет, только stepRef — находка 7 GC-R-F4; бэковый KeyOf
// падает на id только для задач без stepRef, а здесь элементы — карточки.)
function keyOf(item: Foldable): string {
  return normKey(item.stepRef);
}

// Готов ли шаг: состояние карточки 'done' (бэковая корзина accepted становится
// 'done' в карточке — FeatureProdCards.cardState).
function isDone(item: Foldable): boolean {
  return DONE_STATES.has((item.state ?? '').trim().toLowerCase());
}

// Снят ли шаг: человек его снял — он не будет сделан (GC-R-F4).
function isWithdrawn(item: Foldable): boolean {
  return WITHDRAWN_STATES.has((item.state ?? '').trim().toLowerCase());
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
 * Снятые не показываются ни в одной ветке, включая showAll: ни как незакрытые,
 * ни в хвосте готовых (GC-R-F4). Обе ветки возвращают один и тот же набор шагов —
 * схлопанный и без снятых: счётчик «Показать все N» (visible+hiddenDone свёртки)
 * сходится с тем, что откроется в раскрытой ленте (GC-F4R2).
 *
 * @param cards карточки в исходном порядке (порядок видимых сохраняется);
 * @param expandedKey ключ раскрытой карточки — она видна всегда; null — ничего не раскрыто;
 * @param showAll тот же набор, что считает свёртка (без дублей и без снятых),
 * но хвост готовых не прячется.
 */
export function foldProd<T extends Foldable>(
  cards: T[],
  expandedKey: string | null,
  showAll: boolean,
): { visible: T[]; hiddenDone: T[] } {
  const input = cards ?? [];
  // Набор показываемых шагов один для обеих веток: схлопанный и без снятых
  // (GC-F4R2). Счётчик «Показать все N» берут из свёртки (visible+hiddenDone) —
  // он обязан сходиться с тем, что откроется в раскрытой ленте.
  const source = dedupe(input).filter((c) => !isWithdrawn(c));
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
    // Снятые уже отфильтрованы в source: здесь их нет ни как незакрытых, ни в хвосте.
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
 * Без схлопывания: одинаковые stepRef — разные задачи (шаг и его доработка,
 * GC-F10), длина входа сохраняется.
 *
 * @param items элементы в исходном порядке;
 * @param expandedKey ключ раскрытого элемента — он виден всегда; null — ничего не раскрыто;
 * @param showAll показать ровно то, что пришло (без свёртки);
 * @param n сколько последних элементов остаётся видимыми.
 */
export function foldLast<T extends Foldable>(
  items: T[],
  expandedKey: string | null,
  showAll: boolean,
  n: number = 10,
): { visible: T[]; hiddenEarly: T[] } {
  const source = items ?? [];
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
