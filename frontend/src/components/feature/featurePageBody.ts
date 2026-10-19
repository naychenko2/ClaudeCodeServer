// Чистая логика тела страницы фичи (волна 2 «Обзор фич», шаг Ф2.5).
// Фронт НЕ пересчитывает — только раскладывает: конвейер (PipelineFlow),
// шаги (stepChains), чаты (из индекса), задачи (счётчики), лента (events).
// Все функции чистые: тестируются в node без DOM.

import type { FeaturesIndex, FeatureIndexFeature, FeatureChatLink, FeatureEvent } from '../../types/featureIndex';
import type { FeatureRun } from '../../types/featureRun';

// === Чаты фичи ===

// Связь «чат × фича»: sessionId + роль/stepRef/lane.
export interface FeatureChatEntry {
  sessionId: string;
  link: FeatureChatLink;
  // Ключевой чат фичи (feature.keySessionId === sessionId).
  isKey: boolean;
  // Ключевой чат в архиве (feature.keyArchived && isKey).
  isArchived: boolean;
}

// Чаты фичи из индекса: все sessionId, у которых есть связь с featureId.
// Ключевой чат — первым (feature.keySessionId); остальные — в порядке индекса.
// «Без новых запросов»: данные — только из FeaturesIndex.chats.
export function featureChats(index: FeaturesIndex, feature: FeatureIndexFeature | null): FeatureChatEntry[] {
  if (!feature) return [];
  const keyId = feature.keySessionId ?? null;
  const out: FeatureChatEntry[] = [];
  const seen = new Set<string>();
  for (const [sessionId, links] of Object.entries(index.chats)) {
    if (seen.has(sessionId)) continue;
    for (const link of links) {
      if (link.featureId !== feature.featureId) continue;
      seen.add(sessionId);
      const isKey = sessionId === keyId;
      out.push({
        sessionId,
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

// Строка шага для списка: stepRef, название, линия, исход, число витков.
export interface StepRow {
  stepRef: string;
  title: string;
  lane: string;
  outcome: string;
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
    rounds: c.rounds.length,
  }));
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
// Статус: done / inProgress / todo.
export function taskCounters(run: FeatureRun): TaskCounters {
  const all = [
    run.epic,
    ...run.steps,
    ...run.reviews,
    ...run.qa,
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

// Строка события для рендера: время, вид, исполнитель, чат (если есть).
export interface EventRow {
  at: string;
  kind: string;
  personaId: string | null;
  // Ссылка на чат: sessionId ≠ null → есть ссылка; null — нет.
  sessionId: string | null;
  reason: string | null;
}

// События из page.events: порядок — как в бэке (at desc).
// Событие без sessionId — не даёт ссылки на чат (fail-closed).
export function eventRows(events: FeatureEvent[]): EventRow[] {
  return events.map(e => ({
    at: e.at,
    kind: e.kind,
    personaId: e.personaId ?? null,
    sessionId: e.sessionId ?? null,
    reason: e.reason ?? null,
  }));
}

// «Показать все» — нужно, если лента обрезана (events.length < eventsTotal).
export function showAllNeeded(events: FeatureEvent[], eventsTotal: number): boolean {
  return events.length < eventsTotal;
}
