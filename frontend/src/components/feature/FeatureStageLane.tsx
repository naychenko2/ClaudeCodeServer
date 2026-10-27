// Дорожка этапа «Хронологии» (GC-F5a, план «Гант на Хронологии», макеты feature-gantt-6/7).
// Рендерит StageLane из FeatureLaneModel как есть: ничего НЕ считает, всё позиционирует
// через axisFrac — ту же функцию, что у модели и оси (одна шкала на все дорожки).
// Своего состояния нет: roving tabindex, раскрытие поповера и hover — у родителя
// (GC-F5c/GC-F6) и приходят пропсами.
//
// Геометрия (feature-gantt-6 §«Спецификация для фронта»): дорожка 14 px, интервал 10 px
// (top 2 px), минимальная ширина 3 px — она расширяет только нарисованное, left остаётся
// точным. Цвета по состоянию интервала: done C.success, live C.accent, wait C.info,
// stalled — штриховка C.warning/C.warningBg; пауза — пунктир 1 px C.textMuted по середине
// дорожки; lastOutcome — риска 2 px C.danger на конце интервала; возвраты — «↩» C.dangerText
// над дорожкой в момент вердикта (возвраты в модели есть только у prod).
//
// Продуктовое решение (находка m1 ревью GC-R-F2, 26.10): счётчик плана бывает только у
// «Производства». На дорожке это контур «ещё N» — он рисован ТОЛЬКО у prod; у прочих этапов,
// где работа ещё не начиналась, — слово «впереди» (слово, не число); у «Ревью» никаких
// плановых элементов нет: факт уже в aria-label каждого интервала («интервал i из n»).
//
// Пустая дорожка на закрытой оси (GC-F10c): у закрытой/смерженной фичи будущего нет, поэтому
// этап без интервалов здесь — не «начатый» и не «впереди», а «этапа не было»: тот же вид,
// что у skipped. Пустая дорожка живой фичи остаётся «впереди», approx — без слов (там
// отсутствие интервалов не является фактом об этапе: журнала нет).
//
// Состояние «ждёт» (контракт из ревью GC-R-F2R): интервал красится C.info — модель ставит
// 'wait' только интервалам, чей taskIds содержит ждущую задачу. То, что склеенный интервал
// частично занят чужой работой той же стадии, видно в деталях (GC-F6), не на дорожке.
//
// Подсветка интервалов шага (GC-F8R): состояние и таймер 2,4 с живут во вкладке
// (FeatureTimelineTab), здесь только рисование — проп `highlight`: перечисленные интервалы
// получают кольцо (форма .hl макета feature-gantt-7), прочие гаснут до opacity .35 (.dim);
// prefers-reduced-motion — без перехода (флаг приходит от вызывающего). На 360 интервал
// неинтерактивен — подсветка рисуется и там (тот же barStyle).
//
// Зона касания (дефект a67f8057, GC-F5aR2): полоса интервала остаётся тонкой (10 px по
// макету), но целью указателя является зона вокруг неё: высотой SP.xl (24 px), отцентрованная
// на полосе; по ширине — полоса плюс половина зазора до соседнего интервала (для первого и
// последнего — половина зазора до края дорожки). Зоны соседних интервалов касаются середины
// зазора и НЕ перекрываются. Зона — невидимая кнопка: на ней aria-label, tabIndex и
// обработчики (родитель меряет поповер по ней и возвращает фокус на неё); видимая полоса —
// отдельный слой `lane-band-*` с pointer-events: none, его позиция, цвет и ширина не
// меняются. На 360 интервал неинтерактивен — зоны там нет, та же полоса, что была. Все
// декоративные слои дорожки — пауза, линия и подпись «этапа не было», флажок «↩», зона
// «стоит», контур «впереди» — pointer-events: none: они не интерактивны, а раньше пауза
// перехватывала указатель поверх интервалов (31 из 39, вердикт GC-QA).

import { Fragment } from 'react';
import { C, R, FS, FONT, SP, SHADOW } from '../../lib/design';
import { axisFrac, type LaneAxis, type LaneIntervalState, type StageLane } from './FeatureLaneModel';
import { formatClock, formatDateTime, formatDuration } from './FeatureStatsBits';

// «Какие интервалы подсвечены» (GC-F8R): множество индексов lane.intervals + флаг рисования.
// Состояние подсветки (какой шаг, когда снять) живёт у вызывающего; здесь — только данные
// для рисования. animate — «меньше движения» вызывающего: с ним затемнение без перехода.
export interface LaneHighlight {
  at: Set<number>;
  animate: boolean;
}

export interface FeatureStageLaneProps {
  // Дорожка этапа из buildStageLanes.
  lane: StageLane;
  // Ось модели — одна для всех дорожек и оси вкладки.
  axis: LaneAxis;
  // Имя этапа для aria-label («{Этап}, интервал i из n: …») — его даёт вызывающий
  // (stageLabel(key)); компонент имён стадий не знает.
  label: string;
  // Индекс интервала с tabIndex=0. Roving tabindex: одна точка табуляции на весь Гант —
  // её выбирает родитель. По умолчанию 0 — первый интервал (одиночное использование).
  tabIndexAt?: number;
  // Интервал с закреплённым поповером/шторкой (aria-expanded="true"); null — ничего.
  expandedAt?: number | null;
  // Клик / Enter / Space по интервалу: родитель открывает поповер (1280) или шторку (360).
  // Второй аргумент — элемент интервала: им поповер меряет позицию и на него возвращает фокус.
  onSelect?: (index: number, el: HTMLElement | null) => void;
  // Наведение-превью (GC-F6); null — курсор ушёл с интервала.
  onHover?: (index: number | null, el: HTMLElement | null) => void;
  // Мобильный режим (360): интервал НЕ интерактивен — 10 px не тач-цель (GC-F6).
  // Вход в интервал — только через шторку этапа (кнопка 44 px под дорожкой).
  interactive?: boolean;
  // Кольцо выделения дорожки (клик по заголовку этапа — «лента → Гант»).
  selected?: boolean;
  // Индекс интервала под курсором: на него вешаем aria-describedby превью.
  hoverAt?: number | null;
  // id превью (role=tooltip) — ссылка описания для интервала под курсором.
  describedById?: string | null;
  // Подсветка интервалов шага («Показать интервалы на дорожке ↑», GC-F8R): null — подсветки
  // нет. Её состояние и таймер — у вызывающего; здесь только рисование.
  highlight?: LaneHighlight | null;
}

// Штриховка «без движения» (135°, та же геометрия штриховки, что у notWorking в FeatureStatsBits).
const STALLED_HATCH = `repeating-linear-gradient(135deg, ${C.warning} 0 5px, ${C.warningBg} 5px 10px)`;

// Геометрия полосы по макету (feature-gantt-6): top 2, height 10 — общая для полосы и центровки зоны.
const BAR_TOP = 2;
const BAR_H = 10;
// Зона касания (дефект a67f8057): высота SP.xl (24 px) — не меньше строки дорожки; центр зоны —
// центр полосы (2..12), поэтому top = 7 − 12 = −5.
const ZONE_H = SP.xl;
const ZONE_TOP = BAR_TOP + BAR_H / 2 - ZONE_H / 2;

// Заливка по состоянию интервала — цветовая таблица feature-gantt-6 §«Интервалы вместо
// одной полосы». Состояние решает модель (FeatureLaneModel), компонент только красит.
const stateBackground = (state: LaneIntervalState): string => {
  switch (state) {
    case 'done': return C.success;
    case 'live': return C.accent;
    case 'wait': return C.info;
    case 'stalled': return STALLED_HATCH;
  }
};

// Длительность интервала в минуты — та же арифметика spanMinutes, что у модели и Статистики
// (одна на весь продукт, урок W5-R-F3b). Нужна только для aria-label.
function spanMinutes(from: string, to: string): number {
  const a = new Date(from).getTime();
  const b = new Date(to).getTime();
  if (Number.isNaN(a) || Number.isNaN(b)) return 0;
  return Math.max(0, Math.round((b - a) / 60000));
}

export function FeatureStageLane({
  lane, axis, label, tabIndexAt = 0, expandedAt = null, onSelect, onHover,
  interactive = true, selected = false, hoverAt = null, describedById = null, highlight = null,
}: FeatureStageLaneProps) {
  const n = lane.intervals.length;
  // Процент оси для момента: ровно axisFrac модели × 100 — тесты сверяют координаты
  // дорожки с этой же функцией.
  const pct = (iso: string): number => axisFrac(iso, axis) * 100;

  // Зона «стоит» отдельным элементом — только когда на дорожке нет штрихованного интервала:
  // это случай «начатая задача без живого хода» (макет). Когда живой интервал уже штрихован
  // моделью (state 'stalled'), зона не рисуется поверх — тот же время не дважды.
  const stalledZone = lane.stalled && !lane.intervals.some(iv => iv.state === 'stalled') ? lane.stalled : null;

  // Зона «впереди» на оси есть только у живой фичи: у закрытой timeFrac = 1, зоны нет.
  const hasAheadZone = axis.timeFrac < 1 && !lane.skipped && !lane.approx;
  // «ещё N» — только prod (продуктовое решение m1 GC-R-F2): у других стадий это число
  // значит другое, а счётчик плана — только у «Производства». Только для этапа,
  // где работа уже шла (иначе это pending — ниже «впереди»).
  const showMore = hasAheadZone && lane.key === 'prod' && n > 0 && (lane.plan?.ahead ?? 0) > 0;
  // «впереди» — этап, чья работа ещё не начиналась: ни интервалов, ни skipped, ни approx.
  const showAhead = hasAheadZone && n === 0;
  // «Этапа не было» (GC-F10c): model-ный skipped или пустая дорожка на закрытой оси
  // (timeFrac = 1): у закрытой фичи будущего нет, и пустая дорожка здесь — не начатый
  // этап, а этап, которого не было. Взаимоисключено с showAhead (тот требует timeFrac < 1);
  // approx не участвует: там отсутствие интервалов — не факт об этапе.
  const noStage = !lane.skipped && n === 0 && !lane.approx && axis.timeFrac >= 1;
  const showNoStage = lane.skipped || noStage;

  // Видимая геометрия интервалов — один раз, по ней же считаются границы зон касания:
  // зона дотянута до середины зазора до соседа (или до края дорожки), поэтому зоны
  // соседних интервалов не перекрываются (дефект a67f8057).
  const bars = lane.intervals.map((iv) => {
    const left = pct(iv.from);
    return { left, width: Math.max(0, pct(iv.to) - left) };
  });

  return (
    <div
      data-testid={`lane-${lane.key}`}
      data-stage={lane.key}
      style={{
        position: 'relative',
        height: 14,
        borderRadius: R.sm,
        // Тики четвертей оси (макет .gtrack) — как в прежнем Ганте.
        background: `repeating-linear-gradient(90deg, transparent 0 calc(25% - 1px), ${C.borderLight} calc(25% - 1px) 25%)`,
        // Кольцо выделения дорожки (клик по заголовку этапа — «лента → Гант»).
        boxShadow: selected ? SHADOW.selected : undefined,
      }}
    >
      {showNoStage && (
        <>
          <div
            data-testid={`lane-no-stage-line-${lane.key}`}
            style={{ position: 'absolute', left: 0, right: 0, top: 7, borderTop: `1px dotted ${C.dashed}`, pointerEvents: 'none' }}
          />
          <span
            data-testid={`lane-no-stage-${lane.key}`}
            style={{
              position: 'absolute', left: 0, top: 0, bottom: 0, display: 'flex', alignItems: 'center',
              paddingLeft: SP.xs, fontFamily: FONT.mono, fontSize: FS.xxs, color: C.textMuted, pointerEvents: 'none',
            }}
          >
            этапа не было
          </span>
        </>
      )}

      {stalledZone && (
        <div
          data-testid={`lane-stalled-${lane.key}`}
          style={{
            position: 'absolute', left: `${pct(stalledZone.from)}%`,
            width: `${Math.max(0, pct(stalledZone.to) - pct(stalledZone.from))}%`,
            top: 2, height: 10, borderRadius: R.sm, background: STALLED_HATCH, pointerEvents: 'none',
          }}
        />
      )}

      {lane.intervals.map((iv, i) => {
        const b = bars[i]!;
        const ariaLabel = `${label}, интервал ${i + 1} из ${n}: ${formatDateTime(iv.from)} – ${formatClock(iv.to)}, ${formatDuration(spanMinutes(iv.from, iv.to))}`;
        // Подсветка шага (GC-F8R): кольцо — форма .hl макета (2 px подложки + 4px accent);
        // затемнение прочих — .dim (opacity .35), оно есть при любом режиме движения;
        // переход по opacity — только когда вызывающий не просил меньше движения
        // (prefers-reduced-motion: подсветка есть и снимается, но ничего не анимируется).
        const hl = highlight?.at.has(i) === true;
        const dim = highlight !== null && !hl;
        const barStyle = {
          position: 'absolute' as const,
          left: `${b.left}%`,
          width: `${b.width}%`,
          // Минимальная видимая ширина (макет: 3 px) — этап в минуты не превращается
          // в щель; left при этом остаётся точным.
          minWidth: 3,
          top: BAR_TOP,
          height: BAR_H,
          borderRadius: R.sm,
          background: stateBackground(iv.state),
          ...(dim ? { opacity: 0.35 } : {}),
          ...(dim && highlight?.animate ? { transition: 'opacity 0.3s' } : {}),
          ...(hl ? { boxShadow: `0 0 0 2px ${C.bgCard}, 0 0 0 4px ${C.accent}`, zIndex: 3 } : {}),
        };
        const risk = iv.lastOutcome ? (
          // Риска 2 px на конце интервала с плохим исходом последнего хода.
          <span
            data-testid={`lane-risk-${lane.key}-${i}`}
            aria-hidden="true"
            style={{ position: 'absolute', right: 0, top: 0, bottom: 0, width: 2, background: C.danger, pointerEvents: 'none' }}
          />
        ) : null;
        // Мобильный режим: интервал — НЕ кнопка (10 px не тач-цель, критерий c4).
        // Рисуем те же полоски, но без интерактивности; вход в интервал — шторка этапа.
        if (!interactive) {
          return (
            <div
              key={`iv-${i}`}
              role="img"
              data-testid={`lane-interval-${lane.key}-${i}`}
              data-state={iv.state}
              aria-label={ariaLabel}
              style={barStyle}
            >
              {risk}
            </div>
          );
        }
        // Десктоп: зона касания (дефект a67f8057) — невидимая кнопка вокруг полосы:
        // границы — середина зазора до соседа (для первого/последнего — до края дорожки),
        // поэтому зоны соседних интервалов не перекрываются; высота ZONE_H — центр на полосе.
        // Интерактивность (aria, tabIndex, обработчики) — на зоне; видимая полоса — отдельный
        // слой с pointer-events: none, указатель сквозь неё проходит к зоне.
        const extL = Math.max(0, (b.left - (i === 0 ? 0 : bars[i - 1]!.left + bars[i - 1]!.width)) / 2);
        const extR = Math.max(0, ((i === n - 1 ? 100 : bars[i + 1]!.left) - b.left - b.width) / 2);
        return (
          <Fragment key={`iv-${i}`}>
            <button
              type="button"
              data-testid={`lane-interval-${lane.key}-${i}`}
              data-state={iv.state}
              aria-label={ariaLabel}
              aria-haspopup="dialog"
              aria-expanded={expandedAt === i}
              aria-describedby={hoverAt === i ? describedById ?? undefined : undefined}
              tabIndex={i === tabIndexAt ? 0 : -1}
              onClick={(e) => onSelect?.(i, e?.currentTarget ?? null)}
              onMouseEnter={(e) => onHover?.(i, e?.currentTarget ?? null)}
              onMouseLeave={() => onHover?.(null, null)}
              style={{
                position: 'absolute',
                left: `${b.left - extL}%`,
                width: `${b.width + extL + extR}%`,
                // Не уже минимальной видимой полосы (те же 3 px).
                minWidth: 3,
                top: ZONE_TOP,
                height: ZONE_H,
                background: 'transparent',
                border: 'none',
                padding: 0,
                cursor: 'pointer',
              }}
            />
            <div data-testid={`lane-band-${lane.key}-${i}`} aria-hidden="true" style={{ ...barStyle, pointerEvents: 'none' }}>
              {risk}
            </div>
          </Fragment>
        );
      })}

      {lane.intervals.slice(0, -1).map((iv, i) => {
        if (iv.pauseAfterMinutes <= 0) return null;
        const next = lane.intervals[i + 1]!;
        const left = pct(iv.to);
        const width = Math.max(0, pct(next.from) - left);
        if (width <= 0) return null;
        return (
          <div
            key={`pause-${i}`}
            data-testid={`lane-pause-${lane.key}-${i}`}
            style={{ position: 'absolute', left: `${left}%`, width: `${width}%`, top: 7, height: 0, borderTop: `1px dashed ${C.textMuted}`, pointerEvents: 'none' }}
          />
        );
      })}

      {lane.returns.map((r, i) => (
        // «↩» возврата над дорожкой в момент вердикта (возвраты в модели — только у prod).
        // aria-hidden: глиф — повтор, сами возвраты и их причины — в деталях шага.
        <span
          key={`ret-${i}`}
          data-testid={`lane-return-${lane.key}-${i}`}
          aria-hidden="true"
          style={{
            position: 'absolute', left: `${pct(r.at)}%`, top: -12, transform: 'translateX(-50%)',
            fontFamily: FONT.mono, fontSize: FS.xxs, color: C.dangerText, pointerEvents: 'none',
          }}
        >
          ↩
        </span>
      ))}

      {(showMore || showAhead) && (
        <span
          data-testid={`lane-ahead-${lane.key}`}
          style={{
            position: 'absolute', left: `${axis.timeFrac * 100}%`, right: 0, top: 0, bottom: 0,
            display: 'flex', alignItems: 'center', justifyContent: 'flex-end', paddingRight: SP.sm,
            border: `1px dashed ${C.dashed}`, borderRadius: R.sm,
            fontFamily: FONT.mono, fontSize: FS.xxs, color: C.textMuted, pointerEvents: 'none',
          }}
        >
          {showMore ? `ещё ${lane.plan?.ahead ?? 0}` : 'впереди'}
        </span>
      )}
    </div>
  );
}
