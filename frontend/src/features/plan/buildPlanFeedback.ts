// Сборка текста обратной связи по списку замечаний к плану.
//
// Формат (визуально):
//
//   Раздел «Заголовок» → текст замечания
//   > цитата из выделения
//
//   Раздел «Заголовок» → другое замечание
//
//   Разделы без замечаний согласованы
//
// Замечания группируются по заголовку раздела. Порядок разделов — как они идут
// в плане (headingOrder). Замечания на заголовок, которого нет в плане, идут
// в хвосте — так раздел не «повиснет» в воздухе из-за опечатки в якоре.
//
// Пустой список замечаний → одна строка «Разделы без замечаний согласованы».
// Планировщик читает её как сигнал: пользователь не согласовал план полностью,
// но и не указал, что править — переделывает целиком.

export interface PlanRemark {
  // Точный текст заголовка раздела (как отрендерил Markdown*)
  anchorHeading: string;
  // Цитата выделения внутри раздела — по ней планировщик найдёт место
  quote?: string;
  // Текст замечания от пользователя
  text: string;
}

export const PLAN_FEEDBACK_FOOTER = 'Разделы без замечаний согласованы';

export function buildPlanFeedback(
  remarks: readonly PlanRemark[],
  headingOrder: readonly string[],
): string {
  if (remarks.length === 0) return PLAN_FEEDBACK_FOOTER;

  // Группировка по заголовку. Map сохраняет порядок вставки — её и используем
  // для сортировки по headingOrder ниже
  const byHeading = new Map<string, PlanRemark[]>();
  for (const r of remarks) {
    const list = byHeading.get(r.anchorHeading) ?? [];
    list.push(r);
    byHeading.set(r.anchorHeading, list);
  }

  const orderIndex = new Map<string, number>();
  headingOrder.forEach((h, i) => orderIndex.set(h, i));

  // Стабильная сортировка: известные заголовки — по их индексу в headingOrder;
  // неизвестные (опечатка в якоре) — равномерно после, в порядке появления в remarks.
  // Этого достаточно, чтобы ключ был детерминированным без явной нумерации
  const ordered = [...byHeading.keys()].sort((a, b) => {
    const ai = orderIndex.get(a);
    const bi = orderIndex.get(b);
    if (ai === undefined && bi === undefined) return 0;
    if (ai === undefined) return 1;
    if (bi === undefined) return -1;
    return ai - bi;
  });

  const blocks: string[] = [];
  for (const heading of ordered) {
    const items = byHeading.get(heading) ?? [];
    for (const r of items) {
      const lines = [`Раздел «${heading}» → ${r.text}`];
      const q = r.quote?.trim();
      if (q) lines.push(`> ${q}`);
      blocks.push(lines.join('\n'));
    }
  }

  blocks.push(PLAN_FEEDBACK_FOOTER);
  return blocks.join('\n\n');
}
