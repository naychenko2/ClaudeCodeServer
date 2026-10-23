// Гант вкладки «Статистика» (волна 4, точка 4 — ступени, плюс метки 6А; спецификация
// docs/mockups/features-overview-4-spec.md §«Точка 6»). W4-4F1 — ЧИСТАЯ модель: для каждого
// этапа конвейера строка — длительность, разбивка «работа · ожидание · простой» по минутам,
// флажки возвратов, пилюля «сейчас» на активном этапе, хвост «без движения» при простое.
// Новых полей API нет: всё из существующего FeatureRun (stages/stepChains/rounds/now/attention)
// и stats.timeline; чего нет в данных — явный null. Рендер (W4-4F2) ничего не считает —
// только читает эту модель.

import type { FeatureRun, FeatureRound, StageKey, StageState } from '../../types/featureRun';
import type { FeatureStats } from '../../types/featureStats';
import { stageLabel } from './FeatureStatsBits';

// Вид сегмента полосы: виды таймлайна, а трековый review — отдельный вид (красится C.plan).
export type GanttSegmentKind = 'work' | 'waiting' | 'idle' | 'error' | 'notWorking' | 'review';

export interface GanttSegment {
  from: string;
  to: string;
  kind: GanttSegmentKind;
  minutes: number;
}

// Флажок возврата: виток с вердиктом returned/accepted_with_remarks и закрытым completedAt.
export interface GanttReturnFlag {
  // completedAt витка — точка флажка на оси времени.
  at: string;
  roundN: number;
  // PersonaId оценившего — для tooltip «{Имя} вернул {Ш3}».
  who: string | null;
  // Код возвращённого шага (у витков цепочки; у витков qa/final — null, шага в данных нет).
  stepRef: string | null;
  // Причина: Verification.Notes.
  reason: string | null;
  reviewChatId: string | null;
}

// Части пилюли «сейчас» на активном этапе; текст собирает рендер (десктоп/360 — разный).
export interface GanttNowPill {
  stageKey: StageKey;
  stepRef: string | null;
  title: string | null;
  startedAt: string | null;
  // Длительность от startedAt до `now`; null — startedAt нет (тогда только «{Ш3}»).
  durationMinutes: number | null;
}

export interface GanttRow {
  key: StageKey;
  label: string;
  // done | active | pending | skipped — «absent» строки не имеет (этапа нет в конвейере).
  state: StageState;
  // skipped (W4-DES-B): необязательный этап без задач — строка «этапа не было»,
  // без минут и полосы. Рендер читает этот флаг, а не разбирает state.
  isSkipped: boolean;
  // Длительность этапа — approxMinutes бэка (фронт не пересчитывает).
  minutes: number;
  // Границы полосы на оси: firstStartedAt / lastCompletedAt; null — этапа нет в данных.
  from: string | null;
  to: string | null;
  // Сегменты полосы в хронологическом порядке (main + review).
  segments: GanttSegment[];
  // Разбивка по main-сегментам; error/notWorking — явные части (не теряются: сумма
  // breakdown равна сумме минут всех main-сегментов). null — main-сегментов нет.
  breakdown: { work: number; waiting: number; idle: number; error: number; notWorking: number } | null;
  // Самый долгий этап (макс approxMinutes > 0; при равенстве — первый по порядку;
  // единственный кандидат — не подсвечиваем).
  isLongest: boolean;
  // Этап, где фича сейчас (run.now).
  isCurrent: boolean;
  nowPill: GanttNowPill | null;
  // Хвост «без движения»: начало (attention.since) — только при attention.kind === 'stopped'.
  stalledSince: string | null;
  stalledReason: string | null;
  // Действие строки под Гантом — существующая тревога: чат исполнителя; null — кнопки нет.
  stalledActionChatId: string | null;
  // Флажки стоят на строке этапа, куда вернулась работа — «Производство».
  returns: GanttReturnFlag[];
  returnCount: number;
}

export interface GanttModel {
  rows: GanttRow[];
  // Ось времени из таймлайна (единственный честный источник границ); null — данных нет.
  axis: { from: string; to: string } | null;
  // false — существующее пустое состояние вкладки (без шапки с кнопкой резюме).
  hasData: boolean;
  // Для легенды «возврат» (появляется только если > 0).
  returnTotal: number;
  // Для легенды «без движения» (только в состоянии «зависла»).
  hasStalled: boolean;
  // Смержена/закрыта: ни «сейчас», ни «зависла»; самый долгий и флажки остаются.
  closed: boolean;
}

// Длительность интервала в минуты (0, если невалидно/отрицательно).
function spanMinutes(from: string, to: string): number {
  const a = new Date(from).getTime();
  const b = new Date(to).getTime();
  if (Number.isNaN(a) || Number.isNaN(b)) return 0;
  return Math.max(0, Math.round((b - a) / 60000));
}

// Минуты от ISO-момента до `now` (округление вверх, min 0); null/невалидно — null.
function minutesSince(iso: string | null | undefined, now: number): number | null {
  if (!iso) return null;
  const t = new Date(iso).getTime();
  if (Number.isNaN(t)) return null;
  return Math.max(0, Math.ceil((now - t) / 60000));
}

// Витки-возвраты: цепочки шагов + стадии qa/final. «Принят с замечаниями» — тоже возврат
// (работа вернулась к исполнителю); незакрытый виток (completedAt null) — не флажок.
function collectReturnFlags(run: FeatureRun): GanttReturnFlag[] {
  const flags: GanttReturnFlag[] = [];
  const push = (r: FeatureRound, stepRef: string | null) => {
    if ((r.verdict === 'returned' || r.verdict === 'accepted_with_remarks') && r.completedAt) {
      flags.push({
        at: r.completedAt,
        roundN: r.n,
        who: r.personaId ?? null,
        stepRef,
        reason: r.reason ?? null,
        reviewChatId: r.reviewChatId ?? null,
      });
    }
  };
  for (const chain of run.stepChains ?? []) {
    for (const r of chain.rounds ?? []) push(r, chain.stepRef ?? null);
  }
  for (const s of run.stages ?? []) {
    if (s.key !== 'qa' && s.key !== 'final') continue;
    for (const r of s.rounds ?? []) push(r, null);
  }
  flags.sort((a, b) => new Date(a.at).getTime() - new Date(b.at).getTime());
  return flags;
}

// Модель ступеней Ганта. `now` — текущее время (мс) для длительности пилюли; тесты дают
// фиксированное, в работе — Date.now() на момент построения.
export function buildGanttModel(run: FeatureRun, stats: FeatureStats | null, now: number): GanttModel {
  const closed = run.closed === true;
  const tl = stats?.timeline;

  const axis = tl?.from && tl?.to ? { from: tl.from, to: tl.to } : null;

  // Строки: этапы, которые фича проходила или пройдёт (absent — строки нет).
  // Порядок даёт бэк — по жизненному циклу (cut → … → merge).
  const rows: GanttRow[] = (run.stages ?? [])
    .filter(s => s.state !== 'absent')
    .map(s => {
      // skipped (W4-DES-B): необязательный этап без задач. Явно обнуляем всё, чего
      // «не было»: минуты, границы, сегменты, разбивка — рендер строит строку
      // «этапа не было» и не должен опираться на случайные значения бэка.
      const skipped = s.state === 'skipped';
      const segments: GanttSegment[] = skipped ? [] : (tl?.segments ?? [])
        .filter(seg => seg.stage === s.key)
        .sort((a, b) => new Date(a.from).getTime() - new Date(b.from).getTime())
        .map(seg => ({
          from: seg.from,
          to: seg.to,
          kind: seg.track === 'review' ? 'review' as const : seg.kind,
          minutes: spanMinutes(seg.from, seg.to),
        }));
      const main = segments.filter(g => g.kind !== 'review');
      const sum = (kind: GanttSegmentKind) => main.filter(g => g.kind === kind).reduce((a, g) => a + g.minutes, 0);
      // Все виды main-сегментов названы явно: сумма частей равна длине этапа,
      // error/notWorking не теряются молча.
      const breakdown = main.length === 0
        ? null
        : { work: sum('work'), waiting: sum('waiting'), idle: sum('idle'), error: sum('error'), notWorking: sum('notWorking') };
      return {
        key: s.key,
        label: stageLabel(s.key),
        state: s.state,
        isSkipped: skipped,
        minutes: skipped ? 0 : (s.approxMinutes ?? 0),
        from: skipped ? null : (s.firstStartedAt ?? null),
        to: skipped ? null : (s.lastCompletedAt ?? null),
        segments,
        breakdown,
        isLongest: false,
        isCurrent: false,
        nowPill: null,
        stalledSince: null,
        stalledReason: null,
        stalledActionChatId: null,
        returns: [],
        returnCount: 0,
      };
    });

  // Флажки возвратов — на строке «Производства» (туда возвращается работа). Строки нет —
  // флажки негде ставить; returnTotal остаётся для легенды (честная деградация).
  const flags = collectReturnFlags(run);
  const prod = rows.find(r => r.key === 'prod');
  if (prod) {
    prod.returns = flags;
    prod.returnCount = flags.length;
  }

  // «Сейчас»: первый InProgress-этап (бэк отдаёт now по убыванию старта). У закрытой — пусто.
  const cur = closed ? null : (run.now?.[0] ?? null);
  if (cur) {
    const row = rows.find(r => r.key === cur.stage);
    if (row) {
      row.isCurrent = true;
      row.nowPill = {
        stageKey: row.key,
        stepRef: cur.stepRef ?? null,
        title: cur.title ?? null,
        startedAt: cur.startedAt ?? null,
        durationMinutes: minutesSince(cur.startedAt, now),
      };
    }
  }

  // «Зависла»: только явный stopped (wait — обычное ожидание, без штриховки). Хвост —
  // на строке этапа остановившейся задачи: по attention.taskId (стадия, где она в
  // taskIds, или запись now с этим taskId); стадии нет — фолбэк на текущую строку.
  // Строки нет — хвоста нет.
  const att = closed ? null : (run.attention ?? null);
  let hasStalled = false;
  if (att?.kind === 'stopped' && att.since) {
    let row: GanttRow | undefined;
    if (att.taskId) {
      const taskId = att.taskId; // локальная копия: сужение типа в замыкании теряется
      const stage = (run.stages ?? []).find(s => (s.taskIds ?? []).includes(taskId));
      if (stage) row = rows.find(r => r.key === stage.key);
      if (!row) {
        const nowEntry = (run.now ?? []).find(n => n.taskId === taskId);
        if (nowEntry) row = rows.find(r => r.key === nowEntry.stage);
      }
    }
    if (!row) row = rows.find(r => r.isCurrent);
    if (row) {
      row.stalledSince = att.since;
      row.stalledReason = att.reason ?? null;
      row.stalledActionChatId = att.linkedSessionId ?? null;
      hasStalled = true;
    }
  }

  // «Самый долгий»: максимум approxMinutes среди > 0; при равенстве — первый по порядку;
  // один кандидат — не подсвечиваем (сравнивать не с чем).
  const candidates = rows.filter(r => r.minutes > 0);
  if (candidates.length >= 2) {
    let best = candidates[0]!;
    for (const r of candidates) {
      if (r.minutes > best.minutes) best = r;
    }
    best.isLongest = true;
  }

  return {
    rows,
    axis,
    // hasData — та же семантика, что в FeatureStatsBits: сегменты таймлайна или текущее
    // состояние (у закрытой фичи current не считается). Одна ось без сегментов — не данные.
    hasData: (tl?.segments?.length ?? 0) > 0 || (!closed && tl?.current != null),
    returnTotal: flags.length,
    hasStalled,
    closed,
  };
}
