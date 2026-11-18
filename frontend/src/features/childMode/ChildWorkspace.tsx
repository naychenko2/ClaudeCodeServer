// KM-F10: детская оболочка — тот же каркас AI Home, только меньше (макет kids-shell-v2).
// Собрана из примитивов взрослого каркаса: шапка, рельса RailCapsule, острова Island,
// холст CanvasBackdrop. Взрослые экраны, хаб-табы, поиск, уведомления, док проектов,
// стена и взрослые панели здесь не монтируются — а значит, не шлют запросов.
// Запросы оболочки: только /api/child/* (status, heartbeat, subjects, spaces/*/chats,
// spaces/*/chat, rules/start) и то, что делает чат урока (ChildLessonChat).
//
// Т3c: предметы и предмет чата — с сервера. Плитки ChildHome получает из GET /api/child/subjects
// (порядок — порядок сервера, И5), предмет открытого чата — поле subject элемента списка чатов
// (ключ — sessionId). Предмета нет ни в клиентском хранилище, ни в фолбэке 'math': их не стало
// вместе с серверным источником. Моста волны 1 больше нет: пока список предметов
// не пришёл, плиток нет.

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
import { SidebarSection } from '../../components/ui/SidebarSection';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { PillSwitch } from '../../components/Toolbar';
import { setPersonasChildMode } from '../../lib/personas';
import { childApi } from './api';
import { useChildStatus } from './useChildStatus';
import { ChildHome, TutorMark } from './ChildHome';
import { ChildHintsPanel } from './ChildHintsPanel';
import { ChildLessonChat } from './ChildLessonChat';
import { ChildRulesOnboarding, type ChildRulesBrief } from './ChildRulesOnboarding';
import { ChildTimeBadge } from './ChildTimeBadge';
import { ChildTimeUpScreen } from './ChildTimeUpScreen';
import type { ChildChatListItem, ChildStatus, ChildSubjectColor, ChildSubjectTutor, ChildSubjectView } from './types';

// Единственное пространство волны 1 (ADR-041)
const SPACE = 'lessons';
// Ширины раскладки панели «Как мы учимся» (спека макета): >1200 — в зоне, 900–1200 — поверх, <900 — поверх без сайдбара
const PANEL_INLINE_MIN = 1200;
const SIDEBAR_MIN = 900;
const SIDEBAR_W = 280;
const PANEL_W = 300;

// Цвет холста по ключу цвета предмета — та же строка, что у палитры subjectPalette (Т3b):
// CanvasBackdrop берёт hex из AGENT_COLORS по ключу, а не пару токенов (accent = #D97757,
// он же AGENT orange; warning — тот же жёлтый ряд).
const SUBJECT_INK: Record<ChildSubjectColor, string> = { info: 'blue', rose: 'purple', success: 'green', warning: 'yellow', accent: 'orange' };
const subjectAsProject = (color: ChildSubjectColor): Project =>
  ({ id: `child-subject-${color}`, icon: { color: SUBJECT_INK[color] } }) as unknown as Project;

type Tab = 'lessons' | 'studio' | 'rules';

const isTimeUp = (s: ChildStatus) => s.state === 'time_up' || s.state === 'out_of_window' || s.state === 'budget_exhausted';

interface Props {
  auth: AuthState;
  onLogout: () => void;
}

// Шапка: логотип, крошка предмета, «спокойное время», меню аватара с одним пунктом «Выйти».
// На мобиле вместо крошки — .mobt макета Д1: аватар репетитора, его имя и строка предмета
// (шапка самого урока на мобиле скрыта, T3d — .m .chathead): хаб и есть мобильная шапка урока.
function ChildHeader({ crumb, crumbClosed, tutor, status, isMobile, onLogout }: {
  crumb: string | null; crumbClosed: boolean; tutor: ChildSubjectTutor | null; status: ChildStatus | null; isMobile: boolean; onLogout: () => void;
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
        {isMobile && tutor ? (
          <span style={{ display: 'flex', alignItems: 'center', gap: SP.sm, minWidth: 0, flex: 1 }}>
            <TutorMark tutor={tutor} size={28} />
            <span style={{ minWidth: 0 }}>
              <span style={{ display: 'block', fontSize: FS.md, fontWeight: 600, color: C.textHeading, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>{tutor.name}</span>
              {crumb && (
                <span style={{ display: 'block', fontSize: FS.sm, color: C.textMuted, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
                  {crumb}{crumbClosed ? ' · закрыт' : ''}
                </span>
              )}
            </span>
          </span>
        ) : (
          crumb && (
            <>
              <span aria-hidden style={{ color: C.textMuted }}>›</span>
              <span style={{
                fontFamily: FONT.sans, fontSize: FS.lg, fontWeight: 500, color: C.textHeading,
                overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
              }}>{crumb}</span>
            </>
          )
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
function LessonRow({ chat, subjectLabel, active, onOpen }: { chat: ChildChatListItem; subjectLabel: string | null; active: boolean; onOpen: () => void }) {
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
      {/* строка предмета (макет .lrow .p): рисуется только label, репетитор в строке списка
          не показывается. Метки «закрыт» здесь больше нет: закрытые чаты живут в группе
          «Старые уроки» (Д2) и её заголовок говорит то же самое. Метка закрытого предмета
          приходит из subjectLabel чата (ХВ2-Б1); строки нет только когда он null */}
      {subjectLabel && (
        <span style={{ display: 'block', fontSize: FS.xs, color: C.textMuted }}>
          {subjectLabel}
        </span>
      )}
    </button>
  );
}

export function ChildWorkspace({ auth, onLogout }: Props) {
  // Синхронно до эффектов дочерних чат-компонентов: те зовут ensurePersonasLoaded (взрослая ручка → 403)
  setPersonasChildMode(true);
  // Флаг живёт, пока смонтирована детская оболочка: после выхода ребёнка взрослый грузит персоны как обычно
  useEffect(() => { setPersonasChildMode(true); return () => setPersonasChildMode(false); }, []); // true повторно — для StrictMode
  const isMobile = useIsMobile();
  const w = useWindowWidth();
  const { status, offline } = useChildStatus(SPACE);
  const childName = displayNameOf(auth);

  const [tab, setTab] = useState<Tab>('lessons');
  // Выбранный предмет (плитка) — серверный ключ (slug), не литерал: с Т3c их два источника —
  // плитка и поле subject чата
  const [subjectId, setSubjectId] = useState<string | null>(null);
  const [sessionId, setSessionId] = useState<string | null>(null);
  const [chats, setChats] = useState<ChildChatListItem[]>([]);
  // null — список предметов ещё не пришёл (или его загрузка не удалась): плиток до ответа нет
  const [subjects, setSubjects] = useState<ChildSubjectView[] | null>(null);
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

  // Уроки и предметы — только когда они нужны ребёнку: не при rules_pending и не при «время вышло»
  const lessonsAllowed = !!eff && eff.state !== 'rules_pending' && !isTimeUp(eff);
  // Предметы и список уроков грузятся вместе, одной функцией и одним счётчиком: ответ
  // прежнего запроса не перетирает новый (тот же порядок, что у опроса статуса в
  // useChildStatus); при размонтировании и перезапуске in-flight отменяется AbortController.
  const loadSeq = useRef(0);
  const loadLists = useCallback(() => {
    const mine = ++loadSeq.current;
    const ac = new AbortController();
    childApi.getSubjects({ signal: ac.signal })
      .then(r => { if (loadSeq.current === mine) setSubjects(r); })
      // Сбой предметов — тот же спокойный случай, что у сбоя списка чатов: технического
      // текста нет, офлайн покажет опрос статуса; плиток нет, пока список не придёт снова.
      .catch(() => { /* офлайн покажет опрос статуса */ });
    childApi.spaceChats(SPACE, { signal: ac.signal })
      .then(r => { if (loadSeq.current === mine) setChats(r.chats); })
      .catch(() => { /* офлайн покажет опрос статуса */ });
    return () => ac.abort();
  }, []);
  useEffect(() => (lessonsAllowed ? loadLists() : undefined), [lessonsAllowed, loadLists]);

  // Предмет открытого чата — поле subject элемента списка (ключ — sessionId); предмет
  // выбранной плитки — локальный ключ. Фолбэка 'math' больше нет: нет предмета —
  // закрытый или до-предметный чат (только для чтения, И1-бис).
  const subjectViewOf = (id: string | null): ChildSubjectView | null => {
    if (!id) return null;
    return subjects?.find(s => s.id === id) ?? null;
  };
  const activeChat = sessionId ? chats.find(c => c.sessionId === sessionId) : undefined;
  const activeSubject = subjectViewOf(activeChat ? activeChat.subject : subjectId);
  const subjectClosed = !!activeChat && activeChat.subjectClosed;

  // Д2 (решение Андрея по дефекту QA-1): чаты закрытого предмета — не в общем списке уроков,
  // а в группе «Старые уроки» ниже обычных, свёрнутой по умолчанию. Порядок внутри групп —
  // порядок сервера (filter его не меняет); сам список, его открытие и режим «только
  // чтение» не меняются: меняется только то, где строка лежит и чем группа подписана.
  const openChats = chats.filter(c => !c.subjectClosed);
  const closedChats = chats.filter(c => c.subjectClosed);

  async function startLesson(subject: string) {
    if (starting) return; // двойной клик: второй игнорируется, пока первый в полёте
    setStarting(true);
    setError(null);
    try {
      const r = await childApi.spaceChat(SPACE, subject);
      setSubjectId(subject);
      setSessionId(r.sessionId);
      loadLists();
    } catch (e) {
      // Отказ по предмету (unknown_subject/subject_disabled) или по состоянию (time_up/…) —
      // спокойный серверный текст как есть (ADR-041, «Общие правила»), без технических кодов
      const msg = (e as { body?: { message?: string } }).body?.message;
      setError(msg ?? 'Сейчас урок не получится начать. Попробуй чуть позже.');
    } finally {
      setStarting(false);
    }
  }

  const openLesson = (sid: string) => { setSessionId(sid); setPanelOpen(false); };
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
  const canvasProject = !timeUp && activeSubject ? subjectAsProject(activeSubject.color) : undefined;

  const hintsBody = sessionId ? <ChildHintsPanel step={step} sessionId={sessionId} /> : null;
  const showPanel = inLesson && (panelInline || panelOpen) && !isMobile;

  // Плашка «Нет связи» видна всегда — и со статусом, и без него (первая загрузка без сети)
  const noticeBar = (
    <>
      {offline && (
        <Notice tone="danger" icon={WifiOff} role="status">Нет связи. Как только она вернётся, продолжим.</Notice>
      )}
      {!offline && eff?.state === 'warn' && !warnDismissed && (
        <Notice
          tone="warning"
          icon={Lightbulb}
          role="status"
          action={<Button size="xs" variant="ghost" onClick={() => setWarnDismissed(true)}>Хорошо</Button>}
        >
          Осталось {eff?.minutesLeft ?? eff?.warnMinutes} минут. Доделай то, что начала, и отдохни.
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

  // ХВ7 (решение Софьи по QA-2): тело списка уроков — одна разметка для двух мест:
  // острова сайдбара (широкий экран) и такого же острова под главной (узкий экран:
  // мобильная 360 и планшет 601–899 — ХВ11).
  // Строки, порядок, группировка «Старые уроки» (Д2) — общие; меняётся только обёртка.
  // Текст «Уроков пока нет.» нужен только сайдбару: на мобиле при пустом списке острова
  // нет вовсе (путь к уроку — плитка предмета).
  const lessonsBody = (
    <>
      {chats.length === 0 && <div style={{ padding: SP.md, color: C.textMuted, fontSize: FS.sm }}>Уроков пока нет.</div>}
      {openChats.map(c => (
        <LessonRow
          key={c.sessionId}
          chat={c}
          // живой список предметов главнее; поле чата — запасной источник для закрытого предмета (ХВ2-Б1)
          subjectLabel={subjectViewOf(c.subject)?.label ?? c.subjectLabel ?? null}
          active={c.sessionId === sessionId}
          onOpen={() => openLesson(c.sessionId)}
        />
      ))}
      {/* «Старые уроки · N» — та же сворачиваемая секция сайдбара, что в других
          панелях (SidebarSection): строка-кнопка с заголовком и счётчиком,
          свёрнута по умолчанию. Нет закрытых чатов — нет раздела. */}
      {closedChats.length > 0 && (
        <SidebarSection title="Старые уроки" count={closedChats.length} defaultOpen={false}>
          {closedChats.map(c => (
            <LessonRow
              key={c.sessionId}
              chat={c}
              subjectLabel={subjectViewOf(c.subject)?.label ?? c.subjectLabel ?? null}
              active={c.sessionId === sessionId}
              onOpen={() => openLesson(c.sessionId)}
            />
          ))}
        </SidebarSection>
      )}
    </>
  );

  let center: React.ReactNode;
  if (!eff) {
    center = null;
  } else if (timeUp) {
    center = <ChildTimeUpScreen status={eff} minutesToday={eff.minutesToday} />;
  } else if (inLesson && sessionId) {
    center = (
      <ChildLessonChat
        key={sessionId}
        sessionId={sessionId}
        // Предмет чата — с сервера: живой view или null (закрытый/до-предметный чат)
        subject={activeSubject}
        subjectClosed={subjectClosed}
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
        title={activeSubject.label}
        // ХВ12 (дефект 4ef0f8c5): подпись зависит от наличия боковой панели (wide), а не
        // от мобильного флага: на 601–899 панели нет (ХВ11), и прежняя подпись звала
        // «выбрать слева» там, где слева ничего нет; прошлые уроки — в списке под главной.
        // ХВ2-Ф2 (дефект 698bcb27): уроков нет вовсе — указывать негде, текст один на любой
        // ширине (дословно, утвердила Софья).
        subtitle={chats.length === 0 ? 'Начни свой первый урок.' : wide ? 'Выбери урок слева или начни новый.' : 'Начни новый урок. Прошлые уроки — на главной, в списке «Мои уроки».'}
        action={<Button size="lg" onClick={() => void startLesson(activeSubject.id)} disabled={starting}>Начать урок</Button>}
      />
    );
  } else {
    const home = (
      <ChildHome
        childName={childName}
        status={eff}
        subjects={subjects ?? []}
        lastLesson={chats[0] ? { subjectId: chats[0].subject ?? '', title: chats[0].name || 'Новый урок' } : null}
        onOpenSubject={id => { setSubjectId(id); setSessionId(null); }}
        onResume={() => { if (chats[0]) openLesson(chats[0].sessionId); }}
        onStartRules={() => void startRules()}
      />
    );
    // ХВ7: под главной встаёт тот же остров, что на широком экране рисует
    // сайдбар, — в той же прокрутке, что и главная (один scroll-контейнер на двоих).
    // Точка входа — вкладка «Уроки» или «назад» из урока: обе ведут сюда. Полей — по
    // ChildHome (макет .mlist). Уроков нет — острова нет (кадр 3). Открытый урок или
    // экран предмета — острова нет: список живёт на главной.
    // ХВ11 (миноры ревью 0fbaa524): остров — на любой ширине без боковой панели
    // (< SIDEBAR_MIN, планшет 601–899 включён), а не только на мобиле; и его нет при
    // !lessonsAllowed (rules_pending): раньше загруженный список не должен оставаться
    // мёртвыми строками.
    const lessonsIsland = !wide && lessonsAllowed && chats.length > 0 ? (
      <div data-testid="child-mobile-lessons-list" style={{ maxWidth: 760, width: '100%', margin: '0 auto', boxSizing: 'border-box', padding: `0 ${SP.lg}px ${SP.xl}px` }}>
        <Island style={{ width: '100%', overflow: 'visible' }}>
          <IslandHeader title="Мои уроки" />
          <div style={{ padding: SP.sm }}>{lessonsBody}</div>
        </Island>
      </div>
    ) : null;
    center = lessonsIsland ? <div style={{ height: '100%', overflowY: 'auto' }}>{home}{lessonsIsland}</div> : home;
  }

  return (
    <PageCanvas project={canvasProject}>
      <ChildHeader
        crumb={timeUp ? null : activeSubject?.label ?? null}
        crumbClosed={subjectClosed}
        tutor={!timeUp && inLesson ? activeSubject?.tutor ?? null : null}
        status={timeUp ? null : eff}
        isMobile={isMobile}
        onLogout={onLogout}
      />

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
            <IslandHeader title={activeSubject?.label ?? 'Уроки'} />
            <div style={{ flex: 1, minHeight: 0, overflowY: 'auto', padding: SP.sm }}>{lessonsBody}</div>
            {activeSubject && (
              <div style={{ padding: SP.sm }}>
                <Button size="md" fullWidth onClick={() => void startLesson(activeSubject.id)} disabled={starting}>Новый урок</Button>
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

// ChildRulesBrief собирается из status и имени ребёнка: лимит и окна — настоящие, из профиля
function briefFrom(status: ChildStatus, childName: string, sessionId: string): ChildRulesBrief {
  return {
    childName,
    dailyMinutes: status.dailyMinutes,
    windows: status.windows,
    warnMinutes: status.warnMinutes,
    sessionId,
    agreedAt: status.rulesAgreedAt,
  };
}
