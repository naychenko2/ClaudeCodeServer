import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { KeyboardEvent as ReactKeyboardEvent } from 'react';
import { Info } from 'lucide-react';
import { useFeatureStageFocus, clearFeatureStageFocus, focusFeatureStage } from '../../lib/featurePage';
import { EMPTY_NAV_MARKS, buildNavMarks, type NavMarks } from '../../lib/featureNavMarks';
import { C, R, FS, SP, FONT } from '../../lib/design';
import { stepProgressFromRun, formatStepProgress } from '../../lib/featureProgress';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';
import { Tooltip } from '../ui/Tooltip';
import { Button } from '../ui/Button';
import { getPersonaById } from '../../lib/personas';
import { recordFeatureReturn, registerFeatureViewProvider, usePendingFeatureView, clearPendingFeatureView } from '../../lib/featureBackNav';
import { openChatById } from '../../lib/openChat';
import { openTaskInSection } from '../../lib/tasks';
import { usePrefersReducedMotion } from '../../hooks/usePrefersReducedMotion';
import {
  stageName, stageStatusLabel, stageBoundsLabel, stageDurationLabel,
  stageDotStyle, stageLineStyle, buildTaskMap, stageStatusBadge, ExecutorRow, buildExecutorIndex,
  tasksCountLabel,
} from './FeatureTimelineBits';
import type { StepProgress } from '../../lib/featureProgress';
import { FeatureStageLane } from './FeatureStageLane';
import { FeatureLaneAxis } from './FeatureLaneAxis';
import {
  FeatureLanePopover, FeatureLanePreview, FeatureLaneSheet, LANE_TIP_ID,
  type LaneIntervalDetails, type LaneTaskLine,
} from './FeatureLanePopover';
import { buildStageLanes, axisFrac, planCounterText, type StageLane, type LaneAxis } from './FeatureLaneModel';
import { foldLast } from './featureListFold';
import { formatDuration, formatDateTime } from './FeatureStatsBits';
import type { FeatureStats, StageTurn } from '../../types/featureStats';
import { FeatureProdCardsView } from './FeatureProdCardsView';
import type { FeatureRun, FeatureStage, FeatureStageExecutor, StageKey } from '../../types/featureRun';

// Вкладка «Хронология» (волна 3, Ф2a, макет features-overview-3-final §5): поясняющая
// строка + вертикальный список 7 этапов в порядке жизненного цикла (cut → plan → prod →
// review → qa → final → merge). У каждого: точка/линия, имя, состояние, границы, «≈»,
// плашка «ждёт вас · N мин» / «стоит · N мин». Один раскрытый этап за раз; текущий
// (active) открыт по умолчанию. «Производство» (W4-2F3) — лента карточек
// FeatureProdCardsView вместо «исполнители/шаги»; остальные этапы — исполнители.
// Сводка/график — не здесь (Ф7/Ф8).
//
// GC-F5c: в строку этапа встала ДОРОЖКА времени (вариант C макета feature-gantt-7):
// сетка `250px | 1fr | 104px` — имя · состояние · счётчик | дорожка | «10 ч 59 мин · 33×».
// Над лентой — липкая ось времени (FeatureLaneAxis) с той же шкалой. Арифметики своей
// нет: интервалы, ось и план приходят из модели (FeatureLaneModel), красит —
// FeatureStageLane/FeatureLaneAxis. Клавиатура: roving tabindex — одна точка табуляции
// на весь Гант, ←→ по интервалам этапа, ↑↓ по соседним этапам (ближайший по x), Home/End.

// Сетка строки этапа (макет feature-gantt-6 §«Рендер»): колонка имени, дорожка, колонка
// длительности. Константы — потому что ось обязана встать ровно над дорожкой (её отступы
// считаются от тех же чисел).
const COL_NAME = 250;
const COL_META = 104;
// Отступ строки этапа и зазор между её колонками — часть геометрии дорожки: ось
// сдвигается на те же значения, иначе шкала встаёт левее дорожки (GC-F5cR).
const ROW_PAD = SP.xl;
const GRID_GAP = SP.md;
// Ширина шкалы, ниже которой часовые отметки налезают на подпись «начало …»:
// первая отметка стоит на 16,6 % шкалы, подпись «начало 20.10 · 03:00» — ~102 px;
// перекрытие начинается с шкалы ~690 px. Порог — про ширину САМОЙ шкалы (label
// «начало» и отметка считаются от её левого края), поэтому отступы на него не влияют.
const MIN_SCALE_W = 690;

// Отступы шкалы под колонку дорожек: ось обязана лежать ровно над дорожкой.
// Дорожка — вторая колонка сетки строки этапа, а сама строка сдвинута на ROW_PAD:
// отсюда колонка имени (COL_NAME) + зазор сетки (GRID_GAP). Справа — колонка
// длительности (COL_META) + зазор. На 360 сетка одноколонная (дорожка под именем),
// но отступ строки остаётся — шкала встаёт по нему же.
export function laneAxisInsets(isMobile: boolean): { left: number; right: number } {
  return isMobile
    ? { left: ROW_PAD, right: 0 }
    : { left: ROW_PAD + COL_NAME + GRID_GAP, right: COL_META + GRID_GAP };
}

// Ширина шкалы — та же, что у дорожки: контейнер минус отступы оси.
export function laneScaleWidth(containerW: number, isMobile: boolean): number {
  const { left, right } = laneAxisInsets(isMobile);
  return containerW - left - right;
}

// Активный интервал Ганта: этап + индекс. Одна точка табуляции на весь Гант.
interface LaneFocus { stage: StageKey; index: number }

// Клавиши навигации по интервалам (макет feature-gantt-6 §«Клавиатура»).
const NAV_KEYS = ['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown', 'Home', 'End'];

// Склонение слова «интервал» под число: 1 интервал / 2 интервала / 5 интервалов.
function pluralIntervals(n: number): string {
  const m100 = n % 100;
  const m10 = n % 10;
  if (m100 >= 11 && m100 <= 14) return `${n} интервалов`;
  if (m10 === 1) return `${n} интервал`;
  if (m10 >= 2 && m10 <= 4) return `${n} интервала`;
  return `${n} интервалов`;
}

// Длительность интервала в минуты — та же арифметика, что у модели дорожек.
function spanMinutes(from: string, to: string): number {
  const a = new Date(from).getTime();
  const b = new Date(to).getTime();
  if (Number.isNaN(a) || Number.isNaN(b)) return 0;
  return Math.max(0, Math.round((b - a) / 60000));
}

// Выбранный интервал дорожки: этап, индекс и сам элемент кнопки (позиция поповера
// и возврат фокуса при закрытии).
interface LanePick { stage: StageKey; index: number; el: HTMLElement | null }

// Поясняющая строка (макет §5): Info 13, 11 textSecondary, bgCard, borderLight, R.md, поля 6 10.
function TimelineNote() {
  return (
    <div
      data-testid="timeline-note"
      style={{
        display: 'flex', gap: SP.sm, alignItems: 'flex-start',
        background: C.bgCard, border: `1px solid ${C.borderLight}`, borderRadius: R.md,
        padding: `${SP.xs}px ${SP.sm}px`, marginBottom: SP.md, fontFamily: FONT.sans,
        fontSize: FS.xs, color: C.textSecondary, lineHeight: 1.45,
      }}
    >
      <Info size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} aria-hidden style={{ flexShrink: 0, marginTop: SP.xxs }} />
      <span>
        Порядок этапов, их состояние и кто работал — точные. Длительность со знаком «≈» — по
        задачам этапа. Ревью идёт по шагам, поэтому пересекается с производством.
      </span>
    </div>
  );
}

// Строка этапа: точка + линия + заголовок (имя · состояние · «≈») + границы + (раскрыто)
// карточка с плашкой состояния; у «Производство» — лента карточек (W4-2F3), у других
// этапов — исполнители. Клик по заголовку — раскрыть/схлопнуть. Список исполнителей
// длинных этапов (кроме prod) свёрнут: последние 10 + «Показать все N (ранних скрыто: K)»
// через foldLast (GC-F10c), снятые задачи в N/K/список не входят.
function StageRow({ stage, isLast, expanded, onToggle, taskMap, now, featureId, featureTitle = null,
  run, showProd = false, marks = EMPTY_NAV_MARKS, isMobile = false,
  execIndex = new Map<string, FeatureStageExecutor>(), progress = null,
  lane = null, axis = null, tabIndexAt = -1,
  expandedAt = null, hoverAt = null, laneSelected = false, flash = false,
  tailIds = null, turnsByTask = new Map<string, StageTurn[]>(),
  prodInitialKey = null, prodExpandedKeyRef = null,
  onIntervalSelect, onIntervalHover, onOpenSheet }: {
  stage: FeatureStage; isLast: boolean; expanded: boolean;
  onToggle: () => void; taskMap: Map<string, import('../../types/featureRun').FeatureTask>;
  now: number; featureId: string; featureTitle?: string | null;
  run: FeatureRun; showProd?: boolean;
  marks?: NavMarks;
  isMobile?: boolean;
  execIndex?: Map<string, FeatureStageExecutor>;
  progress?: StepProgress | null;
  // Дорожка этапа из модели (GC-F5c). null — дорожки нет (approx: журнала нет).
  lane?: StageLane | null;
  // Ось фичи — та же шкала, что у дорожек.
  axis?: LaneAxis | null;
  // Индекс интервала с tabIndex=0 в ЭТОМ этапе; -1 — точка табуляции не здесь.
  tabIndexAt?: number;
  // Интервал с закреплённым поповером (aria-expanded на дорожке).
  expandedAt?: number | null;
  // Интервал под курсором — на него вешаем aria-describedby превью.
  hoverAt?: number | null;
  // Кольцо выделения дорожки (клик по заголовку этапа).
  laneSelected?: boolean;
  // Вспышка строки после «Показать в ленте».
  flash?: boolean;
  // Id хвостов после мержа (run.postMergeTails): строки этих задач в списке этапа
  // помечаются маркером (GC-F10a). null — хвостов нет.
  tailIds?: Set<string> | null;
  // Ходы задач по id (GC-F10b): результат buildTurnsByTask — единственный источник.
  // Строка исполнителя получает только ходы своей задачи; здесь их никто не пересчитывает.
  turnsByTask?: Map<string, StageTurn[]>;
  // Начальная раскрытая карточка ленты «Производство» (GC-F11): ключ из снимка возврата
  // по чипу. null — восстановления нет.
  prodInitialKey?: string | null;
  // Канал чтения текущего expandedKey ленты для снимка (состояние живёт в ленте, ref —
  // только чтение; на раскрытие карточки он не влияет).
  prodExpandedKeyRef?: { current: string | null } | null;
  onIntervalSelect?: (index: number, el: HTMLElement | null) => void;
  onIntervalHover?: (index: number | null, el: HTMLElement | null) => void;
  onOpenSheet?: () => void;
}) {
  // Свёртка списка этапа (GC-F10c): работает только в ветке этапов КРОМЕ prod (лента prod —
  // карточки, её свёртка не здесь). Видны последние 10 строк, ранние скрыты; выбор
  // «показать всё» живёт, пока вкладка смонтирована. Снятые задачи исключены ДО свёртки:
  // признак — вердикт задачи 'withdrawn', тот же, которым бэк исключает задачу из цепочки
  // (FeatureStepAccounting.cs:145,190), — второй истины нет. Снятые не входят ни в N, ни в K;
  // N и K берутся из результата свёртки (visible + hiddenEarly = длина входа): то, что
  // откроется по кнопке, равно тому, что посчитано в её подписи.
  const [showAll, setShowAll] = useState(false);
  const rows = stage.executors.filter(x => taskMap.get(x.taskId)?.verification?.verdict !== 'withdrawn');
  // expandedKey: у строки этапа нет состояния, поднятого к списку (раскрытая часть
  // ExecutorRow — её собственная) — скрывать «раскрытое» нечего.
  const fold = foldLast(rows, null, showAll);
  const hiddenSet = new Set(fold.hiddenEarly);

  const status = stageStatusLabel(stage);
  const bounds = stageBoundsLabel(stage);
  const duration = stageDurationLabel(stage.approxMinutes);
  const badge = stageStatusBadge(stage, taskMap, now);
  const nameMuted = stage.state === 'pending' || stage.state === 'absent';
  const line = stageLineStyle(stage, isLast);

  const statusColor =
    status?.tone === 'info' ? C.info :
    status?.tone === 'danger' ? C.dangerText :
    C.textMuted;

  // Дорожка есть только когда есть и ось, и данные журнала (approx — данных нет).
  const hasLane = !!lane && !!axis && !lane.approx;
  const intervals = hasLane ? lane!.intervals : [];
  const pauseMinutes = intervals.reduce((a, iv) => a + iv.pauseAfterMinutes, 0);

  // Счётчик колонки имени: у «Производства» — план шагов «N/M» (решение m1: счётчик
  // плана только у prod); у прочих стадий плана нет — показываем факт (число задач).
  // Текст плана — из planCounterText() модели, тот же, что печатает сводка «Статистики»:
  // N = принято, M = всего; своей арифметики здесь нет (GC-F12R). Текст факта —
  // tasksCountLabel(), согласованный с числом (GC-F10a).
  let counter: string | null = null;
  if (hasLane && stage.key === 'prod') {
    counter = planCounterText(lane);
  } else if (stage.executors.length > 0) {
    counter = tasksCountLabel(stage.executors.length);
  }

  // Хвост после мержа (GC-F10a): строка этой задачи в списке этапа помечается маркером —
  // она остаётся в списке (бэк держит её в stage.taskIds), отдельной строкой не дублируется.
  const isTail = (taskId: string): boolean => tailIds?.has(taskId) === true;

  // Правая колонка: у этапа с дорожкой — факт времени и число интервалов («10 ч 59 мин · 33×»);
  // без дорожки — прежняя приблизительная длительность («≈ 6 ч»).
  const meta = hasLane && intervals.length > 0
    ? { duration: formatDuration(lane!.totalMinutes), count: `${lane!.count}×` }
    : { duration: duration, count: null };

  // Под строкой: границы по интервалам дорожки + паузы; без дорожки — прежние границы.
  const boundsText = hasLane && intervals.length > 0
    ? `${formatDateTime(intervals[0]!.from)} – ${formatDateTime(intervals[intervals.length - 1]!.to)}`
    : bounds;
  const detail = hasLane && intervals.length > 0
    ? ` · ${pluralIntervals(lane!.count)}${pauseMinutes > 0 ? `, паузы ${formatDuration(pauseMinutes)}` : ''}`
    : '';

  const gridColumns = isMobile ? '1fr' : `${COL_NAME}px 1fr ${COL_META}px`;

  return (
    <div
      data-testid={`timeline-stage-${stage.key}`}
      style={{
        position: 'relative', paddingLeft: ROW_PAD, paddingBottom: SP.lg,
        // Вспышка строки после «Показать в ленте» (C.accentLight, 1,4 с).
        ...(flash ? { background: C.accentLight, transition: 'background 0.3s' } : {}),
      }}
    >
      {/* Точка этапа (14) */}
      <span style={{ ...stageDotStyle(stage), position: 'absolute', left: SP.xxs, top: SP.xs }} />
      {/* Линия к следующему этапу */}
      {line && <span style={line} />}
      {/* Строка этапа: сетка 250px | 1fr | 104px (на 360 — одна колонка: кнопка, дорожка под ней) */}
      <div
        data-testid={`timeline-stage-grid-${stage.key}`}
        style={{ display: 'grid', gridTemplateColumns: gridColumns, gap: GRID_GAP, alignItems: 'center' }}
      >
        {/* Колонка 1: имя (кнопка раскрытия) · состояние · счётчик */}
        <div style={{ gridColumn: '1', minWidth: 0 }}>
          <button
            type="button"
            onClick={onToggle}
            aria-expanded={expanded}
            data-testid={`timeline-stage-header-${stage.key}`}
            style={{
              display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap',
              background: 'transparent', border: 'none', cursor: 'pointer',
              padding: `${SP.xs}px 0`, minHeight: isMobile ? 44 : 40,
              fontFamily: FONT.sans, width: '100%', textAlign: 'left',
            }}
          >
            <span style={{ fontSize: FS.md, fontWeight: nameMuted ? 600 : 700, color: nameMuted ? C.textMuted : C.textHeading }}>
              {stageName(stage.key)}
            </span>
            {status && (
              <span style={{ fontSize: FS.xs, fontWeight: 600, color: statusColor, whiteSpace: 'nowrap' }}>
                {status.text}
              </span>
            )}
          </button>
          {counter && (
            <div
              data-testid="timeline-stage-counter"
              style={{ fontFamily: FONT.mono, fontSize: FS.xxs, color: C.textMuted, paddingBottom: SP.xxs }}
            >
              {counter}
            </div>
          )}
        </div>
        {/* Колонка 2: дорожка. Она НЕ внутри кнопки строки — вложенных кнопок быть не может. */}
        <div style={{ gridColumn: isMobile ? '1' : '2', minWidth: 0 }}>
          {hasLane && (
            <FeatureStageLane
              lane={lane!}
              axis={axis!}
              label={stageName(stage.key)}
              tabIndexAt={tabIndexAt}
              expandedAt={expandedAt}
              hoverAt={hoverAt}
              describedById={LANE_TIP_ID}
              selected={laneSelected}
              // На 360 интервал — не тач-цель: вход только через шторку этапа.
              interactive={!isMobile}
              onSelect={onIntervalSelect}
              onHover={onIntervalHover}
            />
          )}
          {/* 360: вход в интервалы этапа — кнопка 44 px под дорожкой (шторка). */}
          {isMobile && hasLane && intervals.length > 0 && onOpenSheet && (
            <Button
              variant="secondary"
              size="md"
              fullWidth
              onClick={onOpenSheet}
              dataTestId={`timeline-stage-intervals-${stage.key}`}
              style={{ marginTop: SP.sm, minHeight: 44, justifyContent: 'flex-start' }}
            >
              {`Все интервалы этапа · ${lane!.count}`}
            </Button>
          )}
        </div>
        {/* Колонка 3: длительность и число интервалов */}
        <div
          data-testid="timeline-stage-meta"
          style={{
            gridColumn: isMobile ? '1' : '3',
            display: 'flex', flexDirection: 'column', alignItems: 'flex-end', gap: SP.xxs,
            fontFamily: FONT.mono, fontSize: FS.xs, color: C.textSecondary, whiteSpace: 'nowrap',
          }}
        >
          {meta.duration && (
            <Tooltip
              content="Приблизительно: сумма работы задач этапа — от старта исполнителя до сдачи. Ожидание и простой внутри не вычитаются, параллельные задачи складываются."
            >
              <span
                data-testid="timeline-stage-duration"
                style={{
                  fontFamily: FONT.mono, fontSize: FS.xs, color: C.textSecondary, whiteSpace: 'nowrap',
                  cursor: 'help', minHeight: isMobile ? 40 : undefined, display: 'inline-flex', alignItems: 'center',
                }}
              >
                {meta.duration}
              </span>
            </Tooltip>
          )}
          {meta.count && <span data-testid="timeline-stage-count">{meta.count}</span>}
        </div>
      </div>
      {/* Границы и паузы: «24.10 13:47 – 25.10 05:44 · 33 интервала, паузы 4 ч 58 мин» */}
      {boundsText && (
        <div
          data-testid="timeline-stage-bounds"
          style={{
            fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted, marginTop: SP.xxs,
            // Длинная строка не должна вылезать за вкладку (360): рвём по любому символу.
            overflowWrap: 'anywhere',
          }}
        >
          {boundsText}{detail}
        </div>
      )}
      {/* Раскрыто: карточка с плашкой состояния и исполнителями */}
      {expanded && (
        <div
          data-testid={`timeline-stage-body-${stage.key}`}
          style={{
            background: C.bgCard,
            border: `1px solid ${badge?.tone === 'danger' ? C.dangerBorder : badge?.tone === 'info' ? C.info : C.borderLight}`,
            borderRadius: R.lg, padding: `${SP.sm}px ${SP.sm}px ${SP.sm}px`, marginTop: SP.xs,
          }}
        >
          {badge && (
            <div
              data-testid="timeline-stage-badge"
              style={{
                display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap',
                background: badge.tone === 'info' ? C.infoBg : C.dangerBg,
                borderRadius: R.md, padding: `${SP.xs}px ${SP.sm}px`, marginBottom: SP.xs,
                fontFamily: FONT.sans, fontSize: FS.sm,
              }}
            >
              <span style={{ fontWeight: 700, color: badge.tone === 'danger' ? C.dangerText : C.textHeading }}>
                {badge.text}
              </span>
            </div>
          )}
          {showProd ? (
            /* W4-2F3: «Производство» — одна лента карточек (шаг — карточка, порядок
               по старту даёт модель). Сироты «Производства» (фикс R4e): если карточек
               нет, но у стадии есть исполнители — прежние ExecutorRow, задача не
               исчезает из вкладки. Пустое состояние остаётся только когда показывать
               действительно некого. */
            FeatureProdCardsView({ run, featureId, featureTitle, marks, isMobile, initialExpandedKey: prodInitialKey, expandedKeyRef: prodExpandedKeyRef }) ?? (stage.executors.length > 0 ? (
              stage.executors.map((x, i) => (
                <ExecutorRow
                  key={`${x.taskId}-${i}`}
                  x={x}
                  task={x.taskId ? taskMap.get(x.taskId) : undefined}
                  now={now}
                  featureId={featureId}
                  featureTitle={featureTitle}
                  marks={marks}
                  isMobile={isMobile}
                  taskMap={taskMap}
                  execIndex={execIndex}
                  progress={progress}
                  isTail={isTail(x.taskId)}
                  turns={turnsByTask.get(x.taskId) ?? null}
                  axis={axis}
                />
              ))
            ) : (
              <div data-testid="feature-steps-empty" style={{ fontFamily: FONT.sans }}>
                <div style={{ fontSize: FS.md, fontWeight: 700, color: C.textHeading, padding: `${SP.xs}px 0` }}>
                  Шагов пока нет
                </div>
                <div style={{ fontSize: FS.sm, color: C.textMuted }}>
                  Появятся, когда Егор составит план.
                </div>
              </div>
            ))
          ) : rows.length > 0 ? (
            <>
              {/* Порядок строк — исходный (тот же порядок stage.executors, что даёт бэк);
                  скрытые ранние не рендерятся, ключи видимых строк не меняются. */}
              {rows.map((x, i) => (hiddenSet.has(x) ? null : (
                <ExecutorRow
                  key={`${x.taskId}-${i}`}
                  x={x}
                  task={x.taskId ? taskMap.get(x.taskId) : undefined}
                  now={now}
                  featureId={featureId}
                  featureTitle={featureTitle}
                  marks={marks}
                  isMobile={isMobile}
                  taskMap={taskMap}
                  execIndex={execIndex}
                  progress={progress}
                  isTail={isTail(x.taskId)}
                  turns={turnsByTask.get(x.taskId) ?? null}
                  axis={axis}
                />
              )))}
              {/* Кнопка свёртки (GC-F10c): живёт, пока есть скрытые ранние; при showAll
                  hiddenEarly пуст — кнопки нет (это нормально, список уже весь). */}
              {fold.hiddenEarly.length > 0 && (
                <Button
                  variant="ghost"
                  size="md"
                  fullWidth
                  dataTestId={`timeline-stage-showall-${stage.key}`}
                  onClick={() => setShowAll(true)}
                  style={{
                    marginTop: SP.sm,
                    border: `1px solid ${C.border}`, background: C.bgSelected,
                    borderRadius: R.md, padding: `${SP.xs}px ${SP.sm}px`,
                    fontSize: FS.sm, color: C.textHeading,
                  }}
                >
                  {`Показать все ${fold.visible.length + fold.hiddenEarly.length} (ранних скрыто: ${fold.hiddenEarly.length})`}
                </Button>
              )}
            </>
          ) : (
            <div data-testid="timeline-stage-no-executors" style={{ fontSize: FS.sm, color: C.textMuted }}>
              Исполнителей нет
            </div>
          )}
        </div>
      )}
    </div>
  );
}

// === GC-F10b: ходы задач для строки исполнителя (единственный источник) ===

// Ходы задач из интервалов бэка: taskId → его ходы в хронологическом порядке. Один источник
// на всю вкладку: StageRow/ExecutorRow/FeatureStepDetails ничего не пересчитывают.
// Отбор — по taskId ХОДА (не по taskIds интервала: живой хвост состоит в taskIds, но ходом
// не является). Закрытые ходы — все интервалы всех стадий, где turns есть (бэк атрибутирует
// ход стадии по роли задачи, FeatureTimeMath.RoleToStage, — поэтому ход задачи лежит в её
// интервалах ровно один раз; хвост после мержа — тоже: его ходы входят в интервалы своей
// стадии, отдельного счёта по run.stages[].taskIds нет).
// Живой ход синтезируется здесь (контракт — комментарий в types/featureStats.ts): для
// live-интервала неоконченной фичи и задачи из run.now, состоящей в его taskIds, — ход
// [max(старт задачи, конец её последнего закрытого хода) – «сейчас»], та же формула, что у
// живого хвоста бэка (FeatureStageIntervals.cs:79-92). У finished-фичи живых ходов нет.
export function buildTurnsByTask(run: FeatureRun, stats: FeatureStats | null, now: number): Map<string, StageTurn[]> {
  const out = new Map<string, StageTurn[]>();
  const add = (taskId: string, t: StageTurn): void => {
    const list = out.get(taskId);
    if (list) list.push(t); else out.set(taskId, [t]);
  };
  // approx (журнала нет / он не полный) — ходов нет: не выдумываем и не падаем.
  const byStage = stats?.timeline?.intervalsByStage;
  if (!byStage) return out;
  const finished = run.closed === true || run.state === 'merged' || run.state === 'closed';
  const nowIso = new Date(now).toISOString();
  // Идущие задачи: id → старт (run.now.startedAt = ClaudeStartedAt задачи).
  const runningStart = new Map<string, string | null>();
  for (const n of run.now ?? []) {
    if (n.taskId) runningStart.set(n.taskId, n.startedAt ?? null);
  }
  for (const intervals of Object.values(byStage)) {
    for (const iv of intervals ?? []) {
      // Конец последнего закрытого хода задачи внутри ЭТОГО интервала — старт его живого
      // хвоста: бэк клеит хвост к последнему закрытому ходу, значит он в том же интервале.
      const lastEnd = new Map<string, number>();
      for (const t of iv.turns ?? []) {
        if (!t?.taskId) continue;
        add(t.taskId, t);
        const toMs = new Date(t.to).getTime();
        if (Number.isFinite(toMs)) lastEnd.set(t.taskId, Math.max(lastEnd.get(t.taskId) ?? 0, toMs));
      }
      if (!iv.live || finished) continue;
      for (const id of iv.taskIds ?? []) {
        // Живой хвост принадлежит только идущей задаче (run.now); остальные задачи
        // интервала — закрытые ходы, живого у них нет.
        if (!runningStart.has(id)) continue;
        const started = new Date(runningStart.get(id) ?? '').getTime();
        const from = Math.max(Number.isFinite(started) ? started : 0, lastEnd.get(id) ?? 0);
        // Нет старта или нулевая длина — живого хода нет (то же, что у бэка).
        if (from <= 0 || from >= now) continue;
        add(id, { taskId: id, from: new Date(from).toISOString(), to: nowIso, outcome: null, model: null });
      }
    }
  }
  return out;
}

// Вкладка «Хронология»: поясняющая строка + список 7 этапов. `now` — фиксированный
// момент (Date.now на рендер) для минут «идёт/ждёт/стоит»; стабильен в пределах кадра.
// W4-2F3: «Производство» — лента карточек (FeatureProdCardsView), другие этапы — исполнители.
export function FeatureTimelineTab({ run, marks: marksProp, isMobile = false, stats = null }: {
  run: FeatureRun; marks?: NavMarks; isMobile?: boolean; stats?: FeatureStats | null;
}) {
  const stages = run.stages ?? [];
  // Честный прогресс (Ф-pr): из run.stepProgress; null — старые ответы (строки нет).
  const progress = stepProgressFromRun(run);
  const taskMap = useMemo(() => buildTaskMap(run), [run]);
  // W4-3F: индекс исполнителей по taskId — статус чипов «оценивает →» берётся только из
  // stages[].executors[] (бэк W4-3B уже отфильтровал связь по задачам этого run).
  const execIndex = useMemo(() => buildExecutorIndex(stages), [stages]);
  // Пометки «в архиве»/«задача закрыта»: страница даёт полные (с чатами), без пропа —
  // из самого агрегата (исполнители стадий + задачи).
  const marks = useMemo(() => marksProp ?? buildNavMarks(run), [marksProp, run]);
  const now = useMemo(() => Date.now(), []);
  // Название фичи (epic.title) — для «← к фиче {название}» в шапке чата (Ф-pl-b).
  const featureTitle = run.epic?.title ?? null;
  // «Хвосты после мержа» (GC-F10a): id незакрытых дочерних задач после мержа — с бэка
  // (run.postMergeTails; FeatureProjector.cs:86-93). Тревогу бэк от них не строит
  // (FeatureProjector.cs:139-141) — и фронт не выдумывает «ждёт вас»: тон этапа только
  // с бэка. Те же id остаются в списках этапов (StageRow метит их маркером), сводка —
  // только число под лентой. Множество — чтобы метка и счёт считали один хвост один раз.
  const tailIds = useMemo(() => new Set(run.postMergeTails ?? []), [run]);
  // Ходы задач (GC-F10b): один источник на всю вкладку — buildTurnsByTask; строки
  // исполнителей получают только ходы своей задачи, «Время шага на оси» — ту же ось,
  // что у дорожек.
  const turnsByTask = useMemo(() => buildTurnsByTask(run, stats, now), [run, stats, now]);

  // Дорожки и ось (GC-F5c): только из модели (FeatureLaneModel) — своей арифметики нет.
  const lanesModel = useMemo(() => buildStageLanes(run, stats, now), [run, stats, now]);
  const axis = lanesModel.axis;
  // Ось для строки исполнителя (GC-F10b): полоса «Время шага на оси» живёт только на настоящей
  // оси — оси журнала. При approx (журнала нет) ходов нет и сама ось над лентой не рисуется
  // (hasLanes ложен): полоса на несуществующей оси была бы ложью.
  const rowAxis = axis && stats?.timeline?.intervalsByStage != null ? axis : null;
  // Дорожка есть у этапа, у которого есть и ось, и данные журнала (approx — данных нет).
  const laneOf = useCallback(
    (key: StageKey): StageLane | null => {
      const l = lanesModel.lanes[key];
      if (!l || !axis || l.approx) return null;
      return l;
    },
    [lanesModel, axis],
  );
  // Этапы с дорожками — в порядке ленты: по ним ходят ↑↓ (соседний этап).
  const laneStages = useMemo(
    () => stages.map(s => s.key).filter(k => !!laneOf(k)),
    [stages, laneOf],
  );

  // «Сейчас» на оси — только у открытой фичи (у закрытой/смерженной его нет, последняя
  // подпись — дата закрытия). Контракт держит вызывающий (замечание ревью GC-R-F5a/б).
  const finished = run.closed === true || run.state === 'merged' || run.state === 'closed';
  const nowIso = finished ? null : new Date(now).toISOString();

  // Текущий этап: первый active (в порядке жизненного цикла); его открыто по умолчанию.
  const currentKey = useMemo<StageKey | null>(() => {
    const active = stages.find(s => s.state === 'active');
    return active ? active.key : null;
  }, [stages]);

  // Один раскрытый этап за раз; null — все схлопнуты.
  const [expanded, setExpanded] = useState<StageKey | null>(currentKey);
  const toggle = (key: StageKey) => setExpanded(cur => (cur === key ? null : key));

  // === Снимок вида и восстановление по чипу (GC-F11) ===
  // Снимок берётся recordFeatureReturn в момент перехода (регистрация ниже). Состояние
  // вида живёт в компоненте, как жило; refs — только канал чтения текущего значения
  // для поставщика: на поведение вкладки они не влияют.
  const expandedRef = useRef<StageKey | null>(null);
  expandedRef.current = expanded;
  const prodExpandedKeyRef = useRef<string | null>(null);
  useEffect(() => {
    return registerFeatureViewProvider(run.featureId, () => ({
      stageKey: expandedRef.current,
      expandedKey: prodExpandedKeyRef.current,
      // Прокрутка окна: в тестовом фейк-DOM window.scrollY нет — 0 (не выдумываем).
      scrollY: typeof window !== 'undefined' && typeof window.scrollY === 'number' ? window.scrollY : 0,
    }));
  }, [run.featureId]);

  // Восстановление по чипу: однократный снимок, забранный кликом чипа из указателя.
  // Приём — как якорь этапа (Ф-pl-b): seq — применённый не применяется заново; после
  // применения канал гасится (повторный монтаж вкладки старый снимок не перечитает).
  // Карточка: ключ уходит в ленту как initialExpandedKey (лента сама раскроет её и
  // поставит фокус на шапку). Прокрутка — записанная, «умного» позиционирования нет:
  // человек был вот здесь.
  const pendingView = usePendingFeatureView(run.featureId);
  const [prodInitialKey, setProdInitialKey] = useState<string | null>(null);
  const pendingViewSeq = useRef(0);
  useEffect(() => {
    if (!pendingView || pendingView.seq === pendingViewSeq.current) return;
    pendingViewSeq.current = pendingView.seq;
    const v = pendingView.view;
    if (v.stageKey) setExpanded(v.stageKey as StageKey);
    // Ставим безусловно: снимок без карточки должен снять и старую начальную карточку
    // (иначе свежая лента после нового возврата раскрыла бы карточку прошлого возврата).
    setProdInitialKey(v.expandedKey ?? null);
    clearPendingFeatureView();
    if (typeof window !== 'undefined' && typeof window.scrollTo === 'function') window.scrollTo(0, v.scrollY);
  }, [pendingView]);

  // Roving tabindex (GC-F5c): одна точка табуляции на весь Гант — первый интервал
  // первого этапа с дорожкой.
  const [laneFocus, setLaneFocus] = useState<LaneFocus | null>(null);
  const defaultFocus = useMemo<LaneFocus | null>(() => {
    const first = laneStages.find(k => (lanesModel.lanes[k]?.intervals.length ?? 0) > 0);
    return first ? { stage: first, index: 0 } : null;
  }, [laneStages, lanesModel]);
  const focus = useMemo<LaneFocus | null>(() => {
    if (!laneFocus) return defaultFocus;
    const lane = lanesModel.lanes[laneFocus.stage];
    if (!lane || laneFocus.index < 0 || laneFocus.index >= lane.intervals.length) return defaultFocus;
    return laneFocus;
  }, [laneFocus, defaultFocus, lanesModel]);

  // Ширина контейнера — для узкой шкалы (часовые отметки прячутся). Без ResizeObserver
  // (тестовый фейк-DOM) ширины нет — шкала считается широкой.
  const rootRef = useRef<HTMLDivElement | null>(null);
  const [containerW, setContainerW] = useState<number | null>(null);
  useEffect(() => {
    const el = rootRef.current;
    if (!el || typeof ResizeObserver === 'undefined') return;
    const ro = new ResizeObserver(entries => {
      for (const e of entries) setContainerW(e.contentRect.width);
    });
    ro.observe(el);
    return () => ro.disconnect();
  }, []);
  const scaleW = containerW === null ? null : laneScaleWidth(containerW, isMobile);
  const dense = !isMobile && scaleW !== null && scaleW < MIN_SCALE_W;
  // Отступы шкалы — те же, что у колонки дорожек (GC-F5cR): ось лежит ровно над ней.
  const axisInsets = useMemo(() => laneAxisInsets(isMobile), [isMobile]);

  // Перевод фокуса на соседний интервал: ←→ — по этапу, ↑↓ — ближайший по x интервал
  // соседнего этапа, Home/End — первый/последний интервал этапа (макет feature-gantt-6).
  const moveFocus = useCallback((key: string): LaneFocus | null => {
    if (!focus || !axis) return null;
    const lane = lanesModel.lanes[focus.stage];
    if (!lane) return null;
    const n = lane.intervals.length;
    if (key === 'ArrowRight' && focus.index + 1 < n) return { stage: focus.stage, index: focus.index + 1 };
    if (key === 'ArrowLeft' && focus.index > 0) return { stage: focus.stage, index: focus.index - 1 };
    if (key === 'Home') return { stage: focus.stage, index: 0 };
    if (key === 'End') return { stage: focus.stage, index: n - 1 };
    if (key === 'ArrowUp' || key === 'ArrowDown') {
      const pos = laneStages.indexOf(focus.stage);
      const dir = key === 'ArrowUp' ? -1 : 1;
      // Центр текущего интервала на шкале — по нему ищем ближайший в соседнем этапе.
      const cur = lane.intervals[focus.index]!;
      const curX = (axisFrac(cur.from, axis) + axisFrac(cur.to, axis)) / 2;
      for (let p = pos + dir; p >= 0 && p < laneStages.length; p += dir) {
        const target = lanesModel.lanes[laneStages[p]!];
        if (!target || target.intervals.length === 0) continue;
        let best = 0;
        let bestD = Infinity;
        target.intervals.forEach((iv, i) => {
          const x = (axisFrac(iv.from, axis) + axisFrac(iv.to, axis)) / 2;
          const d = Math.abs(x - curX);
          if (d < bestD) { bestD = d; best = i; }
        });
        return { stage: laneStages[p]!, index: best };
      }
    }
    return null;
  }, [focus, axis, lanesModel, laneStages]);

  // Фокус после смены точки табуляции — на сам интервал (кнопку дорожки).
  const focusPending = useRef(false);
  useEffect(() => {
    if (!focusPending.current || !focus) return;
    focusPending.current = false;
    // Тестовый фейк-DOM не умеет querySelector — тогда фокус просто не двигаем.
    if (typeof document.querySelector !== 'function') return;
    const el = document.querySelector(`[data-testid="lane-interval-${focus.stage}-${focus.index}"]`);
    if (el && typeof (el as HTMLElement).focus === 'function') (el as HTMLElement).focus();
  }, [focus]);

  const onKeyDown = useCallback((e: ReactKeyboardEvent<HTMLDivElement>) => {
    if (!NAV_KEYS.includes(e.key)) return;
    const next = moveFocus(e.key);
    if (!next) return;
    e.preventDefault();
    setLaneFocus(next);
    focusPending.current = true;
  }, [moveFocus]);

  // Ось рисуется, когда есть хоть одна дорожка: у approx-фичи (журнала нет) дорожек нет.
  const hasLanes = laneStages.length > 0;

  // === GC-F6: поповер интервала (1280) и шторка (360) ===
  const reducedMotion = usePrefersReducedMotion();
  // Закреплённый интервал (поповер) и интервал под курсором (превью).
  const [pinned, setPinned] = useState<LanePick | null>(null);
  const [hover, setHover] = useState<LanePick | null>(null);
  // Мобильная шторка: этап и выбранный в ней интервал.
  const [sheetStage, setSheetStage] = useState<StageKey | null>(null);
  const [sheetIndex, setSheetIndex] = useState(0);
  // Кольцо дорожки (клик по заголовку этапа) и вспышка строки («Показать в ленте»).
  const [selectedStage, setSelectedStage] = useState<StageKey | null>(null);
  const [flashStage, setFlashStage] = useState<StageKey | null>(null);
  const flashTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  useEffect(() => () => {
    if (flashTimer.current !== null) clearTimeout(flashTimer.current);
  }, []);

  // Детали интервала: состояние, состав по taskIds, кто работал и модели, пауза.
  // Собираются здесь (в родителе): только у него есть задачи, исполнители и персоны.
  const detailsOf = useCallback((stage: StageKey, index: number): LaneIntervalDetails | null => {
    const lane = lanesModel.lanes[stage];
    if (!lane) return null;
    const iv = lane.intervals[index];
    if (!iv) return null;
    // Кто ждёт — только из timeline.current (waiting). Но ждёт задача в том интервале,
    // где она ЖИВЬЁМ ждёт: по правилу модели это интервал со state === 'wait' (живой
    // хвост с ждущей задачей). Исторический интервал с тем же taskId — 'done', и
    // ждущей он не был: помечать его «ждёт» — ложь (GC-F6R).
    const cur = stats?.timeline?.current ?? null;
    const waitingTaskId = cur && cur.kind === 'waiting' ? cur.taskId ?? null : null;
    const waitingSince = cur && cur.kind === 'waiting' ? cur.since ?? null : null;
    const isWaitInterval = iv.state === 'wait';
    const tasks: LaneTaskLine[] = (iv.taskIds ?? []).map(id => {
      const ex = execIndex.get(id);
      const task = taskMap.get(id);
      const persona = ex?.personaId ? getPersonaById(ex.personaId)?.name ?? null : null;
      const waiting = isWaitInterval && !!waitingTaskId && id === waitingTaskId;
      return {
        taskId: id,
        title: task?.title ?? null,
        persona,
        model: ex?.modelLabel ?? ex?.modelFull ?? null,
        chatId: ex?.chatId ?? null,
        waiting,
        // С какого момента задача ждёт (timeline.current.since) — только для ждущей.
        waitingSince: waiting ? waitingSince : null,
      };
    });
    return {
      stageKey: stage,
      stageName: stageName(stage),
      index,
      total: lane.intervals.length,
      from: iv.from,
      to: iv.to,
      minutes: spanMinutes(iv.from, iv.to),
      state: iv.state,
      lastOutcome: iv.lastOutcome,
      tasks,
      pauseAfterMinutes: iv.pauseAfterMinutes,
    };
  }, [lanesModel, stats, execIndex, taskMap]);

  // Клик / Enter / Space по интервалу: закрепляем поповер. Повторный клик по тому же
  // интервалу НЕ закрывает (переключения нет — «закрепить» значит закрепить).
  const onIntervalSelect = useCallback((stage: StageKey, index: number, el: HTMLElement | null) => {
    setPinned({ stage, index, el });
    setHover(null);
    setLaneFocus({ stage, index });
  }, []);

  // Наведение: превью. Пока поповер закреплён — превью не нужно.
  const onIntervalHover = useCallback((stage: StageKey, index: number | null, el: HTMLElement | null) => {
    if (index === null) { setHover(null); return; }
    if (pinned) return;
    setHover({ stage, index, el });
  }, [pinned]);

  // Закрытие поповера: фокус возвращается на интервал, превью заново не всплывает.
  const closePopover = useCallback(() => {
    const el = pinned?.el ?? null;
    setPinned(null);
    setHover(null);
    if (el && typeof el.focus === 'function') el.focus();
  }, [pinned]);

  // «Показать в ленте»: раскрыть этап и прокрутить к нему + вспышка строки.
  // При prefers-reduced-motion — без вспышки и без плавной прокрутки.
  const showInFeed = useCallback((stage: StageKey) => {
    focusFeatureStage(stage);
    if (reducedMotion) return;
    setFlashStage(stage);
    if (flashTimer.current !== null) clearTimeout(flashTimer.current);
    flashTimer.current = setTimeout(() => setFlashStage(null), 1400);
  }, [reducedMotion]);

  // Переходы из поповера/шторки: задача и чат — с записью возврата (чип «← к фиче»).
  const openTask = useCallback((taskId: string) => {
    const task = taskMap.get(taskId);
    recordFeatureReturn(run.featureId, { kind: 'task', id: taskId }, featureTitle);
    openTaskInSection({ id: taskId, projectId: task?.projectId ?? undefined });
  }, [taskMap, run.featureId, featureTitle]);

  const openChat = useCallback((chatId: string) => {
    recordFeatureReturn(run.featureId, { kind: 'chat', id: chatId }, featureTitle);
    void openChatById(chatId);
  }, [run.featureId, featureTitle]);

  // Мобильная шторка: этап и выбранный в ней интервал (по умолчанию — последний).
  const openSheet = useCallback((stage: StageKey) => {
    const lane = lanesModel.lanes[stage];
    setSheetStage(stage);
    setSheetIndex(Math.max(0, (lane?.intervals.length ?? 1) - 1));
  }, [lanesModel]);

  const sheetItems = useMemo<LaneIntervalDetails[]>(() => {
    if (!sheetStage) return [];
    const lane = lanesModel.lanes[sheetStage];
    if (!lane) return [];
    const out: LaneIntervalDetails[] = [];
    for (let i = 0; i < lane.intervals.length; i += 1) {
      const d = detailsOf(sheetStage, i);
      if (d) out.push(d);
    }
    return out;
  }, [sheetStage, lanesModel, detailsOf]);

  const pinnedDetails = useMemo(
    () => (pinned ? detailsOf(pinned.stage, pinned.index) : null),
    [pinned, detailsOf],
  );
  const hoverDetails = useMemo(
    () => (hover ? detailsOf(hover.stage, hover.index) : null),
    [hover, detailsOf],
  );

  // Прямоугольник якоря (интервал) — из элемента: поповер меряется по нему.
  const anchorRect = (pick: LanePick | null): { left: number; right: number; bottom: number } => {
    const el = pick?.el ?? null;
    if (!el || typeof el.getBoundingClientRect !== 'function') return { left: 0, right: 0, bottom: 0 };
    const r = el.getBoundingClientRect();
    return { left: r.left, right: r.right, bottom: r.bottom };
  };
  // Мемоизировано: новая ссылка на прямоугольник гоняла бы позиционирование поповера
  // по кругу (эффект позиционирования зависит от якоря).
  const pinnedAnchor = useMemo(() => anchorRect(pinned), [pinned]);
  const hoverAnchor = useMemo(() => anchorRect(hover), [hover]);

  // Якорь этапа (Ф-pl-b): клик по точке на плашке чата открыл страницу на «Хронологии» —
  // раскрываем/прокручиваем на этот этап. One-shot по seq (тот же приём, что focusFeatureStep):
  // применённый seq запоминаем — ререндер/повторное чтение не сбрасывают ручной выбор.
  const stageFocus = useFeatureStageFocus();
  const stageFocusSeq = useRef(0);
  useEffect(() => {
    if (!stageFocus || stageFocus.seq === stageFocusSeq.current) return;
    stageFocusSeq.current = stageFocus.seq;
    setExpanded(stageFocus.key as StageKey);
    const el = typeof document.querySelector === 'function'
      ? document.querySelector(`[data-testid="timeline-stage-${stageFocus.key}"]`)
      : null;
    if (el && 'scrollIntoView' in el) {
      // Без плавной прокрутки, если человек просил меньше движения.
      (el as HTMLElement).scrollIntoView({ block: 'center', behavior: reducedMotion ? 'auto' : 'smooth' });
    }
    // M4: якорь прочитан — гасим. Повторный монтаж вкладки (смена вкладки/фичи) старый
    // этап не раскроет, даже если последнее открытие было без stage.
    clearFeatureStageFocus();
  }, [stageFocus, reducedMotion]);

  return (
    <div
      ref={rootRef}
      data-testid="feature-timeline-tab"
      onKeyDown={onKeyDown}
      style={{ fontFamily: FONT.sans }}
    >
      <TimelineNote />
      {progress && (
        <div
          data-testid="timeline-progress"
          style={{ fontSize: FS.sm, color: C.textSecondary, marginBottom: SP.sm }}
        >
          {formatStepProgress(progress)}
        </div>
      )}
      {/* Ось времени над лентой: подписи встают ровно над дорожкой (отступы под колонки сетки) */}
      {hasLanes && axis && (
        <FeatureLaneAxis
          axis={axis}
          now={nowIso}
          isMobile={isMobile}
          dense={dense}
          // Отступы под колонку дорожек: ось лежит ровно над дорожкой (GC-F5cR).
          // На 360 сетка одноколонная — дорожка под именем, но отступ строки остаётся.
          insetLeft={axisInsets.left}
          insetRight={axisInsets.right}
        />
      )}
      {stages.map((stage, i) => (
        <StageRow
          key={stage.key}
          stage={stage}
          isLast={i === stages.length - 1}
          expanded={expanded === stage.key}
          onToggle={() => { toggle(stage.key); setSelectedStage(stage.key); }}
          taskMap={taskMap}
          now={now}
          featureId={run.featureId}
          featureTitle={featureTitle}
          run={run}
          showProd={stage.key === 'prod'}
          marks={marks}
          isMobile={isMobile}
          execIndex={execIndex}
          progress={progress}
          lane={laneOf(stage.key)}
          axis={rowAxis}
          tabIndexAt={focus?.stage === stage.key ? focus.index : -1}
          expandedAt={pinned?.stage === stage.key ? pinned.index : null}
          hoverAt={hover?.stage === stage.key ? hover.index : null}
          laneSelected={selectedStage === stage.key}
          flash={flashStage === stage.key}
          tailIds={tailIds}
          turnsByTask={turnsByTask}
          prodInitialKey={prodInitialKey}
          prodExpandedKeyRef={prodExpandedKeyRef}
          onIntervalSelect={(idx, el) => onIntervalSelect(stage.key, idx, el)}
          onIntervalHover={(idx, el) => onIntervalHover(stage.key, idx, el)}
          onOpenSheet={isMobile ? () => openSheet(stage.key) : undefined}
        />
      ))}
      {/* «Хвосты после мержа · N» (GC-F10a): после ленты этапов («Мерж» — последний
          этап жизненного цикла, строка стоит за ним). Только сводка: id хвостов не
          перечисляются — они уже в списках этапов с маркером «· хвост после мержа».
          Строки нет, когда хвостов нет (и для старых данных без поля). */}
      {tailIds.size > 0 && (
        <div
          data-testid="timeline-tails"
          style={{
            fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted,
            marginTop: SP.sm, overflowWrap: 'anywhere',
          }}
        >
          {`хвосты после мержа · ${tailIds.size}`}
        </div>
      )}
      {/* Превью по наведению (1280) — слой над лентой, в потоке его нет. */}
      {!pinned && hoverDetails && (
        <FeatureLanePreview details={hoverDetails} anchor={hoverAnchor} />
      )}
      {/* Закреплённый поповер (1280). */}
      {pinnedDetails && (
        <FeatureLanePopover
          details={pinnedDetails}
          anchor={pinnedAnchor}
          anchorEl={pinned?.el ?? null}
          onClose={closePopover}
          onShowInFeed={() => showInFeed(pinnedDetails.stageKey)}
          onOpenTask={openTask}
          onOpenChat={openChat}
        />
      )}
      {/* Мобильная шторка (360) — детали выбранного интервала и список всех интервалов этапа. */}
      {sheetStage && sheetItems.length > 0 && (
        <FeatureLaneSheet
          items={sheetItems}
          selectedIndex={Math.min(sheetIndex, sheetItems.length - 1)}
          onSelect={setSheetIndex}
          onClose={() => setSheetStage(null)}
          onShowInFeed={() => showInFeed(sheetStage)}
          onOpenTask={openTask}
          onOpenChat={openChat}
        />
      )}
    </div>
  );
}
