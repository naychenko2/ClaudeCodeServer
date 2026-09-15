// Контракт входа в раздел Spend (Spend Analytics v2): разрезы, фильтр, контекст
// открытия из внешних точек (виджет «Домой», бейдж чата, обзор) и query-параметры
// /api/spend/* (docs/architecture/spend-analytics-api.md). Данные и форматирование —
// features/spend/spendModel.ts.
export type SpendDim = 'user' | 'project' | 'chat' | 'task' | 'persona' | 'provider' | 'model' | 'source';

// Активный фильтр среза; label — читаемое имя значения для чипа (val может быть id)
export interface SpendFilter {
  dim: SpendDim;
  val: string;
  label: string;
}

// Контекст открытия раздела из внешних точек (виджет «Домой», бейдж чата, обзор)
export interface SpendOpenContext {
  screen?: 'overview' | 'analysis';
  filters?: SpendFilter[];
  day?: string;               // срез одного дня (клик по бару обзора)
  preset?: string;            // ключ раскладки уровней
  pivotDim?: SpendDim;        // «разложить →»: разрез первым уровнем
  turnId?: string;            // сразу открыть паспорт хода
}

export const OPEN_SPEND_EVENT = 'cc-open-spend';
export function openSpend(ctx?: SpendOpenContext) {
  stashSpendContext(ctx ?? {});
  window.dispatchEvent(new CustomEvent<SpendOpenContext>(OPEN_SPEND_EVENT, { detail: ctx ?? {} }));
}

// Контекст «положен перед открытием / забран при монтировании»: переменная на уровне
// модуля, обнуляется при чтении. Передаёт контекст открытия без прокидывания через
// SubsystemTabProps (у которых для него нет слота).
let stashedSpendContext: SpendOpenContext | null = null;
export function stashSpendContext(ctx: SpendOpenContext): void {
  stashedSpendContext = ctx;
}
export function consumeSpendContext(): SpendOpenContext | null {
  const ctx = stashedSpendContext;
  stashedSpendContext = null;
  return ctx;
}

const DAY_MS = 24 * 60 * 60 * 1000;
export const todayUtc = () => new Date().toISOString().slice(0, 10);
export const addDaysUtc = (date: string, n: number) =>
  new Date(Date.parse(date + 'T12:00:00Z') + n * DAY_MS).toISOString().slice(0, 10);

// Query-параметры /api/spend/*: период + скоуп + фильтры (кроме отсутствующих)
export function spendQuery(opts: {
  from?: string; to?: string; scope?: 'mine' | 'all';
  filters?: SpendFilter[]; extra?: Record<string, string | number | undefined>;
}): string {
  const q = new URLSearchParams();
  if (opts.from) q.set('from', opts.from);
  if (opts.to) q.set('to', opts.to);
  if (opts.scope) q.set('scope', opts.scope);
  for (const f of opts.filters ?? []) q.set(f.dim, f.val);
  for (const [k, v] of Object.entries(opts.extra ?? {})) if (v !== undefined) q.set(k, String(v));
  const s = q.toString();
  return s ? `?${s}` : '';
}
