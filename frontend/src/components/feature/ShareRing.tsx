// Малое кольцо долей над таблицей разбивки (волна 5, ST-F9; записка
// docs/mockups/feature-stats-breakdown-5.md §«Токены»/§«Рекомендация», макет
// feature-stats-breakdown-5.html §A). По одной дуге (circle со stroke-dasharray)
// на группу, число дуг равно числу групп; при <2 групп кольца нет (записка:
// «Без групп или с одной группой кольца нет»). Доли — от СУММЫ частей, а не из
// percentText: независимое округление каждой доли даёт 101 %, а кольцо обязано
// замкнуться ровно. Дуги стартуют сверху (svg rotate(-90), макет :119) и идут по
// часовой; между ненулевыми дугами зазор 3 px (макет :294) — если ненулевая дуга
// одна (доминантная группа 100 %), зазора нет, дуга замыкает круг. Дуга самой
// крупной группы — толстая (макет: 26 против 20). Цвета — палитра записки
// (§Токены): accent, info, plan, success, warning, progressTrack по порядку,
// цикл при большем числе групп; вызывающий может задать цвет группы сам (colorOf —
// у персон цвет персоны). В центре — итог выбранной меры; если у меры нет данных
// (null у всех групп) — фолбэк на время (состояние «без расхода» записки).
// Легенды в кольце нет: подписи — в таблице ниже, смысл несёт aria-label, а не
// цвет (цвета не единственный носитель смысла).
//
// Контракт costPartial: центр показывает сумму только по группам с надёжной ценой
// (null-цены в сумму не входят). Что цена известна лишь по части моделей, говорит
// не кольцо, а оболочка (ST-F13): плашка «цена по части моделей недоступна».

import { type CSSProperties } from 'react';
import { C, FS, SP, FONT } from '../../lib/design';
import { formatDuration } from './FeatureStatsBits';
import { formatTokens } from './FeatureStatsFormat';
import { formatMoney, percentText, sortGroups, measureValue, type Measure } from './FeatureBreakdownModel';
import type { FeatureBreakdownGroup } from '../../types/featureStats';

// Палитра дуг — токены дизайн-системы в порядке записки (§Токены, «Цвета групп»).
// Если групп больше, чем цветов — цикл (так же в макете, colorOf :258).
const ARC_COLORS = [C.accent, C.info, C.plan, C.success, C.warning, C.progressTrack];

// Зазор между дугами — 3 px по макету (:294).
const ARC_GAP = 3;

// Мера в aria-label: «доли по времени», «доли по токенам», «доли по деньгам».
const MEASURE_WORD: Record<Measure, string> = { time: 'времени', tokens: 'токенам', cost: 'деньгам' };

export interface ShareRingProps {
  groups: FeatureBreakdownGroup[];
  measure: Measure;
  // По умолчанию 140; на 360 вызывающий передаёт 180 (записка: «На 360 кольцо
  // 180 px по центру»).
  size?: number;
  // Человекочитаемое имя группы для aria-label: ключи бэка — personaId/modelFull/
  // taskRole (UUID и сырые роли скринридер не читает). Фолбэк — сам ключ.
  labelOf?: (key: string) => string;
  // Цвет группы от вызывающего (у персон — цвет персоны, AGENT_COLORS);
  // фолбэк — палитра по порядку.
  colorOf?: (key: string) => string;
  // Название разреза для второй строки центра («персоны суммарно · …», макет :298).
  axisLabel?: string;
}

// Сумма меры по всем группам; null — ни у одной группы данных нет (тот же
// принцип, что cost в макете §A).
function measureTotal(groups: FeatureBreakdownGroup[], measure: Measure): number | null {
  if (!groups.some(g => measureValue(g, measure) != null)) return null;
  return groups.reduce((a, g) => a + (measureValue(g, measure) ?? 0), 0);
}

export function ShareRing({ groups, measure, size = 140, labelOf, colorOf, axisLabel }: ShareRingProps) {
  if (groups.length < 2) return null;

  // Фолбэк на время — только когда у меры нет данных ни у одной группы (null у
  // всех, состояние «без расхода» записки). Нулевая сумма — это данные: мера
  // остаётся выбранной, дуги нулевые (пустое кольцо), центр — «бесплатно».
  const effMeasure: Measure = measureTotal(groups, measure) == null ? 'time' : measure;

  // Порядок — как в таблице (сортировка по мере), первая дуга — самая крупная и
  // толстая. Вход не мутируется (sortGroups копирует).
  const sorted = sortGroups(groups, effMeasure);
  const raw = sorted.map(g => measureValue(g, effMeasure) ?? 0);
  const total = raw.reduce((a, b) => a + b, 0);

  // Геометрия макета (R=70 при viewBox 180, :292) в долях диаметра: толстая дуга
  // 0.15, обычная 0.11; радиус — с запасом, чтобы толстая дуга не вышла за viewBox.
  const thick = Math.max(8, Math.round(size * 0.15));
  const base = Math.max(6, Math.round(size * 0.11));
  const r = (size * 70) / 180;
  const L = 2 * Math.PI * r;

  // Доли от суммы частей: длина дуги — доля × длина окружности (макет :293),
  // «сырые» длины точно заполняют окружность — без перекрытия (в отличие от
  // percentText, который может дать 101 %). Зазор вычитается из каждой ненулевой
  // дуги, но только если ненулевых дуг больше одной: одна дуга (100 %) замыкает
  // круг без зазора. Смещение — сумма длин перед дугой (макет: off += len).
  const lens = raw.map(v => (total > 0 ? (v / total) * L : 0));
  const positive = lens.filter(v => v > 0).length;
  const gap = positive > 1 ? ARC_GAP : 0;
  const shares = raw.map(v => (total > 0 ? v / total : 0));
  // Смещение — сумма длин перед дугой (префиксная сумма, без mutable-счётчика:
  // eslint.design запрещает переприсваивание после рендера).
  const arcs = sorted.map((g, i) => ({
    key: g.key,
    i,
    dash: `${Math.max(0, lens[i] - gap)} ${L}`,
    offset: -lens.slice(0, i).reduce((a, b) => a + b, 0),
  }));

  const big: CSSProperties = {
    fontFamily: FONT.serif,
    fontSize: size >= 180 ? FS.xl : FS.lg,
    fontWeight: 700,
    color: C.textHeading,
    lineHeight: 1.15,
  };
  const small: CSSProperties = {
    fontSize: FS.xs,
    color: C.textMuted,
    fontFamily: FONT.sans,
    marginTop: SP.xs,
  };

  // Центр по мере (макет :298): крупная строка — итог выбранной меры; вторая —
  // «<разрез> суммарно» и деньги, если они есть и не совпадают с крупной строкой.
  const totalCost = measureTotal(groups, 'cost');
  const totalTokens = groups.reduce((a, g) => a + (g.tokens ?? 0), 0);
  const moneyText = totalCost == null
    ? 'нет учёта'
    : formatMoney({ key: '', minutes: 0, tokens: totalTokens, costUsd: totalCost, chatIds: [] });
  const bigText =
    effMeasure === 'time'
      ? formatDuration(raw.reduce((a, b) => a + b, 0))
      : effMeasure === 'tokens'
        ? formatTokens(raw.reduce((a, b) => a + b, 0))
        : moneyText;
  const smallText = axisLabel
    ? `${axisLabel} суммарно${effMeasure === 'cost' || totalCost == null ? '' : ' · ' + moneyText}`
    : effMeasure === 'cost' ? '' : moneyText;

  return (
    <div data-testid="share-ring" style={{ position: 'relative', width: size, height: size }}>
      <svg
        role="img"
        aria-label={`Доли по ${MEASURE_WORD[effMeasure]}: ${sorted.map((g, i) => `${labelOf ? labelOf(g.key) : g.key} — ${percentText(shares[i])}`).join(', ')}`}
        style={{ transform: 'rotate(-90deg)' }}
        width={size}
        height={size}
        viewBox={`0 0 ${size} ${size}`}
      >
        <circle data-testid="share-ring-bg" cx={size / 2} cy={size / 2} r={r} fill="none" stroke={C.bgSelected} strokeWidth={base} />
        {arcs.map(({ key, i, dash, offset: off }) => (
          <circle
            key={`${key}:${i}`}
            cx={size / 2}
            cy={size / 2}
            r={r}
            fill="none"
            stroke={colorOf ? colorOf(key) : ARC_COLORS[i % ARC_COLORS.length]}
            strokeWidth={i === 0 ? thick : base}
            strokeDasharray={dash}
            strokeDashoffset={off}
          />
        ))}
      </svg>
      <div
        style={{
          position: 'absolute', inset: 0, display: 'flex', flexDirection: 'column',
          alignItems: 'center', justifyContent: 'center', textAlign: 'center',
        }}
      >
        <b data-testid="share-ring-total" style={big}>{bigText}</b>
        <span data-testid="share-ring-cost" style={small}>{smallText}</span>
      </div>
    </div>
  );
}
