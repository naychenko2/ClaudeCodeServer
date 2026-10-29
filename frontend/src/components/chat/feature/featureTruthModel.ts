// «Правда о прогрессе» для трёх поверхностей чата (F2, утверждённые макеты v3/v4):
// вторая строка шапки В2 «Трек», плашка панели чатов С1 «Трек снизу», пилюля и попап
// П2 «Мой шаг». ЧИСТАЯ модель: ни DOM, ни запросов, ни новой семантики — всё читается
// из готового run (stepProgress с inMaster, tracker, now, stepChains, epic.note.estimate)
// или из строки индекса (те же числа: бэк «отдаёт то же, что страница фичи»).
//
// Единственная арифметика здесь:
//   • процент по трекеру — общий trackerPercent (lib/featureProgress, та же, что блок
//     «Готовность» на странице фичи: числитель и знаменатель бэковые, фронт только делит);
//   • момент готовности — note.at + etaMinutes (тот же момент, что etaEndsAt блока «Срок»),
//     формат — общий readyAtText.
//
// Чего НЕТ в данных и потому нет в surfaces (не выдумываем):
//   • «Дальше» — поля нет в run (есть только в brief страницы фичи);
//   • «от чего зависит шаг» и «кто ждёт после него» — связей между шагами в run нет
//     (строки Ш7/Ш9 макета); цепочка П2 стоит на том, что есть: сам шаг, его витки,
//     ревью, которое ждёт этот шаг (reviewOf).
//   • «устарелость» оценки (stale) — флаг brief-а, не голоса на wire: без него оценка
//     показывается как написана, со временем записи («N назад») — свежести человек
//     решает сам, ложной метки нет.

import { C } from '../../../lib/design';
import { trackerPercent, type StepProgress } from '../../../lib/featureProgress';
import { roleLabel } from '../../../lib/roleLabel';
import { formatClock, formatDuration, minutesSince } from '../../feature/FeatureStatsBits';
import { readyAtText } from '../../feature/FeatureBriefModel';
import type { FeatureRun, FeatureRoundVerdict, FeatureStepChain } from '../../../types/featureRun';

// Резолвер имени персоны (тот же контракт, что PersonaNameResolver в FeatureBriefModel).
export type NameOf = (personaId: string) => string | null;

// ── Полоса трекера (В2 / С1 / П2 / «По трекеру») ──────────────────────────────────────
// Четыре корзины в порядке: принято, в ревью, в работе, не заведено. Нулевых корзин на
// полосе нет — пустые части скрываются (макет v3 §Состояния). Числа сегментов — бэковые
// stepProgress БЕЗ пересчёта; сумма сегментов == total (корзины взаимоисключающие,
// lib/featureProgress.ts:34-35).
// «в master» — ортогональный бэковый счётчик (FeatureStepAccounting.cs:354,381-395): он
// НЕ вычитается из «принято» и может быть больше accepted (шаг сдан в master без витка
// принятия). На полосе он — подсветка начала принятой части (тот же приём, что полоска
// «Готовности» страницы фичи: FeatureBriefModel.ts:483-491, FeatureBriefBlock.tsx:225):
// длина для рисования masterDraw = min(inMaster, accepted), а РЕАЛЬНОЕ число — в подписи
// и aria (inMaster). Так же живёт страница фичи — здесь то же правило, второй копии нет.
export type TruthSegmentKey = 'accepted' | 'review' | 'work' | 'notCreated';
export interface TruthSegment { key: TruthSegmentKey; count: number; color: string; label: string }
export interface TruthBar {
  percent: number;
  segments: TruthSegment[];
  // Реальный ортогональный счётчик бэка — для подписей и aria (0 — поля нет).
  inMaster: number;
  // Только длина подсветка начала принятой части: min(inMaster, accepted).
  masterDraw: number;
}

export function featureTruthBar(sp: StepProgress | null, inMaster: number | null | undefined): TruthBar | null {
  if (!sp) return null;
  const percent = trackerPercent(sp.accepted, sp.total);
  const inMasterReal = Math.max(0, inMaster ?? 0);
  const segments: TruthSegment[] = [];
  const push = (key: TruthSegmentKey, count: number, color: string, label: string) => {
    if (count > 0) segments.push({ key, count, color, label });
  };
  push('accepted', sp.accepted, C.success, 'принято');
  push('review', sp.review, C.info, 'в ревью');
  push('work', sp.work, C.accent, 'в работе');
  push('notCreated', sp.notCreated, C.progressTrack, 'не заведено');
  return { percent, segments, inMaster: inMasterReal, masterDraw: Math.min(inMasterReal, sp.accepted) };
}

// Строка числа для правого края: «56 % · ≈ 18:30» (без срока — только «56 %»).
// Момент готовности нечитаемый (нет etaMinutes/дата) — срока нет.
export function truthRightText(bar: TruthBar, eta: TruthEstimateView | null): string {
  return bar.percent + ' %' + (eta?.rowEta ? ` · ${eta.rowEta}` : '');
}

// ── Срок по оценке оркестратора (epic.note.estimate) ─────────────────────────────────
export interface TruthEstimateView {
  // Строка: «≈ 18:30» (дальше 12 ч — «≈ 31.10»). Нет etaMinutes — нет срока.
  rowEta: string | null;
  // Плитка «Срок»: главное число «≈ 2 ч 40» и хвост «к 18:30».
  etaMain: string | null;
  readyAt: string | null;
  // Когда оценку написали: «14 мин назад» — человек сам видит свежесть.
  ago: string | null;
  // Кто писал оценку (по флагу byUser голоса): «оценка оркестратора» / «оценка человека».
  // Имени автора на wire нет (FeatureNote его не несёт) — второй копии имени нет.
  sourceLabel: string;
  // Процент оценки: её percent, а без него — доля done/total (тот же trackerPercent).
  percent: number;
  // «Готовность» плитки: «4 готово · 3 в работе · из 9» — как на странице фичи.
  done: number;
  inProgress: number;
  total: number;
}

export function featureEstimateView(run: FeatureRun, nowMs: number): TruthEstimateView | null {
  const note = run.epic?.note;
  const est = note?.estimate;
  if (!est || !(est.total > 0)) return null;
  // Процент оценки: её собственная percent (0 — тоже ответ), без неё — доля done/total.
  const percent = est.percent != null
    ? Math.max(0, Math.min(100, Math.round(est.percent)))
    : trackerPercent(est.done, est.total);
  const etaMin = est.etaMinutes != null && est.etaMinutes >= 0 ? Math.round(est.etaMinutes) : null;
  const atMs = Date.parse(note.at);
  const readyIso = etaMin != null && Number.isFinite(atMs) ? new Date(atMs + etaMin * 60000).toISOString() : null;
  const ready = readyAtText(readyIso, nowMs);
  const ago = agoText(note.at, nowMs);
  return {
    rowEta: readyAtText(readyIso, nowMs, '≈'),
    etaMain: etaMin != null ? (etaMin < 1 ? 'меньше минуты' : `≈ ${formatDuration(etaMin)}`) : null,
    readyAt: ready,
    ago,
    sourceLabel: note.byUser ? 'оценка человека' : 'оценка оркестратора',
    percent,
    done: est.done,
    inProgress: est.inProgress,
    total: est.total,
  };
}

// «N мин/ч назад» — та же шкала, что agoText на странице фичи (полные минуты, «только
// что» меньше минуты). Короткая копия здесь — у страницы свой agoText привязан к её
// форматам обновления; менять их место — не задача F2.
function agoText(iso: string, nowMs: number): string | null {
  const t = Date.parse(iso);
  if (!Number.isFinite(t)) return null;
  const m = Math.max(0, Math.floor((nowMs - t) / 60000));
  if (m < 1) return 'только что';
  if (m < 60) return `${m} мин назад`;
  if (m < 24 * 60) return `${Math.floor(m / 60)} ч назад`;
  return null;
}

// ── Тревога: ОДНИ слова для строки В2 и попапа П2 ───────────────────────────────────
// «ждёт вас»/«ждёт согласия» — warn, «стоит» — danger (тоны В2). Десктоп — с причиной,
// мобила — с именем (причина на 360 не помещается). Имена резолвит вызывающий.
export interface AlarmView { tone: 'warn' | 'stop'; text: string; actionLabel: string }

export function alarmView(state: string, reason: string | null, personName: string | null, isMobile: boolean): AlarmView {
  if (state === 'waiting') {
    const text = isMobile
      ? (personName ? `Ждёт вас · ${personName}` : 'Ждёт вас')
      : `Ждёт вас · ${reason ?? personName ?? 'нужен ваш ответ'}`;
    return { tone: 'warn', text, actionLabel: personName && !isMobile ? `Ответить ${personName}` : 'Ответить' };
  }
  if (state === 'merge_pending') return { tone: 'warn', text: 'Ждёт согласия на мерж', actionLabel: 'К мержу' };
  return { tone: 'stop', text: personName ? `Стоит · ${personName}` : 'Стоит', actionLabel: personName && !isMobile ? `Чат ${personName}` : 'Чат' };
}

// ── «Сейчас»: строки из run.now (список бэка, порядок бэка) ──────────────────────────
export interface TruthNowRow {
  taskId: string;
  name: string | null;
  // «{stepRef} · {title}» для шага; без stepRef (ревью/проверка/финал) — заголовок задачи.
  what: string;
  // «24 мин» — от старта задачи до сейчас; нет старта или меньше минуты — нет времени.
  time: string | null;
  isThisChat: boolean;
}

export function featureNowRows(run: FeatureRun, sessionId: string | null, nameOf: NameOf, nowMs: number): TruthNowRow[] {
  return (run.now ?? []).map(n => {
    const dur = n.startedAt ? minutesSince(n.startedAt, nowMs) : null;
    return {
      taskId: n.taskId,
      name: n.personaId ? nameOf(n.personaId) : null,
      what: n.stepRef ? `${n.stepRef} · ${n.title}` : n.title,
      time: dur != null && dur > 0 ? formatDuration(dur) : null,
      isThisChat: n.linkedSessionId != null && n.linkedSessionId === sessionId,
    };
  });
}

// ── «Трекер расходится»: перевод видов FeatureTracker.cs в слова ──────────────────────
// Неизвестный вид (более новый бэк) — слово как есть: строка не исчезает, расхождение
// показывается (тот же приём, что stageWord для невиданной стадии).
const TRACKER_KIND_TEXT: Record<string, string> = {
  unreviewed: 'сдан без ревью',
  returned_no_rework: 'возврат без доработки',
  dead_verdict: 'вердикт, которого уже не нужно',
  reported_open: 'отчёт без закрытия задачи',
  epic_open_after_merge: 'мерж закрыт, эпик открыт',
  orphans_in_closed: 'открытые задачи в закрытой фиче',
};

export interface TruthTrackerIssue { text: string; since: string | null }

export function featureTrackerIssues(run: FeatureRun): TruthTrackerIssue[] {
  return (run.tracker ?? []).map(t => {
    const what = TRACKER_KIND_TEXT[t.kind] ?? t.kind;
    return { text: t.stepRef ? `${what} — шаг ${t.stepRef}` : what, since: t.since ?? null };
  });
}

// ── Шаг этого чата (П2) ───────────────────────────────────────────────────────────────
// Цепочка, чьи taskIds содержат задачу-владельца чата (Session.TaskId). null — шага нет
// (оркестратор, ревью, свободный чат): пилюля «фича · %», попап — сводка (П1).
export function findChatChain(run: FeatureRun, chatTaskId: string | null | undefined): FeatureStepChain | null {
  if (!chatTaskId) return null;
  return (run.stepChains ?? []).find(c => (c.taskIds ?? []).includes(chatTaskId)) ?? null;
}

// Тон плашки статуса шага: принят — success, «ждёт ревью» — info, снят — neutral,
// работа — accent (макет v4 .chip.a/.r/.w). Слова — бэковые (chain.status), второй
// копии слов нет.
export type StepBadgeTone = 'accent' | 'info' | 'success' | 'neutral';

export function stepBadgeTone(chain: FeatureStepChain): StepBadgeTone {
  if (chain.outcome === 'accepted') return 'success';
  if (chain.status === 'ждёт ревью') return 'info';
  if (chain.outcome === 'withdrawn') return 'neutral';
  return 'accent';
}

// Статус шага со временем — ЧАСТЯМИ (плашба карточки печатает целым, пилюля — с
// разделителем): «идёт» · «24 мин» (длительность бэка: у идущей — до сейчас),
// «ждёт ревью» · «8 мин» (waitingSince), «принят» «13:40» (acceptedAt, без разделителя —
// «принят 13:40» читается одним фактом). Без времени — только слово (слов — бэковых).
export interface StepStatusParts { word: string; time: string | null; sep: string }

export function stepStatusParts(chain: FeatureStepChain, nowMs: number): StepStatusParts {
  if (chain.outcome === 'accepted') {
    return { word: 'принят', time: chain.acceptedAt ? formatClock(chain.acceptedAt) : null, sep: ' ' };
  }
  if (chain.status === 'ждёт ревью') {
    const w = chain.waitingSince ? minutesSince(chain.waitingSince, nowMs) : null;
    return { word: 'ждёт ревью', time: w != null && w > 0 ? formatDuration(w) : null, sep: ' · ' };
  }
  const d = chain.durationMinutes;
  return { word: chain.status, time: d != null && d > 0 ? formatDuration(d) : null, sep: ' · ' };
}

export function stepStatusText(chain: FeatureStepChain, nowMs: number): string {
  const p = stepStatusParts(chain, nowMs);
  return p.time ? `${p.word}${p.sep}${p.time}` : p.word;
}

// Строка цепочки шага: точка цвета корзины, текст, время справа.
export interface TruthChainRow {
  id: string;
  color: string;
  text: string;
  time: string | null;
}

// Подпись вердикта витка — те же слова, что «Хронология» (roundLabel, FeatureProdCards.ts).
const VERDICT_WORD: Record<FeatureRoundVerdict, string> = {
  returned: 'вернул',
  accepted_with_remarks: 'принят с замечаниями',
  accepted: 'принят',
  withdrawn: 'снят',
  running: 'идёт',
};

// Роль этого чата по его задаче (Session.TaskId): подпись roleLabel по роли задачи
// (тот же словарь, что «этот чат — …» в строке и на странице). Задачи нет в run (или
// чат без задачи) — ''.
export function chatRoleFromRun(run: FeatureRun, chatTaskId: string | null | undefined): string {
  if (!chatTaskId) return '';
  const all = [
    run.epic, ...run.steps, ...(run.cut ?? []), ...(run.plan ?? []),
    ...run.reviews, ...run.qa, run.final, run.merge,
  ].filter((t): t is NonNullable<typeof t> => t != null);
  const t = all.find(x => x.id === chatTaskId);
  return t ? roleLabel(t.role ?? null, t.stepRef ?? null, t.lane ?? null) : '';
}

// Цепочка шага для попапа П2: сам шаг → его витки (кто, исход, когда) → ревью/проверка/
// финал, которые жДУТ этот шаг (reviewOf ∩ задачи цепочки, незакрытые — «после сдачи»).
export function featureStepChainRows(run: FeatureRun, chain: FeatureStepChain, nameOf: NameOf): TruthChainRow[] {
  const rows: TruthChainRow[] = [];
  rows.push({
    id: `step-${chain.stepRef}`,
    color: chain.outcome === 'accepted' ? C.success : chain.status === 'ждёт ревью' ? C.info : chain.outcome === 'withdrawn' ? C.textMuted : C.accent,
    text: `${chain.stepRef} · ${chain.title} — ${chain.status}`,
    time: null,
  });
  for (const r of chain.rounds ?? []) {
    const name = r.personaId ? nameOf(r.personaId) : null;
    const verb = VERDICT_WORD[r.verdict] ?? r.verdict;
    rows.push({
      id: r.reviewTaskId ?? `round-${r.n}`,
      color: r.verdict === 'accepted' ? C.success : r.verdict === 'running' ? C.accent : C.warning,
      text: `виток ${r.n} — ${name ? `${name}, ${verb}` : verb}`,
      time: r.completedAt ? formatClock(r.completedAt) : null,
    });
  }
  const taskIds = new Set(chain.taskIds ?? []);
  const verdictTasks = [...(run.reviews ?? []), ...(run.qa ?? []), ...(run.final ? [run.final] : [])];
  for (const t of verdictTasks) {
    if (t.status === 'done') continue;
    if (!(t.reviewOf ?? []).some(id => taskIds.has(id))) continue;
    const name = t.personaId ? nameOf(t.personaId) : null;
    const role = roleLabel(t.role ?? null, null, null) || 'проверка';
    rows.push({
      id: t.id,
      color: t.status === 'inProgress' ? C.accent : C.progressTrack,
      text: `${role}${name ? ` — ${name}` : ''}, после сдачи`,
      time: null,
    });
  }
  return rows;
}
