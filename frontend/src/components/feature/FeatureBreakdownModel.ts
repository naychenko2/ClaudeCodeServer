// Модель блока «Кто и на чём работал» (волна 5, ST-F8; записка
// docs/mockups/feature-stats-breakdown-5.md, вариант C «Таблица» + малое кольцо).
// Чистые функции: бэк считает числа (stats.breakdown), фронт только раскладывает —
// тот же принцип, что в FeatureStatsPulseModel. Вид (ST-F9+) читает эту модель;
// цвета и разметка здесь отсутствуют.
//
// Ключи групп — сырые, как их отдаёт бэк (FeatureBreakdown.cs: personaId | modelFull |
// taskRole); сопоставление с run.stages[].executors[] — по тем же полям.

import type { FeatureRun, FeatureStageExecutor } from '../../types/featureRun';
import type { FeatureBreakdownGroup } from '../../types/featureStats';
import { roleLabel } from '../../lib/roleLabel';
import { formatUsd } from './FeatureStatsFormat';

// === Сортировка по мере ===

export type Measure = 'time' | 'tokens' | 'cost';

// Значение группы по мере; null — данных нет (не считалось).
function measureValue(g: FeatureBreakdownGroup, measure: Measure): number | null {
  if (measure === 'time') return g.minutes;
  if (measure === 'tokens') return g.tokens;
  return g.costUsd;
}

// Группы по мере по убыванию; null (без учёта) — в конце, порядок среди них
// сохраняется (стабильная сортировка). Вход не мутируется.
export function sortGroups(groups: FeatureBreakdownGroup[], measure: Measure): FeatureBreakdownGroup[] {
  return [...groups].sort((a, b) => {
    const va = measureValue(a, measure);
    const vb = measureValue(b, measure);
    if (va == null && vb == null) return 0;
    if (va == null) return 1;
    if (vb == null) return -1;
    return vb - va;
  });
}

// === Деньги ===

// Цена ячейки таблицы (записка §«Таблица», п. 4): 0 при токенах — «бесплатно»
// (локальная модель отработала даром); null — «нет учёта»; иначе formatUsd.
export function formatMoney(group: FeatureBreakdownGroup): string {
  if (group.costUsd == null) return 'нет учёта';
  if (group.costUsd === 0 && (group.tokens ?? 0) > 0) return 'бесплатно';
  return formatUsd(group.costUsd);
}

// === Чипы «Модели и роли» ===

export type Axis = 'personas' | 'models' | 'roles';

export interface BreakdownChip {
  kind: 'model' | 'role' | 'persona';
  // Сырой ключ: modelFull | taskRole | personaId — подписи строит вид.
  key: string;
}

// Все исполнители run в порядке бэка (стадии → их executors).
function executorsOf(run: FeatureRun): FeatureStageExecutor[] {
  return (run.stages ?? []).flatMap(s => s.executors ?? []);
}

// Чипы колонки «Модели и роли» (записка §«Таблица», п. 3): для персоны — модели и
// роли, на которых она работала; для модели или роли — персоны участников.
// Без executors — пустой список, а не отсутствие строки.
export function groupChips(axis: Axis, key: string, run: FeatureRun): BreakdownChip[] {
  const chips: BreakdownChip[] = [];
  const seen = new Set<string>();
  for (const e of executorsOf(run)) {
    if (axis === 'personas') {
      if (e.personaId !== key) continue;
      if (e.modelFull && !seen.has(`m:${e.modelFull}`)) {
        seen.add(`m:${e.modelFull}`);
        chips.push({ kind: 'model', key: e.modelFull });
      }
      if (e.taskRole && !seen.has(`r:${e.taskRole}`)) {
        seen.add(`r:${e.taskRole}`);
        chips.push({ kind: 'role', key: e.taskRole });
      }
    } else {
      const match = axis === 'models' ? e.modelFull === key : e.taskRole === key;
      if (!match) continue;
      if (e.personaId && !seen.has(`p:${e.personaId}`)) {
        seen.add(`p:${e.personaId}`);
        chips.push({ kind: 'persona', key: e.personaId });
      }
    }
  }
  return chips;
}

// === Подписи чатов ===

// Слово роли без шага: roleLabel с null stepRef/lane даёт чистое слово
// («доработка», «ревью»…); для 'step' оно пусто — нейтральное «шаг», как в
// roleGroupLabel (FeatureStatsBlocks.tsx). Макет рисует шаг и роль раздельными
// частями («Ш2» + «доработка») — склеивает вид.
function roleWord(taskRole: string | null): string {
  const label = roleLabel(taskRole, null, null);
  if (label) return label;
  return taskRole === 'step' ? 'шаг' : '';
}

// Подпись строки чата (записка §«Нужны данные»): исполнитель с этим chatId —
// «Ш2 · доработка» (stepRef · роль); не найден — «чат», без id.
export function chatLabel(chatId: string, run: FeatureRun): string {
  const executor = executorsOf(run).find(e => e.chatId === chatId);
  if (!executor) return 'чат';
  const parts: string[] = [];
  if (executor.stepRef) parts.push(executor.stepRef);
  const role = roleWord(executor.taskRole);
  if (role) parts.push(role);
  return parts.length > 0 ? parts.join(' · ') : 'чат';
}

// === Живые чипы и «мало данных» ===

// Живой чип персоны (записка §«Состояния»): running — «работает», waiting —
// «ждёт вас»; у закрытой фичи — ничего. Если статусов несколько, running важнее.
export function liveChipText(run: FeatureRun, personaId: string): string | null {
  if (run.closed === true || run.state === 'merged' || run.state === 'closed') return null;
  const execs = executorsOf(run).filter(e => e.personaId === personaId);
  if (execs.some(e => e.status === 'running')) return 'работает';
  if (execs.some(e => e.status === 'waiting')) return 'ждёт вас';
  return null;
}

// «Мало данных» (записка §«Состояния»): меньше двух групп в разрезе — пунктир,
// а не таблица.
export function fewData(groups: FeatureBreakdownGroup[]): boolean {
  return groups.length < 2;
}

// === Проценты ===

// Доля 0..1 → «42 %». Округление каждой доли независимо: сумма текстов может
// дать 101 % — полосу кольца вид строит от суммы частей, а не из этих текстов.
export function percentText(share: number): string {
  return `${Math.round(share * 100)} %`;
}
