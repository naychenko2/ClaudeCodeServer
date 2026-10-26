// Блоки деталей шага (GC-F7, план «Гант на „Хронологии“», карточка GC-F7; макет
// feature-gantt-7 §«Карточка шага»). Три блока в раскрытой части карточки «Производства»
// (GC-F8) и внутри раскрытой строки исполнителя (GC-F9): «Приёмка», «Итог»,
// «Время шага на оси фичи».
//
// Компонент презентационный: не пересчитывает конвейер, не строит цепочки, не выдумывает
// вердикты — всё приходит пропсами. Единственные данные, за которыми он ходит сам, —
// полный текст задачи для «Итога»: агрегат фичи намеренно не несёт ResultMarkdown (SlimRun),
// поэтому «Итог» догружается лениво через useEnsureFullTask — только пока блок смонтирован
// (закрытая карточка не запрашивает ничего).
//
// «✓»/«○» — по criterionId, а не по индексу (живой контракт 26.10, GC-R-F7): критерий
// считается подтверждённым, если есть хотя бы одна запись свидетельства с этим criterionId.
//
// Хвост шага (открытая задача после мержа): его ходы ВХОДЯТ в интервалы своей стадии —
// пересечение закреплено тестом бэка. Полоса строится по ним как обычно, но блок помечается
// «хвост после мержа»: это не обычная работа стадии (живой контракт GC-F7).

import { useState, type CSSProperties, type ReactElement } from 'react';
import { C, R, FS, SP, FONT } from '../../lib/design';
import { Button } from '../ui/Button';
import { axisFrac, type LaneAxis } from './FeatureLaneModel';
import { formatClock, formatDateTime } from './FeatureStatsBits';
import { useEnsureFullTask, useTasks } from '../../lib/tasks';
import type { AcceptanceCriterion, AcceptanceEvidence, Task } from '../../types';

// Ход на полосе шага. Вызывающий собирает ходы из stats.timeline.intervalsByStage:
// 'work' — ход рабочей стадии (prod/rework), 'review' — ход ревью/проверки/финала
// (стадию интервала знает вызывающий), 'live' — живой хвост идущей задачи: журнала таких
// ходов не содержит, вызывающий синтезирует его из live-интервала (его 'to' — край
// интервала, то есть «сейчас»).
export interface StepDetailTurn {
  from: string;
  to: string;
  kind: 'work' | 'review' | 'live';
}

export interface FeatureStepDetailsProps {
  // Задача, чьи приёмка и итог показываются: её id — в подписи «Приёмка · {id}»
  // (8 первых знаков, как в других списках id), по нему же — добор полного текста в «Итоге».
  taskId: string;
  // Критерии приёмки (FeatureTask.acceptance из run). Нет/пусто — блока «Приёмка» нет.
  acceptance?: AcceptanceCriterion[] | null;
  // Свидетельства по критериям (FeatureTask.evidence). «✓» — по наличию записи с этим
  // criterionId, а не по индексу строки.
  evidence?: AcceptanceEvidence[] | null;
  // Строка вердикта — готовая фраза вызывающего («Ревью {id}: принял · {Имя} · {дата}»).
  // Компонент вердикты не считает: не передано — строки нет.
  verdictLine?: string | null;
  // Ходы шага на полосе. Нет/пусто — пустая полоса (честно: времени у шага нет).
  turns?: StepDetailTurn[] | null;
  // Ось фичи — та же, что у дорожек (buildStageLanes().axis). Нет — блока времени нет.
  axis?: LaneAxis | null;
  // Хвост после мержа: блок времени помечается маркером и не выдаётся за обычную
  // работу стадии.
  isTail?: boolean;
  isMobile?: boolean;
  // Фиксированное «сейчас» (тесты); без него — Date.now(), зафиксированный на монтирование
  // вне тела рендера (purity, GC-R-F7).
  nowMs?: number;
  // «Показать интервалы на дорожке ↑» — рисуется только когда колбэк передан (в карточке
  // «Производства» он ведёт подсветку на дорожке, в строке исполнителя — нет).
  onShowIntervals?: () => void;
}

// Подпись секции — та же, что у блоков «Статистики» (FeatureStatsLosses, StatsHero).
const SEC_LABEL: CSSProperties = {
  fontSize: FS.xxs, textTransform: 'uppercase', letterSpacing: 0.6,
  color: C.textMuted, fontFamily: FONT.sans,
};

// Визуально скрытый текст (sr-only): состояние читается скринридеру словом, глиф при
// этом остаётся декоративным (GC-R-F7: «✓/○» без подписи для AT — просто список текстов).
const SR_ONLY: CSSProperties = {
  position: 'absolute', width: 1, height: 1, overflow: 'hidden',
  clipPath: 'inset(50%)', whiteSpace: 'nowrap',
};

// === «Приёмка · {id}» ===

function AcceptanceBlock({ taskId, acceptance, evidence, verdictLine }: {
  taskId: string; acceptance: AcceptanceCriterion[]; evidence: AcceptanceEvidence[]; verdictLine?: string | null;
}) {
  // Подтверждённость — множеством criterionId: порядок и число свидетельств не важны,
  // важна связь по id (живой контракт GC-F7).
  const covered = new Set(evidence.map(e => e.criterionId));
  return (
    <div data-testid="step-acceptance">
      <div style={SEC_LABEL}>Приёмка · {taskId.slice(0, 8)}</div>
      {acceptance.map(c => {
        const ok = covered.has(c.id);
        return (
          <div key={c.id} data-testid={`step-crit-${c.id}`} style={{ display: 'flex', gap: SP.sm, alignItems: 'baseline', padding: '2px 0' }}>
            <span
              data-testid="step-crit-glyph"
              style={{ width: 14, flexShrink: 0, fontSize: FS.sm, fontWeight: 700, color: ok ? C.successText : C.textMuted }}
            >
              {ok ? '✓' : '○'}
            </span>
            {/* состояние — словом, рядом с глифом (GC-R-F7): скринридеру «подтверждён/не подтверждён» */}
            <span data-testid="step-crit-state" style={SR_ONLY}>{ok ? 'подтверждён' : 'не подтверждён'}</span>
            <span style={{ fontSize: FS.sm, color: C.textPrimary, minWidth: 0, overflowWrap: 'anywhere' }}>{c.text}</span>
          </div>
        );
      })}
      {verdictLine && (
        <div data-testid="step-verdict" style={{ marginTop: SP.xs, fontSize: FS.sm, color: C.textSecondary, overflowWrap: 'anywhere' }}>
          {verdictLine}
        </div>
      )}
    </div>
  );
}

// === «Итог» ===

// Заглушка задачи, которой ещё нет в сторе: useEnsureFullTask читает только id и listItem,
// остальные поля — фиктивные; настоящая полная версия приходит из запроса.
const notInStore = (id: string): Task => ({
  id, title: '', description: '', status: 'todo', priority: 'medium',
  linkedFiles: [], subtasks: [], labels: [], order: 0, createdAt: '', updatedAt: '',
  listItem: true,
});

function ResultBlock({ taskId, isMobile }: { taskId: string; isMobile: boolean }) {
  const tasks = useTasks();
  const stored = tasks.find(t => t.id === taskId);
  const knownNoResult = !!stored && stored.listItem === true && stored.hasResult === false;
  const { state, retry } = useEnsureFullTask(knownNoResult ? { ...stored!, listItem: false } : stored ?? notInStore(taskId));
  const full = stored && !stored.listItem ? stored : undefined;
  // Первая непустая строка итога; полный текст — на странице задачи (макет).
  const firstLine = (full?.resultMarkdown ?? '').split(/\r?\n/).find(l => l.trim() !== '') ?? '';
  if (knownNoResult || (state === 'ready' && !firstLine)) return null;
  return (
    <div data-testid="step-result">
      <div style={SEC_LABEL}>Итог</div>
      {state === 'loading' && (
        <span data-testid="step-result-loading" style={{ fontSize: FS.sm, color: C.textMuted }}>…</span>
      )}
      {state === 'error' && (
        <span style={{ display: 'inline-flex', alignItems: 'center', gap: SP.sm }}>
          {/* отказ — текстом, а не пустотой (GC-R-F7) */}
          <span data-testid="step-result-error" style={{ fontSize: FS.sm, color: C.textSecondary }}>итог не загрузился</span>
          {/* на мобильном цель не меньше 40 px (макет §360; тот же приём, что у «Показать интервалы») */}
          <Button variant="ghost" size="xs" dataTestId="step-result-retry" onClick={retry} style={isMobile ? { minHeight: 40 } : undefined}>Повторить</Button>
        </span>
      )}
      {state === 'ready' && (
        <div data-testid="step-result-text" style={{ fontSize: FS.sm, color: C.textSecondary, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
          {firstLine}
        </div>
      )}
    </div>
  );
}

// === «Время шага на оси фичи» ===

function TimeBlock({ turns, axis, isTail, isMobile, nowMs }: {
  turns: StepDetailTurn[]; axis: LaneAxis; isTail: boolean; isMobile: boolean; nowMs: number;
}) {
  // «Живое» гаснет на закрытой оси (тот же порядок, что у модели дорожек): там нет «сейчас».
  const live = axis.timeFrac < 1;
  // Ход целиком за правым краем окна не рисуется (то же правило, что у buildStageLanes).
  const visible = turns.filter(t => new Date(t.from).getTime() < new Date(axis.to).getTime());
  const nowIso = new Date(nowMs).toISOString();
  const color = (kind: StepDetailTurn['kind']): string =>
    kind === 'review' ? C.plan : kind === 'live' ? C.accent : C.success;
  return (
    <div data-testid="step-time">
      <div style={SEC_LABEL}>Время шага на оси фичи{isTail ? ' · хвост после мержа' : ''}</div>
      <div data-testid="step-strip" aria-hidden style={{ position: 'relative', height: 14, background: C.bgInset, borderRadius: R.sm }}>
        {visible.map((t, i) => {
          // Доли — ровно axisFrac (та же шкала, что у дорожки этапа). Минимум 0.3 % —
          // чтобы короткий ход был виден; left при этом не сдвигается (минимум расширяет вправо).
          const left = axisFrac(t.from, axis) * 100;
          const width = Math.max(0.3, (axisFrac(t.to, axis) - axisFrac(t.from, axis)) * 100);
          return (
            <span
              key={`${t.from}-${i}`}
              data-testid="step-turn"
              data-kind={t.kind}
              style={{ position: 'absolute', top: 3, height: 8, left: `${left}%`, width: `${width}%`, borderRadius: R.sm, background: color(t.kind) }}
            />
          );
        })}
        {live && (
          <span data-testid="step-now" style={{ position: 'absolute', top: 0, bottom: 0, width: 2, marginLeft: -1, background: C.accent, left: `${axisFrac(nowIso, axis) * 100}%` }} />
        )}
      </div>
      <div style={{ display: 'flex', justifyContent: 'space-between', gap: SP.sm, marginTop: SP.xs }}>
        <span style={{ fontFamily: FONT.mono, fontSize: FS.xxs, color: C.textMuted }}>{formatDateTime(axis.from)}</span>
        {/* легенда цветов — только на десктопе (на 360 строка и так тесная; подписей достаточно);
            ходы названы, цвета — токенами дизайн-системы (GC-R-F7): при смене палитры не разойдутся */}
        {!isMobile && <span style={{ fontFamily: FONT.sans, fontSize: FS.xxs, color: C.textMuted }}>работа — C.success · ревью — C.plan · живой ход — C.accent</span>}
        {live && <span style={{ fontFamily: FONT.mono, fontSize: FS.xxs, color: C.textMuted }}>сейчас {formatClock(nowIso)}</span>}
      </div>
    </div>
  );
}

// === Компонент ===

export function FeatureStepDetails({
  taskId, acceptance, evidence, verdictLine, turns, axis,
  isTail = false, isMobile = false, nowMs, onShowIntervals,
}: FeatureStepDetailsProps): ReactElement {
  const hasAcceptance = !!acceptance && acceptance.length > 0;
  // «Сейчас» — фиксированный момент на монтирование (purity, GC-R-F7): ленивый инициализатор
  // useState, тот же чистый для правила приём, что у useNow.ts; прямой Date.now() в теле рендера
  // (в т. ч. внутри useMemo) правило не допускает. Тестовое значение — пропом nowMs.
  const [mountNow] = useState(() => Date.now());
  const now = nowMs ?? mountNow;
  return (
    <div data-testid="step-details" style={{ display: 'flex', flexDirection: 'column', gap: SP.md }}>
      {hasAcceptance && (
        <AcceptanceBlock taskId={taskId} acceptance={acceptance} evidence={evidence ?? []} verdictLine={verdictLine} />
      )}
      <ResultBlock taskId={taskId} isMobile={isMobile} />
      {axis && <TimeBlock turns={turns ?? []} axis={axis} isTail={isTail} isMobile={isMobile} nowMs={now} />}
      {onShowIntervals && (
        <Button
          variant="ghost"
          size="xs"
          dataTestId="step-show-intervals"
          onClick={onShowIntervals}
          ariaLabel="Подсветить интервалы этого шага на дорожке «Производства»"
          style={isMobile ? { minHeight: 40 } : undefined}
        >
          Показать интервалы на дорожке ↑
        </Button>
      )}
    </div>
  );
}
