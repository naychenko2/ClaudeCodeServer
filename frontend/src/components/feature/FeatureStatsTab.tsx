import { useState } from 'react';
import { C, R, FS, SP, FONT, SHADOW } from '../../lib/design';
import { openTaskInSection } from '../../lib/tasks';
import { EmptyState } from '../ui/EmptyState';
import { NavTargetButton } from './NavTargetButton';
import { recordFeatureReturn } from '../../lib/featureBackNav';
import { EMPTY_NAV_MARKS, type NavMarks, type FeatureNavCtx } from '../../lib/featureNavMarks';
import {
  buildTimelineModel, formatDuration,
  type TimelineModel,
} from './FeatureStatsBits';
import { personaGroupLabel, modelGroupLabel, roleGroupLabel, formatTokens } from './FeatureStatsFormat';
import { plural } from '../../lib/plural';
import { usePersonas } from '../../lib/personas';
import { useModels } from '../../lib/models';
import { AGENT_COLORS } from '../AgentSelector';
import { FeatureStatsTiles } from './FeatureStatsTiles';
import { FeatureStatsLosses } from './FeatureStatsLosses';
import { FeatureBreakdownBlock } from './FeatureBreakdownBlock';
import { FeatureAnalysisLine } from './FeatureAnalysisLine';
import { stageLabelShort } from './FeatureAnalysisModel';
import { COMP_LABEL } from './FeatureStatsPulseModel';
import type { FeatureStats, FeatureSpend, FeatureStatsSource, FeatureTimeline } from '../../types/featureStats';
import type { FeatureRun } from '../../types/featureRun';

// Вкладка «Статистика» (вариант A «Пульс» макета feature-stats-5, упрощение Ш6).
// Порядок: строка анализа → значки точности → плитки → «Главные потери» → «Кто и на чём
// работал» → свёрнутый «Подробнее» (то, что плитки не покрывают: «не работали», эпизоды
// ошибок, детали расхода). Героя «прошло с начала», «Сейчас работают» и таблицы этапов
// нет: первое и второе — в сводке страницы, третье — в Ганте «Хронологии» (Ш6).
// `buildTimelineModel` живёт (его читает ряд «задача»).
// Данные ТОЛЬКО из `stats` и `run` (features/{id}): фронт не пересчитывает, только раскладывает.
// Честность: нет stats (старые данные) → пустое состояние с причиной, а не нули.

export interface FeatureStatsTabProps {
  // Статистика фичи (features/{id} → stats); null — бэк ещё не посчитал (старые данные).
  stats: FeatureStats | null;
  // Конвейер фичи (features/{id} → run) — строки сводки этапов (GC-F12). Без него
  // (старые ответы) — сводки нет.
  run?: FeatureRun | null;
  // Фича и её название — для точки возврата («← к фиче · Статистика»); без featureId
  // переходы работают, но чипа возврата нет.
  featureId?: string | null;
  featureTitle?: string | null;
  // Пометки «в архиве»/«задача закрыта» (buildNavMarks страницы); у статистики своих
  // агрегатов нет — без пропа пометок нет.
  marks?: NavMarks;
  // Тач-цели ≥ 40 на 360.
  isMobile?: boolean;
}

// Строка точности (Майя, P1): одна фраза FS.xs textMuted без повтора:
// «≈ по задачам — числа приблизительные» / «по журналу ходов»; при trunc —
// отдельная «журнал обрезан».
export function AccuracyBadges({ source, truncated }: { source: 'approx' | 'journal'; truncated: boolean }) {
  const phrase = source === 'approx'
    ? '≈ по задачам — числа приблизительные'
    : 'по журналу ходов';
  return (
    <div
      data-testid="stats-accuracy"
      style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap', marginBottom: SP.sm, fontFamily: FONT.sans }}
    >
      <span data-testid="stats-accuracy-phrase" style={{ fontSize: FS.xs, color: C.textMuted }}>
        {phrase}
      </span>
      {truncated && (
        <span data-testid="stats-accuracy-truncated" style={{ fontSize: FS.xs, color: C.warningText }}>
          журнал обрезан
        </span>
      )}
    </div>
  );
}

// «Задача» по taskId сегмента/этапа: NavTargetButton (открывает задачу в «Задачах»).
// run нужен для projectId: задачи фичи лежат в проекте её эпика (как у «Главных потерь»
// FeatureStatsLosses) — без projectId openTaskInSection ведёт в личный «Календарь» (дефект ee6825ff).
function TaskButtons({ model, run, nav, isMobile }: { model: TimelineModel; run?: FeatureRun | null; nav?: FeatureNavCtx; isMobile: boolean }) {
  const marks = nav?.marks ?? EMPTY_NAV_MARKS;
  // Собираем уникальные taskId по этапам (из сегментов) — по одному «задача» на этап.
  const byStage = new Map<string, string | null>();
  for (const b of model.blocks) {
    if (b.type !== 'stage') continue;
    // taskId — из main-сегментов, иначе из дорожки ревью (оба несут задачу).
    const tid = (b.segments.find(s => s.taskId) ?? b.reviewSegments.find(s => s.taskId))?.taskId ?? null;
    if (tid && !byStage.has(b.key)) byStage.set(b.key, tid);
  }
  if (byStage.size === 0) return null;
  return (
    <div
      data-testid="stats-tasks"
      style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap', marginTop: SP.md }}
    >
      {[...byStage.entries()].map(([stage, taskId]) => (
        <span key={stage} style={{ display: 'inline-flex', alignItems: 'center', gap: 4, fontSize: FS.xs, color: C.textMuted }}>
          {stageLabelShort(stage)}
          <NavTargetButton
            kind="task"
            taskId={taskId}
            taskClosed={marks.taskClosed(taskId)}
            isMobile={isMobile}
            onOpen={() => {
              if (nav?.featureId && taskId) recordFeatureReturn(nav.featureId, { kind: 'task', id: taskId }, nav.featureTitle);
              openTaskInSection({ id: taskId!, projectId: run?.epic?.projectId ?? undefined });
            }}
          />
        </span>
      ))}
    </div>
  );
}

// Раскрытие «Подробнее» помним между заходами (план ST-F14).
export const MORE_OPEN_KEY = 'featureStats.moreOpen';
function readMoreOpen(): boolean {
  try { return localStorage.getItem(MORE_OPEN_KEY) === '1'; } catch { return false; }
}
function writeMoreOpen(open: boolean): void {
  try { localStorage.setItem(MORE_OPEN_KEY, open ? '1' : '0'); } catch { /* приватный режим/квота */ }
}

// Кольцо фокуса строки «Подробнее» (P2): инлайн-стиль не умеет :focus-visible —
// класс инжектится один раз (тот же приём, что ui/Chip и FeatureBreakdownShared).
const MORE_FOCUS_CLASS = 'cc-stats-more';
if (typeof document !== 'undefined' && !document.getElementById('cc-stats-more-style')) {
  const el = document.createElement('style');
  el.id = 'cc-stats-more-style';
  el.textContent = `.${MORE_FOCUS_CLASS}:focus-visible{outline:none;box-shadow:${SHADOW.focus};}`;
  document.head.appendChild(el);
}

// «Подробнее» (ST-F14a, макет feature-stats-5.md §«Правки фронта», п. 8): только то,
// что новые блоки НЕ покрывают: «не работали» (в составе героя его нет), «в очереди»
// абсолютными минутами (в составе героя — только проценты), эпизоды
// ошибок, детали расхода (кэш, «данные с», общие чаты, модели без цены). Нечего
// добавить — раскрывашки нет (пустой «Подробнее» не бывает).
function MoreDetails({ totals, spend, source, isMobile }: {
  totals: FeatureTimeline['totals'];
  spend: FeatureSpend | null;
  source: FeatureStatsSource;
  isMobile: boolean;
}) {
  const rows: string[] = [];
  const approx = source === 'approx' ? '≈ ' : '';
  if (totals.notWorkingMinutes != null && totals.notWorkingMinutes > 0) {
    rows.push(`не работали — ${approx}${formatDuration(totals.notWorkingMinutes)}`);
  }
  // «В очереди» (Q5H-1, R-Q3B-2): самостоятельный вид разряды (ADR-031 §«Время в очереди (queued)») —
  // минуты отдаёт бэк (totals.queuedMinutes), фронт ничего не пересчитывает.
  // Порядок — как в составе героя (FeatureStatsPulseModel): простой → очередь → ошибки,
  // т. е. рядом с «не работали». При null/0/нет поля строки нет (прежнее поведение).
  // Слово вида — из единого словаря составных видов COMP_LABEL (Q5H-3): герой, эта строка
  // и мини-сводка формулируются из одного места, не разъезжаются молча.
  if (totals.queuedMinutes != null && totals.queuedMinutes > 0) {
    rows.push(`${COMP_LABEL.queued} — ${approx}${formatDuration(totals.queuedMinutes)}`);
  }
  if (totals.errorCount != null && totals.errorCount > 0) {
    rows.push(`ошибки: ${totals.errorCount} ${plural(totals.errorCount, 'эпизод', 'эпизода', 'эпизодов')}`);
  }
  if (spend) {
    // Кэш — отдельной строкой подробно; в итог плитки «Расход» он тоже входит (tilesModel).
    const cache = spend.cacheReadTokens + spend.cacheCreationTokens;
    if (cache > 0) {
      rows.push(`кэш: ${formatTokens(spend.cacheReadTokens)} чтение · ${formatTokens(spend.cacheCreationTokens)} создание`);
    }
    // «Данные с …» — только если расход ненулевой (иначе у пустой фичи это дата эпика).
    const nonzero = spend.inputTokens + spend.outputTokens + cache > 0 || spend.costUsd > 0;
    if (nonzero) rows.push(`данные с ${new Date(spend.from).toLocaleDateString('ru-RU')}`);
    // Метод оценки shared — из spend.shared.method, не из source (расход и лента могут
    // считаться разными способами) — то же правило, что в прежнем блоке Spend.
    if (spend.shared) {
      rows.push(
        `в т. ч. доля общих чатов ≈ ${formatTokens(spend.shared.inputTokens + spend.shared.outputTokens)} токенов` +
        ` (оценка, по ${spend.shared.method === 'journal' ? 'журналу' : 'задачам ≈'})`,
      );
    }
    if (spend.costPartial && spend.hiddenCostModels.length > 0) {
      rows.push(`моделей без цены: ${spend.hiddenCostModels.join(', ')}`);
    }
  }
  const [open, setOpen] = useState(readMoreOpen);
  if (rows.length === 0) return null;
  return (
    <details
      data-testid="stats-more"
      open={open}
      onToggle={e => {
        const next = (e.currentTarget as HTMLDetailsElement).open;
        if (next === open) return;
        setOpen(next);
        writeMoreOpen(next);
      }}
      style={{ margin: `${SP.md}px 0 0` }}
    >
      <summary
        data-testid="stats-more-summary"
        className={MORE_FOCUS_CLASS}
        style={{
          // Карточка-строка по макету .more: высота ≥ 44 (тач-цель на 360), рамка и
          // фон карточки; кольцо фокуса — класс MORE_FOCUS_CLASS (:focus-visible).
          display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: SP.sm,
          minHeight: 44, padding: `${SP.md}px ${SP.lg}px`,
          background: C.bgCard, border: `1px solid ${C.borderLight}`, borderRadius: R.lg,
          cursor: 'pointer', fontSize: FS.sm, fontWeight: 600, color: C.textSecondary, fontFamily: FONT.sans,
        }}
      >
        <span>Подробнее</span>
        <span aria-hidden>▾</span>
      </summary>
      <div
        style={{
          marginTop: SP.sm, padding: isMobile ? `${SP.sm}px ${SP.md}px` : `${SP.md}px ${SP.lg}px`,
          background: C.bgCard, border: `1px solid ${C.borderLight}`, borderRadius: R.lg,
          display: 'flex', flexDirection: 'column', gap: SP.xs,
          fontFamily: FONT.mono, fontSize: FS.xs, color: C.textSecondary,
        }}
      >
        {rows.map((r, i) => <span key={i} data-testid="stats-more-row">{r}</span>)}
      </div>
    </details>
  );
}

export function FeatureStatsTab({ stats, run = null, featureId = null, featureTitle = null, marks, isMobile = false }: FeatureStatsTabProps) {
  // Хуки выше ранних return: labelOf/colorOf нужны только с run, но хук не может
  // быть условным (react-hooks/rules-of-hooks)
  const personas = usePersonas();
  useModels(); // реактивность: догрузка каталога моделей меняет подписи
  const nav: FeatureNavCtx = { featureId, featureTitle, marks };
  // Строка анализа (W5-F1) — про фичу, а не про статистику: показываем и в пустых
  // состояниях вкладки, поэтому вынесена до ранних return. Без run (старые ответы) — нет.
  const line = run ? <FeatureAnalysisLine run={run} marks={marks} isMobile={isMobile} /> : null;

  // Пустое состояние: stats нет (старые данные) — причина, а не нули.
  if (!stats) {
    return (
      <>
        {line}
        <EmptyState
          compact
          icon="📊"
          title="Статистики пока нет"
          subtitle="Появится, когда будет посчитано время фичи."
        />
      </>
    );
  }

  // Закрыта/смержена: current == null (контракт: null — закрыта).
  const closed = stats.timeline.current == null;
  const model = buildTimelineModel(stats.timeline, stats.source, stats.truncated, closed);

  // Пустое состояние: нет ни сегментов, ни текущего состояния (макет: «Считать пока нечего»).
  if (!model.hasData) {
    return (
      <>
        {line}
        <EmptyState
          compact
          icon="📊"
          title="Считать пока нечего"
          subtitle="Статистика появится с первой задачей исполнителя."
        />
      </>
    );
  }

  // Плитки и «Главные потери» — из run + stats; без run (старые ответы) их нет.
  const costPartial = stats.spend?.costPartial ?? false;

  // labelOf/colorOf — те же источники подписей, что у прежнего Breakdown:
  // персона → имя, роль → roleLabel, модель → modelLabel. Цвет — цвет аватара персоны
  // (AGENT_COLORS); остальным — '' (палитра ShareRing решает сама). Ось приходит от
  // оболочки разбивки: по ключу её не угадать (UUID недоступной персоны не отличить
  // от id модели — он ушёл бы в подпись сырым).
  const labelOf = (key: string, axis: 'personas' | 'models' | 'roles'): string => {
    if (axis === 'personas') return personaGroupLabel(key, personas);
    if (axis === 'roles') return roleGroupLabel(key);
    return modelGroupLabel(key);
  };
  const colorOf = (key: string): string => {
    const c = personas.find(x => x.id === key)?.avatar?.color;
    return c ? AGENT_COLORS[c] ?? '' : '';
  };

  return (
    <div data-testid="feature-stats-tab" style={{ fontFamily: FONT.sans }}>
      {line}
      <AccuracyBadges source={stats.source} truncated={stats.truncated} />
      {run && (
        <div style={{ display: 'flex', flexDirection: 'column', gap: SP.lg, marginTop: SP.lg }}>
          <FeatureStatsTiles run={run} stats={stats} isMobile={isMobile} />
          <FeatureStatsLosses run={run} stats={stats} nav={nav} isMobile={isMobile} />
        </div>
      )}
      <TaskButtons model={model} run={run} nav={nav} isMobile={isMobile} />
      {run && (
        <div style={{ marginTop: SP.lg }}>
          <FeatureBreakdownBlock
            breakdown={stats.breakdown}
            costPartial={costPartial}
            run={run}
            labelOf={labelOf}
            colorOf={colorOf}
            nav={nav}
            isMobile={isMobile}
          />
        </div>
      )}
      <MoreDetails totals={stats.timeline.totals} spend={stats.spend} source={stats.source} isMobile={isMobile} />
    </div>
  );
}
