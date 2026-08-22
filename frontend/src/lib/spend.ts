// Аналитика расхода токенов (Spend Analytics v2): словари разрезов, форматирование,
// событие открытия раздела. Данные — /api/spend/* (docs/architecture/spend-analytics-api.md).
import { C } from './design';

// Разрезы pivot-дерева и фильтров. 'turn' — терминальный псевдо-уровень (лист-ходы).
export type SpendDim = 'user' | 'project' | 'chat' | 'task' | 'persona' | 'provider' | 'model' | 'source';
export type SpendLevel = SpendDim | 'turn';

export const DIM_LABELS: Record<SpendDim, string> = {
  user: 'Пользователь',
  project: 'Проект',
  chat: 'Чат / задача',
  task: 'Задача',
  persona: 'Персона',
  provider: 'Провайдер',
  model: 'Модель',
  source: 'Источник',
};

// Разрез «пользователь» доступен только админу в режиме «Все»
export const ADMIN_ONLY_DIMS: SpendDim[] = ['user'];

// Готовые раскладки уровней дерева (порядок собирает фронт — параметр groupBy per-уровень)
export const SPEND_PRESETS: { key: string; label: string; admin: SpendLevel[]; user: SpendLevel[] }[] = [
  { key: 'who', label: 'Кто и где', admin: ['user', 'project', 'chat', 'turn'], user: ['project', 'chat', 'turn'] },
  { key: 'models', label: 'По моделям', admin: ['model', 'user', 'project'], user: ['model', 'project', 'chat'] },
  { key: 'personas', label: 'По персонам', admin: ['persona', 'chat', 'turn'], user: ['persona', 'chat', 'turn'] },
  // Задача → персона → ход: разрез «чат» вторым уровнем почти всегда вырожден
  // (у задачи-исполнителя ровно один чат), поэтому в цепочке его нет
  { key: 'tasks', label: 'По задачам', admin: ['task', 'persona', 'turn'], user: ['task', 'persona', 'turn'] },
  { key: 'sources', label: 'По источникам', admin: ['source', 'model', 'chat'], user: ['source', 'model', 'chat'] },
];

// Источники расхода: подпись + цвет серии (токены темы, обе темы автоматически)
export const SPEND_SOURCES: Record<string, { label: string; color: string }> = {
  'chat-turn': { label: 'Ходы', color: C.accent },
  'one-shot': { label: 'Фоновые', color: C.info },
  fal: { label: 'fal.ai', color: C.plan },
  glif: { label: 'glif', color: C.warning },
  free: { label: 'Бесплатные', color: C.success },
  // Озвучка ответов (Yandex SpeechKit): токенов нет, считается запросами и рублями.
  // Цвет — чернильный токен хаба: свободных семантических цветов серий не осталось,
  // а он заметно отличается от accent/plan/warning соседних источников
  tts: { label: 'Озвучка', color: C.navInk },
};

export const sourceLabel = (s: string) => SPEND_SOURCES[s]?.label ?? s;
export const sourceColor = (s: string) => SPEND_SOURCES[s]?.color ?? C.textMuted;
// Текстовый вариант цвета источника — для моноширинных значений в анализе
// (у SPEND_SOURCES цвета серий «плотные», на подложке читается text-пара)
export const sourceTextColor = (s: string) =>
  s === 'fal' ? C.planText : s === 'glif' ? C.warningText : sourceColor(s);

// Источники-«генерации медиа»: денежной суммы у записи нет, показываются счётчиком
// генераций (как fal). Бэкенд считает Generations у любых source — фронт лишь
// различает такие источники для подписей и формата значений.
export const GEN_SOURCES: readonly string[] = ['fal', 'glif', 'tts'];
export const isGenSource = (s: string) => GEN_SOURCES.includes(s);

// Единица измерения источника без токенов: у fal/glif это генерации, у озвучки — запросы
// синтеза (SpeechKit тарифицируется ровно за запрос, см. docs/research/speechkit-pricing.md)
export const genUnit = (s: string) => (s === 'tts' ? 'запр.' : 'ген.');
export const genUnitLong = (s: string) => (s === 'tts' ? 'запросов' : 'генераций');

// Рубли расхода на сервисы Яндекса: копейки показываем только когда они есть —
// «0,50 ₽» информативно, а «12,00 ₽» просто шумит
export function fmtRub(v: number): string {
  const rounded = Math.round(v * 100) / 100;
  return `${rounded.toLocaleString('ru-RU', {
    minimumFractionDigits: Number.isInteger(rounded) ? 0 : 2,
    maximumFractionDigits: 2,
  })} ₽`;
}

// Имена «пустых» узлов (key: "") бэкенд уже подставляет в name; страховка на null
export function nodeName(_dim: SpendDim, key: string, name: string | null): string {
  if (name) return name;
  if (name === null && key !== '') return 'удалено';
  return key || '—';
}

// Периоды раздела: последние N дней (UTC-дни, как у бэкенда)
export const SPEND_PERIODS: { key: string; label: string; days: number }[] = [
  { key: 'week', label: 'Неделя', days: 7 },
  { key: 'month', label: 'Месяц', days: 30 },
  { key: 'q', label: '90 дней', days: 90 },
];

const DAY_MS = 24 * 60 * 60 * 1000;
export const todayUtc = () => new Date().toISOString().slice(0, 10);
export const addDaysUtc = (date: string, n: number) =>
  new Date(Date.parse(date + 'T12:00:00Z') + n * DAY_MS).toISOString().slice(0, 10);
export function periodRange(periodKey: string): { from: string; to: string } {
  const p = SPEND_PERIODS.find(x => x.key === periodKey) ?? SPEND_PERIODS[1];
  const to = todayUtc();
  return { from: addDaysUtc(to, -(p.days - 1)), to };
}

// Компактные токены: 1.2M / 456.7k / 320
export function fmtTok(n: number): string {
  n = Math.round(n);
  if (n >= 1e6) return (n / 1e6).toFixed(1).replace(/\.0$/, '') + 'M';
  if (n >= 1e3) return (n / 1e3).toFixed(1).replace(/\.0$/, '') + 'k';
  return String(n);
}

// Склонение счётчиков (та же логика, что у локальных plural в виджетах/списках)
export function plural(n: number, one: string, few: string, many: string): string {
  const mod10 = n % 10;
  const mod100 = n % 100;
  if (mod10 === 1 && mod100 !== 11) return one;
  if (mod10 >= 2 && mod10 <= 4 && (mod100 < 10 || mod100 >= 20)) return few;
  return many;
}
// «1 ход», «2 хода», «47 ходов»
export const fmtTurns = (n: number) => `${n} ${plural(n, 'ход', 'хода', 'ходов')}`;
export const fmtDate = (d: string) => d.slice(8, 10) + '.' + d.slice(5, 7);
export const fmtTime = (iso: string) =>
  new Date(iso).toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit' });

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
  window.dispatchEvent(new CustomEvent<SpendOpenContext>(OPEN_SPEND_EVENT, { detail: ctx ?? {} }));
}

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
