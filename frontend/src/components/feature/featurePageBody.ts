// Чистая логика тела страницы фичи (волна 2 «Обзор фич», шаг Ф2.5).
// Фронт НЕ пересчитывает — только раскладывает: конвейер (PipelineFlow),
// шаги (stepChains), чаты (из индекса), задачи (счётчики), лента (events).
// Все функции чистые: тестируются в node без DOM.

import type { FeaturesIndex, FeatureIndexFeature, FeatureChatLink, FeatureEvent } from '../../types/featureIndex';
import type { FeatureRun } from '../../types/featureRun';

// === Чаты фичи ===

// Связь «чат × фича»: sessionId + роль/stepRef/lane.
// name — название чата из уже загруженного списка сессий проекта (M4: без новых
// запросов); null — названия нет (чат вне проекта / список ещё не загружен) → «Чат».
export interface FeatureChatEntry {
  sessionId: string;
  name: string | null;
  link: FeatureChatLink;
  // Ключевой чат фичи (feature.keySessionId === sessionId).
  isKey: boolean;
  // Ключевой чат в архиве (feature.keyArchived && isKey).
  isArchived: boolean;
}

// Чаты фичи из индекса: все sessionId, у которых есть связь с featureId.
// Ключевой чат — первым (feature.keySessionId); остальные — в порядке индекса.
// «Без новых запросов»: данные — только из FeaturesIndex.chats; названия — из
// chatNames (список сессий проекта, уже загружен SessionList'ом).
export function featureChats(
  index: FeaturesIndex,
  feature: FeatureIndexFeature | null,
  chatNames?: Record<string, string>,
): FeatureChatEntry[] {
  if (!feature) return [];
  const keyId = feature.keySessionId ?? null;
  const out: FeatureChatEntry[] = [];
  const seen = new Set<string>();
  for (const [sessionId, links] of Object.entries(index.chats ?? {})) {
    if (seen.has(sessionId)) continue;
    for (const link of links) {
      if (link.featureId !== feature.featureId) continue;
      seen.add(sessionId);
      const isKey = sessionId === keyId;
      const name = chatNames?.[sessionId];
      out.push({
        sessionId,
        name: name ? name : null,
        link,
        isKey,
        isArchived: isKey && feature.keyArchived,
      });
      break; // один чат — одна запись (сильнейшая роль)
    }
  }
  // Ключевой чат — первым.
  out.sort((a, b) => {
    if (a.isKey && !b.isKey) return -1;
    if (!a.isKey && b.isKey) return 1;
    return 0;
  });
  return out;
}

// === Шаги ===

// Строка шага для списка: stepRef, название, линия, исход, готовая подпись бэка,
// число витков.
export interface StepRow {
  stepRef: string;
  title: string;
  lane: string;
  outcome: string;
  // Готовая подпись бэка (chain.status: «ждёт ревью», «закрыт без повторного ревью»,
  // «принят», «идёт», «снят с выкладки»); null — бэк не отдал (старый ответ).
  status: string | null;
  rounds: number;
}

// Шаги из stepChains: порядок — как в бэке (не пересортировываем).
// «Шагов нет» — если stepChains пусто.
export function stepRows(run: FeatureRun): StepRow[] {
  const chains = run.stepChains ?? [];
  return chains.map(c => ({
    stepRef: c.stepRef,
    title: c.title,
    lane: c.lane ?? '',
    outcome: c.outcome,
    status: c.status ?? null,
    rounds: c.rounds?.length ?? 0,
  }));
}

// Подпись исхода шага (M2, одна точка правды): готовая подпись бэка chain.status
// (те же слова, что «Конвейер»/PipelineFlow). Бэк не отдал status (старый ответ) —
// собственные слова бэка по outcome (FeatureProjector.BuildStepChains), не трактовка
// фронта: pending = «ждёт ревью» (шаг сделан, ревью ещё не было),
// returned = «закрыт без повторного ревью».
const OUTCOME_FALLBACK: Record<string, string> = {
  accepted: 'принят',
  running: 'идёт',
  returned: 'закрыт без повторного ревью',
  pending: 'ждёт ревью',
  withdrawn: 'снят с выкладки',
  accepted_with_remarks: 'доработка',
};
export function stepStatusLabel(outcome: string, status: string | null): string {
  if (status) return status;
  return OUTCOME_FALLBACK[outcome] ?? outcome;
}

// === Задачи ===

// Счётчики задач: готово / в работе / впереди.
export interface TaskCounters {
  done: number;
  inProgress: number;
  pending: number;
  total: number;
}

// Счётчики из run: epic + steps + reviews + qa + final + merge + cut + plan.
// Статус: done / inProgress / todo. Контракт: UI обязан пережить отсутствие любого
// поля ответа — списки, которых бэк не отдал, раскладываются как пустые.
export function taskCounters(run: FeatureRun): TaskCounters {
  const all = [
    run.epic,
    ...(run.steps ?? []),
    ...(run.reviews ?? []),
    ...(run.qa ?? []),
    ...(run.final ? [run.final] : []),
    ...(run.merge ? [run.merge] : []),
    ...(run.cut ?? []),
    ...(run.plan ?? []),
  ];
  let done = 0, inProgress = 0, pending = 0;
  for (const t of all) {
    if (t.status === 'done') done++;
    else if (t.status === 'inProgress') inProgress++;
    else pending++;
  }
  return { done, inProgress, pending, total: all.length };
}

// === Лента событий ===

// Строка события для рендера: время, вид, исполнитель, роль/шаг, чат (если есть).
export interface EventRow {
  at: string;
  kind: string;
  personaId: string | null;
  // Роль задачи (feature · cut · plan · step · rework · review · qa · final · merge · consult).
  role: string | null;
  // Код шага («Ш1», «D1c»); у step/rework, иначе null.
  stepRef: string | null;
  // Ссылка на чат: sessionId ≠ null → есть ссылка; null — нет.
  sessionId: string | null;
  reason: string | null;
}

// События из page.events: порядок — как в бэке (at desc).
// Событие без sessionId — не даёт ссылки на чат (fail-closed).
// Контракт: UI обязан пережить отсутствие любого поля ответа (events/eventsTotal
// нормализует вызывающий; здесь — полевые ?? null).
export function eventRows(events: FeatureEvent[]): EventRow[] {
  return (events ?? []).map(e => ({
    at: e.at,
    kind: e.kind,
    personaId: e.personaId ?? null,
    role: e.role ?? null,
    stepRef: e.stepRef ?? null,
    sessionId: e.sessionId ?? null,
    reason: e.reason ?? null,
  }));
}

// «Показать все» — нужно, если лента обрезана (events.length < eventsTotal).
export function showAllNeeded(events: FeatureEvent[], eventsTotal: number): boolean {
  return events.length < eventsTotal;
}
