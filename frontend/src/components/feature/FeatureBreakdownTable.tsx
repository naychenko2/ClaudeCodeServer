// «Кто и на чём работал» — таблица разреза (волна 5, ST-F11; вариант C «Таблица» макета
// feature-stats-breakdown-5.html, записка §«Таблица»). Числа живут в FeatureBreakdownModel
// (ST-F8): sortGroups, formatMoney, groupChips, liveChipText, fewData — компонент ничего
// не пересчитывает. Оболочка блока (заголовок, переключитель разреза, плашки «мало данных»
// и «без расхода») — ST-F13; мобильные карточки — ST-F12. Разрезать нечего → null.
//
// Колонки: имя (персона — аватар цвета группы, модель/роль — квадрат цвета), «Модели и
// роли» (чипы groupChips), время/токены/деньги — полоса 8 px от максимума колонки, значение
// и доля от суммы. Клик по заголовку числовой колонки сортирует по этой мере (aria-sort
// «descending» у активной; по умолчанию — время). «N чатов ›» раскрывает чаты строки
// (FeatureBreakdownChats, ST-F10) — одна строка за раз, блок под таблицей, как в макете.
// hiddenKeys — строки, которые вид решил не показывать (роль key='feature' не решена,
// поэтому здесь только проп).

import { useState, type CSSProperties, type ReactNode } from 'react';
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

export interface FeatureBreakdownTableProps {
  // Разрез выбранной оси (stats.breakdown[axis]).
  groups: FeatureBreakdownGroup[];
  axis: Axis;
  // Run для чипов «Модели и роли» и живых чипов (run.stages[].executors[]).
  run: FeatureRun;
  // Человекочитаемое имя группы (контракт тот же, что у ShareRing): сырой ключ (UUID)
  // в таблице не читается. Фолбэк — сам ключ.
  labelOf?: (key: string) => string;
  // Цвет группы: у персон — цвет персоны; фолбэк — палитра по порядку (как у ShareRing).
  colorOf?: (key: string) => string;
  // Ключи строк, которые не рисуем (роль key='feature' — вид ещё не решил).
  hiddenKeys?: string[];
  // Передаётся только раскрытым чатам: сама таблица — десктопная (ST-F12 — карточки).
  isMobile?: boolean;
}

// Палитра групп — та же, что у ShareRing: если colorOf не передан.
const ARC_COLORS = [C.accent, C.info, C.plan, C.success, C.warning, C.progressTrack];

const MEASURE_WORD: Record<Measure, string> = { time: 'время', tokens: 'токены', cost: 'деньги' };
const MEASURES: Measure[] = ['time', 'tokens', 'cost'];

function groupColor(key: string, index: number, colorOf?: (k: string) => string): string {
  return colorOf ? colorOf(key) : ARC_COLORS[index % ARC_COLORS.length];
}

// Подпись чипа: модель — нормализованное имя (groupChips уже снимает «Авто → »); роль —
// слово roleLabel без шага; для 'step' roleLabel пуст — нейтральное «шаг» (тот же приём,
// что в roleWord модели).
function chipLabel(chip: BreakdownChip, labelOf?: (k: string) => string): string {
  if (chip.kind === 'model') return chip.key;
  if (chip.kind === 'role') return roleLabel(chip.key, null, null) || (chip.key === 'step' ? 'шаг' : chip.key);
  return labelOf ? labelOf(chip.key) : chip.key;
}

// Шапка: FS.xxs UPPERCASE textMuted (макет .c-tab th). Кнопка внутри th — с явным сбросом
// UA-стилей (урок ST-F7): без font/cursor/textAlign браузер центрирует текст и даёт
// cursor: default.
const TH: CSSProperties = {
  fontSize: FS.xxs, fontWeight: 700, textTransform: 'uppercase', letterSpacing: 0.5,
  color: C.textMuted, textAlign: 'left', padding: `0 ${SP.sm}px ${SP.xs}px`, whiteSpace: 'nowrap',
};
const TH_BTN: CSSProperties = { ...TH, background: 'none', border: 'none', cursor: 'pointer', font: 'inherit' };

const TD: CSSProperties = {
  padding: `${SP.sm}px ${SP.md}px`,
  borderTop: `1px solid ${C.borderLight}`,
  verticalAlign: 'middle',
};
// Строка «Всего»: двойной разделитель сверху, жирный текст (макет .c-tot).
const TD_TOTAL: CSSProperties = { ...TD, borderTop: `2px solid ${C.border}`, fontWeight: 700, color: C.textHeading };

// Ячейка меры: полоса 8 px от максимума колонки + значение (mono) + доля (макет .c-cell).
const CELL: CSSProperties = { display: 'flex', alignItems: 'center', gap: SP.sm, minWidth: 150 };
const BAR: CSSProperties = { flex: 1, height: 8, borderRadius: R.max, background: C.bgSelected, overflow: 'hidden' };
const VAL: CSSProperties = {
  fontFamily: FONT.mono, fontSize: FS.sm, fontWeight: 600,
  color: C.textHeading, width: 84, textAlign: 'right', whiteSpace: 'nowrap',
};

// Имя группы: аватар персоны 22 (цвет группы, буква) или квадрат 12 для модели/роли
// (макет .av / .sw).
const AV: CSSProperties = {
  width: 22, height: 22, borderRadius: R.full, display: 'inline-flex',
  alignItems: 'center', justifyContent: 'center',
  fontSize: FS.xs, fontWeight: 700, color: C.onAccent, flexShrink: 0,
};
const SW: CSSProperties = { width: 12, height: 12, borderRadius: R.sm, flexShrink: 0 };

// Чипы «Модели и роли»: нейтральные рамки (макет .mdl); персона — аватар + имя.
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

// Живые чипы персоны (макет .chip): «работает» — accent на accentLight с пульсом,
// «ждёт вас» — info на infoBg.
const LIVE: CSSProperties = {
  display: 'inline-flex', alignItems: 'center', gap: SP.xs, height: 20,
  padding: `0 ${SP.xs}px`, borderRadius: R.max, fontSize: FS.xxs, fontWeight: 600,
};

// «N чатов ›» — акцентная ссылка раскрытия (макет: accent, 12px).
const CHAT_LINK: CSSProperties = {
  background: 'none', border: 'none', cursor: 'pointer',
  color: C.accent, fontSize: FS.sm, fontWeight: 600, whiteSpace: 'nowrap',
};

// Одна ячейка меры. Без учёта — фраза один раз: ни полосы, ни процента (состояние
// «без расхода» записки). «Бесплатно» и деньги — через formatMoney модели.
function measureCell(
  g: FeatureBreakdownGroup,
  measure: Measure,
  max: number,
  color: string,
  total: number,
): ReactNode {
  const v = measureValue(g, measure);
  if (v == null) {
    return <span style={{ fontSize: FS.xs, color: C.textMuted }}>нет учёта</span>;
  }
  const value = measure === 'time' ? formatDuration(v) : measure === 'tokens' ? formatTokens(v) : formatMoney(g);
  return (
    <div style={CELL}>
      <span aria-hidden data-testid="measure-bar" style={BAR}>
        <i style={{ display: 'block', height: '100%', borderRadius: R.max, background: color, width: `${(v / max) * 100}%` }} />
      </span>
      <span style={VAL}>
        {value}
        <small style={{ color: C.textMuted, fontWeight: 400 }}> {percentText(total > 0 ? v / total : 0)}</small>
      </span>
    </div>
  );
}

export function FeatureBreakdownTable({
  groups, axis, run, labelOf, colorOf, hiddenKeys, isMobile = false,
}: FeatureBreakdownTableProps) {
  // Хуки выше раннего return: мера сортировки и раскрытая строка — состояние таблицы.
  const [measure, setMeasure] = useState<Measure>('time');
  const [expandedKey, setExpandedKey] = useState<string | null>(null);

  // Скрытые строки отсекаются до guard'а и до «Всего»: чего не рисуем — в итогах нет.
  const hidden = new Set(hiddenKeys ?? []);
  const visible = groups.filter(g => !hidden.has(g.key));

  // Меньше двух групп — сравнивать нечего; пустое состояние рисует оболочка (ST-F13).
  if (fewData(visible)) return null;

  const sorted = sortGroups(visible, measure);
  const maxOf = (m: Measure) => Math.max(...visible.map(g => measureValue(g, m) ?? 0), 1);
  const totalOf = (m: Measure) => visible.reduce((a, g) => a + (measureValue(g, m) ?? 0), 0);
  const hasOf = (m: Measure) => visible.some(g => measureValue(g, m) != null);

  const expandedGroup = expandedKey ? visible.find(g => g.key === expandedKey) ?? null : null;
  const axisWord = axis === 'personas' ? 'персоны' : axis === 'models' ? 'модели' : 'роли';

  return (
    <div data-testid="feature-breakdown-table">
      <table style={{ width: '100%', borderCollapse: 'separate', borderSpacing: 0 }}>
        <thead>
          <tr>
            <th style={TH}>{axisWord}</th>
            <th style={TH}>{axis === 'personas' ? 'Модели и роли' : 'Кто'}</th>
            {MEASURES.map(m => (
              <th
                key={m}
                aria-sort={measure === m ? 'descending' : 'none'}
                style={measure === m ? { ...TH, color: C.textHeading } : TH}
              >
                <button type="button" data-testid={`breakdown-sort-${m}`} style={TH_BTN} onClick={() => setMeasure(m)}>
                  {MEASURE_WORD[m]}
                  {measure === m ? <span aria-hidden style={{ color: C.accent }}> ↓</span> : null}
                </button>
              </th>
            ))}
            <th style={TH}>чаты</th>
          </tr>
        </thead>
        <tbody>
          {sorted.map((g, i) => {
            const color = groupColor(g.key, i, colorOf);
            const label = labelOf ? labelOf(g.key) : g.key;
            const live = axis === 'personas' ? liveChipText(run, g.key) : null;
            const chips = groupChips(axis, g.key, run);
            const chatIds = (g.chatIds ?? []).filter(Boolean);
            const selected = expandedKey === g.key;
            return (
              <tr
                key={g.key}
                data-testid={`breakdown-row-${g.key}`}
                style={selected ? { background: C.accentLight } : undefined}
              >
                <td style={selected ? { ...TD, boxShadow: `inset 3px 0 0 ${C.accent}` } : TD}>
                  <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap' }}>
                    {axis === 'personas' ? (
                      <span aria-hidden style={{ ...AV, background: color }}>{label.charAt(0).toUpperCase()}</span>
                    ) : (
                      <span aria-hidden style={{ ...SW, background: color }} />
                    )}
                    <span style={{ fontSize: FS.base, fontWeight: 600, color: C.textHeading }}>{label}</span>
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
                  </div>
                </td>
                <td style={TD}>
                  <div style={{ display: 'flex', gap: SP.xs, flexWrap: 'wrap' }}>
                    {chips.map(c => c.kind === 'persona' ? (
                      <span key={`${c.kind}:${c.key}`} style={{ ...CHIP, border: 'none' }}>
                        <span aria-hidden style={{ ...CHIP_AV, background: groupColor(c.key, 0, colorOf) }}>
                          {(labelOf ? labelOf(c.key) : c.key).charAt(0).toUpperCase()}
                        </span>
                        {chipLabel(c, labelOf)}
                      </span>
                    ) : (
                      <span key={`${c.kind}:${c.key}`} style={CHIP}>{chipLabel(c, labelOf)}</span>
                    ))}
                  </div>
                </td>
                {MEASURES.map(m => (
                  <td key={m} style={TD}>{measureCell(g, m, maxOf(m), color, totalOf(m))}</td>
                ))}
                <td style={{ ...TD, textAlign: 'right' }}>
                  {chatIds.length > 0 ? (
                    <button
                      type="button"
                      data-testid={`breakdown-chats-toggle-${g.key}`}
                      style={CHAT_LINK}
                      onClick={() => setExpandedKey(selected ? null : g.key)}
                    >
                      {chatIds.length} {plural(chatIds.length, 'чат', 'чата', 'чатов')} ›
                    </button>
                  ) : null}
                </td>
              </tr>
            );
          })}
          <tr data-testid="breakdown-total">
            <td style={TD_TOTAL}>Всего</td>
            <td style={TD_TOTAL} />
            <td style={TD_TOTAL}><span style={VAL}>{formatDuration(totalOf('time'))}</span></td>
            <td style={TD_TOTAL}>
              <span style={VAL}>{hasOf('tokens') ? formatTokens(totalOf('tokens')) : 'нет учёта'}</span>
            </td>
            <td style={TD_TOTAL}>
              <span style={VAL}>
                {formatMoney({
                  key: '',
                  minutes: 0,
                  tokens: hasOf('tokens') ? totalOf('tokens') : 0,
                  costUsd: hasOf('cost') ? totalOf('cost') : null,
                  chatIds: [],
                })}
              </span>
            </td>
            <td style={TD_TOTAL} />
          </tr>
        </tbody>
      </table>
      {expandedGroup ? <FeatureBreakdownChats group={expandedGroup} run={run} isMobile={isMobile} /> : null}
    </div>
  );
}
