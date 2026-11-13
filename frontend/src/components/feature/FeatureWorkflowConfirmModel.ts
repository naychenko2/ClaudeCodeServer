// Модель блока подтверждения маршрута фичи (feature-workflows, C2; разрез docs/research/
// ai-fabrika-workflow-cut-2026-11.md §4–§5): чистые функции по шаблону, предложению и
// подтверждённому маршруту — ни DOM, ни запросов. Истина остаётся сервером (confirmWorkflow
// проверяет тело): здесь — форма запроса, начальное выбранное и строка «шаблон изменился».
//
// Этапы опознаются ПО ID, не по роли: в шаблоне бывает два этапа одной роли (например
// 'qa-plan' и 'qa' — повтор разрешён для design/plan/step/review/qa, SingleRoles на бэке).
// Нормализация id ??= role — из normalizeStages (G6a), своей здесь нет: любое сравнение
// составов и skipped строятся на id.

import { normalizeStages } from '../../features/workflowTemplates/workflowGraphModel';
import { isNonSkippable } from '../../features/workflowTemplates/workflowTemplateModel';
import type {
  ConfirmWorkflowBody, RouteStage, WorkflowProposal, WorkflowStage,
  WorkflowTemplate, WorkflowTemplateEntry, WorkflowTemplateScope,
} from '../../features/workflowTemplates/types';

// Подтверждённый маршрут эпика — зеркало серверного FeatureWorkflow (TaskItem.Workflow),
// только поля блока. C3b несёт epic.workflow как есть; лишних полей (confirmedAt/confirmedByUser)
// здесь нет — они блоку не нужны.
export interface ConfirmedRoute {
  templateId: string;
  scope: WorkflowTemplateScope;
  // Этапы маршрута после Resolve (сняттых и 'off' в них уже нет); порядок — топологический.
  stages: RouteStage[];
  // Id этапов, снятых при подтверждении (среди них — только optional).
  skipped: string[];
  // Версия шаблона на момент подтверждения: с неё сравнивается «шаблон изменился».
  confirmedVersion: number;
  // «Оставить vN» (B03): версия шаблона, обновление до которой человек отклонил; предупреждение
  // «шаблон изменился» не возвращается, пока версия шаблона не вырастет выше.
  dismissedUpdateVersion?: number | null;
  // Подтверждение сделано моделью (бэк: `FeatureWorkflow.AutoConfirmed`): true — строка
  // блока говорит «выбрано моделью — можно сменить». Отсутствует/false — «подтверждён вами».
  autoConfirmed?: boolean;
  // Из предложения, которое сервер не стирает при подтверждении (TaskWorkflowController.ConfirmedDto):
  // обоснование относится к шаблону ПРЕДЛОЖЕНИЯ (proposedTemplateId), не обязательно подтверждённого.
  rationale?: string | null;
  proposedAt?: string | null;
  proposedTemplateId?: string | null;
  needsHuman?: boolean | null;
  doubtReason?: string | null;
}

// Съёмный ли этап: presence='optional' и роль съёмная. Несъёмные (step/merge/final) — без
// переключателя в UI и никогда в skipped, даже на битых данных (валидатор таких шаблонов
// не пропускает, но модель не полагается на это).
export function isRemovableStage(stage: WorkflowStage): boolean {
  return (stage?.presence ?? '').trim() === 'optional' && !isNonSkippable((stage?.role ?? '').trim());
}

// Id этапов, которые человек может снять в этом шаблоне (порядок — как в маршруте).
export function removableIds(template: WorkflowTemplate): string[] {
  return normalizeStages(template?.stages ?? []).filter(isRemovableStage).map(s => s.id ?? '');
}

// Начальное выбранное при показе шаблона: снятие предложения модели, но только те id,
// которые действительно съёмны в ПОКАЗАННОМ шаблоне. Предложение к другому шаблону —
// пустое снятие: чужие id вслепую не снимаем.
export function initialRemovedIds(template: WorkflowTemplate, proposal: WorkflowProposal | null): string[] {
  const allowed = new Set(removableIds(template));
  return (proposal?.skipped ?? []).filter(id => allowed.has(id));
}

// Тело POST /api/tasks/{id}/workflow/confirm (B4b): skipped — только id снятых optional-этапов.
// Несъёмные и чужие id не попадают ни при каком входе вызывающего (тот же отсев, что на бэке:
// «id несъёмного или неизвестного этапа игнорируется»).
export function confirmBodyOf(
  template: WorkflowTemplate,
  scope: WorkflowTemplateScope,
  removedIds: readonly string[],
): ConfirmWorkflowBody {
  // Порядок skipped — как в маршруте (removableIds), а не как их принёс вызывающий:
  // тело запроса не зависит от порядка кликов в UI.
  const removed = new Set(removedIds);
  return { templateId: template.id, scope, skipped: removableIds(template).filter(id => removed.has(id)) };
}

// Что ТЕКУЩИЙ шаблон дал бы маршруту при ТОМ ЖЕ снятии, что подтвердил человек: без 'off'
// и без optional-этапов из подтверждённого skipped. Именно состав маршрута (какие этапы в
// него войдут), а не список шаблона: правка промпта/модели/названия состава не меняет.
// Этапы presence='off' с обеих сверки вне: они не были в маршруте и не записаны в skipped —
// считать их было бы ложная «перемена» при живом шаблоне с выключенным этапом.
function activeIdsOfTemplate(template: WorkflowTemplate, skipped: string[]): Set<string> {
  const skippedSet = new Set(skipped ?? []);
  return new Set(
    normalizeStages(template?.stages ?? [])
      .filter(s => (s.presence ?? '').trim() !== 'off' && !((s.presence ?? '').trim() === 'optional' && skippedSet.has(s.id ?? '')))
      .map(s => s.id ?? ''),
  );
}

// Состав маршрута изменился — только когда наборы id различаются (§4: состав фиксируется
// подтверждением человека, правка шаблона сам его не меняет). С левой стороны — id
// подтверждённого маршрута (его stages — уже после Resolve), с правой — что дал бы текущий
// шаблон при том же снятии. Та же версия с теми же id — не перемена.
export function templateChanged(route: ConfirmedRoute, template: WorkflowTemplate): boolean {
  const confirmed = new Set((route.stages ?? []).map(s => s.id));
  const current = activeIdsOfTemplate(template, route.skipped);
  if (confirmed.size !== current.size) return true;
  for (const id of confirmed) if (!current.has(id)) return true;
  return false;
}

// Строка «обновить маршрут» (§4): подтверждённая версия старше текущей И состав правда
// другой. Иначе null — блок не намекает на перемену без причины.
export function templateUpdateText(route: ConfirmedRoute | null, template: WorkflowTemplate | null): string | null {
  if (!route || !template) return null;
  if (!(route.confirmedVersion < template.version)) return null;
  if ((route.dismissedUpdateVersion ?? 0) >= template.version) return null;
  if (!templateChanged(route, template)) return null;
  return `Шаблон изменился: v${route.confirmedVersion} → v${template.version}, обновить маршрут?`;
}

// Этапы, которых не было в подтверждённом маршруте, а текущий шаблон при том же снятии их
// включил бы (порядок — как в шаблоне). Состав прежнего снятия берётся из route.skipped,
// а не из предложения модели.
export function newStageIds(route: ConfirmedRoute, template: WorkflowTemplate): string[] {
  const confirmed = new Set((route.stages ?? []).map(s => s.id));
  const current = activeIdsOfTemplate(template, route.skipped);
  return normalizeStages(template?.stages ?? []).map(s => s.id ?? '').filter(id => current.has(id) && !confirmed.has(id));
}

// Пояснение карточки «шаблон изменился»: какие этапы v2 стали обязательными (presence 'on'),
// какие добавлены «по решению». Названия — title этапа, иначе подпись роли, иначе id.
export function updateExplainText(
  route: ConfirmedRoute,
  template: WorkflowTemplate,
  roleLabel: (role: string) => string,
): string {
  const ids = new Set(newStageIds(route, template));
  const stages = normalizeStages(template?.stages ?? []).filter(s => ids.has(s.id ?? ''));
  const name = (s: WorkflowStage) => `«${(s.title ?? '').trim() || roleLabel(s.role) || s.id}»`;
  const required = stages.filter(s => (s.presence ?? '').trim() !== 'optional');
  const optional = stages.filter(s => (s.presence ?? '').trim() === 'optional');
  const v = `v${template.version}`;
  const parts: string[] = [];
  if (required.length === 1) parts.push(`В ${v} ${name(required[0])} стала обязательной.`);
  else if (required.length > 1) parts.push(`В ${v} обязательными стали ${required.map(name).join(', ')}.`);
  if (optional.length > 0) parts.push(`В ${v} добавлено по решению: ${optional.map(name).join(', ')}.`);
  if (parts.length === 0) parts.push(`В ${v} состав этапов изменился.`);
  parts.push('Настройки этапов действуют со следующего шага без подтверждения.');
  return parts.join(' ');
}

// Какой шаблон показать первым: предложение модели, затем шаблон подтверждённого маршрута
// (предложение могло исчезнуть из каталога — подтверждённый остаётся). Искать — по id+scope:
// один id может жить в обеих областях. Ничего не предложено и не подтверждено — null:
// шаблон выбирает человек.
export function pickInitialTemplate(
  entries: WorkflowTemplateEntry[],
  proposal: WorkflowProposal | null,
  confirmed: ConfirmedRoute | null,
): WorkflowTemplateEntry | null {
  const wanted = [
    proposal ? { id: proposal.templateId, scope: proposal.scope } : null,
    confirmed ? { id: confirmed.templateId, scope: confirmed.scope } : null,
  ].filter((w): w is { id: string; scope: WorkflowTemplateScope } => w !== null);
  for (const w of wanted) {
    const hit = entries.find(e => e.template?.id === w.id && e.scope === w.scope);
    if (hit) return hit;
  }
  return null;
}

// Гонка «Подобрать маршрут»: ответ применяется только если фича, для которой его просили,
// всё ещё текущая. Устаревший ответ — null: чужое предложение не перетирает выбор
// показанной фичи.
export function applyRecommend(
  requestedFeatureId: string,
  currentFeatureId: string,
  proposal: WorkflowProposal | null,
): WorkflowProposal | null {
  return requestedFeatureId === currentFeatureId ? proposal : null;
}

// Состояние блока маршрута на экране фичи (M4): 'none' — режим маршрута, но предложения
// нет (модель не ответила / ответ не разобран); 'proposal' — предложение есть;
// 'confirmed' — маршрут подтверждён; null — не режим маршрута (легаси-эпик): блока нет.
// Порядок решения — по силе факта: подтверждённый важнее предложения, предложение важнее
// пустоты; routeRequired включает блок только когда ни того, ни другого нет.
export type RouteBlockState = 'none' | 'proposal' | 'confirmed';

export function routeBlockState(
  routeRequired: boolean | null | undefined,
  proposal: WorkflowProposal | null | undefined,
  confirmed: ConfirmedRoute | null | undefined,
): RouteBlockState | null {
  if (confirmed) return 'confirmed';
  // {state:'none'} — «модель не предложила»: предложением не считается, блок в режиме маршрута.
  if (proposal && proposal.state !== 'none') return 'proposal';
  if (proposal && proposal.state === 'none') return 'none';
  if (routeRequired === true) return 'none';
  return null;
}

// Вид маршрута фичи для страницы (F08): состояние блока маршрута либо 'legacy' — режима
// маршрута нет вовсе (эпик создан до шаблонов).
export type RunRouteKind = RouteBlockState | 'legacy';

export function runRouteKind(epic: {
  routeRequired?: boolean | null;
  workflowProposal?: WorkflowProposal | null;
  workflow?: ConfirmedRoute | null;
} | null | undefined): RunRouteKind {
  return routeBlockState(epic?.routeRequired, epic?.workflowProposal, epic?.workflow) ?? 'legacy';
}

// Сводка подтверждённого маршрута (R25): «Имя vN · снято: … / все этапы». Этапы маршрута
// в строку не перечисляются — они только в графе. Имя снятого этапа — title этапа шаблона
// с этим id (после нормализации id = роль), иначе сам id: замороженного RouteStage.Title
// здесь нет — строка строится по шаблону, он под рукой.
export function confirmedSummaryText(route: ConfirmedRoute, template: WorkflowTemplate | null): string {
  const head = `${template?.name || route.templateId} v${route.confirmedVersion}`;
  const skipped = route.skipped ?? [];
  if (skipped.length === 0) return `${head} · все этапы`;
  const titles = new Map(
    normalizeStages(template?.stages ?? [])
      .map(s => [(s.id ?? '').trim(), (s.title ?? '').trim()] as const)
      .filter(([id, title]) => id && title),
  );
  return `${head} · снято: ${skipped.map(id => titles.get(id) ?? id).join(', ')}`;
}

// Кто подтвердил (R25/R26): человек или модель сама, когда выбор был очевиден.
export function confirmedByText(route: ConfirmedRoute): string {
  return route.autoConfirmed === true
    ? 'выбрано моделью автоматически — выбор был очевиден'
    : 'подтвердил человек';
}

// Постоянная строка о подтверждённом маршруте (M4): сводка и кто подтвердил одной строкой.
export function confirmedLineText(route: ConfirmedRoute, template: WorkflowTemplate | null): string {
  return `${confirmedSummaryText(route, template)} · ${confirmedByText(route)}`;
}

// Причины сомнения модели (серверный WorkflowDoubtReasons): человеческая подпись к тегу
// «ждёт вас»; незнакомое значение показывается как есть, пустое — без пояснения.
const DOUBT_LABEL: Record<string, string> = {
  'no-fit': 'ни один шаблон не подходит под описание',
  'ambiguous': 'несколько шаблонов подходят одинаково',
  'custom-route': 'нужен маршрут сложнее, чем снятие этапов',
  'low-confidence': 'выбор неочевиден',
};
export function doubtReasonText(reason: string | null | undefined): string | null {
  const r = (reason ?? '').trim();
  return r ? DOUBT_LABEL[r] ?? r : null;
}

// Подпись автора обоснования: «Flash · 12.11 18:02 · обоснование модели». Нет даты или
// модели — без них; даты нет в данных — не выдумываем.
export function rationaleByText(model: string | null | undefined, proposedAt: string | null | undefined, suffix: string): string {
  const parts: string[] = [];
  if ((model ?? '').trim()) parts.push(model!.trim());
  const d = proposedAt ? new Date(proposedAt) : null;
  if (d && !Number.isNaN(d.getTime())) {
    const pad = (n: number) => String(n).padStart(2, '0');
    parts.push(`${pad(d.getDate())}.${pad(d.getMonth() + 1)} ${pad(d.getHours())}:${pad(d.getMinutes())}`);
  }
  if (suffix) parts.push(suffix);
  return parts.join(' · ');
}
