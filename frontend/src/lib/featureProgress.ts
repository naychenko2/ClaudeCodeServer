// Честный прогресс фичи по шагам (волна 3, Ф-pr): ОДИН форматтер на все шесть мест
// (плашка, пилюля, попап, индекс, шапка страницы, «Хронология») — никаких локальных
// разночтений. Данные — из `FeatureRun.stepProgress` (бэк считает, фронт только
// показывает) или из строки индекса (features/index) — см. stepProgressFrom*.
//
// Зависимости — только тип (дизайн-токены не нужны: функция строит строку, а не JSX).
//
// Формат (правила задачи):
//   • «принято N из T» — единственный случай, когда цифры идут с предлогом «из»;
//     голый «N из M» без слов УБРАН (читается как «сделано»).
//   • «· R в ревью», «· W в работе», «· ∅N не заведено» — если поле > 0.
//     ↺ НЕ ставим: тот глиф — за возвратами (loops) в итоге смерженной (FeatureRunView
//     mergedSummary «↺K»). Один глиф, два смысла — недопустимо: корзина ревью без глифа.
//   • «· план не разобран» — planState === 'pending'; «· план частично» — 'partial';
//     метки плана нет, если 'cut'/'done' (там и так понятно).
//   • «· X снято» — только withdrawn > 0 (слово «снято», не «отменено»).
//   • «· Y без привязки» — только unlinkedReworks > 0.
//   • Склеиваем «·»; последний элемент без точки.
//   • «готово» — ТОЛЬКО когда accepted === total && total > 0 && planState !== 'pending';
//     никаких «все готово» без чисел.

// Состояние плана (контракт Ф-pr): pending — план не разобран; partial — разобран
// частично; cut/done — метку плана не показываем (разрез/итог говорят сами за себя).
import type { FeatureRun } from '../types/featureRun';
import type { FeatureIndexFeature } from '../types/featureIndex';

export type PlanState = 'pending' | 'partial' | 'cut' | 'done';

// Честный прогресс по шагам (зеркало бэкового `FeatureStepProgress`, camelCase).
// Корзины взаимоисключающие: total = accepted + review + work + notCreated. Вне
// знаменателя: withdrawn (шаг снят с выкладки) и unlinkedReworks (доработки без привязки).
export interface StepProgress {
  planState: PlanState;
  total: number;
  accepted: number;
  review: number;
  work: number;
  notCreated: number;
  withdrawn: number;
  unlinkedReworks: number;
}

// «Готово»: все шаги приняты ревью, знаменатель ненулевой, план не «не разобран».
// Единственная точка правды условия — её дублируют все шесть мест (и тесты).
export function isStepProgressDone(p: StepProgress): boolean {
  return p.total > 0 && p.accepted === p.total && p.planState !== 'pending';
}

// ОДИН форматтер: строка прогресса для плашки/пилюли/попапа/индекса/«Хронологии».
// Порядок сегментов фиксирован: [готово|принято N из T] → [R в ревью] → [W в работе]
// → [∅N не заведено] → [метка плана] → [X снято] → [Y без привязки].
export function formatStepProgress(p: StepProgress): string {
  const parts: string[] = [];
  // База: «готово» (только при полном принятии) либо «принято N из T».
  parts.push(isStepProgressDone(p) ? 'готово' : `принято ${p.accepted} из ${p.total}`);
  // Корзины знаменателя — только ненулевые (не вытесняют «принято» в 0 — база всегда есть).
  if (p.review > 0) parts.push(`${p.review} в ревью`);
  if (p.work > 0) parts.push(`${p.work} в работе`);
  if (p.notCreated > 0) parts.push(`∅${p.notCreated} не заведено`);
  // Метка плана: pending → «не разобран», partial → «частично», cut/done → нет.
  if (p.planState === 'pending') parts.push('план не разобран');
  else if (p.planState === 'partial') parts.push('план частично');
  // Вне знаменателя: снято и без привязки — только ненулевые.
  if (p.withdrawn > 0) parts.push(`${p.withdrawn} снято`);
  if (p.unlinkedReworks > 0) parts.push(`${p.unlinkedReworks} без привязки`);
  // Склеиваем «·»; последний элемент без точки.
  return parts.join(' · ');
}

// Честный прогресс из агрегата фичи (run.stepProgress); null — поле отсутствует
// (старые ответы): место показывает прежний вид, не падает.
export function stepProgressFromRun(run: FeatureRun): StepProgress | null {
  return run.stepProgress ?? null;
}

// Защита от переполнения: отрицательные/NaN значения бэка (баг) не должны давать
// «принято -2 из -5» — обнуляем (честнее, чем минус).
const nonNeg = (n: number): number => (Number.isFinite(n) && n > 0 ? n : 0);

// Честный прогресс из строки индекса (features/index). Индекс несёт
// stepsAccepted/stepsTotal/stepsReview/stepsWork/stepsNotCreated/planState и — вне
// знаменателя — stepsWithdrawn/unlinkedReworks (те же числа, что страница фичи;
// бэк — коммит defd6cd19). null — старые ответы (новых полей нет): место показывает
// прежний вид.
export function stepProgressFromIndex(f: FeatureIndexFeature): StepProgress | null {
  const hasNew = f.stepsReview != null || f.stepsWork != null || f.stepsNotCreated != null
    || f.planState != null || f.stepsWithdrawn != null || f.unlinkedReworks != null;
  if (!hasNew) return null;
  return {
    planState: f.planState ?? 'done',
    total: nonNeg(f.stepsTotal),
    accepted: nonNeg(f.stepsAccepted),
    review: nonNeg(f.stepsReview ?? 0),
    work: nonNeg(f.stepsWork ?? 0),
    notCreated: nonNeg(f.stepsNotCreated ?? 0),
    withdrawn: nonNeg(f.stepsWithdrawn ?? 0),
    unlinkedReworks: nonNeg(f.unlinkedReworks ?? 0),
  };
}
