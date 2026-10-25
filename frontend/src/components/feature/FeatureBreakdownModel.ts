// Модель блока «Кто и на чём работал» (волна 5, ST-F8; записка
// docs/mockups/feature-stats-breakdown-5.md, вариант C «Таблица» + малое кольцо).
// Чистые функции: бэк считает числа (stats.breakdown), фронт только раскладывает —
// тот же принцип, что в FeatureStatsPulseModel. Вид (ST-F9+) читает эту модель;
// цвета и разметка здесь отсутствуют.
//
// Ключи групп — сырые, как их отдаёт бэк (FeatureBreakdown.cs: personaId | modelFull |
// taskRole); сопоставление с run.stages[].executors[] — по тем же полям, кроме моделей:
// их ключ лежит в двух пространствах (см. normalizeModelKey ниже) и сопоставляется
// нормализованным.

import type { FeatureRun, FeatureStageExecutor } from '../../types/featureRun';
import type { FeatureBreakdownGroup } from '../../types/featureStats';
import { roleLabel } from '../../lib/roleLabel';
import { formatUsd, percentText } from './FeatureStatsFormat';

// Проценты живут в FeatureStatsFormat.ts (ST-F8); ре-экспорт — потребители
// (ShareRing, тесты) импортируют percentText из этой модели.
export { percentText };

// === Сортировка по мере ===

export type Measure = 'time' | 'tokens' | 'cost';

// Значение группы по мере; null — данных нет (не считалось). Публична: ShareRing
// считает по ней и доли, и итог центра (хвост 7 доработки ST-F9).
export function measureValue(g: FeatureBreakdownGroup, measure: Measure): number | null {
  if (measure === 'time') return g.minutes;
  if (measure === 'tokens') return g.tokens;
  return g.costUsd;
}

// Сумма меры по группам; null — ни у одной группы данных нет (не 0).
export function measureTotal(groups: FeatureBreakdownGroup[], measure: Measure): number | null {
  if (!groups.some(g => measureValue(g, measure) != null)) return null;
  return groups.reduce((a, g) => a + (measureValue(g, measure) ?? 0), 0);
}

// Максимум колонки — база полос (минимум 1, чтобы не делить на 0).
export function measureMax(groups: FeatureBreakdownGroup[], measure: Measure): number {
  return Math.max(...groups.map(g => measureValue(g, measure) ?? 0), 1);
}

// Действующая мера: если у выбранной нет данных ни у одной группы — время. Единственное
// место правила: оболочка блока и кольцо берут его отсюда.
export function effectiveMeasure(groups: FeatureBreakdownGroup[], measure: Measure): Measure {
  return measureTotal(groups, measure) == null ? 'time' : measure;
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

// === Ключ модели ===

// Ключ модели приходит из двух пространств: ось «модели» — из журнала (полный
// журнал — «Claude Opus 5.5», без журнала — «Авто → Claude Opus 5.5»), снимок
// исполнителя executors[].modelFull — всегда «Авто → …» (FeatureProjector.cs:437-440).
// Сопоставляем и показываем нормализованное имя: снимаем префикс «Авто → » с обеих
// сторон. Смена модели в чате (журнал — первый ход, снимок — текущий) этим не
// лечится: ключи тогда разные модели, совпадения не будет.
export function normalizeModelKey(key: string): string {
  return key.startsWith('Авто → ') ? key.slice('Авто → '.length) : key;
}

// === Чипы «Модели и роли» ===

export type Axis = 'personas' | 'models' | 'roles';

export interface BreakdownChip {
  kind: 'model' | 'role' | 'persona';
  // Ключ: нормализованная модель | taskRole | personaId — подписи строит вид.
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
      if (e.modelFull) {
        const model = normalizeModelKey(e.modelFull);
        if (!seen.has(`m:${model}`)) {
          seen.add(`m:${model}`);
          chips.push({ kind: 'model', key: model });
        }
      }
      if (e.taskRole && !seen.has(`r:${e.taskRole}`)) {
        seen.add(`r:${e.taskRole}`);
        chips.push({ kind: 'role', key: e.taskRole });
      }
    } else {
      // Модель — по нормализованному ключу: журнал «Claude Opus 5.5» находит
      // исполнителя со снимком «Авто → Claude Opus 5.5» и наоборот.
      const match = axis === 'models'
        ? e.modelFull != null && normalizeModelKey(e.modelFull) === normalizeModelKey(key)
        : e.taskRole === key;
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

// Проценты — в FeatureStatsFormat.ts (percentText), ре-экспорт выше.
