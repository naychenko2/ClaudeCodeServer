// Сборка текста обратной связи по списку замечаний к плану.
import { PLAN_GENERAL_HEADING } from './PlanRemarks';
//
// Формат (визуально):
//
//   Раздел «Заголовок» → текст замечания
//   > цитата из выделения
//
//   Раздел «Заголовок» (2-й) → другое замечание
//
//   Разделы без замечаний согласованы
//
// Замечания группируются по якорю: паре (текст заголовка, порядковый номер
// вхождения в плане). Если в плане два раздела с одинаковым заголовком — у
// них разные индексы, поэтому собираются в разные группы. В тексте подпись
// приобретает «(N-й)» ТОЛЬКО когда в списке замечаний действительно есть
// разные индексы у одного заголовка: «(1-й)» у единственного раздела —
// мусор в глазах читателя.
//
// Порядок разделов — как они идут в плане (headingOrder): позиция первого
// вхождения заголовка в плане задаёт место группы. Замечания на заголовок,
// которого нет в плане, идут в хвосте — так раздел не «повиснет» в воздухе
// из-за опечатки в якоре.
//
// Пустой список замечаний → одна строка «Разделы без замечаний согласованы».
// Планировщик читает её как сигнал: пользователь не согласовал план полностью,
// но и не указал, что править — переделывает целиком.

export interface PlanRemark {
  // Точный текст заголовка раздела (как отрендерил Markdown*)
  anchorHeading: string;
  // 0-based номер вхождения этого заголовка в плане. По умолчанию 0 —
  // обратная совместимость со старыми замечаниями и с обычным случаем
  // «заголовок в плане один». У одноимённых разделов в плане индексы
  // разные: иначе замечания к разным вхождениям склеиваются в одну группу.
  anchorIndex?: number;
  // Цитата выделения внутри раздела — по ней планировщик найдёт место
  quote?: string;
  // Текст замечания от пользователя
  text: string;
}

export const PLAN_FEEDBACK_FOOTER = 'Разделы без замечаний согласованы';

interface Anchor {
  // Текст заголовка
  heading: string;
  // 0-based номер вхождения; отсутствие anchorIndex в замечании = 0
  index: number;
}

const anchorOf = (r: PlanRemark): Anchor => ({
  heading: r.anchorHeading,
  index: r.anchorIndex ?? 0,
});

// Ключ для Map как JSON-строка — пара (heading, index) детерминированно
// кодируется в одну строку, коллизий при любом содержимом heading нет
const keyOf = (a: Anchor): string => JSON.stringify([a.heading, a.index]);
const parseKey = (k: string): Anchor => {
  const parsed = JSON.parse(k) as [string, number];
  return { heading: parsed[0], index: parsed[1] };
};

export function buildPlanFeedback(
  remarks: readonly PlanRemark[],
  headingOrder: readonly string[],
): string {
  if (remarks.length === 0) return PLAN_FEEDBACK_FOOTER;

  // Группировка по якорю (heading + index). Map сохраняет порядок вставки —
  // её и используем для сортировки по headingOrder ниже
  const byAnchor = new Map<string, PlanRemark[]>();
  for (const r of remarks) {
    const k = keyOf(anchorOf(r));
    const list = byAnchor.get(k) ?? [];
    list.push(r);
    byAnchor.set(k, list);
  }

  // УНИКАЛЬНЫЙ заголовок с одним вхождением в подписи «(N-й)» не нуждается —
  // шум. Считаем только заголовки, у которых в СПИСКЕ замечаний есть несколько
  // РАЗНЫХ индексов; на единственный индекс суффикс не вешаем
  const distinctByHeading = new Map<string, Set<number>>();
  for (const k of byAnchor.keys()) {
    const a = parseKey(k);
    const set = distinctByHeading.get(a.heading) ?? new Set<number>();
    set.add(a.index);
    distinctByHeading.set(a.heading, set);
  }
  const needsIndexSuffix = (heading: string): boolean =>
    (distinctByHeading.get(heading)?.size ?? 0) > 1;

  // Стабильная сортировка. У одной группы две координаты:
  //  1) позиция заголовка в headingOrder (по первому вхождению) — основная;
  //  2) индекс вхождения — при равной позиции заголовка идём по возрастанию,
  //     ниже в документе = раньше в выводе.
  //  Заголовок, не найденный в плане (опечатка в якоре), — в хвосте.
  //  Этого достаточно, чтобы ключ был детерминированным без явной нумерации.
  const firstPos = new Map<string, number>();
  headingOrder.forEach((h, i) => {
    if (!firstPos.has(h)) firstPos.set(h, i);
  });

  const orderedKeys = [...byAnchor.keys()].sort((a, b) => {
    const ka = parseKey(a);
    const kb = parseKey(b);
    const ai = firstPos.get(ka.heading);
    const bi = firstPos.get(kb.heading);
    if (ai === undefined && bi === undefined) return 0;
    if (ai === undefined) return 1;
    if (bi === undefined) return -1;
    if (ai !== bi) return ai - bi;
    return ka.index - kb.index;
  });

  const blocks: string[] = [];
  for (const k of orderedKeys) {
    const a = parseKey(k);
    const items = byAnchor.get(k) ?? [];
    const suffix = needsIndexSuffix(a.heading) ? ` (${a.index + 1}-й)` : '';
    // Общий якорь — не раздел плана, оборачивать в «Раздел «…»» нельзя:
    // планировщик прочтёт это как имя раздела и пойдёт его искать в документе.
    // У реальных заголовков формат прежний.
    const isGeneral = a.heading === PLAN_GENERAL_HEADING;
    for (const r of items) {
      const head = isGeneral
        ? `${PLAN_GENERAL_HEADING}${suffix} → ${r.text}`
        : `Раздел «${a.heading}»${suffix} → ${r.text}`;
      const lines = [head];
      const q = r.quote?.trim();
      if (q) lines.push(`> ${q}`);
      blocks.push(lines.join('\n'));
    }
  }

  blocks.push(PLAN_FEEDBACK_FOOTER);
  return blocks.join('\n\n');
}
