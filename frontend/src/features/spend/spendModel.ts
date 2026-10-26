// Данные аналитики расхода токенов (Spend Analytics v2): словари разрезов, периоды,
// источники, форматирование. Контракт входа в раздел — lib/spendContract.ts;
// данные — /api/spend/* (docs/architecture/spend-analytics-api.md).
import { C } from 'aihome_shell/kit';
import { addDaysUtc, todayUtc, type SpendDim } from '../../lib/spendContract';
import { COST_TIERS, COST_TIER_KEYS, costTierColor, type CostTierKey } from '../../lib/costTiers';

// Разрезы pivot-дерева и фильтров. 'turn' — терминальный псевдо-уровень (лист-ходы).
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

// Ценовые классы моделей: бесплатно / дёшево / дорого. Класс приходит с бэка
// (SpendCardRow.tier, SpendDayPoint.byTier) — словарь «какая модель какого
// класса» на фронте НЕ дублируем, красим только по ключу. Сам словарь классов
// (подпись + цвет) — общий на всё приложение, в lib/costTiers.ts.
export const SPEND_TIERS = COST_TIERS;

// Порядок классов в стеке столбика СНИЗУ ВВЕРХ: дорогие внизу, дешёвые в середине,
// бесплатные сверху (просьба владельца 26.10). Это порядок рендера (column-reverse),
// а не общий порядок классов — он остаётся в COST_TIER_KEYS.
export const TIER_KEYS: readonly SpendTierKey[] = [...COST_TIER_KEYS].reverse();
export type SpendTierKey = CostTierKey;

// Цвет класса: неизвестный или null-класс → нейтральный цвет палитры (не выдуманный)
export const tierColor = costTierColor;

// Сегмент столбика дня: класс и его значение
export interface SpendTierSegment {
  key: SpendTierKey;
  value: number;
  color: string;
}

// Сегменты столбика дня по ценовым классам. Значения берутся ИЗ byTier
// (разбивка по источникам в столбике не показывается — решение владельца),
// порядок сегментов фиксирован снизу вверх: дорого → дёшево → бесплатно.
export function tierSegments(day: {
  byTier?: Partial<Record<SpendTierKey, number>> | null;
  bySource?: Record<string, number> | null;
}): SpendTierSegment[] {
  const byTier = day.byTier ?? {};
  return TIER_KEYS
    .map(key => ({ key, value: byTier[key] ?? 0, color: tierColor(key) }))
    .filter(s => s.value > 0);
}

// Отступ столбика дня от краёв области графика (высота самого столбика = chartH - отступ)
const DAY_CHART_PAD = 12;
// Минимальная высота сегмента: иначе копеечный класс не виден вовсе
const DAY_SEG_MIN = 1;

// Столбик дня: сегменты по ценовым классам и их высоты. Правило живёт здесь,
// а не в рендере: высота столбика — от ДНЕВНОГО ИТОГА, классы лишь делят её
// (сумма высот сегментов == высоте столбика, ровно).
export interface SpendDaySegment {
  key: SpendTierKey;
  value: number;
  color: string;
  height: number;
}

export interface SpendDayColumn {
  segments: SpendDaySegment[];
  height: number;      // высота всего столбика, px
  neutral: boolean;    // день без разбивки по классам — столбик одним нейтральным куском
}

// Собирает столбик дня: dayMax — максимум по дням (масштаб), chartH — высота
// области графика. День без byTier (старый ответ бэка) — нейтральный столбик.
export function dayColumn(
  day: { total: number; byTier?: Partial<Record<SpendTierKey, number>> | null; bySource?: Record<string, number> | null },
  dayMax: number,
  chartH: number,
): SpendDayColumn {
  const scale = (chartH - DAY_CHART_PAD) / Math.max(1, dayMax);
  const height = Math.max(2, Math.round(day.total * scale));
  const segs = tierSegments(day);
  if (!segs.length) return { segments: [], height, neutral: true };

  const sum = segs.reduce((a, s) => a + s.value, 0);
  // Границы — по накопленной доле: сумма высот ровно равна высоте столбика дня
  let acc = 0;
  let prev = 0;
  const segments = segs.map(s => {
    acc += s.value;
    const bound = Math.round((acc / sum) * height);
    const h = bound - prev;
    prev = bound;
    return { key: s.key, value: s.value, color: s.color, height: h };
  });
  // Копеечный класс не должен исчезать: единицу высоты берём у самого высокого
  // сегмента (сумма высот при этом не меняется)
  for (const seg of segments) {
    if (seg.height >= DAY_SEG_MIN) continue;
    const donor = segments.reduce((a, b) => (b.height > a.height ? b : a), segments[0]);
    if (donor === seg || donor.height <= DAY_SEG_MIN) break;
    donor.height -= DAY_SEG_MIN - seg.height;
    seg.height = DAY_SEG_MIN;
  }
  return { segments, height, neutral: false };
}

// Цвет полосы строки-карточки: у разрезов «модель»/«провайдер» — по классу
// (чтобы дешёвая модель была видна сразу), у прочих — акцент.
export const rowBarColor = (dim: string, row: { tier?: string | null }) =>
  dim === 'model' || dim === 'provider' ? tierColor(row.tier) : C.accent;

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

export const fmtDate = (d: string) => d.slice(8, 10) + '.' + d.slice(5, 7);
export const fmtTime = (iso: string) =>
  new Date(iso).toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit' });
