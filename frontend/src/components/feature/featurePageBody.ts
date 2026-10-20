// Чистая логика тела страницы фичи (волна 2 «Обзор фич», шаг Ф2.5).
// Фронт НЕ пересчитывает — только раскладывает: конвейер (PipelineFlow),
// шаги (stepChains), чаты (из индекса), задачи (счётчики), лента (events).
// Все функции чистые: тестируются в node без DOM.

import { laneOfChain } from '../chat/feature/PipelineFlow';
import { hhmm, dateOnly } from '../chat/feature/featureTime';
import type { FeaturesIndex, FeatureIndexFeature, FeatureChatLink, FeatureEvent } from '../../types/featureIndex';
import type { FeatureRun } from '../../types/featureRun';

// === Чаты фичи ===

// Связь «чат × фича»: sessionId + роль/stepRef/lane.
// name — название чата из уже загруженного списка сессий проекта (M4: без новых
// запросов); null — названия нет (чат вне проекта / список ещё не загружен) → «Чат».
// personaId — персона чата (из списка сессий); null — персоне не ведётся.
export interface FeatureChatEntry {
  sessionId: string;
  name: string | null;
  link: FeatureChatLink;
  // Ключевой чат фичи (feature.keySessionId === sessionId).
  isKey: boolean;
  // Ключевой чат в архиве (feature.keyArchived && isKey).
  isArchived: boolean;
  personaId: string | null;
}

// Роль чата (FeatureChatLink.kind) → русское слово — общий словарь (lib/featureChatRole,
// один с плашкой FeatureBanner; M1: не сырые коды curator/executor/orchestrator).

// Чаты фичи из индекса: все sessionId, у которых есть связь с featureId.
// Ключевой чат — первым (feature.keySessionId); остальные — в порядке индекса.
// «Без новых запросов»: данные — только из FeaturesIndex.chats; названия и персоны —
// из chatNames/chatPersonas (список сессий проекта, уже загружен SessionList'ом).
export function featureChats(
  index: FeaturesIndex,
  feature: FeatureIndexFeature | null,
  chatNames?: Record<string, string>,
  chatPersonas?: Record<string, string>,
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
        personaId: chatPersonas?.[sessionId] ?? null,
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

// Код шага не повторяется в названии (M4): бэковый title может начинаться с кода
// («Ш1 · индекс поиска») — в этом случае код и разделитель срезается: код уже
// выводится отдельно слева в строке. «Ш10» для кода «Ш1» не режется (посмотрка
// на следующий символ: он должен быть разделителем или концом строки).
export function stripStepCode(title: string, stepRef: string): string {
  if (!stepRef || !title) return title;
  const esc = stepRef.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const m = title.match(new RegExp(`^\\s*${esc}(?=\\s|[·:—-]|$)`));
  if (!m) return title;
  const rest = title.slice(m[0].length).replace(/^\s*[·:—-]\s*/, '');
  return rest.trim() ? rest : title;
}

// Линия шага (back/front/прочее) → русское слово (M4: не английский код).
// Прочие линии (тест, дизайн) — как в данных.
export function laneLabel(lane: string): string {
  const l = laneOfChain(lane);
  if (l === 'back') return 'бэк';
  if (l === 'front') return 'фронт';
  return lane;
}

// Чат шага: чат из индекса, у которого связь executor к этой фиче с тем же
// stepRef. Такого чата нет — шаг никуда не ведёт (строка — текст, не кнопка, M4).
function stepChatSessionId(index: FeaturesIndex, feature: FeatureIndexFeature, stepRef: string): string | null {
  for (const [sessionId, links] of Object.entries(index.chats ?? {})) {
    for (const link of links) {
      if (link.featureId === feature.featureId && link.kind === 'executor' && link.stepRef === stepRef) {
        return sessionId;
      }
    }
  }
  return null;
}

// Строка шага для списка: stepRef, название (без повторённого кода), линия,
// исполнитель, чат шага (null — не ведёт), исход, готовая подпись бэка, число витков.
export interface StepRow {
  stepRef: string;
  title: string;
  lane: string;
  personaId: string | null;
  sessionId: string | null;
  outcome: string;
  // Готовая подпись бэка (chain.status: «ждёт ревью», «закрыт без повторного ревью»,
  // «принят», «идёт», «снят с выкладки»); null — бэк не отдал (старый ответ).
  status: string | null;
  rounds: number;
}

// Шаги из stepChains: порядок — как в бэке (не пересортировываем).
// «Шагов нет» — если stepChains пусто.
export function stepRows(run: FeatureRun, index: FeaturesIndex, feature: FeatureIndexFeature | null): StepRow[] {
  const chains = run.stepChains ?? [];
  return chains.map(c => ({
    stepRef: c.stepRef,
    title: stripStepCode(c.title, c.stepRef),
    lane: c.lane ?? '',
    personaId: c.personaId ?? null,
    sessionId: feature ? stepChatSessionId(index, feature, c.stepRef) : null,
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
    // N-epic: одиночный epic — той же защитой, что и списки (контракт: UI обязан
    // пережить отсутствие любого поля; без epic → [], а не undefined в массиве).
    ...(run.epic ? [run.epic] : []),
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

// Число задач фичи — ОДНА точка правды для шапки (FeaturePage) и блока «Задачи»
// (FeaturePageBody) (п.64): из индекса (эпик + ВСЕ дочерние, включая consult);
// фичи нет в индексе — ОДИН фолбэк на сумму по шагам (taskCounters). Раньше шапка
// при feature==null не показывала число вовсе, а блок — показывал counters.total:
// два расходящихся фолбэка. Теперь один.
export function featureTaskTotal(feature: FeatureIndexFeature | null, run: FeatureRun): number {
  if (feature?.taskCount != null) return feature.taskCount;
  return taskCounters(run).total;
}

// === Лента событий ===

// Строка события для рендера: время, вид, исполнитель, роль/шаг, чат (если есть).
export interface EventRow {
  at: string;
  kind: string;
  // Id задачи события: в ключ строки ленты (at|kind|sessionId|taskId) — два события
  // одного вида в одном чате с равным at иначе дают дубль ключа и потерянную строку.
  taskId: string;
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
    taskId: e.taskId,
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

// Единая функция времени события (M3/N2): «сегодня» — только время, другой день —
// дата + время. Отдаёт ЧАСТИ (date + time) в местном поясе; КАК их склеить (одна
// строка «dd.mm ЧЧ:ММ» для тревоги vs две строки для ленты) решает МЕСТО РЕНДЕРА,
// не эта функция — логика «сегодня» не продублирована. now — «сейчас» в мс
// (стабильные тесты; не задано — Date.now()).
function eventTimeCore(at: string, now?: number): { date: string | null; time: string } {
  const d = new Date(at);
  if (Number.isNaN(d.getTime())) return { date: null, time: '' };
  const n = new Date(now ?? Date.now());
  const sameDay = d.getFullYear() === n.getFullYear() && d.getMonth() === n.getMonth() && d.getDate() === n.getDate();
  const time = hhmm(at) ?? '';
  if (sameDay) return { date: null, time };
  return { date: dateOnly(at) ?? null, time };
}

// Время события в ЛЕНТЕ (N2): сегодня — «ЧЧ:ММ» (одна строка); другой день — ДВЕ строки
// в колонке 44: дата «dd.mm» сверху, время «ЧЧ:ММ» снизу. Сетка 44·16 не меняется:
// «dd.mm ЧЧ:ММ» одной строкой в 44 px не влезает (минуты уходили под аватар).
export function eventTimeParts(at: string, now?: number): { date: string | null; time: string } {
  return eventTimeCore(at, now);
}

// Время события ОДНОЙ строкой (тревога страницы фичи): сегодня — «ЧЧ:ММ»,
// другой день — «dd.mm ЧЧ:ММ» (дата вместе со временем: иначе события разных дней
// в тревоге не различить).
export function eventTimeLabel(at: string, now?: number): string {
  const { date, time } = eventTimeCore(at, now);
  if (!date && !time) return '';
  return date ? `${date} ${time}` : time;
}
