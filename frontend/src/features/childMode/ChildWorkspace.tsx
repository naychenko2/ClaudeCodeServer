// KM-F10: детская оболочка — тот же каркас AI Home, только меньше (макет kids-shell-v2).
// Собрана из примитивов взрослого каркаса: шапка, рельса RailCapsule, острова Island,
// холст CanvasBackdrop. Взрослые экраны, хаб-табы, поиск, уведомления, док проектов,
// стена и взрослые панели здесь не монтируются — а значит, не шлют запросов.
// Запросы оболочки: только /api/child/* (status, heartbeat, spaces/*/chats, rules/start)
// и то, что делает чат урока (ChildLessonChat).

import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { BookOpen, Brush, Lightbulb, ListChecks, LogOut, User, WifiOff } from 'lucide-react';
import type { AuthState, Project } from '../../types';
import { displayNameOf } from '../../types';
import { C, FONT, FS, R, SHADOW, SP, TB } from '../../lib/design';
import { useIsMobile, useWindowWidth } from '../../lib/breakpoints';
import { PageCanvas } from '../../components/ui/PageCanvas';
import { Island, IslandHeader } from '../../components/ui/Island';
import { RailCapsule, RAIL_GAP } from '../../components/ui/RailCapsule';
import { RailIconButton } from '../../components/ui/RailIconButton';
import { RailHat } from '../../components/ui/RailHat';
import { RailSep } from '../../components/ui/RailSep';
import { Dot } from '../../components/ui/Dot';
import { Button } from '../../components/ui/Button';
import { IconButton } from '../../components/ui/IconButton';
import { BackButton } from '../../components/ui/BackButton';
import { EmptyState } from '../../components/ui/EmptyState';
import { Menu, MenuItem } from '../../components/ui/Menu';
import { Modal } from '../../components/ui/Modal';
import { Notice } from '../../components/ui/Notice';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { PillSwitch } from '../../components/Toolbar';
import { childApi } from './api';
import { useChildStatus } from './useChildStatus';
import { ChildHome, CHILD_SUBJECTS, type ChildSubjectId } from './ChildHome';
import { ChildHintsPanel } from './ChildHintsPanel';
import { ChildLessonChat } from './ChildLessonChat';
import { ChildRulesOnboarding, type ChildRulesBrief } from './ChildRulesOnboarding';
import { ChildTimeBadge } from './ChildTimeBadge';
import { ChildTimeUpScreen } from './ChildTimeUpScreen';
import type { ChildStatus } from './types';

// Единственное пространство волны 1 (ADR-041)
const SPACE = 'lessons';
const SUBJECTS_KEY = 'cc_child_lesson_subjects';
// Ширины раскладки панели «Как мы учимся» (спека макета): >1200 — в зоне, 900–1200 — поверх, <900 — поверх без сайдбара
const PANEL_INLINE_MIN = 1200;
const SIDEBAR_MIN = 900;
const SIDEBAR_W = 280;
const PANEL_W = 300;

// Цвет холста предмета — ключ палитры проекта (ADR-008): математика синяя, русский сливовый, английский зелёный
const SUBJECT_INK: Record<ChildSubjectId, string> = { math: 'blue', russian: 'purple', english: 'green' };
const subjectAsProject = (id: ChildSubjectId): Project =>
  ({ id: `child-subject-${id}`, icon: { color: SUBJECT_INK[id] } }) as unknown as Project;

type Chat = { sessionId: string; name: string; updatedAt: string; voiceMode?: boolean };
type Tab = 'lessons' | 'studio' | 'rules';

// Предмет урока хранит клиент: контракт чатов пространства поля «предмет» не несёт
function readSubjects(): Record<string, ChildSubjectId> {
  try { return JSON.parse(localStorage.getItem(SUBJECTS_KEY) ?? '{}') as Record<string, ChildSubjectId>; } catch { return {}; }
}
function rememberSubject(sessionId: string, subject: ChildSubjectId) {
  try { localStorage.setItem(SUBJECTS_KEY, JSON.stringify({ ...readSubjects(), [sessionId]: subject })); } catch { /* квота — не страшно */ }
}

const isTimeUp = (s: ChildStatus) => s.state === 'time_up' || s.state === 'out_of_window' || s.state === 'budget_exhausted';

interface Props {
  auth: AuthState;
  onLogout: () => void;
}

// Шапка: логотип, крошка предмета, «спокойное время», меню аватара с одним пунктом «Выйти»
function ChildHeader({ crumb, status, isMobile, onLogout }: {
  crumb: string | null; status: ChildStatus | null; isMobile: boolean; onLogout: () => void;
}) {
  const [menu, setMenu] = useState(false);
  return (
    <header style={{
      flexShrink: 0, display: 'flex', alignItems: 'center', gap: TB.gap,
      height: isMobile ? TB.heightMobile : TB.heightDesktop,
      padding: `0 ${isMobile ? TB.padXMobile : TB.padX}px`, boxSizing: 'border-box',
      borderBottom: isMobile ? `1px solid ${C.border}` : 'none',
    }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, minWidth: 0, flex: 1 }}>
        <img src="/favicon.svg" alt="" width={26} height={26} style={{ display: 'block', flexShrink: 0 }} />
        {!isMobile && (
          <span style={{ fontFamily: FONT.serif, fontSize: FS.xl, fontWeight: 500, color: C.textHeading, whiteSpace: 'nowrap' }}>Home AI</span>
        )}
        {crumb && (
          <>
            <span aria-hidden style={{ color: C.textMuted }}>›</span>
            <span style={{
              fontFamily: FONT.sans, fontSize: FS.lg, fontWeight: 500, color: C.textHeading,
              overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
            }}>{crumb}</span>
          </>
        )}
      </div>
      {status && <ChildTimeBadge status={status} compact={isMobile} />}
      <div style={{ position: 'relative', flexShrink: 0 }}>
        <IconButton ariaLabel="Меню" onClick={() => setMenu(v => !v)}>
          <User size={ICON_SIZE.md} strokeWidth={ICON_STROKE} />
        </IconButton>
        {menu && (
          <Menu onClose={() => setMenu(false)} minWidth={160}>
            <MenuItem icon={<LogOut size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />} label="Выйти" onClick={() => { setMenu(false); onLogout(); }} isMobile={isMobile} />
          </Menu>
        )}
      </div>
    </header>
  );
}

// Строка урока в сайдбаре предмета
function LessonRow({ chat, active, onOpen }: { chat: Chat; active: boolean; onOpen: () => void }) {
  return (
    <button
      type="button"
      aria-current={active ? 'true' : undefined}
      onClick={onOpen}
      style={{
        display: 'block', width: '100%', textAlign: 'left', cursor: 'pointer', boxSizing: 'border-box',
        padding: `${SP.sm}px ${SP.md}px`, marginBottom: 2, minHeight: 40,
        border: 'none', borderRadius: R.md, fontFamily: FONT.sans, fontSize: FS.base,
        background: active ? C.bgSelected : 'transparent', color: C.textHeading,
        overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
      }}
    >
      {chat.name || 'Новый урок'}
    </button>
  );
}

export function ChildWorkspace({ auth, onLogout }: Props) {
  const isMobile = useIsMobile();
  const w = useWindowWidth();
  const { status, offline } = useChildStatus(SPACE);
  const childName = displayNameOf(auth);

  const [tab, setTab] = useState<Tab>('lessons');
  const [subjectId, setSubjectId] = useState<ChildSubjectId | null>(null);
  const [sessionId, setSessionId] = useState<string | null>(null);
  const [chats, setChats] = useState<Chat[]>([]);
  const [hint, setHint] = useState<{ sid: string; step: number }>({ sid: '', step: 0 });
  const [panelOpen, setPanelOpen] = useState(false);
  const [rulesBrief, setRulesBrief] = useState<ChildRulesBrief | null>(null);
  const [agreedLocal, setAgreedLocal] = useState(false);
  const [warnDismissed, setWarnDismissed] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [starting, setStarting] = useState(false);

  // Согласие снимает rules_pending сразу, не дожидаясь следующего опроса статуса
  const eff: ChildStatus | null = useMemo(() => {
    if (!status) return null;
    return agreedLocal && status.state === 'rules_pending'
      ? { ...status, state: 'ok', rulesAgreedAt: new Date().toISOString() }
      : status;
  }, [status, agreedLocal]);

  // Список уроков — только когда он нужен ребёнку: не при rules_pending и не при «время вышло»
  const lessonsAllowed = !!eff && eff.state !== 'rules_pending' && !isTimeUp(eff);
  const loadChats = useCallback(() => {
    const ac = new AbortController();
    childApi.spaceChats(SPACE, { signal: ac.signal })
      .then(r => setChats(r.chats))
      .catch(() => { /* офлайн покажет опрос статуса */ });
    return () => ac.abort();
  }, []);
  useEffect(() => (lessonsAllowed ? loadChats() : undefined), [lessonsAllowed, loadChats]);

  const subjects = readSubjects();
  const subjectOf = (sid: string): ChildSubjectId => subjects[sid] ?? 'math';
  const activeSubject: ChildSubjectId | null = sessionId ? subjectOf(sessionId) : subjectId;
  const subjectMeta = CHILD_SUBJECTS.find(s => s.id === activeSubject);
  const activeChat = chats.find(c => c.sessionId === sessionId);

  async function startLesson(subject: ChildSubjectId) {
    if (starting) return;
    setStarting(true);
    setError(null);
    try {
      const r = await childApi.spaceChat(SPACE);
      rememberSubject(r.sessionId, subject);
      setSubjectId(subject);
      setSessionId(r.sessionId);
      loadChats();
    } catch {
      setError('Сейчас урок не получится начать. Попробуй чуть позже.');
    } finally {
      setStarting(false);
    }
  }

  const openLesson = (sid: string) => { setSessionId(sid); setSubjectId(subjectOf(sid)); setPanelOpen(false); };
  const goHome = () => { setSessionId(null); setSubjectId(null); setTab('lessons'); setPanelOpen(false); };

  async function startRules() {
    if (!eff) return;
    setError(null);
    try {
      const r = await childApi.rulesStart();
      setRulesBrief(briefFrom(eff, childName, r.sessionId));
    } catch {
      setError('Правила пока не открываются. Скажи папе, он поможет.');
    }
  }
  function reviewRules() {
    if (!eff) return;
    setRulesBrief(briefFrom(eff, childName, ''));
  }

  const rulesPending = eff?.state === 'rules_pending';
  const onRulesTab = () => { setTab('rules'); if (rulesPending) void startRules(); else reviewRules(); };
  const closeRules = () => { setRulesBrief(null); setTab('lessons'); };

  // Пока открыто окно правил, вкладка «Правила» подсвечена; после закрытия — снова «Уроки»
  const tabs: { value: Tab; label: string; icon: React.ReactNode }[] = [
    { value: 'lessons', label: 'Уроки', icon: <BookOpen size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} /> },
    { value: 'studio', label: 'Студия · скоро', icon: <Brush size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} /> },
    { value: 'rules', label: 'Правила', icon: <ListChecks size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} /> },
  ];

  const timeUp = eff && isTimeUp(eff);
  const inLesson = !!sessionId && !!eff && lessonsAllowed;
  const wide = w >= SIDEBAR_MIN;
  const panelInline = w > PANEL_INLINE_MIN;
  const showSidebar = wide && (!!activeSubject || inLesson) && lessonsAllowed;
  const step = hint.sid === sessionId ? hint.step : 0;
  const canvasProject = !timeUp && activeSubject ? subjectAsProject(activeSubject) : undefined;

  const hintsBody = sessionId ? <ChildHintsPanel step={step} sessionId={sessionId} /> : null;
  const showPanel = inLesson && (panelInline || panelOpen) && !isMobile;

  const noticeBar = eff && (
    <>
      {offline && (
        <Notice tone="danger" icon={WifiOff} role="status">Нет связи. Как только она вернётся, продолжим.</Notice>
      )}
      {!offline && eff.state === 'warn' && !warnDismissed && (
        <Notice
          tone="warning"
          icon={Lightbulb}
          role="status"
          action={<Button size="xs" variant="ghost" onClick={() => setWarnDismissed(true)}>Хорошо</Button>}
        >
          Осталось {eff.minutesLeft ?? eff.warnMinutes} минут. Доделай то, что начала, и отдохни.
        </Notice>
      )}
      {error && <Notice tone="warning" icon={Lightbulb} role="alert">{error}</Notice>}
    </>
  );

  // Возврат на «ок» после предупреждения: плашка должна появиться снова при следующем warn
  const prevState = useRef(eff?.state);
  useEffect(() => {
    if (prevState.current !== eff?.state && eff?.state !== 'warn') setWarnDismissed(false);
    prevState.current = eff?.state;
  }, [eff?.state]);

  let center: React.ReactNode;
  if (!eff) {
    center = null;
  } else if (timeUp) {
    center = <ChildTimeUpScreen status={eff} minutesToday={0} />;
  } else if (inLesson && sessionId && activeSubject) {
    center = (
      <ChildLessonChat
        key={sessionId}
        sessionId={sessionId}
        subjectId={activeSubject}
        title={activeChat?.name || 'Новый урок'}
        status={eff}
        initialVoiceMode={activeChat?.voiceMode === true}
        onHintStep={n => setHint({ sid: sessionId, step: n })}
      />
    );
  } else if (activeSubject && !rulesPending) {
    center = (
      <EmptyState
        icon={<BookOpen size={ICON_SIZE.lg} strokeWidth={ICON_STROKE} />}
        title={subjectMeta?.label ?? 'Урок'}
        subtitle="Выбери урок слева или начни новый."
        action={<Button size="lg" onClick={() => void startLesson(activeSubject)} disabled={starting}>Начать урок</Button>}
      />
    );
  } else {
    center = (
      <ChildHome
        childName={childName}
        status={eff}
        lastLesson={chats[0] ? { subjectId: subjectOf(chats[0].sessionId), title: chats[0].name || 'Новый урок' } : null}
        onOpenSubject={id => { setSubjectId(id); setSessionId(null); }}
        onResume={() => { if (chats[0]) openLesson(chats[0].sessionId); }}
        onStartRules={() => void startRules()}
      />
    );
  }

  return (
    <PageCanvas project={canvasProject}>
      <ChildHeader crumb={timeUp ? null : subjectMeta?.label ?? null} status={timeUp ? null : eff} isMobile={isMobile} onLogout={onLogout} />

      {isMobile && (
        <div style={{ padding: `0 ${TB.padXMobile}px ${SP.sm}px` }}>
          <PillSwitch<Tab>
            variant="hub"
            isMobile
            fill
            value={tab}
            options={tabs.map(t => ({ value: t.value, label: t.label, icon: t.icon }))}
            onChange={v => {
              if (v === 'studio') return;
              if (v === 'rules') onRulesTab(); else goHome();
            }}
          />
        </div>
      )}

      <div style={{ flex: 1, minHeight: 0, display: 'flex', gap: RAIL_GAP, padding: isMobile ? 0 : `0 ${TB.padX}px ${TB.padX}px 0` }}>
        {!isMobile && (
          <RailCapsule side="left" aria-label="Разделы">
            <RailHat side="left" label={childName} title={childName} />
            <RailIconButton side="left" label="Уроки" active={tab === 'lessons'} onClick={goHome}>
              <BookOpen size={ICON_SIZE.md} strokeWidth={ICON_STROKE} />
            </RailIconButton>
            <RailIconButton side="left" label="Студия · скоро" hint="скоро" disabled wrapper={{ 'aria-disabled': true }}>
              <Brush size={ICON_SIZE.md} strokeWidth={ICON_STROKE} />
            </RailIconButton>
            <RailSep />
            <RailIconButton side="left" label="Правила" active={tab === 'rules'} onClick={onRulesTab}>
              <span style={{ position: 'relative', display: 'inline-flex' }}>
                <ListChecks size={ICON_SIZE.md} strokeWidth={ICON_STROKE} />
                {rulesPending && (
                  <span data-testid="rules-dot" style={{ position: 'absolute', top: -2, right: -3 }}><Dot color={C.accent} size={8} /></span>
                )}
              </span>
            </RailIconButton>
          </RailCapsule>
        )}

        {showSidebar && (
          <Island style={{ width: SIDEBAR_W, flexShrink: 0 }}>
            <IslandHeader title={subjectMeta?.label ?? 'Уроки'} />
            <div style={{ flex: 1, minHeight: 0, overflowY: 'auto', padding: SP.sm }}>
              {chats.length === 0 && <div style={{ padding: SP.md, color: C.textMuted, fontSize: FS.sm }}>Уроков пока нет.</div>}
              {chats.map(c => (
                <LessonRow key={c.sessionId} chat={c} active={c.sessionId === sessionId} onOpen={() => openLesson(c.sessionId)} />
              ))}
            </div>
            {activeSubject && (
              <div style={{ padding: SP.sm }}>
                <Button size="md" fullWidth onClick={() => void startLesson(activeSubject)} disabled={starting}>Новый урок</Button>
              </div>
            )}
          </Island>
        )}

        <main style={{ flex: 1, minWidth: 0, minHeight: 0, display: 'flex', flexDirection: 'column', position: 'relative' }}>
          <div style={{ padding: `0 ${isMobile ? TB.padXMobile : 0}px`, display: 'flex', flexDirection: 'column', gap: SP.xs }}>{noticeBar}</div>

          {inLesson && (!wide || isMobile || !panelInline) && (
            <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, padding: `${SP.xs}px ${isMobile ? TB.padXMobile : 0}px` }}>
              {!wide && (
                <BackButton onClick={() => setSessionId(null)} ariaLabel="К урокам" title="К урокам" />
              )}
              <div style={{ flex: 1 }} />
              <Button size="xs" variant="ghost" leftIcon={<Lightbulb size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />} onClick={() => setPanelOpen(v => !v)}>
                Как мы учимся
              </Button>
            </div>
          )}

          <div style={{ flex: 1, minHeight: 0, position: 'relative' }}>
            {center}
            {showPanel && !panelInline && (
              <div style={{ position: 'absolute', top: 0, right: 0, bottom: 0, width: PANEL_W, zIndex: 5 }}>
                <Island style={{ height: '100%', boxShadow: SHADOW.peek }}>
                  <IslandHeader title="Как мы учимся" icon={<Lightbulb size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />} />
                  <div style={{ overflowY: 'auto' }}>{hintsBody}</div>
                </Island>
              </div>
            )}
          </div>
        </main>

        {showPanel && panelInline && (
          <Island style={{ width: PANEL_W, flexShrink: 0 }}>
            <IslandHeader title="Как мы учимся" icon={<Lightbulb size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />} />
            <div style={{ overflowY: 'auto' }}>{hintsBody}</div>
          </Island>
        )}
      </div>

      {isMobile && inLesson && panelOpen && (
        <Modal title="Как мы учимся" onClose={() => setPanelOpen(false)}>{hintsBody}</Modal>
      )}

      {rulesBrief && (
        <ChildRulesOnboarding
          open
          mode={rulesPending ? 'agree' : 'review'}
          rules={rulesBrief}
          onAgreed={() => { setAgreedLocal(true); closeRules(); }}
          onClose={closeRules}
        />
      )}
    </PageCanvas>
  );
}

// ChildRulesBrief собирается из status и имени ребёнка; лимиты профиля статус не несёт,
// поэтому окна пустые, а дневной лимит — текущий остаток (форма окна онбординга не меняется)
function briefFrom(status: ChildStatus, childName: string, sessionId: string): ChildRulesBrief {
  return {
    childName,
    dailyMinutes: status.minutesLeft,
    windows: [],
    warnMinutes: status.warnMinutes,
    sessionId,
    agreedAt: status.rulesAgreedAt,
  };
}
