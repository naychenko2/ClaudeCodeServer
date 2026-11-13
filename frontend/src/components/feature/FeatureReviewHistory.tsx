// Секция «Хронология» вкладки «Разбор» (FS-FR6; записка docs/mockups/feature-page-simplify-v4-clickable.md
// §2 «Гант этапов»/«Лента событий», §5 «6. Хронология»; в макете — секция rz-hist). С F18b
// (docs/mockups/feature-workflows/materials-review-placement.html, M33) здесь только ключевые
// события и подпись: по макету «Хронология» — лента событий без дорожек.
//
// Дорожки этапов переехали в секцию «Время и срок» (M33): они экспортируются отсюда как
// FeatureReviewStageLanes и подключаются в FeatureReviewTime. Модель и рендеры перенесены
// целиком (buildStageLanes, FeatureLaneAxis, FeatureStageLane) — второй копии lane-модели
// во фронте не заводим; компонент живёт в этом файле, здесь же и его тесты.
// Порядок «Хронологии»: события → подпись.
//
// События — только из пропа events, отфильтрованные по ключевым видам (вердикты, остановки,
// ожидания, мерж — записка §5 «6. Хронология»); строки — FeatureEventsTab, переходы через
// featureId/marks без изменений. Вкладке eventsTotal равен длине отфильтрованного списка:
// вкладка не должна предлагать догрузку полной ленты. Подпись внизу называет настоящий
// eventsTotal из пропа.

import { useEffect, useMemo, useRef, useState } from 'react';
import type { CSSProperties } from 'react';
import { C, FS, SP, FONT } from '../../lib/design';
import { useNow } from '../../lib/useNow';
import { stageName, stageDurationLabel } from './FeatureTimelineBits';
import { FeatureStageLane } from './FeatureStageLane';
import { FeatureLaneAxis } from './FeatureLaneAxis';
import { buildStageLanes, type StageLane } from './FeatureLaneModel';
import { laneAxisInsets, laneScaleWidth, MIN_SCALE_W } from './FeatureTimelineTab';
import { FeatureEventsTab } from './FeatureEventsTab';
import { EMPTY_NAV_MARKS, type NavMarks } from '../../lib/featureNavMarks';
import { formatDuration } from './FeatureStatsBits';
import type { FeatureRun, FeatureStage, StageKey } from '../../types/featureRun';
import type { FeatureStats } from '../../types/featureStats';
import type { FeatureEvent } from '../../types/featureIndex';

// Ключевые виды событий (записка §5 «6. Хронология»: «вердикты, остановки, ожидания, мерж»).
// Виды — те же, что формулирует FeatureEventsTab (FeatureEventsTab.tsx:59-90), — подмножество
// его словаря, нового здесь не заводим.
const KEY_EVENT_KINDS: ReadonlySet<string> = new Set([
  'accepted', 'accepted_with_remarks', 'returned', 'stopped', 'waiting', 'merged', 'closed',
]);

export interface FeatureReviewStageLanesProps {
  run: FeatureRun;
  // Статистика страницы. null или approx (нет intervalsByStage) — шкалы нет: вместо дорожек
  // строка о журнале времени.
  stats: FeatureStats | null;
  isMobile?: boolean;
}

// Дорожки этапов для секции «Время и срок» (F18b; макет M33 «Время и срок · дорожки этапов»):
// по одной строке на этап, в порядке run.stages (порядок жизненного цикла, своей сортировки
// нет), ось времени — над ними. Вызовы те же, что у вкладки «Хронология» (FeatureTimelineTab),
// но без строк исполнителей, без раскрытия этапа и без поповеров: здесь интервал — статичная
// картинка (interactive={false}), открывать в нём нечего.
// Геометрия шкалы — та же, что у строки этапа: отступы оси — экспортируемые laneAxisInsets,
// сетка строки строится из тех же отступов (колонка имени = левый отступ, колонка
// длительности = правый, дорожка между ними) — ось лежит ровно над дорожкой (GC-F5cR).
// Своей арифметики времени нет: длительность — lane.totalMinutes модели (когда есть
// интервалы) или stage.approxMinutes бэка (когда их нет).
export function FeatureReviewStageLanes({
  run, stats, isMobile = false,
}: FeatureReviewStageLanesProps) {
  // «Сейчас» — тот же паттерн, что у «Хронологии» (FeatureTimelineTab.tsx:597): useNow(60_000);
  // дорожки и ось только читают это значение.
  const now = useNow(60_000);
  const lanesModel = useMemo(() => buildStageLanes(run, stats, now), [run, stats, now]);
  const axis = lanesModel.axis;
  // Шкалы нет — журнала времени нет: нет оси или журнал approx (intervalsByStage null/отсутствует).
  // Тот же признак, что у модели (FeatureLaneModel.ts:255-261).
  const noScale = axis === null || stats?.timeline?.intervalsByStage == null;
  // Дорожка есть у этапа, у которого есть и ось, и данные журнала (тот же laneOf,
  // что FeatureTimelineTab.tsx:619-626).
  const laneOf = (key: StageKey): StageLane | null => {
    const l = lanesModel.lanes[key];
    if (!l || !axis || l.approx) return null;
    return l;
  };
  // Строки этапов в порядке run.stages (порядок жизненного цикла, своей сортировки нет).
  const laneStages = (run.stages ?? []).filter((s) => laneOf(s.key) !== null);
  const hasLanes = laneStages.length > 0;

  // «Сейчас» на оси — только у открытой фичи (тот же контракт, что FeatureTimelineTab.tsx:633-636).
  const finished = run.closed === true || run.state === 'merged' || run.state === 'closed';
  const nowIso = finished ? null : new Date(now).toISOString();

  // Геометрия шкалы — та же, что у строки этапа «Хронологии»: отступы оси — экспортируемые
  // laneAxisInsets; десктопная сетка строится из тех же отступов (колонка имени = левый отступ,
  // колонка длительности = правый, дорожка между ними) — ось лежит ровно над дорожкой.
  // На 360 строка одноколонная (имя, под ним дорожка, под ней длительность) и сдвинута на
  // левый отступ оси — как строка этапа FeatureTimelineTab на мобиле.
  const insets = laneAxisInsets(isMobile);

  // Ширина контейнера — для узкой шкалы (часовые отметки прячутся; тот же dense, что
  // FeatureTimelineTab.tsx:743-757). Без ResizeObserver (тестовый фейк-DOM, SSR) ширины нет —
  // шкала считается широкой.
  const rootRef = useRef<HTMLDivElement | null>(null);
  const [containerW, setContainerW] = useState<number | null>(null);
  useEffect(() => {
    const el = rootRef.current;
    if (!el || typeof ResizeObserver === 'undefined') return;
    const ro = new ResizeObserver((entries) => {
      for (const e of entries) setContainerW(e.contentRect.width);
    });
    ro.observe(el);
    return () => ro.disconnect();
  }, []);
  const scaleW = containerW === null ? null : laneScaleWidth(containerW, isMobile);
  const dense = !isMobile && scaleW !== null && scaleW < MIN_SCALE_W;

  // Подпись этапа — та же, что у заголовка строки этапа (FeatureTimelineTab.tsx:282),
  // без кнопки: здесь этапа не раскрывают.
  const nameStyle = (stage: FeatureStage): CSSProperties => {
    const muted = stage.state === 'pending' || stage.state === 'absent';
    return { fontSize: FS.md, fontWeight: muted ? 600 : 700, color: muted ? C.textMuted : C.textHeading };
  };
  const metaStyle: CSSProperties = { fontFamily: FONT.mono, fontSize: FS.xs, color: C.textSecondary, whiteSpace: 'nowrap' };

  return (
    <div ref={rootRef} data-testid="review-stage-lanes" style={{ fontFamily: FONT.sans }}>
      {noScale ? (
        <div data-testid="review-stage-lanes-no-scale" style={{ fontSize: FS.sm, color: C.textMuted, padding: `${SP.sm}px 0` }}>
          Шкала появится, когда у фичи будет журнал времени
        </div>
      ) : hasLanes && axis ? (
        <div data-testid="review-stage-lanes-gantt">
          {/* Ось времени над дорожками: отступы — та же геометрия, что у сетки строк (GC-F5cR). */}
          <FeatureLaneAxis
            axis={axis}
            now={nowIso}
            isMobile={isMobile}
            dense={dense}
            insetLeft={insets.left}
            insetRight={insets.right}
          />
          {laneStages.map((stage) => {
            const lane = laneOf(stage.key)!;
            // Длительность: факт времени модели (когда есть интервалы), иначе — приблизительная
            // оценка бэка (тот же fallback, что FeatureTimelineTab.tsx:236-238).
            const duration = lane.count > 0 ? formatDuration(lane.totalMinutes) : stageDurationLabel(stage.approxMinutes);
            return (
              <div
                key={stage.key}
                data-testid={`review-stage-lane-${stage.key}`}
                style={isMobile
                  ? { paddingLeft: insets.left, paddingBottom: SP.lg }
                  : { display: 'grid', gridTemplateColumns: `${insets.left}px 1fr ${insets.right}px`, alignItems: 'center', paddingBottom: SP.lg }}
              >
                {isMobile ? (
                  <>
                    <div style={{ minWidth: 0 }}>
                      <span style={nameStyle(stage)}>{stageName(stage.key)}</span>
                    </div>
                    <div style={{ marginTop: SP.xs, minWidth: 0 }}>
                      <FeatureStageLane lane={lane} axis={axis} label={stageName(stage.key)} interactive={false} />
                    </div>
                    {duration && <div style={{ ...metaStyle, marginTop: SP.xs }}>{duration}</div>}
                  </>
                ) : (
                  <>
                    <div style={{ minWidth: 0, paddingRight: SP.md }}>
                      <span style={nameStyle(stage)}>{stageName(stage.key)}</span>
                    </div>
                    <div style={{ minWidth: 0 }}>
                      <FeatureStageLane lane={lane} axis={axis} label={stageName(stage.key)} interactive={false} />
                    </div>
                    {duration && <div style={{ ...metaStyle, textAlign: 'right', paddingLeft: SP.md }}>{duration}</div>}
                  </>
                )}
              </div>
            );
          })}
        </div>
      ) : null}
    </div>
  );
}

export interface FeatureReviewHistoryProps {
  // run и stats с F18b здесь не читаются: дорожки — в FeatureReviewStageLanes (их подключает
  // FeatureReviewTime). Пропы оставлены как есть: их передаёт вкладка FeatureReviewTab,
  // а её правка — вне этого шага (F18a); снятие — там же.
  run: FeatureRun;
  // Статистика страницы. null или approx (нет intervalsByStage) — шкалы нет: вместо дорожек
  // строка о журнале времени.
  stats: FeatureStats | null;
  // Лента событий страницы (at desc) — единственный источник событий здесь.
  events: FeatureEvent[];
  // Общее число событий истории — для подписи внизу.
  eventsTotal: number;
  // Навигация строк событий (FeatureEventsTab): точка возврата, пометки «в архиве»/«закрыта».
  featureId: string;
  featureTitle?: string | null;
  projectId?: string;
  marks?: NavMarks;
  isMobile?: boolean;
}

export function FeatureReviewHistory({
  events, eventsTotal, featureId, featureTitle = null, projectId,
  marks = EMPTY_NAV_MARKS, isMobile = false,
}: FeatureReviewHistoryProps) {
  // Ключевые события — только из пропа events, порядок бэка (at desc) сохраняется.
  const keyEvents = useMemo(() => events.filter((e) => KEY_EVENT_KINDS.has(e.kind)), [events]);

  return (
    <div data-testid="review-history" style={{ fontFamily: FONT.sans }}>
      {/* Ключевые события: строки FeatureEventsTab (переходы — через featureId/marks, без изменений).
          eventsTotal вкладке — длина отфильтрованного списка: полной ленты здесь нет и догружать
          нечего; свой fold (8 строк) вкладка делает по этой же длине. */}
      <div data-testid="review-history-events" style={{ marginTop: SP.lg }}>
        <FeatureEventsTab
          events={keyEvents}
          eventsTotal={keyEvents.length}
          featureId={featureId}
          featureTitle={featureTitle}
          projectId={projectId}
          marks={marks}
          isMobile={isMobile}
        />
        <div data-testid="review-history-events-note" style={{ fontSize: FS.sm, color: C.textMuted, marginTop: SP.sm }}>
          {`Ключевые события: вердикты, остановки, ожидания, мерж — из ${eventsTotal} в истории`}
        </div>
      </div>
    </div>
  );
}
