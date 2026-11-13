// Экран выбранного этапа (D1c/D1e) под графом процесса: выбор задачи этапа, затем её экран по
// GET /api/tasks/{id}/stage.
// Вариант A (решение Андрея 13.11, макет docs/mockups/feature-workflows/stage-screen.html):
// над блоками — сводка задачи (название, состояние, ключевые цифры — время, ходы, «ждёт вас»),
// под ней — полоса вкладок «Артефакты · Вход · Ревью · Метрики», и один блок на экране.
// Сводка видна при любой вкладке — иначе теряется контекст, когда открыт только «Ревью».
// metrics = null (у задачи нет фичи с эпиком) — вкладка «Метрики» не показывается.
// При выборе другой задачи открывается первая вкладка: выбор вкладки помечен taskId, как и
// состояние загрузки. Полоса на 360 — горизонтальный скролл внутри полосы (кит ui/Tabs,
// видимая полоса прокрутки выключена, как у полосы страницы фичи).
//
// Устаревший ответ: состояние помечено taskId, запрос несёт AbortSignal, а ответ отменённого
// запроса не применяется — быстрое переключение A → B не покажет данные A, а данные A не
// затрут уже пришедшее B. Пока состояние чужой задачи, рисуется загрузка. Отказ запроса показывается в области экрана текстом
// с «Повторить», необработанных rejection нет.

import { useEffect, useRef, useState } from 'react';
import { AlertTriangle } from 'lucide-react';
import { SP, FS, C } from '../../lib/design';
import { Button } from '../ui/Button';
import { Notice } from '../ui/Notice';
import { Tabs } from '../ui/Tabs';
import { api } from '../../lib/api';
import { FeatureStageArtifacts } from './FeatureStageArtifacts';
import { FeatureStageInput } from './FeatureStageInput';
import { FeatureStageReview } from './FeatureStageReview';
import { FeatureStageMetrics } from './FeatureStageMetrics';
import { formatDuration, minutesSince } from './FeatureStatsBits';
import { useNow } from '../../lib/useNow';
import type { StageScreen } from '../../types/featureStage';
import type { StageMetrics } from '../../types/featureStats';

export interface StageScreenTask { id: string; title: string; stepRef?: string | null }

type LoadState = { taskId: string } & (
  | { status: 'loading' }
  | { status: 'ok'; screen: StageScreen }
  | { status: 'error'; message: string });

// Вкладки экрана этапа (вариант A): первая — «Артефакты», она же открывается при выборе
// другой задачи. Подписи — по постановке D1e, блок под каждой — существующий FeatureStage*.
type StageTab = 'artifacts' | 'input' | 'review' | 'metrics';
const FIRST_TAB: StageTab = 'artifacts';
const TAB_LABEL: Record<StageTab, string> = { artifacts: 'Артефакты', input: 'Вход', review: 'Ревью', metrics: 'Метрики' };

const TASK_STATUS: Record<string, string> = { todo: 'не начата', inProgress: 'в работе', done: 'готово' };
const NO_DATA = 'нет данных';

// Горизонтальный скролл полосы без видимой полосы прокрутки — тот же приём, что у
// FeaturePageTabs (инжект отдельный: чужой стиль переживать не будем).
const NO_SB_CLASS = 'cc-stage-screen-tabs';
if (typeof document !== 'undefined' && !document.getElementById('cc-stage-screen-tabs-style')) {
  const el = document.createElement('style');
  el.id = 'cc-stage-screen-tabs-style';
  el.textContent =
    `.${NO_SB_CLASS}::-webkit-scrollbar{display:none;}` +
    `.${NO_SB_CLASS}{scrollbar-width:none;}`;
  document.head.appendChild(el);
}

// Сводка задачи: название, состояние и ключевые цифры — время / ходы / «ждёт вас».
// Числа из `metrics` как есть, фронт ничего не досчитывает; «ждёт вас» — живая
// длительность от waitingNow.since: `now` приходит из StageBody (useNow) — то же значение,
// что у блока «Метрики» (R-D1e: две цифры об одном не должны расходиться).
// metrics = null — цифр нет: сводка остаётся названием и состоянием.
function StageSummary({ s, now }: { s: StageScreen; now: number }) {
  return (
    <div data-testid="stage-screen-summary" style={{ display: 'flex', flexDirection: 'column', gap: SP.xs, minWidth: 0 }}>
      <div style={{ display: 'flex', alignItems: 'baseline', gap: SP.sm, flexWrap: 'wrap' }}>
        <span data-testid="stage-screen-title" style={{ fontSize: FS.md, fontWeight: 600, color: C.textHeading, overflowWrap: 'anywhere' }}>
          {s.stepRef ? `${s.stepRef} · ` : ''}{s.title}
        </span>
        <span data-testid="stage-screen-status" style={{ fontSize: FS.xs, color: C.textMuted }}>
          {TASK_STATUS[s.status] ?? 'не начата'}
        </span>
      </div>
      {s.metrics && <SummaryKpis metrics={s.metrics} now={now} />}
    </div>
  );
}

function SummaryKpis({ metrics: m, now }: { metrics: StageMetrics; now: number }) {
  const waiting = m.human.waitingNow;
  const waitMin = waiting ? minutesSince(waiting.since, now) : null;
  const waitText = waiting
    ? (waitMin != null ? `ждёт ${formatDuration(waitMin)}` : 'ждёт')
    : m.human.minutes != null ? formatDuration(m.human.minutes) : NO_DATA;
  const kpis = [
    { label: 'Время', value: formatDuration(m.window.elapsedMinutes) },
    { label: 'Ходы', value: m.executor.turns == null ? NO_DATA : String(m.executor.turns) },
    { label: 'Ждёт вас', value: waitText },
  ];
  return (
    <div data-testid="stage-screen-kpis" style={{ display: 'flex', gap: SP.md, flexWrap: 'wrap' }}>
      {kpis.map(k => (
        <span key={k.label} style={{ fontSize: FS.xs, color: C.textMuted, overflowWrap: 'anywhere' }}>
          {k.label}: <span style={{ fontSize: FS.sm, fontWeight: 600, color: C.textHeading }}>{k.value}</span>
        </span>
      ))}
    </div>
  );
}

function StageBody({ taskId, isMobile }: { taskId: string; isMobile: boolean }) {
  const [loaded, setState] = useState<LoadState>({ taskId, status: 'loading' });
  const [attempt, setAttempt] = useState(0);
  // Выбор вкладки помечен taskId: при выборе другой задачи открыта первая вкладка,
  // а не та, что была у прошлой задачи (тот же приём, что у состояния загрузки).
  const [tabSel, setTabSel] = useState<{ taskId: string; tab: StageTab }>({ taskId, tab: FIRST_TAB });
  const bodyRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const controller = new AbortController();
    const { signal } = controller;
    setState({ taskId, status: 'loading' });
    api.tasks.stage(taskId, signal)
      .then(screen => {
        if (signal.aborted) return;
        setState({ taskId, status: 'ok', screen });
      })
      .catch((e: unknown) => {
        if (signal.aborted) return;
        setState({ taskId, status: 'error', message: (e instanceof Error && e.message) || 'Не удалось загрузить экран этапа' });
      });
    return () => controller.abort();
  }, [taskId, attempt]);

  // На 360: активная вкладка докручивается в видимость (полоса скроллится внутри себя).
  useEffect(() => {
    if (!isMobile) return;
    const el = bodyRef.current?.querySelector<HTMLElement>('[role="tab"][aria-selected="true"]');
    el?.scrollIntoView({ inline: 'nearest', block: 'nearest' });
  }, [tabSel, isMobile]);

  const state: LoadState = loaded.taskId === taskId ? loaded : { taskId, status: 'loading' };
  const tab: StageTab = tabSel.taskId === taskId ? tabSel.tab : FIRST_TAB;
  // Живое «сейчас» для всего экрана — одно значение у сводки и блока «Метрики» (R-D1e):
  // через useNow, и интервал живёт только когда есть от чего считать (waitingNow).
  const waitingActive = state.status === 'ok' && state.screen.metrics?.human.waitingNow != null;
  const now = useNow(60_000, waitingActive);
  if (state.status === 'loading') {
    return <div data-testid="stage-screen-loading" style={{ fontSize: FS.sm, color: C.textMuted }}>Загружаю экран этапа…</div>;
  }
  if (state.status === 'error') {
    return (
      <Notice tone="danger" icon={AlertTriangle} data-testid="stage-screen-error"
        action={<Button size="xs" variant="ghost" style={isMobile ? { minHeight: 40 } : undefined} onClick={() => setAttempt(n => n + 1)}>Повторить</Button>}>
        {state.message}
      </Notice>
    );
  }
  const s = state.screen;
  const items = [
    { value: 'artifacts' as StageTab, label: TAB_LABEL.artifacts },
    { value: 'input' as StageTab, label: TAB_LABEL.input },
    { value: 'review' as StageTab, label: TAB_LABEL.review },
    // metrics = null — нечего показывать: вкладка «Метрики» не предлагается вовсе.
    ...(s.metrics ? [{ value: 'metrics' as StageTab, label: TAB_LABEL.metrics }] : []),
  ];
  return (
    <div ref={bodyRef} data-testid="stage-screen-body" data-task-id={s.taskId} style={{ display: 'flex', flexDirection: 'column', gap: SP.md, minWidth: 0 }}>
      <StageSummary s={s} now={now} />
      <Tabs
        transparent
        size="md"
        isMobile={isMobile}
        className={NO_SB_CLASS}
        ariaLabel="Разделы экрана этапа"
        value={tab}
        items={items}
        onChange={(t) => setTabSel({ taskId, tab: t })}
      />
      {tab === 'artifacts' && <FeatureStageArtifacts key={s.taskId} taskId={s.taskId} artifacts={s.artifacts} isMobile={isMobile} />}
      {tab === 'input' && <FeatureStageInput input={s.input} isMobile={isMobile} />}
      {tab === 'review' && <FeatureStageReview review={s.review} />}
      {tab === 'metrics' && s.metrics && <FeatureStageMetrics metrics={s.metrics} nowMs={now} />}
    </div>
  );
}

export function FeatureStageScreen({ tasks, isMobile }: { tasks: StageScreenTask[]; isMobile: boolean }) {
  // Выбор задачи этапа: явный — только если такая задача ещё в списке, иначе первая
  const [picked, setPicked] = useState<string | null>(null);
  if (tasks.length === 0) return null;
  const taskId = tasks.some(t => t.id === picked) ? picked! : tasks[0].id;
  return (
    <div data-testid="stage-screen" style={{ display: 'flex', flexDirection: 'column', gap: SP.md, minWidth: 0 }}>
      {tasks.length > 1 && (
        <div data-testid="stage-screen-picker" role="group" aria-label="Задача этапа" style={{ display: 'flex', flexWrap: 'wrap', gap: SP.xs }}>
          {tasks.map(t => (
            <Button key={t.id} size="xs" variant={t.id === taskId ? 'primary' : 'ghost'}
              style={isMobile ? { minHeight: 40 } : undefined}
              dataTestId={`stage-screen-pick-${t.id}`}
              onClick={() => setPicked(t.id)}>
              {t.stepRef || t.title}
            </Button>
          ))}
        </div>
      )}
      <StageBody taskId={taskId} isMobile={isMobile} />
    </div>
  );
}
