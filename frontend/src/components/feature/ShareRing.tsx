// Малое кольцо долей над таблицей разбивки (волна 5, ST-F9; записка
// docs/mockups/feature-stats-breakdown-5.md §«Рекомендация», макет
// feature-stats-breakdown-5.html §A). По одной дуге (circle со stroke-dasharray)
// на группу, число дуг равно числу групп; при <2 групп кольца нет (записка:
// «Без групп или с одной группой кольца нет»). Доли — от СУММЫ частей, а не из
// percentText: независимое округление каждой доли даёт 101 %, а кольцо обязано
// замкнуться ровно. Дуга самой крупной группы — толстая (макет: 26 против 20).
// В центре итог: время и деньги («нет учёта», когда расхода нет). Цвета — только
// токены C.* по порядку. Легенды в кольце нет: подписи — в таблице ниже, смысл
// несёт aria-label, а не цвет (цвета не единственный носитель смысла).

import { type CSSProperties } from 'react';
import { C, FS, SP, FONT } from '../../lib/design';
import { formatDuration } from './FeatureStatsBits';
import { formatMoney, percentText, sortGroups, type Measure } from './FeatureBreakdownModel';
import type { FeatureBreakdownGroup } from '../../types/featureStats';

// Палитра дуг — токены дизайн-системы по порядку (макет: «по порядку из
// токенов»); если групп больше, чем цветов — цикл.
const ARC_COLORS = [C.accent, C.plan, C.info, C.success, C.warning, C.danger, C.navInk, C.smoke];

// Мера в aria-label: «доли по времени», «доли по токенам», «доли по деньгам».
const MEASURE_WORD: Record<Measure, string> = { time: 'времени', tokens: 'токенам', cost: 'деньгам' };

export interface ShareRingProps {
  groups: FeatureBreakdownGroup[];
  measure: Measure;
  // По умолчанию 140; на 360 вызывающий передаёт 180 (записка: «На 360 кольцо
  // 180 px по центру»).
  size?: number;
}

// Значение группы по мере; null («не считалось») — ноль для дуги.
function arcValue(g: FeatureBreakdownGroup, measure: Measure): number {
  const v = measure === 'time' ? g.minutes : measure === 'tokens' ? g.tokens : g.costUsd;
  return v ?? 0;
}

// Суммарная стоимость: null — ни у одной группы расхода нет; иначе сумма
// (null-группы как 0) — тот же принцип, что cost в макете §A.
function totalCost(groups: FeatureBreakdownGroup[]): number | null {
  if (!groups.some(g => g.costUsd != null)) return null;
  return groups.reduce((a, g) => a + (g.costUsd ?? 0), 0);
}

export function ShareRing({ groups, measure, size = 140 }: ShareRingProps) {
  if (groups.length < 2) return null;

  // Порядок — как в таблице (сортировка по мере), первая дуга — самая крупная и
  // толстая. Вход не мутируется (sortGroups копирует).
  const sorted = sortGroups(groups, measure);
  const total = sorted.reduce((a, g) => a + arcValue(g, measure), 0);

  // Геометрия макета (R=70, толщины 26/20 при 180) в долях диаметра: толстая
  // дуга 0.15, обычная 0.11; радиус — до середины толстой дуги, чтобы она не
  // вышла за viewBox.
  const thick = Math.max(8, Math.round(size * 0.15));
  const base = Math.max(6, Math.round(size * 0.11));
  const r = size / 2 - thick / 2;
  const L = 2 * Math.PI * r;

  // Доли от суммы частей: их длины в точности заполняют окружность — без зазора
  // и перекрытия (в отличие от percentText, который может дать 101 %).
  const shares = sorted.map(g => (total > 0 ? arcValue(g, measure) / total : 0));
  // Смещение дуги — сумма долей перед ней (без переприсваивания счётчика).
  const arcs = sorted.map((g, i) => ({
    key: g.key,
    i,
    dash: `${shares[i] * L} ${L}`,
    offset: -shares.slice(0, i).reduce((a, b) => a + b, 0) * L,
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

  return (
    <div data-testid="share-ring" style={{ position: 'relative', width: size, height: size }}>
      <svg
        role="img"
        aria-label={`Доли по ${MEASURE_WORD[measure]}: ${sorted.map((g, i) => `${g.key} — ${percentText(shares[i])}`).join(', ')}`}
        width={size}
        height={size}
        viewBox={`0 0 ${size} ${size}`}
      >
        <circle cx={size / 2} cy={size / 2} r={r} fill="none" stroke={C.bgSelected} stroke-width={base} />
        {arcs.map(({ key, i, dash, offset: off }) => (
          <circle
            key={`${key}:${i}`}
            cx={size / 2}
            cy={size / 2}
            r={r}
            fill="none"
            stroke={ARC_COLORS[i % ARC_COLORS.length]}
            stroke-width={i === 0 ? thick : base}
            stroke-dasharray={dash}
            stroke-dashoffset={off}
          />
        ))}
      </svg>
      <div
        style={{
          position: 'absolute', inset: 0, display: 'flex', flexDirection: 'column',
          alignItems: 'center', justifyContent: 'center', textAlign: 'center',
        }}
      >
        <b data-testid="share-ring-total" style={big}>
          {formatDuration(groups.reduce((a, g) => a + g.minutes, 0))}
        </b>
        <span data-testid="share-ring-cost" style={small}>
          {formatMoney({
            key: '',
            minutes: 0,
            tokens: groups.reduce((a, g) => a + (g.tokens ?? 0), 0),
            costUsd: totalCost(groups),
            chatIds: [],
          })}
        </span>
      </div>
    </div>
  );
}
