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
import { Dot } from '../ui/Dot';
import { LiveDot } from '../ui/LiveDot';
import { formatDuration } from './FeatureStatsBits';
import { formatTokens } from './FeatureStatsFormat';
import {
  type Axis, type Measure,
  sortGroups, formatMoney, groupChips, liveChipText, fewData, percentText, measureValue,
  measureTotal, measureMax,
} from './FeatureBreakdownModel';
import {
  MEASURES, MEASURE_WORD, BREAKDOWN_FOCUS_CLASS, groupColor, chipColor, chipLabel, CHIP, CHIP_AV, AV, SW, liveChipStyle,
} from './FeatureBreakdownShared';
import { FeatureBreakdownChats } from './FeatureBreakdownChats';
import type { FeatureBreakdownGroup } from '../../types/featureStats';
import type { FeatureRun } from '../../types/featureRun';
import type { FeatureNavCtx } from '../../lib/featureNavMarks';

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
  // Подпись персоны в чипах связи: чипы персон живут в разрезах моделей и ролей, где labelOf — не про персон.
  personaLabelOf?: (key: string) => string;
  // Цвет группы: у персон — цвет персоны; фолбэк — палитра по порядку.
  colorOf?: (key: string) => string;
  // Ключи групп, которые не рисуем (роль key='feature' — вид ещё не решил).
  hiddenKeys?: string[];
  // Контекст возврата для переходов в чаты раскрытой группы.
  nav?: FeatureNavCtx;
}

// Мера в подписи над карточками: «По убыванию времени / токенов / денег».
const MEASURE_GEN: Record<Measure, string> = { time: 'времени', tokens: 'токенов', cost: 'денег' };
// Подпись над карточками (макет :343): muted 11 px, отступ снизу 8.
const CAPTION: CSSProperties = { fontSize: FS.xs, color: C.textMuted, marginBottom: SP.sm };

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
const NAME: CSSProperties = { fontSize: FS.base, fontWeight: 600, color: C.textHeading, flex: 1, minWidth: 0 };
// Доля по выбранной мере: mono 700 (макет .top .p).
const PCT: CSSProperties = {
  marginLeft: 'auto', fontFamily: FONT.mono, fontSize: FS.base, fontWeight: 700,
  color: C.textHeading, whiteSpace: 'nowrap',
};

// Чипы связи разрезов — те же, что в таблице (макет .c-chips): нейтральная рамка;
// персона — аватар + имя.
const CHIPS: CSSProperties = { display: 'flex', gap: SP.xs, flexWrap: 'wrap', marginTop: SP.xs };

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
  groups, axis, measure, run, labelOf, personaLabelOf: personaLabelOfProp, colorOf, hiddenKeys, nav,
}: FeatureBreakdownCardsProps) {
  const personaLabelOf = personaLabelOfProp ?? labelOf;
  // Хук выше раннего return: раскрытая группа — состояние карточек; мера приходит
  // из props, поэтому при её смене состояние раскрытия не сбрасывается (к3).
  const [expandedKey, setExpandedKey] = useState<string | null>(null);

  // Скрытые группы отсекаются до guard'а: чего не рисуем — в долях нет. (Оболочка
  // уже отдаёт отфильтрованный набор; фильтр здесь — для автономного вызова.)
  const hidden = new Set(hiddenKeys ?? []);
  const visible = groups.filter(g => !hidden.has(g.key));

  // Меньше двух групп — сравнивать нечего; пустое состояние рисует оболочка (ST-F13).
  if (fewData(visible)) return null;

  const sorted = sortGroups(visible, measure);
  const total = measureTotal(visible, measure) ?? 0;
  // Максимум колонки — база мини-полос (как ячейки таблицы ST-F11), не сумма.
  const maxOf = (m: Measure) => measureMax(visible, m);
  // Раскрыта только видимая группа: скрытую состояние не удерживает.
  const openKey = visible.some(g => g.key === expandedKey) ? expandedKey : null;

  return (
    <div data-testid="feature-breakdown-cards">
      <div data-testid="breakdown-cards-caption" style={CAPTION}>
        По убыванию {MEASURE_GEN[measure]} · сортировка — в шапке
      </div>
      {sorted.map(g => {
        const color = groupColor(g.key, visible, colorOf, axis);
        const label = labelOf ? labelOf(g.key) : g.key;
        const live = axis === 'personas' ? liveChipText(run, g.key) : null;
        const chips = groupChips(axis, g.key, run);
        const v = measureValue(g, measure);
        const chatIds = (g.chatIds ?? []).filter(Boolean);
        const open = openKey === g.key;
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
                <span data-testid={`live-chip-${g.key}`} style={liveChipStyle(live)}>
                  {/* Точка декоративна: слово рядом уже говорит «работает». */}
                  {live === 'работает' ? <span aria-hidden><LiveDot color={C.accent} size={6} /></span> : <Dot color={C.info} size={6} />}
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
                  <span aria-hidden style={{ ...CHIP_AV, background: chipColor(c.key, colorOf) }}>
                    {(personaLabelOf ? personaLabelOf(c.key) : c.key).charAt(0).toUpperCase()}
                  </span>
                  {chipLabel(c, personaLabelOf)}
                </span>
              ) : (
                <span key={`${c.kind}:${c.key}`} data-testid={`breakdown-chip-${c.kind}:${c.key}`} style={CHIP}>{chipLabel(c, personaLabelOf)}</span>
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
                className={BREAKDOWN_FOCUS_CLASS}
                data-testid={`breakdown-chats-toggle-${g.key}`}
                aria-expanded={open}
                style={CHAT_BTN}
                onClick={() => setExpandedKey(open ? null : g.key)}
              >
                {chatIds.length} {plural(chatIds.length, 'чат', 'чата', 'чатов')} ›
              </button>
            ) : null}
            {open ? <FeatureBreakdownChats group={g} run={run} isMobile={true} nav={nav} /> : null}
          </div>
        );
      })}
    </div>
  );
}
