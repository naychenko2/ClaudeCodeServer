// Карточка «ход выкатки прода» в ленте чата (ADR-010, макет
// docs/mockups/deploy-progress-card-v1.html).
//
// Носитель — спец-рендер существующего tool_use `mcp__wsp__deploy_start`, тем же приёмом,
// что WorkflowBlockView для `workflow`: нового типа записи в ленте нет, бэкенд не менялся,
// и карточка переживает перезагрузку бесплатно — вызов инструмента уже лежит в транскрипте.
//
// Карточка — НАБЛЮДАТЕЛЬНЫЙ ПРИБОР: кнопок-действий в ней нет ни в одном состоянии.
// Переезд на новую версию делается сам (вкладкой-инициатором), откат при лежащем проде
// слать некому, отмены в ADR-010 не существует. Разворачивание шагов — не действие.
//
// Три вещи, на которых держится правдивость:
//  1. Опрос строго live (мимо офлайн-кэша): подставленный прошлый ответ выдавал бы
//     погашенный сервер за живой — ровно та ошибка, на которой трижды подрывалась DeployModal.
//  2. Провал запроса в переключении/проверке — НЕ ошибка, а ожидаемое состояние: сервер
//     остановлен намеренно. Красной карточки по обрыву связи не бывает.
//  3. Пока выкатка идёт — setDeployInProgress(true): индикатор связи и плашка обновления
//     говорят правду, но сейчас она не адресована человеку, который и так смотрит сюда.

import { memo, useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  Activity, Check, CheckCircle2, ChevronDown, ChevronUp, Hourglass,
  Package, Power, Undo2, X, XCircle,
} from 'lucide-react';
import { api, type DeployJournalRecord } from '../../lib/api';
import { C, FONT, FS, R, SHADOW, SP } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';
import { Badge, type BadgeTone } from '../ui/Badge';
import { useIsMobile } from '../../lib/breakpoints';
import { setDeployInProgress } from '../../lib/deployState';
import { applyUpdateAndReload } from '../../lib/swUpdate';
import { ToolUseView, type ToolUseItem } from './ToolUseView';
import {
  buildPlan, buildStepRows, clearInitiator, clearWatch, clockLabel, durLabel, etaLabel,
  failedStep, GROUP_ORDER, GROUPS, groupMs, isInitiator, isTerminalPhase, markInitiator,
  deriveState, msBeforeGroup, parseDeployId, parseTime, phaseGroup, readWatch, sane,
  switchingHint, writeWatch, type CardState, type DeployGroup, type StepRow,
} from '../../lib/deployProgress';

// Опрос журнала. Три секунды — и пока сервер жив (шаги идут минутами, чаще незачем),
// и пока он мёртв: карточка обещает человеку «проверяю связь каждые 3 секунды».
const POLL_MS = 3000;
// Сколько держится строка «Сервер вернулся». Без отдельного такта момент возвращения
// проскакивает незамеченным — а это лучшая новость за последние полторы минуты.
const BACK_MS = 3000;
// Пауза перед автопереездом на новую версию: успеть прочитать, что происходит
const HANDOVER_MS = 2000;

const BADGE: Record<Exclude<CardState, 'loading'>, { tone: BadgeTone; icon: React.ReactNode; text: string }> = {
  queued: { tone: 'neutral', icon: <Hourglass size={11} strokeWidth={ICON_STROKE} />, text: 'В очереди' },
  building: { tone: 'accent', icon: <Package size={11} strokeWidth={ICON_STROKE} />, text: 'Собираю' },
  switching: { tone: 'info', icon: <Power size={11} strokeWidth={ICON_STROKE} />, text: GROUPS.switching.short },
  verifying: { tone: 'accent', icon: <Activity size={11} strokeWidth={ICON_STROKE} />, text: GROUPS.verifying.short },
  dead: { tone: 'info', icon: <Power size={11} strokeWidth={ICON_STROKE} />, text: 'Сервер перезапускается' },
  succeeded: { tone: 'success', icon: <CheckCircle2 size={11} strokeWidth={ICON_STROKE} />, text: 'Прод обновлён' },
  rolled_back: { tone: 'warning', icon: <Undo2 size={11} strokeWidth={ICON_STROKE} />, text: 'Вернул прежнюю версию' },
  failed: { tone: 'danger', icon: <XCircle size={11} strokeWidth={ICON_STROKE} />, text: 'Выкатка не удалась' },
};

// Цвет ребра карточки — тем же тоном, что и плашка состояния
const RIB: Record<CardState, string> = {
  loading: C.border, queued: C.border, building: C.accent, switching: C.info,
  verifying: C.accent, dead: C.info, succeeded: C.success, rolled_back: C.warning, failed: C.danger,
};

function HeadIcon({ state }: { state: CardState }) {
  const p = { size: ICON_SIZE.sm, strokeWidth: ICON_STROKE };
  switch (state) {
    case 'building': return <Package {...p} />;
    case 'switching': case 'dead': return <Power {...p} />;
    case 'verifying': return <Activity {...p} />;
    case 'succeeded': return <CheckCircle2 {...p} />;
    case 'rolled_back': return <Undo2 {...p} />;
    case 'failed': return <XCircle {...p} />;
    default: return <Hourglass {...p} />;
  }
}

// Тик секундомера: пока выкатка идёт, «идёт 3:07» обязано двигаться
function useNow(active: boolean): number {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    if (!active) return;
    const id = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(id);
  }, [active]);
  return now;
}

interface Props {
  item: ToolUseItem;
  // Чат, в котором стоит карточка: сам собой на новую версию переезжает только тот,
  // кто эту выкатку и заказывал (см. markInitiator)
  sessionId?: string;
  online?: boolean;
  onOpenFile?: (path: string) => void;
}

export const DeployProgressCard = memo(function DeployProgressCard({ item, sessionId, online, onOpenFile }: Props) {
  const isMobile = useIsMobile();
  const deployId = useMemo(() => parseDeployId(item.result), [item.result]);

  const [record, setRecord] = useState<DeployJournalRecord | null>(null);
  const [history, setHistory] = useState<DeployJournalRecord[]>([]);
  const [reachable, setReachable] = useState(true);
  const [loaded, setLoaded] = useState(false);
  // Журнал прочитан, а записи в нём нет: выкатка старше 30 хранимых или контур выключен
  const [missing, setMissing] = useState(false);
  const [backAt, setBackAt] = useState<number | null>(null);
  // Когда сервер перестал отвечать — по этой точке идёт счётчик «не отвечает 1:04»
  const [deadSince, setDeadSince] = useState<number | null>(null);
  const [handover, setHandover] = useState(false);
  const [open, setOpen] = useState(false);
  // Свёрнутую карточку истории человек может развернуть — это не действие над выкаткой
  const [expanded, setExpanded] = useState(false);
  const [showMessage, setShowMessage] = useState(false);
  // Метка мёртвого окна: страницу перезагрузили, пока сервера нет, — журнал не прочитать,
  // и единственное, что мы знаем о выкатке, лежит в localStorage
  const [watch] = useState(() => (deployId ? readWatch() : null));
  const restored = !!watch && watch.deployId === deployId;

  const doneRef = useRef(false);       // терминальный итог получен — опрос больше не нужен
  const ownsFlagRef = useRef(false);   // именно эта карточка подняла setDeployInProgress
  const firstSeenRef = useRef<boolean | null>(null); // была ли выкатка терминальной уже на первом чтении

  // === Опрос журнала ===
  const poll = useCallback(async () => {
    if (!deployId) return;
    try {
      const j = await api.deployJournal.status();
      setReachable(true);
      setHistory(j.history ?? []);
      const rec = j.current?.id === deployId
        ? j.current
        : (j.history ?? []).find(r => r.id === deployId) ?? null;
      if (rec) { setRecord(rec); setMissing(false); }
      else setMissing(true);
      setLoaded(true);
    } catch (e) {
      // Сервер ОТВЕТИЛ отказом (403 не админу, 404, 500) — он жив, просто журнал нам не
      // отдают. Мёртвым его делает только отсутствие ответа: без статуса у ошибки.
      const status = (e as { status?: number }).status;
      if (status) { setReachable(true); setMissing(true); }
      else setReachable(false);
      setLoaded(true);
    }
  }, [deployId]);

  useEffect(() => {
    if (!deployId) return;
    void poll();
    const id = setInterval(() => {
      if (doneRef.current) { clearInterval(id); return; }
      void poll();
    }, POLL_MS);
    return () => clearInterval(id);
  }, [deployId, poll]);

  // === Состояние карточки ===
  const phase = record?.phase ?? (restored ? watch!.phase : null);
  const result = record?.result ?? null;
  const terminal = !!result || isTerminalPhase(phase);
  const running = !!deployId && !terminal && (!!record || restored);

  // Что показываем — решает одна чистая функция (lib/deployProgress): там же под тестом
  // лежит главный инвариант — обрыв связи не превращается в красную карточку
  const state = deriveState({ hasDeployId: !!deployId, phase, result, loaded, restored, reachable });

  const now = useNow(running || state === 'loading');

  // Признак «выкатку заказали в этой вкладке» — sessionStorage, он на вкладку и заведён.
  // Ставим, пока выкатка ЖИВА и стоит в чате-инициаторе: только такой вкладке уместно
  // переезжать на новую версию самой.
  useEffect(() => {
    if (!deployId || terminal || !record) return;
    const initiator = record.initiatedBy?.sessionId;
    if (initiator && sessionId && initiator !== sessionId) return;
    markInitiator(deployId);
  }, [deployId, terminal, record, sessionId]);

  // Пока выкатка идёт, индикатор связи и плашка обновления молчат. Флаг снимает та же
  // карточка, что его подняла: в ленте их может быть несколько, и чужая терминальная
  // карточка не должна гасить флаг живой.
  useEffect(() => {
    if (running) { ownsFlagRef.current = true; setDeployInProgress(true); }
    else if (ownsFlagRef.current) { ownsFlagRef.current = false; setDeployInProgress(false); }
  }, [running]);
  useEffect(() => () => { if (ownsFlagRef.current) { ownsFlagRef.current = false; setDeployInProgress(false); } }, []);

  // Метка мёртвого окна: ставим на входе в переключение (и когда связь уже пропала),
  // снимаем на терминальном итоге — вместе с признаком инициатора
  useEffect(() => {
    if (!deployId) return;
    if (running && (phase === 'switching' || phase === 'verifying' || !reachable)) {
      writeWatch({ deployId, startedAt: record?.startedAt ?? watch?.startedAt ?? null, phase: phase ?? 'switching' });
    }
    if (terminal) { doneRef.current = true; clearWatch(deployId); }
  }, [deployId, running, terminal, phase, reachable, record?.startedAt, watch?.startedAt]);

  // «Сервер вернулся» — отдельный такт на три секунды после возвращения связи: без него
  // момент возвращения проскакивает незамеченным, а это лучшая новость за последние
  // полторы минуты. Заодно отсюда ведётся счётчик «не отвечает M:SS».
  const wasDeadRef = useRef(false);
  useEffect(() => {
    if (!running) return;
    if (!reachable) {
      wasDeadRef.current = true;
      setBackAt(null);
      setDeadSince(v => v ?? Date.now());
      return;
    }
    setDeadSince(null);
    if (wasDeadRef.current) { wasDeadRef.current = false; setBackAt(Date.now()); }
  }, [reachable, running]);
  const back = backAt !== null && now - backAt < BACK_MS;

  // Успех: вкладка-инициатор переезжает на новую версию САМА, без кнопки. Обычный reload
  // под service worker вернул бы тот же старый бандл — тот самый, который только что
  // заменили. В остальных вкладках придёт штатная плашка обновления.
  useEffect(() => {
    if (!deployId || state !== 'succeeded') return;
    if (!isInitiator(deployId)) return;
    clearInitiator(deployId);
    setHandover(true);
    const id = setTimeout(() => { void applyUpdateAndReload(); }, HANDOVER_MS);
    return () => clearTimeout(id);
  }, [deployId, state]);
  useEffect(() => { if (deployId && state === 'rolled_back') clearInitiator(deployId); }, [deployId, state]);
  useEffect(() => { if (deployId && state === 'failed') clearInitiator(deployId); }, [deployId, state]);

  // Первое чтение застало выкатку уже завершённой — она из истории ленты, а не событие
  // этой минуты: такая карточка монтируется свёрнутой в одну строку
  if (firstSeenRef.current === null && (loaded || restored)) firstSeenRef.current = terminal;
  const collapsed = terminal && firstSeenRef.current === true && !expanded;

  const plan = useMemo(() => buildPlan(history), [history]);
  const rows = useMemo(() => buildStepRows(record, plan, running), [record, plan, running]);

  // Вызов отказом (409/400/503) — deployId нет, следить не за чем: обычный блок
  // инструмента покажет текст отказа как есть
  if (item.result !== undefined && !deployId) {
    return <ToolUseView item={item} online={online} onOpenFile={onOpenFile} />;
  }
  // Журнал прочитан, записи нет и она не терминальная — выкатка вытеснена из истории
  // (журнал держит 30) либо контур выключен. Врать про ход нечем
  if (missing && !record && !restored) {
    return <ToolUseView item={item} online={online} onOpenFile={onOpenFile} />;
  }

  const startedAt = parseTime(record?.startedAt ?? watch?.startedAt ?? null);
  const finishedAt = parseTime(result?.finishedAt ?? null);
  const elapsed = startedAt !== null
    ? Math.max(0, (terminal && finishedAt !== null ? finishedAt : now) - startedAt)
    : 0;
  const totalMs = plan?.totalMs ?? 0;
  const doneMs = rows.reduce((a, r) => a + (r.ms ?? 0), 0);
  const pct = totalMs > 0
    ? (state === 'succeeded' || state === 'rolled_back'
        ? 100
        : terminal
          ? Math.min(100, Math.round(doneMs / totalMs * 100))
          : Math.min(92, Math.round(elapsed / totalMs * 100)))
    : 0;

  const curGroup: DeployGroup = state === 'dead'
    ? (phase === 'verifying' ? 'verifying' : 'switching')
    : phaseGroup(phase ?? 'queued');
  const groupElapsed = Math.max(0, elapsed - msBeforeGroup(rows, curGroup));
  const deadElapsed = deadSince !== null ? Math.max(0, now - deadSince) : 0;

  const kindRollback = record?.kind === 'rollback';
  const title = kindRollback ? 'Откат прода' : 'Выкатка на бой';

  if (collapsed) {
    return (
      <CollapsedCard
        state={state} title={title} record={record} elapsed={elapsed}
        onExpand={() => setExpanded(true)}
      />
    );
  }

  const badge = state === 'loading' ? null : BADGE[state];
  const stepInProgress = rows.find(r => r.status === 'run');
  const fail = terminal && state !== 'succeeded' ? failedStep(rows) : null;

  return (
    <div style={{
      // Рамка/фон/ребро — как у семейства карточек ленты (DelegationReportCard и соседи):
      // карточка выкатки в него встраивается, а не выделяется
      border: `1px solid ${C.borderLight}`, borderLeft: `3px solid ${RIB[state]}`,
      borderRadius: R.xl, background: C.bgWhite, boxShadow: SHADOW.card,
      overflow: 'hidden', maxWidth: '100%',
    }}>
      {/* Шапка: значок состояния, название, плашка итога, техподпись отдельной строкой */}
      <div style={{
        display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap', rowGap: SP.xs,
        padding: `${SP.sm}px ${SP.md}px`, borderBottom: `1px solid ${C.divider}`,
      }}>
        <span aria-hidden style={{ display: 'flex', flexShrink: 0, color: C.textSecondary }}>
          <HeadIcon state={state} />
        </span>
        <span style={{
          flex: isMobile ? '1 0 100%' : 1, minWidth: 0, fontSize: FS.base, fontWeight: 600,
          color: C.textHeading, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
        }}>
          {title}
        </span>
        {badge && <Badge tone={badge.tone} icon={badge.icon}>{badge.text}</Badge>}
        <Meta record={record} state={state} elapsed={terminal ? elapsed : null} />
      </div>

      {/* Тело */}
      <div style={{ padding: SP.md, display: 'flex', flexDirection: 'column', gap: SP.sm }}>
        {state === 'loading' && (
          <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, fontSize: FS.sm, color: C.textMuted }}>
            <span className="tool-spinner" style={{ width: 11, height: 11 }} />
            Читаю журнал выкатки…
          </div>
        )}

        {/* Бар прогресса. Истории нет — бара нет вовсе: пустой прогноз честнее выдуманного.
            Локальный, не общий примитив: восемь рукописных копий такого бара по проекту
            выносятся в ui/MeterBar отдельной работой, тащить её сюда незачем */}
        {state !== 'loading' && state !== 'queued' && totalMs > 0 && (
          <ProgressBar
            pct={pct} state={state} estimate={state === 'dead'}
            right={running ? runningHint(state, elapsed, totalMs) : ''}
          />
        )}

        {/* Четыре строки фаз — только пока выкатка идёт. Ведутся по phase журнала,
            а не угадываются по именам шагов */}
        {running && state !== 'loading' && (
          <PhaseRows
            curGroup={curGroup} state={state} rows={rows} isMobile={isMobile}
            elapsed={elapsed} groupElapsed={groupElapsed}
          />
        )}

        {state === 'queued' && (
          <div style={{ fontSize: FS.sm, color: C.textSecondary, lineHeight: 1.5 }}>
            Заявка ушла агенту планировщика. Обычно он приступает за 10–20 секунд.
          </div>
        )}

        {(state === 'building' || state === 'switching' || state === 'verifying') && (
          <>
            <div style={{ fontSize: FS.sm, color: C.textSecondary, lineHeight: 1.5 }}>
              Сейчас:{' '}
              <b style={{ color: C.textPrimary, fontWeight: 600 }}>
                {stepInProgress ? (stepInProgress.label ?? stepInProgress.name) : '—'}
              </b>
            </div>
            {state === 'building' && (
              <div style={{ fontSize: FS.sm, color: C.textMuted, lineHeight: 1.5 }}>
                Прод пока работает — остановлю только на переключении.
              </div>
            )}
          </>
        )}

        {/* ЦЕНТРАЛЬНОЕ состояние: сервера нет, и это часть выкатки. Тон info, не danger */}
        {state === 'dead' && (
          <>
            <div style={{
              background: C.infoBg, borderRadius: R.lg, padding: `${SP.sm}px ${SP.md}px`,
              display: 'flex', flexDirection: 'column', gap: SP.xs,
            }}>
              <div style={{ fontSize: FS.base, color: C.textPrimary, lineHeight: 1.5 }}>
                Сервер остановлен — идёт подмена сборки. Связи с ним сейчас нет, и это часть
                выкатки, а не сбой.
              </div>
              <div style={{ fontSize: FS.base, color: C.textPrimary, lineHeight: 1.5 }}>
                Как только он поднимется, я дочитаю журнал и покажу итог здесь же.
              </div>
              <div style={{ fontSize: FS.sm, color: C.textSecondary, lineHeight: 1.5 }}>
                Не отвечает <span style={{ fontFamily: FONT.mono }}>{clockLabel(deadElapsed)}</span>
                {plan && plan.deadMs > 0 && ` · обычно около ${clockLabel(plan.deadMs)}`}
                <br />Проверяю связь каждые 3 секунды
              </div>
            </div>
            <div style={{ fontSize: FS.sm, color: C.textMuted, lineHeight: 1.6 }}>
              <span style={{ display: 'block', color: C.textSecondary, marginBottom: 2 }}>
                Что сейчас делает агент (по прошлым выкаткам):
              </span>
              {switchingHint(plan)}
            </div>
          </>
        )}

        {back && running && state !== 'dead' && (
          <div style={{ display: 'flex', alignItems: 'center', gap: SP.xs, fontSize: FS.sm, color: C.successText, fontWeight: 600 }}>
            <Check size={ICON_SIZE.xs} strokeWidth={2.5} />
            Сервер вернулся
          </div>
        )}

        {state === 'succeeded' && (
          <>
            <div style={{ fontSize: FS.base, color: C.textPrimary, lineHeight: 1.5, wordBreak: 'break-word' }}>
              Новая версия поднялась и отвечает.
            </div>
            {handover && (
              <>
                <div style={{ display: 'flex', alignItems: 'center', gap: SP.xs, fontSize: FS.sm, color: C.textSecondary, fontWeight: 600 }}>
                  <span className="tool-spinner" style={{ width: 11, height: 11 }} />
                  Перехожу на новую версию — страница сейчас перезагрузится.
                </div>
                <div style={{ fontSize: FS.sm, color: C.textMuted, lineHeight: 1.5 }}>
                  Переезжает только эта вкладка: выкатку заказывали из неё. В остальных придёт
                  обычная плашка обновления.
                </div>
              </>
            )}
          </>
        )}

        {state === 'rolled_back' && (
          <div style={{ fontSize: FS.base, color: C.textPrimary, lineHeight: 1.5, wordBreak: 'break-word' }}>
            Новая сборка не прошла проверку здоровья, и агент вернул предыдущий релиз.
            Прод работает — на прежней версии.
          </div>
        )}

        {state === 'failed' && (
          <>
            <div style={{ fontSize: FS.base, color: C.textPrimary, lineHeight: 1.5, wordBreak: 'break-word' }}>
              {afterSwap(rows)
                ? 'Прод переключён на новую версию, но она не отвечает, и вернуть предыдущую автоматически не вышло. Дальше нужен человек.'
                : 'До переключения не дошло — на бою прежняя версия, простоя не было.'}
            </div>
            {afterSwap(rows) && (
              <div style={{ fontSize: FS.sm, color: C.textMuted, lineHeight: 1.5 }}>
                Откатить отсюда нельзя — запрос ушёл бы тому же лежащему серверу. Возврат на
                прежний релиз делается через трей на машине прода.
              </div>
            )}
          </>
        )}

        {fail && (
          <div style={{ fontSize: FS.sm, color: C.textMuted, lineHeight: 1.5 }}>
            Упало на шаге: {fail.label ?? fail.name}
            {fail.label && <> · <span style={{ fontFamily: FONT.mono }}>{fail.name}</span></>}
            {fail.ms !== null && ` · ${durLabel(fail.ms)}`}
          </div>
        )}

        {/* Сырое слово агента: в тексте состояния его нет — там человеческая формулировка,
            а здесь то, что реально написано в журнале */}
        {terminal && state !== 'succeeded' && result?.message && (
          <>
            <button
              type="button" onClick={() => setShowMessage(v => !v)}
              style={{
                display: 'flex', alignItems: 'center', gap: SP.xs, background: 'none', border: 'none',
                padding: 0, minHeight: 24, font: 'inherit', fontSize: FS.xs, color: C.textMuted,
                cursor: 'pointer', textAlign: 'left',
              }}
            >
              {showMessage
                ? <ChevronUp size={12} strokeWidth={ICON_STROKE} />
                : <ChevronDown size={12} strokeWidth={ICON_STROKE} />}
              Что сказал агент
            </button>
            {showMessage && (
              <div style={{
                paddingLeft: 18, fontSize: FS.sm, color: C.textMuted,
                lineHeight: 1.5, wordBreak: 'break-word',
              }}>
                {result.message}
              </div>
            )}
          </>
        )}
      </div>

      {/* Шаги агента: их семнадцать, в ленте им не место — но по сырому имени человек
          ищет шаг в логе, поэтому они здесь, за разворачиванием */}
      {state !== 'loading' && state !== 'queued' && rows.length > 0 && (
        <>
          <button
            type="button" onClick={() => setOpen(v => !v)}
            style={{
              display: 'flex', alignItems: 'center', gap: SP.xs, width: '100%',
              background: 'none', border: 'none', padding: `${SP.sm}px ${SP.md}px`,
              minHeight: isMobile ? 40 : 32, font: 'inherit', fontSize: FS.xs,
              color: C.textMuted, cursor: 'pointer', textAlign: 'left',
            }}
          >
            {open
              ? <ChevronUp size={12} strokeWidth={ICON_STROKE} />
              : <ChevronDown size={12} strokeWidth={ICON_STROKE} />}
            {open ? 'Скрыть шаги' : 'Показать шаги'}
          </button>
          {open && (
            <div style={{ padding: `0 ${SP.md}px 10px 36px`, display: 'flex', flexDirection: 'column' }}>
              {rows.map(r => <StepRowView key={r.name} row={r} isMobile={isMobile} />)}
            </div>
          )}
        </>
      )}
    </div>
  );
});

// «идёт 3:07 · ≈ 2 мин» — секундомер и прогноз по прошлым выкаткам
function runningHint(state: CardState, elapsed: number, totalMs: number): string {
  const base = `идёт ${clockLabel(elapsed)}`;
  if (state === 'queued') return base;
  if (state === 'dead') return `${base} · по оценке`;
  if (elapsed > totalMs) return `${base} · дольше обычного`;
  return `${base} · ${etaLabel(Math.max(0, totalMs - elapsed))}`;
}

// Провал случился уже после подмены сборки — тогда прод лежит и нужен человек
function afterSwap(rows: StepRow[]): boolean {
  const swapAt = rows.findIndex(r => r.name === 'swap');
  if (swapAt < 0) return false;
  const failAt = rows.findIndex(r => r.status === 'fail');
  return rows[swapAt].status === 'done' && (failAt < 0 || failAt > swapAt);
}

// Техподпись: ветка, коммит, грязное дерево — по ней потом восстанавливают,
// что за код уехал на бой
function Meta({ record, state, elapsed }: { record: DeployJournalRecord | null; state: CardState; elapsed: number | null }) {
  if (!record) return null;
  const parts: React.ReactNode[] = [];
  const sep = (i: number) => <span key={`s${i}`} style={{ opacity: 0.6, padding: '0 2px' }}>·</span>;
  if (record.ref) parts.push(<span key="ref">{record.ref}</span>);
  if (record.sha) parts.push(<span key="sha">{record.sha.slice(0, 7)}</span>);
  const dirty = record.dirtyFiles?.length ?? 0;
  if (dirty > 0 && (state === 'queued' || state === 'building')) {
    parts.push(<span key="dirty">{dirty} {filesWord(dirty)}</span>);
  }
  const total = sane(elapsed);
  if (total !== null && total > 0) parts.push(<span key="dur">заняло {durLabel(total)}</span>);
  if (parts.length === 0) return null;

  return (
    <span style={{
      width: '100%', fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted,
      wordBreak: 'break-word',
    }}>
      {parts.flatMap((p, i) => (i === 0 ? [p] : [sep(i), p]))}
    </span>
  );
}

function filesWord(n: number): string {
  const t = n % 100;
  if (t >= 11 && t <= 14) return 'незакоммиченных файлов';
  const d = n % 10;
  if (d === 1) return 'незакоммиченный файл';
  if (d >= 2 && d <= 4) return 'незакоммиченных файла';
  return 'незакоммиченных файлов';
}

// Тонкий бар прогресса — локальный: общий примитив ui/MeterBar выносится отдельной
// работой вместе с восемью уже существующими рукописными копиями
function ProgressBar({ pct, state, estimate, right }: { pct: number; state: CardState; estimate: boolean; right: string }) {
  const fill = state === 'succeeded' ? C.success
    : state === 'rolled_back' ? C.warning
      : state === 'failed' ? C.danger
        : estimate ? C.accentSoft : C.accent;
  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap', rowGap: SP.xs }}>
      <span style={{
        flex: 1, minWidth: 120, height: 4, borderRadius: R.max,
        background: C.borderLight, overflow: 'hidden',
      }}>
        <span style={{
          display: 'block', height: '100%', width: `${Math.max(0, Math.min(100, pct))}%`,
          borderRadius: R.max, background: fill, transition: 'width .3s ease',
        }} />
      </span>
      {right && (
        <span style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted, whiteSpace: 'nowrap' }}>
          {right}
        </span>
      )}
    </div>
  );
}

// Четыре строки фаз журнала
function PhaseRows({ curGroup, state, rows, isMobile, elapsed, groupElapsed }: {
  curGroup: DeployGroup; state: CardState; rows: StepRow[]; isMobile: boolean;
  elapsed: number; groupElapsed: number;
}) {
  const ci = GROUP_ORDER.indexOf(curGroup);
  return (
    <div style={{ display: 'flex', flexDirection: 'column' }}>
      {GROUP_ORDER.map((key, i) => {
        const g = GROUPS[key];
        const done = i < ci;
        const run = i === ci;
        const label = isMobile ? g.short : done ? g.done : run ? g.run : g.title;
        // Счётчик шагов внутри фазы — только когда сервер жив и журнал читается:
        // в мёртвом окне мы этого не знаем, и рисовать нечего
        const inGroup = rows.filter(r => r.group === key);
        const passed = inGroup.filter(r => r.status === 'done' || r.status === 'fail').length;
        const showCount = run && state === 'building' && inGroup.length > 0;
        const dur = done
          ? durLabel(groupMs(rows, key))
          : run
            ? clockLabel(key === 'queued' ? elapsed : groupElapsed)
            : '';
        return (
          <div key={key} title={g.title} style={{ display: 'flex', alignItems: 'center', gap: SP.sm, minHeight: isMobile ? 32 : 28 }}>
            <span style={{ width: 16, display: 'flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0 }}>
              {done
                ? <span style={{ color: C.success, display: 'flex' }}><Check size={13} strokeWidth={2.5} /></span>
                : run
                  ? <span className="tool-spinner" style={{
                      width: 11, height: 11,
                      ...(state === 'dead' ? { borderColor: C.infoBg, borderTopColor: C.info } : {}),
                    }} />
                  : <span style={{ width: 7, height: 7, borderRadius: R.full, background: C.border, display: 'block' }} />}
            </span>
            <span style={{
              flex: 1, minWidth: 0, fontSize: FS.base, overflow: 'hidden',
              textOverflow: 'ellipsis', whiteSpace: 'nowrap',
              color: done ? C.textSecondary : run ? C.textPrimary : C.textMuted,
              fontWeight: run ? 600 : 400,
            }}>
              {label}
            </span>
            {showCount && (
              <span style={{ fontSize: FS.xs, color: C.textMuted, whiteSpace: 'nowrap', flexShrink: 0 }}>
                {Math.min(passed + 1, inGroup.length)} из {inGroup.length} шагов
              </span>
            )}
            <span style={{
              fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted,
              whiteSpace: 'nowrap', flexShrink: 0, minWidth: 38, textAlign: 'right',
            }}>
              {dur}
            </span>
          </div>
        );
      })}
    </div>
  );
}

// Строка одного шага агента. Незнакомый шаг не исчезает — он и есть сырое имя
// моноширинным: агент выкатки обновляется отдельно от сервера
function StepRowView({ row, isMobile }: { row: StepRow; isMobile: boolean }) {
  const known = row.label !== null;
  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, minHeight: 24, fontSize: FS.sm }}>
      <span style={{ width: 14, display: 'flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0 }}>
        {row.status === 'done'
          ? <span style={{ color: C.success, display: 'flex' }}><Check size={12} strokeWidth={2.5} /></span>
          : row.status === 'fail'
            ? <span style={{ color: C.danger, display: 'flex' }}><X size={12} strokeWidth={2.5} /></span>
            : row.status === 'run'
              ? <span className="tool-spinner" style={{ width: 9, height: 9 }} />
              : <span style={{ width: 7, height: 7, borderRadius: R.full, background: C.border, display: 'block' }} />}
      </span>
      <span style={{
        flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
        fontFamily: known ? undefined : FONT.mono,
        color: row.status === 'fail' ? C.dangerText : row.status === 'run' ? C.textPrimary
          : row.status === 'wait' ? C.textMuted : C.textSecondary,
        fontWeight: row.status === 'fail' || row.status === 'run' ? 600 : 400,
      }}>
        {row.label ?? row.name}
      </span>
      {/* Сырое имя шага — по нему ищут шаг в логе агента. На узкой ленте колонки нет */}
      {known && !isMobile && (
        <span style={{
          width: 132, flexShrink: 0, fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted,
          overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
        }}>
          {row.name}
        </span>
      )}
      <span style={{
        fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted,
        minWidth: 38, textAlign: 'right', flexShrink: 0,
      }}>
        {durLabel(row.ms)}
      </span>
    </div>
  );
}

// Терминальная выкатка, завершившаяся не в эту сессию страницы, лежит в ленте свёрнутой
// в одну строку: это уже история разговора, а не событие. Кликается целиком.
function CollapsedCard({ state, title, record, elapsed, onExpand }: {
  state: CardState; title: string; record: DeployJournalRecord | null;
  elapsed: number; onExpand: () => void;
}) {
  const badge = state === 'loading' ? null : BADGE[state];
  const total = sane(elapsed);
  const tail = [
    record?.ref, record?.sha?.slice(0, 7), total ? durLabel(total) : null,
  ].filter(Boolean).join(' ');
  return (
    <div style={{
      border: `1px solid ${C.borderLight}`, borderLeft: `3px solid ${RIB[state]}`,
      borderRadius: R.xl, background: C.bgWhite, boxShadow: SHADOW.card,
      overflow: 'hidden', maxWidth: '100%',
    }}>
      <button
        type="button" onClick={onExpand} title={title}
        style={{
          display: 'flex', alignItems: 'center', gap: SP.sm, width: '100%', minHeight: 44,
          padding: `0 ${SP.md}px`, background: 'none', border: 'none', font: 'inherit',
          color: C.textPrimary, cursor: 'pointer', textAlign: 'left',
        }}
      >
        <span aria-hidden style={{ display: 'flex', flexShrink: 0, color: RIB[state] }}>
          <HeadIcon state={state} />
        </span>
        <span style={{ flex: 1, minWidth: 0, fontSize: FS.base, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
          {badge?.text ?? title}
          {tail && (
            <span style={{ color: C.textMuted, fontSize: FS.sm, fontFamily: FONT.mono }}> · {tail}</span>
          )}
        </span>
        <span aria-hidden style={{ color: C.textMuted, display: 'flex', flexShrink: 0 }}>
          <ChevronDown size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
        </span>
      </button>
    </div>
  );
}
