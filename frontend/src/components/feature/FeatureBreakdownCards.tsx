// «Кто и на чём работал» — карточки разреза для узкого экрана (волна 5, ST-F12;
// макет feature-stats-breakdown-5.html §.c-mr, записка §360 п.9). Вызывающий
// выбирает этот компонент вместо таблицы по useIsMobile (оболочка — ST-F13);
// сам компонент ширину не смотрит — он по определению мобильный.
//
// Числа и форматы — только из FeatureBreakdownModel (ST-F8): sortGroups,
// measureValue, formatMoney, percentText, liveChipText, fewData — компонент
// ничего не пересчитывает. Мера приходит из шапки (SegmentedControl ST-F13);
// раскрытая группа — состояние карточек и НЕ сбрасывается при смене меры.
//
// Карточка на группу: имя (аватар персоны 22 / квадрат цвета группы 12, как в
// таблице ST-F11), живой чип персоны, доля по выбранной мере (percentText),
// чипы связи разрезов (groupChips, как в таблице ST-F11), три мини-полосы 6 px
// «время / токены / деньги» от максимума колонки (макет .c-mr .ms), «N чатов ›»
// с раскрытием FeatureBreakdownChats (ST-F10, isMobile).
// Скрытые ключи (hiddenKeys) отсекаются до guard'а. Меньше двух групп — null,
// пустое состояние рисует оболочка. Тач-цель кнопки чатов — 44 px (к1).
// Сырые button — с явным сбросом UA-стилей (урок ST-F7).

import { useState, Fragment, type CSSProperties } from 'react';
import { C, FS, SP, R, FONT } from '../../lib/design';
import { plural } from '../../lib/plural';
import { roleLabel } from '../../lib/roleLabel';
import { Dot } from '../ui/Dot';
import { LiveDot } from '../ui/LiveDot';
import { formatDuration } from './FeatureStatsBits';
import { formatTokens } from './FeatureStatsFormat';
import {
  type Axis, type Measure, type BreakdownChip,
  sortGroups, formatMoney, groupChips, liveChipText, fewData, percentText, measureValue,
} from './FeatureBreakdownModel';
import { FeatureBreakdownChats } from './FeatureBreakdownChats';
import type { FeatureBreakdownGroup } from '../../types/featureStats';
import type { FeatureRun } from '../../types/featureRun';

export interface FeatureBreakdownCardsProps {
  // Разрез выбранной оси (stats.breakdown[axis]).
  groups: FeatureBreakdownGroup[];
  axis: Axis;
  // Выбранная мера (переключатель шапки ST-F13): по ней сортировка, сумма и полоса.
  measure: Measure;
  // Run для живых чипов и подписей чатов (run.stages[].executors[]).
  run: FeatureRun;
  // Человекочитаемое имя группы (как у таблицы и ShareRing); фолбэк — сам ключ.
  labelOf?: (key: string) => string;
  // Цвет группы: у персон — цвет персоны; фолбэк — палитра по порядку.
  colorOf?: (key: string) => string;
  // Ключи групп, которые не рисуем (роль key='feature' — вид ещё не решил).
  hiddenKeys?: string[];
}

// Палитра групп — та же, что у таблицы и ShareRing: если colorOf не передан.
const ARC_COLORS = [C.accent, C.info, C.plan, C.success, C.warning, C.progressTrack];

const MEASURE_WORD: Record<Measure, string> = { time: 'время', tokens: 'токены', cost: 'деньги' };
const MEASURES: Measure[] = ['time', 'tokens', 'cost'];

function groupColor(key: string, index: number, colorOf?: (k: string) => string): string {
  return colorOf ? colorOf(key) : ARC_COLORS[index % ARC_COLORS.length];
}

// Подпись чипа — как в таблице (ST-F11): модель — нормализованное имя (groupChips
// уже снял «Авто → »); роль — слово roleLabel без шага; для 'step' оно пусто —
// нейтральное «шаг».
function chipLabel(chip: BreakdownChip, labelOf?: (k: string) => string): string {
  if (chip.kind === 'model') return chip.key;
  if (chip.kind === 'role') return roleLabel(chip.key, null, null) || (chip.key === 'step' ? 'шаг' : chip.key);
  return labelOf ? labelOf(chip.key) : chip.key;
}

// Карточка: bgWhite, рамка borderLight, R.lg, padding SP.sm (макет .c-mr).
// Ширина — только '100%': фиксированных ширинок больше 328 px на 360 нет (к1).
const CARD: CSSProperties = {
  width: '100%',
  background: C.bgWhite,
  border: `1px solid ${C.borderLight}`,
  borderRadius: R.lg,
  padding: `${SP.sm}px`,
};
// Раскрытая карточка — «выбранная» по макету (.c-mr.sel): рамка accent, фон accentLight.
const CARD_OPEN: CSSProperties = { ...CARD, border: `1px solid ${C.accent}`, background: C.accentLight };

const TOP: CSSProperties = { display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap' };
const AV: CSSProperties = {
  width: 22, height: 22, borderRadius: R.full, display: 'inline-flex',
  alignItems: 'center', justifyContent: 'center',
  fontSize: FS.xs, fontWeight: 700, color: C.onAccent, flexShrink: 0,
};
const SW: CSSProperties = { width: 12, height: 12, borderRadius: R.sm, flexShrink: 0 };
const NAME: CSSProperties = { fontSize: FS.base, fontWeight: 600, color: C.textHeading, flex: 1, minWidth: 0 };
// Доля по выбранной мере: mono 700 (макет .top .p).
const PCT: CSSProperties = {
  marginLeft: 'auto', fontFamily: FONT.mono, fontSize: FS.base, fontWeight: 700,
  color: C.textHeading, whiteSpace: 'nowrap',
};

// Живые чипы персоны — те же, что в таблице (макет .chip).
const LIVE: CSSProperties = {
  display: 'inline-flex', alignItems: 'center', gap: SP.xs, height: 20,
  padding: `0 ${SP.xs}px`, borderRadius: R.max, fontSize: FS.xxs, fontWeight: 600,
};

// Чипы связи разрезов — те же, что в таблице (макет .c-chips): нейтральная рамка;
// персона — аватар + имя.
const CHIPS: CSSProperties = { display: 'flex', gap: SP.xs, flexWrap: 'wrap', marginTop: SP.xs };
const CHIP: CSSProperties = {
  display: 'inline-flex', alignItems: 'center', gap: SP.xs,
  fontSize: FS.xxs, padding: `1px ${SP.xs}px`,
  border: `1px solid ${C.border}`, color: C.textSecondary,
  borderRadius: R.sm, whiteSpace: 'nowrap',
};
const CHIP_AV: CSSProperties = {
  width: 18, height: 18, borderRadius: R.full, display: 'inline-flex',
  alignItems: 'center', justifyContent: 'center',
  fontSize: FS.xxs, fontWeight: 700, color: C.onAccent, flexShrink: 0,
};

// Три мини-полосы 6 px (макет .c-mr .ms): сетка 62px/1fr/74px — подпись, полоса от
// МАКСИМУМА колонки (как ячейки таблицы), значение (mono). Без учёта — слово в
// своей строке: ни полосы, ни процента.
const MS: CSSProperties = {
  display: 'grid', gridTemplateColumns: '62px 1fr 74px', gap: `${SP.xs}px ${SP.sm}px`,
  alignItems: 'center', marginTop: SP.sm,
};
const M_LABEL: CSSProperties = { fontSize: FS.xs, color: C.textMuted };
const MINI_BAR: CSSProperties = { height: 6, borderRadius: R.max, background: C.bgSelected, overflow: 'hidden' };
const M_VAL: CSSProperties = {
  fontFamily: FONT.mono, fontSize: FS.xs, fontWeight: 600,
  color: C.textHeading, textAlign: 'right', whiteSpace: 'nowrap',
};

// «N чатов ›» — сырой button со сбросом UA-стилей (урок ST-F7): без font/cursor/
// textAlign браузер центрирует текст и даёт cursor: default. Высота 44 — тач-цель
// на 360 (к1), как строки чатов в ST-F10.
const CHAT_BTN: CSSProperties = {
  background: 'none', border: 'none', cursor: 'pointer', font: 'inherit',
  color: C.accent, fontSize: FS.sm, fontWeight: 600, textAlign: 'left',
  display: 'flex', alignItems: 'center', gap: SP.xs, height: 44, marginTop: SP.xs,
};

// Сумма группы по мере: null — «нет учёта» (не 0); 0 при токенах — «бесплатно»
// (formatMoney модели), остальное — форматтеры ST-F8.
function measureText(g: FeatureBreakdownGroup, measure: Measure): string {
  const v = measureValue(g, measure);
  if (v == null) return 'нет учёта';
  if (measure === 'time') return formatDuration(v);
  if (measure === 'tokens') return formatTokens(v);
  return formatMoney(g);
}

export function FeatureBreakdownCards({
  groups, axis, measure, run, labelOf, colorOf, hiddenKeys,
}: FeatureBreakdownCardsProps) {
  // Хук выше раннего return: раскрытая группа — состояние карточек; мера приходит
  // из props, поэтому при её смене состояние раскрытия не сбрасывается (к3).
  const [expandedKey, setExpandedKey] = useState<string | null>(null);

  // Скрытые группы отсекаются до guard'а: чего не рисуем — в долях нет.
  const hidden = new Set(hiddenKeys ?? []);
  const visible = groups.filter(g => !hidden.has(g.key));

  // Меньше двух групп — сравнивать нечего; пустое состояние рисует оболочка (ST-F13).
  if (fewData(visible)) return null;

  const sorted = sortGroups(visible, measure);
  const total = visible.reduce((a, g) => a + (measureValue(g, measure) ?? 0), 0);
  // Максимум колонки — база мини-полос (как ячейки таблицы ST-F11), не сумма.
  const maxOf = (m: Measure) => Math.max(...visible.map(g => measureValue(g, m) ?? 0), 1);

  return (
    <div data-testid="feature-breakdown-cards">
      {sorted.map((g, i) => {
        const color = groupColor(g.key, i, colorOf);
        const label = labelOf ? labelOf(g.key) : g.key;
        const live = axis === 'personas' ? liveChipText(run, g.key) : null;
        const chips = groupChips(axis, g.key, run);
        const v = measureValue(g, measure);
        const chatIds = (g.chatIds ?? []).filter(Boolean);
        const open = expandedKey === g.key;
        return (
          <div
            key={g.key}
            data-testid={`breakdown-card-${g.key}`}
            style={open ? CARD_OPEN : CARD}
          >
            <div style={TOP}>
              {axis === 'personas' ? (
                <span aria-hidden style={{ ...AV, background: color }}>{label.charAt(0).toUpperCase()}</span>
              ) : (
                <span aria-hidden style={{ ...SW, background: color }} />
              )}
              <span style={NAME}>{label}</span>
              {live ? (
                <span
                  data-testid={`live-chip-${g.key}`}
                  style={live === 'работает'
                    ? { ...LIVE, background: C.accentLight, color: C.accent }
                    : { ...LIVE, background: C.infoBg, color: C.info }}
                >
                  {live === 'работает' ? <LiveDot color={C.accent} size={6} label="работает" /> : <Dot color={C.info} size={6} />}
                  {live}
                </span>
              ) : null}
              {v == null ? null : (
                <span data-testid={`share-pct-${g.key}`} style={PCT}>
                  {percentText(total > 0 ? v / total : 0)}
                </span>
              )}
            </div>
            <div data-testid={`breakdown-chips-${g.key}`} style={CHIPS}>
              {chips.map(c => c.kind === 'persona' ? (
                <span key={`${c.kind}:${c.key}`} data-testid={`breakdown-chip-${c.kind}:${c.key}`} style={{ ...CHIP, border: 'none' }}>
                  <span aria-hidden style={{ ...CHIP_AV, background: groupColor(c.key, 0, colorOf) }}>
                    {(labelOf ? labelOf(c.key) : c.key).charAt(0).toUpperCase()}
                  </span>
                  {chipLabel(c, labelOf)}
                </span>
              ) : (
                <span key={`${c.kind}:${c.key}`} data-testid={`breakdown-chip-${c.kind}:${c.key}`} style={CHIP}>{chipLabel(c, labelOf)}</span>
              ))}
            </div>
            <div style={MS}>
              {MEASURES.map(m => {
                const mv = measureValue(g, m);
                if (mv == null) {
                  return (
                    <Fragment key={m}>
                      <span style={M_LABEL}>{MEASURE_WORD[m]}</span>
                      <span data-testid={`measure-none-${m}`} style={{ fontSize: FS.xs, color: C.textMuted }}>нет учёта</span>
                      <span />
                    </Fragment>
                  );
                }
                return (
                  <Fragment key={m}>
                    <span style={M_LABEL}>{MEASURE_WORD[m]}</span>
                    <span aria-hidden data-testid={`measure-bar-${m}`} style={MINI_BAR}>
                      <i style={{
                        display: 'block', height: '100%', borderRadius: R.max, background: color,
                        width: `${(mv / maxOf(m)) * 100}%`,
                      }} />
                    </span>
                    <span data-testid={`measure-value-${m}`} style={M_VAL}>{measureText(g, m)}</span>
                  </Fragment>
                );
              })}
            </div>
            {chatIds.length > 0 ? (
              <button
                type="button"
                data-testid={`breakdown-chats-toggle-${g.key}`}
                style={CHAT_BTN}
                onClick={() => setExpandedKey(open ? null : g.key)}
              >
                {chatIds.length} {plural(chatIds.length, 'чат', 'чата', 'чатов')} ›
              </button>
            ) : null}
            {open ? <FeatureBreakdownChats group={g} run={run} isMobile={true} /> : null}
          </div>
        );
      })}
    </div>
  );
}
