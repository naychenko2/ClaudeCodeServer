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
import { Dot } from '../ui/Dot';
import { LiveDot } from '../ui/LiveDot';
import { formatDuration } from './FeatureStatsBits';
import { formatTokens } from './FeatureStatsFormat';
import {
  type Axis, type Measure,
  sortGroups, formatMoney, groupChips, liveChipText, fewData, percentText, measureValue,
  measureTotal, measureMax, totalMoney,
} from './FeatureBreakdownModel';
import {
  MEASURES, MEASURE_WORD, BREAKDOWN_FOCUS_CLASS, groupColor, chipColor, chipLabel, CHIP, CHIP_AV, AV, SW, liveChipStyle,
} from './FeatureBreakdownShared';
import { FeatureBreakdownChats } from './FeatureBreakdownChats';
import type { FeatureBreakdownGroup } from '../../types/featureStats';
import type { FeatureRun } from '../../types/featureRun';
import type { FeatureNavCtx } from '../../lib/featureNavMarks';

export interface FeatureBreakdownTableProps {
  // Разрез выбранной оси (stats.breakdown[axis]).
  groups: FeatureBreakdownGroup[];
  axis: Axis;
  // Run для чипов «Модели и роли» и живых чипов (run.stages[].executors[]).
  run: FeatureRun;
  // Человекочитаемое имя группы (контракт тот же, что у ShareRing): сырой ключ (UUID)
  // в таблице не читается. Фолбэк — сам ключ.
  // Часть моделей без цены (spend.costPartial): «Всего» не может быть «бесплатно».
  costPartial?: boolean;
  labelOf?: (key: string) => string;
  // Подпись персоны в чипах связи: чипы персон живут в разрезах моделей и ролей, где labelOf — не про персон.
  personaLabelOf?: (key: string) => string;
  // Цвет группы: у персон — цвет персоны; фолбэк — палитра по порядку (как у ShareRing).
  colorOf?: (key: string) => string;
  // Ключи строк, которые не рисуем (роль key='feature' — вид ещё не решил).
  hiddenKeys?: string[];
  // Мера сортировки: если задана оболочкой — она же мера шапки и карточек (единая мера),
  // заголовок колонки переключает её через onMeasureChange. Без пропа — своё состояние.
  measure?: Measure;
  onMeasureChange?: (m: Measure) => void;
  // Передаётся только раскрытым чатам: сама таблица — десктопная (ST-F12 — карточки).
  isMobile?: boolean;
  // Контекст возврата для переходов в чаты раскрытой группы.
  nav?: FeatureNavCtx;
}

// Шапка: FS.xxs UPPERCASE textMuted (макет .c-tab th). Кнопка внутри th — с явным сбросом
// UA-стилей (урок ST-F7): без font/cursor/textAlign браузер центрирует текст и даёт
// cursor: default.
const TH: CSSProperties = {
  fontSize: FS.xxs, fontWeight: 700, textTransform: 'uppercase', letterSpacing: 0.5,
  color: C.textMuted, textAlign: 'left', padding: `0 ${SP.md}px ${SP.xs}px`, whiteSpace: 'nowrap',
};
// Отступы th уже задал TH — у кнопки их нет (иначе двойной отступ).
const TH_BTN: CSSProperties = { ...TH, padding: 0, background: 'none', border: 'none', cursor: 'pointer', font: 'inherit' };

const TD: CSSProperties = {
  padding: `${SP.sm}px ${SP.md}px`,
  borderTop: `1px solid ${C.borderLight}`,
  verticalAlign: 'middle',
};
// Строка «Всего»: двойной разделитель сверху, жирный текст (макет .c-tot).
const TD_TOTAL: CSSProperties = { ...TD, borderTop: `2px solid ${C.border}`, fontWeight: 700, color: C.textHeading };

// Ячейка меры: полоса 8 px от максимума колонки + значение (mono) + доля (макет .c-cell).
const CELL: CSSProperties = { display: 'flex', alignItems: 'center', gap: SP.sm };
const BAR: CSSProperties = { flex: 1, minWidth: 16, height: 8, borderRadius: R.max, background: C.bgSelected, overflow: 'hidden' };
const VAL: CSSProperties = {
  fontFamily: FONT.mono, fontSize: FS.sm, fontWeight: 600,
  color: C.textHeading, minWidth: 72, textAlign: 'right', whiteSpace: 'nowrap',
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
  total: number | null,
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
        <small style={{ display: 'block', color: C.textMuted, fontWeight: 400 }}>{percentText(total != null && total > 0 ? v / total : 0)}</small>
      </span>
    </div>
  );
}

export function FeatureBreakdownTable({
  groups, axis, run, labelOf, personaLabelOf: personaLabelOfProp, colorOf, hiddenKeys, measure: measureProp, onMeasureChange, isMobile = false, nav, costPartial = false,
}: FeatureBreakdownTableProps) {
  const personaLabelOf = personaLabelOfProp ?? labelOf;
  // Хуки выше раннего return: мера сортировки (если оболочка её не ведёт) и раскрытая строка.
  const [ownMeasure, setOwnMeasure] = useState<Measure>('time');
  const measure = measureProp ?? ownMeasure;
  const setMeasure = (m: Measure) => { setOwnMeasure(m); onMeasureChange?.(m); };
  const [expandedKey, setExpandedKey] = useState<string | null>(null);

  // Скрытые строки отсекаются до guard'а и до «Всего»: чего не рисуем — в итогах нет.
  // (Оболочка уже отдаёт отфильтрованный набор; фильтр здесь — для автономного вызова.)
  const hidden = new Set(hiddenKeys ?? []);
  const visible = groups.filter(g => !hidden.has(g.key));

  // Меньше двух групп — сравнивать нечего; пустое состояние рисует оболочка (ST-F13).
  if (fewData(visible)) return null;

  const sorted = sortGroups(visible, measure);
  const maxOf = (m: Measure) => measureMax(visible, m);
  const totalOf = (m: Measure) => measureTotal(visible, m) ?? 0;
  const totalCost = totalMoney(visible, costPartial);
  const hasOf = (m: Measure) => measureTotal(visible, m) != null;

  // Раскрыта только видимая строка: скрытую группу состояние не удерживает.
  const expandedGroup = expandedKey ? visible.find(g => g.key === expandedKey) ?? null : null;
  const openKey = expandedGroup ? expandedGroup.key : null;
  const axisWord = axis === 'personas' ? 'персоны' : axis === 'models' ? 'модели' : 'роли';

  return (
    <div data-testid="feature-breakdown-table">
      {/* Узкая панель: таблица прокручивается по горизонтали, а не вылезает за край. */}
      <div style={{ overflowX: 'auto' }}>
      <table style={{ width: '100%', borderCollapse: 'separate', borderSpacing: 0 }}>
        <thead>
          <tr>
            <th style={TH}>{axisWord}</th>
            <th style={TH}>{axis === 'personas' ? 'Модели и роли' : 'Кто'}</th>
            {MEASURES.map(m => (
              <th key={m} aria-sort={measure === m ? 'descending' : undefined} style={TH}>
                <button
                  type="button"
                  className={BREAKDOWN_FOCUS_CLASS}
                  data-testid={`breakdown-sort-${m}`}
                  aria-label={`Сортировать по: ${MEASURE_WORD[m]}`}
                  style={measure === m ? { ...TH_BTN, color: C.textHeading } : TH_BTN}
                  onClick={() => setMeasure(m)}
                >
                  {MEASURE_WORD[m]}
                  {measure === m ? <span aria-hidden style={{ color: C.accent }}> ↓</span> : null}
                </button>
              </th>
            ))}
            <th style={TH}>чаты</th>
          </tr>
        </thead>
        <tbody>
          {sorted.map(g => {
            const color = groupColor(g.key, visible, colorOf, axis);
            const label = labelOf ? labelOf(g.key) : g.key;
            const live = axis === 'personas' ? liveChipText(run, g.key) : null;
            const chips = groupChips(axis, g.key, run);
            const chatIds = (g.chatIds ?? []).filter(Boolean);
            const selected = openKey === g.key;
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
                      <span data-testid={`live-chip-${g.key}`} style={liveChipStyle(live)}>
                        {/* Точка декоративна: слово рядом уже говорит «работает». */}
                        {live === 'работает' ? <span aria-hidden><LiveDot color={C.accent} size={6} /></span> : <Dot color={C.info} size={6} />}
                        {live}
                      </span>
                    ) : null}
                  </div>
                </td>
                <td style={TD}>
                  <div style={{ display: 'flex', gap: SP.xs, flexWrap: 'wrap' }}>
                    {chips.map(c => c.kind === 'persona' ? (
                      <span key={`${c.kind}:${c.key}`} style={{ ...CHIP, border: 'none' }}>
                        <span aria-hidden style={{ ...CHIP_AV, background: chipColor(c.key, colorOf) }}>
                          {(personaLabelOf ? personaLabelOf(c.key) : c.key).charAt(0).toUpperCase()}
                        </span>
                        {chipLabel(c, personaLabelOf)}
                      </span>
                    ) : (
                      <span key={`${c.kind}:${c.key}`} style={CHIP}>{chipLabel(c, personaLabelOf)}</span>
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
                      className={BREAKDOWN_FOCUS_CLASS}
                      data-testid={`breakdown-chats-toggle-${g.key}`}
                      aria-expanded={selected}
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
              <span style={VAL}>{hasOf('tokens') ? formatTokens(totalOf('tokens')) : '—'}</span>
            </td>
            <td style={TD_TOTAL}>
              <span style={VAL}>
                {!hasOf('cost') ? '—' : totalCost.text}
                {hasOf('cost') && totalCost.partial ? (
                  <small data-testid="breakdown-total-partial" style={{ display: 'block', color: C.textMuted, fontWeight: 400 }}>
                    не по всем
                  </small>
                ) : null}
              </span>
            </td>
            <td style={TD_TOTAL} />
          </tr>
        </tbody>
      </table>
      </div>
      {expandedGroup ? <FeatureBreakdownChats group={expandedGroup} run={run} isMobile={isMobile} nav={nav} /> : null}
    </div>
  );
}
