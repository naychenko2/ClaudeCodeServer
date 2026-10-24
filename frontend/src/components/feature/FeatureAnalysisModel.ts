// Модель состояния строки анализа (волна 5, W5-F1; спецификация features-overview-5.md §«Точка 6»).
// Чистая функция: `run` и фиксированное «сейчас» → первое подходящее состояние сверху вниз
// (done → stuck → nudged → quiet → attention → idle → moving → other). Фронт ничего не
// пересчитывает за бэком: «тихо» — это run.quiet, а не своя арифметика по датам; длительности
// в тексте — только показ (formatDuration из FeatureStatsBits).
//
// «Зависла» и «тихо» — разные вещи: зависла — исполнитель молчит после оклика, сторож позвал
// человека (опасность); тихо — исполнителя нет, фича забыта (предупреждение, рамка пунктиром).

import { C } from '../../lib/design';
import { getPersonaById } from '../../lib/personas';
import { formatClock, formatDateTime, formatDuration } from './FeatureStatsBits';
import { featureRunStatusTag } from './FeaturePageHeader';
import type { FeatureNow, FeatureRun, FeatureState } from '../../types/featureRun';

export type AnalysisKey =
  | 'done' | 'stuck' | 'nudged' | 'quiet' | 'attention' | 'idle' | 'moving' | 'other';

// Тон короба: neutral — C.bgCard/рамка borderLight; warning — warningBg/warning;
// danger — dangerBg/dangerBorder. quiet — warningBg + пунктирная рамка (dashed).
export type AnalysisTone = 'neutral' | 'warning' | 'danger';

export interface AnalysisBadge {
  tone: 'danger' | 'warning' | 'success';
  icon: 'Clock' | 'Bell' | 'Moon' | 'Check';
  text: string;
}

export interface AnalysisDot {
  // Цвет точки — токен C.*; glow — тень SHADOW.focus (для «движется»).
  color: string;
  glow: boolean;
}

export interface AnalysisAction {
  kind: 'openChat' | 'analysis';
  label: string;
  // Чат перехода (для openChat); «Анализ моделью» — без чата.
  sessionId?: string;
  variant: 'primary' | 'secondary' | 'ghost';
}

export interface AnalysisLineState {
  key: AnalysisKey;
  tone: AnalysisTone;
  dashed: boolean;
  badge?: AnalysisBadge;
  dot?: AnalysisDot;
  // Жирная часть фразы (C.textHeading) и хвост (C.textMuted). Пустая строка — не показывать.
  strong: string;
  tail: string;
  actions: AnalysisAction[];
}

// Короткая подпись стадии конвейера — одна на весь фронт (раньше жила в FeatureStatsTab).
export function stageLabelShort(key: string): string {
  const names: Record<string, string> = {
    cut: 'Разрез', plan: 'План', prod: 'Производство', review: 'Ревью',
    qa: 'Проверка', final: 'Финал', merge: 'Мерж',
  };
  return names[key] ?? key;
}

// «Текущий» элемент run.now: бэк сортирует по убыванию старта; берём первого с тревожной
// отметкой (сначала позвал человека, потом оклик), иначе первого.
function pickCur(run: FeatureRun): FeatureNow | null {
  const list = run.now ?? [];
  return list.find(n => n.staleAlertedAt) ?? list.find(n => n.nudgedAt) ?? list[0] ?? null;
}

// Минуты от iso до nowMs; null — нет даты или она нечитаемая (длительность не пишем).
function minutesSince(iso: string | null | undefined, nowMs: number): number | null {
  if (!iso) return null;
  const t = Date.parse(iso);
  if (!Number.isFinite(t)) return null;
  return Math.max(0, Math.round((nowMs - t) / 60000));
}

// {Имя} — имя персоны по personaId; нет — «исполнитель».
function personName(personaId: string | null | undefined): string {
  if (!personaId) return 'исполнитель';
  return getPersonaById(personaId)?.name ?? 'исполнитель';
}

// {Шаг} — stepRef, иначе title задачи.
function stepLabel(cur: FeatureNow): string {
  return cur.stepRef || cur.title;
}

const ANALYSIS_ACTION: AnalysisAction = { kind: 'analysis', label: 'Анализ моделью', variant: 'ghost' };

export function analysisLineState(run: FeatureRun, nowMs: number): AnalysisLineState {
  // 1. done — смержена или закрыта.
  if (run.state === 'merged' || run.state === 'closed') {
    const badge: AnalysisBadge = {
      tone: 'success', icon: 'Check', text: run.state === 'merged' ? 'смержена' : 'закрыта',
    };
    const s = run.summary;
    if (!s) return { key: 'done', tone: 'neutral', dashed: false, badge, strong: '', tail: '', actions: [ANALYSIS_ACTION] };
    const when = s.mergedAt ? `${formatDateTime(s.mergedAt)} · ` : '';
    return {
      key: 'done', tone: 'neutral', dashed: false, badge,
      strong: `${when}шла ${formatDuration(s.durationMinutes)}`,
      tail: `${s.steps} шагов, ${s.loops} возвратов.`,
      actions: [ANALYSIS_ACTION],
    };
  }

  const cur = pickCur(run);
  const idleDur = minutesSince(run.lastActivityAt, nowMs);

  // 2. stuck — есть now[i].staleAlertedAt: «{Шаг} без движения».
  if (cur?.staleAlertedAt) {
    const actions: AnalysisAction[] = [];
    if (cur.linkedSessionId) {
      actions.push({ kind: 'openChat', label: `Открыть чат ${stepLabel(cur)}`, sessionId: cur.linkedSessionId, variant: 'primary' });
    }
    actions.push(ANALYSIS_ACTION);
    return {
      key: 'stuck', tone: 'danger', dashed: false,
      badge: { tone: 'danger', icon: 'Clock', text: idleDur != null ? `стоит ${formatDuration(idleDur)}` : 'стоит' },
      strong: `${stepLabel(cur)} без движения`,
      tail: `${personName(cur.personaId)} не ответил(а) на оклик, сторож позвал вас в ${formatClock(cur.staleAlertedAt)}.`,
      actions,
    };
  }

  // 3. nudged — есть now[i].nudgedAt: «{Имя} не отвечает на {Шаг}».
  if (cur?.nudgedAt) {
    const nudgeDur = minutesSince(cur.nudgedAt, nowMs);
    const actions: AnalysisAction[] = [];
    if (cur.linkedSessionId) {
      actions.push({ kind: 'openChat', label: `Открыть чат ${stepLabel(cur)}`, sessionId: cur.linkedSessionId, variant: 'secondary' });
    }
    actions.push(ANALYSIS_ACTION);
    return {
      key: 'nudged', tone: 'warning', dashed: false,
      badge: { tone: 'warning', icon: 'Bell', text: nudgeDur != null ? `молчит ${formatDuration(nudgeDur)}` : 'молчит' },
      strong: `${personName(cur.personaId)} не отвечает на ${stepLabel(cur)}`,
      tail: `сторож окликнул в ${formatClock(cur.nudgedAt)}, ждём ответа.`,
      actions,
    };
  }

  // 4. quiet — run.quiet: «Ни одна задача не идёт с …». Рамка пунктиром.
  if (run.quiet === true) {
    const actions: AnalysisAction[] = [];
    const keyChat = run.epic?.linkedSessionId ?? null;
    if (keyChat) {
      actions.push({ kind: 'openChat', label: 'Открыть ключевой чат', sessionId: keyChat, variant: 'secondary' });
    }
    actions.push(ANALYSIS_ACTION);
    return {
      key: 'quiet', tone: 'warning', dashed: true,
      badge: { tone: 'warning', icon: 'Moon', text: idleDur != null ? `тихо ${formatDuration(idleDur)}` : 'тихо' },
      strong: run.lastActivityAt
        ? `Ни одна задача не идёт с ${formatDateTime(run.lastActivityAt)}.`
        : 'Ни одна задача не идёт.',
      tail: '',
      actions,
    };
  }

  // 5. attention — тревога бэка: «Ждёт вас · {причина}».
  if (run.attention != null) {
    const reason = run.attention.reason ?? (run.attention.kind === 'merge' ? 'мерж' : '');
    const actions: AnalysisAction[] = [];
    if (run.attention.linkedSessionId) {
      actions.push({ kind: 'openChat', label: 'Открыть чат', sessionId: run.attention.linkedSessionId, variant: 'secondary' });
    }
    actions.push(ANALYSIS_ACTION);
    return {
      key: 'attention', tone: 'neutral', dashed: false,
      dot: { color: C.info, glow: false },
      strong: reason ? `Ждёт вас · ${reason}` : 'Ждёт вас',
      tail: '',
      actions,
    };
  }

  // 6. idle — state 'idle' (и quiet нет): «Ждёт · {dur}».
  if (run.state === 'idle') {
    return {
      key: 'idle', tone: 'neutral', dashed: false,
      dot: { color: C.textMuted, glow: false },
      strong: idleDur != null ? `Ждёт · ${formatDuration(idleDur)}` : 'Ждёт',
      tail: 'ни одна задача не идёт.',
      actions: [ANALYSIS_ACTION],
    };
  }

  // 7. moving — run.now не пуст: «Идёт · {этап}».
  if (cur) {
    const runDur = minutesSince(cur.startedAt, nowMs);
    return {
      key: 'moving', tone: 'neutral', dashed: false,
      dot: { color: C.accent, glow: true },
      strong: `Идёт · ${stageLabelShort(cur.stage)}`,
      tail: `${stepLabel(cur)} у ${personName(cur.personaId)}${runDur != null ? ` ${formatDuration(runDur)}` : ''}.` +
        (idleDur != null ? ` Последнее событие ${formatDuration(idleDur)} назад.` : ''),
      actions: [ANALYSIS_ACTION],
    };
  }

  // 8. other — всё остальное (not_started, draft, paused, merge_pending…): тег статуса.
  // Неизвестный/отсутствующий state (старые данные) — тега нет, как и в шапке: пустая фраза.
  return {
    key: 'other', tone: 'neutral', dashed: false,
    dot: { color: C.textMuted, glow: false },
    strong: featureRunStatusTag(run.state as FeatureState)?.label ?? '',
    tail: '',
    actions: [ANALYSIS_ACTION],
  };
}
