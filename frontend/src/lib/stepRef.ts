// m-6: заголовок задачи в трекере часто начинается с «{stepRef}: …» (напр. «Ш1: парсер»).
// При показе рядом с моно-номером (stepRef) ведущий «{stepRef}:» (с пробелом) отрезается,
// чтобы не было «Ш1 Ш1: парсер». Общий хелпер: используется в FeatureRunView
// (строка шага, плашка «упал», карточка «Сейчас»).
export function stripStepRefPrefix(title: string, stepRef: string | null | undefined): string {
  const ref = (stepRef ?? '').trim();
  if (!ref) return title;
  const prefix = `${ref}:`;
  if (title.startsWith(prefix)) return title.slice(prefix.length).replace(/^\s+/, '');
  return title;
}

// n-4/r-2: название шага для строки, где рядом уже моно-номер (stepRef): ведущий «{stepRef}:»
// отрезается (stripStepRefPrefix); если результат пуст или равен самому номеру («Ш8 Ш8»),
// название не рисуется (null) — остаётся только номер. Общий хелпер: NowCard (карточка
// «Сейчас») и FeatureRunView (строка шага) — не дублируем логику в двух местах.
export function stepDisplayName(title: string, stepRef: string | null | undefined): string | null {
  const stripped = stripStepRefPrefix(title, stepRef);
  const ref = (stepRef ?? '').trim();
  if (!stripped || stripped === ref) return null;
  return stripped;
}
