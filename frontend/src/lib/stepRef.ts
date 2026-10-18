// m-6: заголовок задачи в трекере часто начинается с «{stepRef}: …» (напр. «Ш1: парсер»).
// При показе рядом с моно-номером (stepRef) ведущий «{stepRef}:» (с пробелом) отрезается,
// чтобы не было «Ш1 Ш1: парсер». Общий хелпер: используется и в PipelineFlow (строка шага),
// и в FeatureRunView (плашка «упал» и карточка «Сейчас»).
export function stripStepRefPrefix(title: string, stepRef: string | null | undefined): string {
  const ref = (stepRef ?? '').trim();
  if (!ref) return title;
  const prefix = `${ref}:`;
  if (title.startsWith(prefix)) return title.slice(prefix.length).replace(/^\s+/, '');
  return title;
}
