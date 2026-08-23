import { useEffect, useState } from 'react';
import { AlertTriangle, Clock, Gauge, ListChecks, Pause, Power, Users, Zap } from 'lucide-react';
import type { Persona, SessionTeamImplement, TeamWavePulse, TeamWaveSnapshot } from '../../types';
import { C, FS, FONT, R, MODAL_W } from '../../lib/design';
import { Button, ConfirmDialog, Menu, Modal } from '../../components/ui';
import { ICON_STROKE } from '../../components/ui/icons';
import { api } from '../../lib/api';
import {
  teamImplementTone, teamImplementBadgeText, teamImplementStageShort, teamBudgetLine, teamBudgetTight,
  teamImplementModeHeld,
  TEAM_IMPLEMENT_DESCRIPTION, TEAM_IMPLEMENT_AUTO_TITLE,
  TEAM_IMPLEMENT_DISABLE_TITLE, TEAM_IMPLEMENT_DISABLE_TEXT,
  TEAM_IMPLEMENT_NO_CODE_ON, TEAM_IMPLEMENT_NO_CODE_OFF, TEAM_IMPLEMENT_MODE_HELD,
  TEAM_IMPLEMENT_STOP_TITLE, TEAM_IMPLEMENT_STOP_TEXT, TEAM_IMPLEMENT_STOPPED_HINT,
  teamPulseTone, teamPulseBadgeText, teamPulseBadgeShort,
  teamPulseMeaning, teamPulseStage, teamWaveTaskStatusLabel, teamWaveTaskRunningLabel, teamWaveTasksSorted,
  isTeamWavePulseStale,
} from '../../lib/teamImplement';
import { teamMechanic } from './teamMechanics';
import { useOnline } from '../../hooks/useOnline';
import type { Mode } from '../../lib/modes';

// Тик для переоценки свежести pulse (см. isTeamWavePulseStale): при живом pulse периодически
// обновляем now, чтобы бейдж вовремя свалился обратно на stageTone, когда бэк замолчал
// дольше TEAM_WAVE_PULSE_STALE_MS. 30с — половина порога: ложное «зависло» не появится
// быстрее реального (а реальное обгоняет порог на ~30с после первого пропуска тика)
const PULSE_NOW_TICK_MS = 30_000;

// Бейдж режима «Командная реализация» в композере.
// По образцу loopBadge цикла «до готово»: pill 24/26, FS.xs, weight 600 — но с иконкой
// Users и пульс-точкой, плюс рядом переключаемый чип «Авто». Три тона — по тому, кто
// должен действовать (макет docs/mockups/team-implement-mode.html, секция 1):
//   work  (accent)  — планирование / волна N из M / проверка — команда работает
//   wait  (warning) — ждёт подтверждения / нужно решение — практика стоит и ждёт человека
//   idle  (muted)   — ждёт задачу — итерация закрыта, режим жив
// На стадии волны/проверки при ЖИВОМ пульсе (Э2) тон дополнительно корректируется
// по liveness: alive — work, quiet — warning, stalled/dead — danger. Без пульса
// (бэк старый) — тон остаётся прежним, обратная совместимость
// Клик по бейджу — поповер (десктоп) / шторка (мобила) с описанием и выключением режима;
// выключение — с подтверждением. Чип «Авто» переключается одним кликом без подтверждения.
export function TeamImplementBadge({ state, chatMode, isMobile, pulse, sessionId, personas, onToggleAuto, onDisable, onStop }: {
  state: SessionTeamImplement;
  // Текущий режим прав чата: под правилом «координатор не пишет код» он удерживается
  // на «Авто» — иначе гард обходится через терминал. Показываем сноской, чтобы
  // подтверждения действий не выглядели необъяснимыми
  chatMode?: Mode;
  isMobile?: boolean;
  // Эфемерный пульс волны (Э2 КР-наблюдаемости). undefined — пульса ещё не было
  // (бэк старый, чат открыт до первой минуты волны). При наличии в стадии волны/
  // проверки бейдж показывает активность («2/5 · 4 мин»), тон пересчитывается по
  // liveness
  pulse?: TeamWavePulse | null;
  // sessionId нужен для refetch снапшота при открытии поповера — список задач волны
  // самодостаточен внутри бейджа, чтобы не раздувать пропсы родителя. Без sessionId
  // список задач не показывается (обратная совместимость при отсутствии данных)
  sessionId?: string;
  // Справочник персон (для имён/аватаров исполнителей задач). Уже есть на фронте —
  // usePersonas() родителя
  personas?: Record<string, Persona>;
  onToggleAuto: () => void | Promise<void>;
  onDisable: () => void | Promise<void>;
  // «Остановить» прогон (режим при этом остаётся включённым). Без обработчика строки нет
  onStop?: () => void | Promise<void>;
}) {
  // Поповер держим на rect кнопки (anchor-режим Menu = fixed), а не на absolute внутри
  // бейджа: полоса бейджей композера живёт под overflow:hidden (схлопывание контролов),
  // и absolute-карточка обрезалась целиком — в DOM была, на экране нет
  const [infoAnchor, setInfoAnchor] = useState<DOMRect | null>(null);
  const [disableConfirm, setDisableConfirm] = useState(false);
  const [stopConfirm, setStopConfirm] = useState(false);
  // Полный снимок волны для поповера: refetch на КАЖДОМ открытии (после реконнекта
  // кэш протухнет, а человек должен видеть свежее состояние). null — нет данных или
  // запрос не успел; undefined — открытия ещё не было
  const [snapshot, setSnapshot] = useState<TeamWaveSnapshot | null | undefined>(undefined);
  const infoOpen = infoAnchor !== null;
  const closeInfo = () => setInfoAnchor(null);

  // Refetch списка задач при КАЖДОМ открытии поповера. Гонку с закрытием (юзер успел
  // нажать «Остановить» до прихода ответа) переживаем молча: закроется бейдж —
  // закроется и поповер, и обновление уйдёт в никуда. Не критично: новый открытие
  // подтянет свежее
  useEffect(() => {
    if (!infoOpen || !sessionId || !teamPulseStage(state.stage)) return;
    let cancelled = false;
    api.chats.getTeamWaveSnapshot(sessionId)
      .then(s => { if (!cancelled) setSnapshot(s); })
      .catch(() => { if (!cancelled) setSnapshot(null); });
    return () => { cancelled = true; };
  }, [infoOpen, sessionId, state.stage]);

  // Свежесть пульса: либо прошло больше порога от lastActivityAt, либо SignalR offline
  // (обновлений физически не будет). В обоих случаях НЕЛЬЗЯ показывать пульс как живую
  // сводку: бейдж падает на stageTone, блок «Что это значит» не рисуется. Это лечит сразу
  // две находки QA: (а) при обрыве связи бейдж перестаёт утверждать «штаб работает» по
  // вчерашнему числу; (б) после F5 до первого WS-события state.teamWavePulse === undefined
  // — livePulse остаётся null, бейдж показывает чистую стадию, без stale-данных
  const online = useOnline();
  // Тик «сейчас»: пока пульс потенциально живой (есть pulse и стейдж дышит), переоцениваем
  // его каждые 30с. Снимаем тик как только livePulse стал null — лишних ререндеров не нужно
  const [now, setNow] = useState(() => Date.now());
  const pulseStage = teamPulseStage(state.stage) && pulse;
  useEffect(() => {
    if (!pulseStage) return;
    const t = setInterval(() => setNow(Date.now()), PULSE_NOW_TICK_MS);
    return () => clearInterval(t);
  }, [pulseStage]);
  const livePulse: TeamWavePulse | null = pulseStage && pulse && !isTeamWavePulseStale(pulse, now, online)
    ? pulse
    : null;

  // Эффективный тон на стадии волны/проверки: пульс живого бэка перебивает дефолтный
  // тон стадии. Вне этих стадий или без (свежего) пульса — как раньше, по стадии
  const stageTone = teamImplementTone(state.stage);
  const effectiveTone: 'work' | 'wait' | 'idle' | 'warning' | 'danger' =
    livePulse ? teamPulseTone(livePulse.liveness) : stageTone;

  // Полная и короткая подпись бейджа при ЖИВОМ пульсе на стадии волны/проверки
  const pulseFullText = livePulse ? teamPulseBadgeText(state, livePulse) : null;
  const pulseShortText = livePulse ? teamPulseBadgeShort(livePulse) : null;

  // Дефолтные подписи (как раньше, без пульса)
  const fullText = teamImplementBadgeText(state.stage, state.waveNumber, state.plannedWaves);
  const text = `${teamMechanic('implementMode').shortName} · ${teamImplementStageShort(state.stage, state.waveNumber, state.plannedWaves)}`;
  const height = isMobile ? 26 : 24;

  // Тон бейджа: для warning/danger отдельные палитры, work/wait/idle — как раньше
  const toneStyle = (() => {
    switch (effectiveTone) {
      case 'work': return { background: C.accentLight, color: C.accent };
      case 'wait': return { background: C.warningBg, color: C.warningText };
      case 'warning': return { background: C.warningBg, color: C.warningText };
      case 'danger': return { background: C.dangerBg, color: C.dangerText };
      case 'idle': return { background: C.bgSelected, color: C.textSecondary };
    }
  })();
  const dotColor = (() => {
    switch (effectiveTone) {
      case 'work': return C.accent;
      case 'wait':
      case 'warning': return C.warning;
      case 'danger': return C.danger;
      case 'idle': return C.textMuted;
    }
  })();
  // Пульс точки: только у «живых» стадий. stalled/dead/quiet — статичная точка,
  // иначе «дышит» поверх сообщения о проблеме сбивает с толку
  const pulseAnimation = (() => {
    if (effectiveTone === 'idle') return undefined;
    if (effectiveTone === 'work') return `pulsedot 1.6s ease-in-out infinite`;
    if (effectiveTone === 'wait' || effectiveTone === 'warning') return `pulsedot 1.2s ease-in-out infinite`;
    return undefined; // danger — статика
  })();

  // Текст и тултип бейджа с учётом пульса
  const badgeText = livePulse
    ? (isMobile ? (pulseShortText ?? text) : (pulseFullText ?? text))
    : text;
  const badgeTitle = livePulse
    ? `${fullText} — ${teamPulseMeaning(livePulse.liveness)}`
    : `${fullText} — ${TEAM_IMPLEMENT_DESCRIPTION}`;

  // Иконка состояния пульса в бейдже: различает liveness ДО чтения текста — периферийным
  // зрением quiet и stalled дают одинаковый тёплый фон (--c-warning-bg vs --c-danger-bg
  // бледные и неразличимы), а иконка видна сразу. alive — точка и так «дышит», лишний
  // Loader рядом шумел бы; quiet — часы (тишина); stalled/dead — треугольник (нужно
  // внимание). Тот же приём ниже усиливает блок «Что это значит»
  const LivenessIcon = livePulse
    ? (effectiveTone === 'warning' ? Clock
      : effectiveTone === 'danger' ? AlertTriangle
        : null)
    : null;

  const budgetLine = teamBudgetLine(state.budget);
  const budgetTight = teamBudgetTight(state.budget);

  const disableBody = (
    <>
      <p style={{ fontSize: FS.sm, color: C.textSecondary, lineHeight: 1.45, margin: 0 }}>
        {TEAM_IMPLEMENT_DESCRIPTION}
      </p>
      {/* Пульс волны: блок «Что это значит» — объясняет состояние человеческим языком.
          На стадии волны/проверки при ЖИВОМ пульсе (livePulse === null при обрыве SignalR
          или после F5 до первого события — тогда не рисуем, чтобы не врать). Тон по
          liveness: alive — спокойный, quiet — янтарный (это нормально), stalled/dead —
          красный (нужно внимание) */}
      {livePulse && (
        <PulseBlock pulse={livePulse} tone={effectiveTone} />
      )}
      {/* Список задач волны: refetch снапшота при КАЖДОМ открытии (живёт в родителе —
          useTeamWaveSnapshot в ChatPanel). Здесь только рендер: задачи, исполнители,
          статусы, сколько минут в работе. Без снапшота (бэк старый) — пропускаем.
          На проверке список тоже показывается — задачи проверки идут тем же путём */}
      {teamPulseStage(state.stage) && snapshot && (
        <WaveTaskList snapshot={snapshot} personas={personas ?? {}} />
      )}
      {/* Правило «любая работа — через задачу»: настройка режима, менять её на ходу нельзя —
          показываем как строку состояния, чтобы поведение штаба не выглядело сюрпризом */}
      <div style={{
        display: 'flex', alignItems: 'flex-start', gap: 6, marginTop: 8,
        fontSize: FS.xs, color: C.textMuted, lineHeight: 1.4,
      }}>
        <ListChecks size={12} strokeWidth={ICON_STROKE} style={{ flexShrink: 0, marginTop: 1 }} />
        {state.coordinatorNoCode ? TEAM_IMPLEMENT_NO_CODE_ON : TEAM_IMPLEMENT_NO_CODE_OFF}
      </div>
      {/* Сноска к правилу: режим прав чата держится на «Авто» не сам по себе — его
          удерживает правило. Без этого подтверждения действий, которых вчера не было,
          выглядят как поломка. Отступ слева = иконка + gap строки правила */}
      {state.coordinatorNoCode && chatMode && teamImplementModeHeld(chatMode) && (
        <div style={{
          marginTop: 4, paddingLeft: 18,
          fontSize: FS.xs, color: C.textMuted, lineHeight: 1.4,
        }}>
          {TEAM_IMPLEMENT_MODE_HELD}
        </div>
      )}
      {/* Расход бюджета итерации: человек должен видеть, что остановка близко, ДО того
          как она случится (иначе исчерпание бюджета выглядит внезапным) */}
      {budgetLine && (
        <div style={{
          display: 'flex', alignItems: 'flex-start', gap: 6, marginTop: 6,
          fontSize: FS.xs, lineHeight: 1.4,
          color: budgetTight ? C.warningText : C.textMuted,
          fontWeight: budgetTight ? 600 : 400,
        }}>
          <Gauge size={12} strokeWidth={ICON_STROKE} style={{ flexShrink: 0, marginTop: 1 }} />
          {budgetLine}
        </div>
      )}
      {/* Остановка прогона: режим остаётся включённым, поэтому она отдельно от «Выключить» */}
      {onStop && (
        <div style={{ marginTop: 10, paddingTop: 10, borderTop: `1px dashed ${C.divider}` }}>
          {state.stopped ? (
            <div style={{ display: 'flex', alignItems: 'flex-start', gap: 6, fontSize: FS.xs, color: C.textMuted, lineHeight: 1.4 }}>
                <Pause size={12} strokeWidth={ICON_STROKE} fill="currentColor" style={{ flexShrink: 0, marginTop: 1 }} />
              {TEAM_IMPLEMENT_STOPPED_HINT}
            </div>
          ) : isMobile ? (
            <Button variant="ghostFilled" size="md" fullWidth
              onClick={() => { closeInfo(); setStopConfirm(true); }}>
              <Pause size={13} strokeWidth={ICON_STROKE} fill="currentColor" />
              Остановить
            </Button>
          ) : (
            <PopoverRow
              icon={<Pause size={13} strokeWidth={ICON_STROKE} fill="currentColor" style={{ flexShrink: 0 }} />}
              label="Остановить"
              onClick={() => { closeInfo(); setStopConfirm(true); }}
            />
          )}
        </div>
      )}
      <div style={{ marginTop: 10, paddingTop: 10, borderTop: `1px dashed ${C.divider}` }}>
        <DisableRow isMobile={isMobile} onClick={() => { closeInfo(); setDisableConfirm(true); }} />
      </div>
    </>
  );

  return (
    <span style={{ position: 'relative', display: 'inline-flex', alignItems: 'center', gap: isMobile ? 6 : 4, flexShrink: 0 }}>
      <button
        onClick={e => {
          const rect = e.currentTarget.getBoundingClientRect();
          setInfoAnchor(prev => (prev ? null : rect));
        }}
        title={badgeTitle}
        style={{
          display: 'inline-flex', alignItems: 'center', gap: 6, height,
          padding: '0 9px', borderRadius: R.pill, border: 'none', cursor: 'pointer',
          // Текст бейджа: при danger — жирнее (700 vs 600), иначе периферийным зрением
          // quiet/stalled различимы только в иконке, шрифт подкрепляет ту же идею
          fontSize: FS.xs, fontWeight: effectiveTone === 'danger' ? 700 : 600,
          whiteSpace: 'nowrap', flexShrink: 0,
          fontFamily: FONT.sans, ...toneStyle,
        }}
      >
        <Users size={11} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />
        {LivenessIcon && (
          <LivenessIcon size={11} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} aria-hidden="true" />
        )}
        {badgeText}
        <span style={{
          width: 6, height: 6, borderRadius: '50%', background: dotColor, flexShrink: 0,
          ...(pulseAnimation ? { animation: pulseAnimation } : {}),
        }} />
      </button>

      <button
        onClick={() => { void onToggleAuto(); }}
        title={TEAM_IMPLEMENT_AUTO_TITLE}
        style={{
          display: 'inline-flex', alignItems: 'center', gap: 5, height,
          padding: '0 9px', borderRadius: R.pill, cursor: 'pointer',
          fontSize: FS.xs, fontWeight: 600, whiteSpace: 'nowrap', flexShrink: 0,
          fontFamily: FONT.sans,
          transition: 'color 0.15s, background 0.15s, border-color 0.15s',
          ...(state.autoWaves
            ? { background: C.accentLight, color: C.accent, border: `1px solid ${C.accentMuted}` }
            : { background: 'transparent', color: C.textMuted, border: `1px solid ${C.border}` }),
        }}
      >
        <Zap size={10} strokeWidth={ICON_STROKE} fill={state.autoWaves ? 'currentColor' : 'none'} style={{ flexShrink: 0 }} />
        Авто
      </button>

      {/* Поповер: десктоп — карточка у бейджа (fixed по якорю, иначе её срезает
          overflow полосы бейджей), мобила — нижняя шторка (Modal) */}
      {infoOpen && !isMobile && (
        <Menu anchor={infoAnchor!} minWidth={300} maxHeight={320} onClose={closeInfo}>
          <div style={{ padding: '7px 9px', fontFamily: FONT.sans, textAlign: 'left' }}>
            <div style={{
              display: 'flex', alignItems: 'center', gap: 7, marginBottom: 4,
              fontFamily: FONT.serif, fontSize: FS.md, fontWeight: 700, color: C.textHeading,
            }}>
              <Users size={14} strokeWidth={ICON_STROKE} style={{ color: C.accent, flexShrink: 0 }} />
              Командная реализация
            </div>
            {disableBody}
          </div>
        </Menu>
      )}
      {infoOpen && isMobile && (
        <Modal title="Командная реализация" width={MODAL_W.confirm} onClose={closeInfo}>
          {disableBody}
        </Modal>
      )}

      {disableConfirm && (
        <ConfirmDialog
          title={TEAM_IMPLEMENT_DISABLE_TITLE}
          subtitle={TEAM_IMPLEMENT_DISABLE_TEXT}
          confirmLabel="Выключить"
          cancelLabel="Оставить"
          onConfirm={async () => { await onDisable(); setDisableConfirm(false); }}
          onCancel={() => setDisableConfirm(false)}
        />
      )}

      {stopConfirm && onStop && (
        <ConfirmDialog
          title={TEAM_IMPLEMENT_STOP_TITLE}
          subtitle={TEAM_IMPLEMENT_STOP_TEXT}
          confirmLabel="Остановить"
          cancelLabel="Пусть работает"
          onConfirm={async () => { await onStop(); setStopConfirm(false); }}
          onCancel={() => setStopConfirm(false)}
        />
      )}
    </span>
  );
}

// Блок «Что это значит» в поповере/шторке. Тон по liveness: alive — нейтральный,
// quiet — янтарный (тишина в пределах нормы), stalled/dead — красный (нужно внимание).
// Текст готов с бэка в lib/teamImplement.teamPulseMeaning — одной строкой на состояние.
// На danger добавляем левый бордер C.dangerBorder и жирный шрифт: бледный dangerBg рядом с
// бледным warningBg периферийным зрением не различить, а разница «тихо vs похоже, зависло»
// должна читаться до чтения текста (находка QA: Вера приняла quiet за stalled, потому что
// десктопный поповер не разводил их цветом). Иконка состояния в строке усиливает тот же
// сигнал — см. LivenessIcon в бейдже
function PulseBlock({ pulse, tone }: {
  pulse: TeamWavePulse;
  tone: 'work' | 'wait' | 'idle' | 'warning' | 'danger';
}) {
  const bg = tone === 'work' ? C.accentLight
    : tone === 'warning' ? C.warningBg
      : tone === 'danger' ? C.dangerBg
        : C.bgSelected;
  const fg = tone === 'work' ? C.accent
    : tone === 'warning' ? C.warningText
      : tone === 'danger' ? C.dangerText
        : C.textSecondary;
  // Левый бордер у danger: 3px заметной ширины, токен из дизайн-системы (C.dangerBorder).
  // Не используем border со всех сторон — это сломало бы ритм блока на общем фоне поповера.
  // padding-left сдвинут, чтобы текст встал ровно по сетке поповера
  const isDanger = tone === 'danger';
  const LivenessIcon = tone === 'warning' ? Clock
    : tone === 'danger' ? AlertTriangle
      : null;
  return (
    <div style={{
      marginTop: 8,
      padding: isDanger ? '6px 8px 6px 10px' : '6px 8px',
      paddingLeft: isDanger ? 10 : 8,
      borderRadius: R.md,
      background: bg, color: fg,
      fontSize: FS.xs, lineHeight: 1.4,
      fontWeight: isDanger ? 700 : 400,
      // borderLeft только у danger: 3px цветной полосы слева. Светлая/тёмная тема обе
      // держат C.dangerBorder как отдельный токен — менять его ради блока нельзя (используют
      // и кнопки, и карточки опасности), и не нужно: сам токен достаточно насыщен
      ...(isDanger ? { borderLeft: `3px solid ${C.dangerBorder}` } : {}),
      display: 'flex', alignItems: 'flex-start', gap: 6,
    }}>
      {LivenessIcon && (
        <LivenessIcon size={12} strokeWidth={ICON_STROKE} style={{ flexShrink: 0, marginTop: 1 }} aria-hidden="true" />
      )}
      <span>{teamPulseMeaning(pulse.liveness)}</span>
    </div>
  );
}

// Список задач волны. Только для стадии волны/проверки и при наличии снапшота: бэк
// старый (без пульса) сюда не доходит — обратная совместимость
function WaveTaskList({ snapshot, personas }: {
  snapshot: TeamWaveSnapshot;
  personas: Record<string, Persona>;
}) {
  const tasks = teamWaveTasksSorted(snapshot.tasks);
  if (tasks.length === 0) return null;
  return (
    <div style={{ marginTop: 8, display: 'flex', flexDirection: 'column', gap: 4 }}>
      <div style={{
        fontSize: FS.xs, color: C.textMuted, fontWeight: 600,
        display: 'flex', alignItems: 'center', gap: 5,
      }}>
        <ListChecks size={11} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />
        Задачи волны · {snapshot.tasks.length}
      </div>
      {tasks.map(t => (
        <WaveTaskRow key={t.id} task={t} persona={t.executorPersonaId ? personas[t.executorPersonaId] : undefined} />
      ))}
    </div>
  );
}

// Строка задачи: исполнитель (аватар/инициалы + имя) — статус — «N мин в работе».
// Время обновляется раз в минуту (useEffect с setInterval) — дешевле секундных тиков,
// и точность до минуты соответствует «4 мин назад» в самом бейдже
function WaveTaskRow({ task, persona }: { task: import('../../types').TeamWaveTask; persona?: Persona }) {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    if (!task.startedAt || task.status !== 'inProgress') return;
    const t = setInterval(() => setNow(Date.now()), 60_000);
    return () => clearInterval(t);
  }, [task.startedAt, task.status]);

  const name = persona?.name || 'Без исполнителя';
  const initials = persona
    ? initialsFromName(persona.name)
    : '—';
  const avatarColor = persona?.avatar?.color || C.bgSelected;
  const statusLabel = teamWaveTaskStatusLabel(task.status);
  const running = task.status === 'inProgress' && task.startedAt
    ? teamWaveTaskRunningLabel(task, now)
    : null;

  return (
    <div style={{
      display: 'flex', alignItems: 'center', gap: 7,
      padding: '4px 6px', borderRadius: R.sm,
      background: C.bgMain,
    }}>
      <span style={{
        width: 20, height: 20, borderRadius: '50%', flexShrink: 0,
        display: 'inline-flex', alignItems: 'center', justifyContent: 'center',
        background: avatarColor, color: C.onNavInk,
        fontSize: 10, fontWeight: 600, fontFamily: FONT.sans,
      }}>
        {initials}
      </span>
      <span style={{ flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
        fontSize: FS.xs, color: C.textHeading, fontWeight: 600 }}>
        {task.title}
      </span>
      <span style={{
        fontSize: FS.xs, color: C.textSecondary, flexShrink: 0,
      }}>
        {name}
      </span>
      <span style={{
        fontSize: FS.xs, flexShrink: 0,
        color: task.status === 'inProgress' ? C.accent
          : task.status === 'done' ? C.successText
            : C.textMuted,
        fontWeight: 600,
      }}>
        {statusLabel}
      </span>
      {running && (
        <span style={{ fontSize: FS.xs, color: C.textMuted, flexShrink: 0 }}>
          {running}
        </span>
      )}
    </div>
  );
}

// Инициалы для аватара исполнителя: берём первые буквы первых двух слов имени.
// У латиницы — ASCII, у кириллицы — первые буквы. Дефолт «?» для пустого имени
function initialsFromName(name: string | undefined | null): string {
  if (!name) return '?';
  const parts = name.trim().split(/\s+/).slice(0, 2);
  return parts.map(p => p[0] ?? '').join('').toUpperCase() || '?';
}

// Строка-действие поповера (десктоп): иконка + подпись с hover-подложкой.
// danger — красная («Выключить режим»), обычная — нейтральная («Остановить»)
function PopoverRow({ icon, label, onClick, danger }: {
  icon: React.ReactNode;
  label: string;
  onClick: () => void;
  danger?: boolean;
}) {
  const [hover, setHover] = useState(false);
  return (
    <button
      type="button"
      onClick={onClick}
      onMouseEnter={() => setHover(true)}
      onMouseLeave={() => setHover(false)}
      style={{
        display: 'flex', alignItems: 'center', gap: 7, width: '100%', textAlign: 'left',
        border: 'none', cursor: 'pointer', padding: '7px 8px', borderRadius: R.md,
        background: hover ? (danger ? C.dangerBg : C.bgSelected) : 'none',
        color: danger ? C.dangerText : C.textHeading,
        fontSize: FS.base, fontWeight: 600, fontFamily: FONT.sans,
      }}
    >
      {icon}
      {label}
    </button>
  );
}

// «Выключить режим» — danger-действие поповера/шторки. На мобиле — полноразмерная
// кнопка (тач-цель), на десктопе — строка с hover-подложкой
function DisableRow({ isMobile, onClick }: { isMobile?: boolean; onClick: () => void }) {
  if (isMobile) {
    return (
      <Button variant="danger" size="md" fullWidth onClick={onClick}>
        <Power size={13} strokeWidth={ICON_STROKE} />
        Выключить режим
      </Button>
    );
  }
  return (
    <PopoverRow
      danger
      icon={<Power size={13} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />}
      label="Выключить режим"
      onClick={onClick}
    />
  );
}
