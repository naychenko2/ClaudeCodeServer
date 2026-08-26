import { useState, useRef, useEffect, useLayoutEffect, useMemo, useCallback, Fragment, type HTMLAttributes } from 'react';
import { ArrowDown, ArrowUp, RotateCw, CircleHelp } from 'lucide-react';
import type { Project, Session, ChatItem, SkillInfo, AgentInfo, ClaudeBilling, Persona, Task, WorkLoopState, SessionTeamImplement, TeamPlanDecision } from '../types';
import { useSession } from '../hooks/useSession';
import { usePersonasVersion, getPersonaById, getPersonasSnapshot, ensurePersonasLoaded, personaLabel } from '../lib/personas';
import { findConsultedPersona } from './chat/PersonaTaskView';
import { showToast } from '../lib/toast';
import { projectMainColor } from '../features/projects/projectUtil';
import { agentDotColor } from './AgentSelector';
import { PersonaGreeting } from '../features/personas/PersonaGreeting';
import { computeTodoBatches } from '../hooks/useSessionArtifacts';
import { useChatScroll } from '../hooks/useChatScroll';
import { useOnline } from '../hooks/useOnline';
import { api, setGitSessionContext } from '../lib/api';
import { ensureGit, loadUnpushedLog } from '../lib/git';
import { slugify } from '../lib/slug';
import { parseWorkflowMeta } from '../lib/workflowMeta';
import { detectTeamMechanic, buildTeamTurnText, DEFAULT_TEAM_SETTINGS, type TeamMechanicId } from '../features/team/teamMechanics';
import {
  hasUserTurnAfter, hasLaunchedAfter, hasFailedLaunchAfter, buildMechanicOffers,
  type TeamMechanicOffer,
} from '../features/team/TeamMechanicOffer';
import {
  buildProjectPresetOffer, resolvePresetCardState, type PresetCardState,
} from '../features/onboarding/ProjectPresetOffer';
import { teamPlanningIndicatorVisible, resolvePlannerPersonaId, itemIdxToNodePos, computeJumpHidden } from '../lib/teamImplement';
import { EscalationStickyBanner, findOpenEscalations } from './chat/EscalationStickyBanner';
import { setLastMechanic } from '../lib/lastMechanic';
import { toRateWindows, worstWindow } from '../lib/rateLimit';
import { estimateContext } from '../lib/context';
import { computeTurnTree, sessionStartedBoundaries } from '../lib/turnWorktree';
import { retryableInterruptedIndex } from '../lib/chatReducer';
import { useCtxThresholds } from '../lib/contextPrefs';
import { notify } from '../lib/notify';
import { speak, stopSpeaking, primeAudio, setSpeechToast, startStreamSpeak, sanitizeForSpeech, splitSentences, type StreamSpeech } from '../lib/tts';
import { turnText, turnStreamChunks, turnStreamTail, turnVoicePersonaId, turnVoiceItemIndex, extractVoiceDigest, TURN_STREAM_INIT, type TurnStreamState } from '../lib/turnSpeechStream';
import { talkDiag } from '../lib/talkDiag';
import { needAnswer, primeBeep } from '../lib/beep';
import { voiceStyleFor, normalizeVoiceStyle, VOICE_STYLE_DIGEST, VOICE_STYLE_TALK } from '../lib/voiceStyle';
import type { SpeechPhase } from '../hooks/useHandsFree';
import { updateChatFields } from '../lib/chatUpdate';
import { type Mode, ModeIcon, MODES, isDangerMode } from '../lib/modes';
import { getDraft } from '../lib/drafts';
import { useModelCaps, assistantName, planModelChange } from '../lib/models';
import { Composer } from './Composer';
import { ProjectGitBar } from './ProjectGitBar';
import { C, R, SHADOW, SP, CHAT_MAX_W, CHAT_GUTTER_L } from '../lib/design';
import { VAR_PAD_R, VAR_SHIFT, VAR_W, useChatGutter } from '../lib/chatGutter';
import { useIsTouch } from '../lib/breakpoints';
import { projectTopWash } from '../lib/projectTone';
import { setChatContext, AI_RECOMPUTE_EVENT } from '../lib/ai/chatContext';
import { setFabObstacle } from '../lib/ai/fabObstacle';
import { ChatHeaderBar, type CostStats, type FalCostStats } from './chat/ChatHeaderBar';
import { computeGlifGenStats } from './chat/glifStats';
import { ChatProjectContext, ChatTreePathContext, ChatSessionContext, ChatOpenFileContext, ChatOpenReaderContext, ChatOpenTaskContext, FalCostContext, GlifCostContext, AssistantNameContext, MediaVisibilityContext, PersonaContext, SpeakingItemContext, TeamPlanContext, TeamEscalationContext, type TeamPlanChatContext, type TeamEscalationChatContext } from './chat/contexts';
import { WaitingIndicator } from './ui/WaitingIndicator';
import { TurnPlanPill } from './chat/TurnPlanPill';
import { Modal, ModalActions, ConfirmDialog, Button } from './ui';
import { ICON_SIZE, ICON_STROKE } from './ui/icons';
import { ChatEmptyState } from './chat/EmptyState';
import { AttachPicker } from './chat/AttachPicker';
import { ToolGroupBlock, AgentActionsBlock, itemKey, type ActivityEntry } from './chat/timeline';
import { splitAgentResultTail } from '../lib/agentTail';
import { ChatItemView, FileChangedRow } from './chat/ChatItemView';
import { PendingMessageList } from './chat/PendingMessageView';
import { type ToolUseItem } from './chat/ToolUseView';
import { buildMediaVisibility } from './chat/mediaDedup';
import { isTasksCreate } from './chat/TaskCreatedView';
import { isWidgetShow } from './chat/WidgetView';
import { WorkflowBlockView } from './chat/WorkflowBlockView';
import { DeployProgressCard } from './chat/DeployProgressCard';
import { isDeployStart } from '../lib/deployProgress';
import { TeamPlanningIndicator } from './chat/TeamPlanningIndicator';

// Боковой отступ мобильной ленты: чуть шире стандартных 12px, чтобы кольца «Эхо»
// индикатора ожидания не резались клипом области прокрутки (overflow-x: hidden).
// Лента, композер и кнопка «вниз» держат ОДНО значение — иначе их левые края
// разъедутся. Десктоп пользуется полем CHAT_GUTTER_L, этот отступ — только мобила.
// Значения сейчас совпадают, но роли разные: мобильное поле держит ширину экрана,
// десктопное — размах колец индикатора. Сливать в одно не надо.
const CHAT_GUTTER_MOBILE = 16;

interface Props {
  session: Session;
  // Отсутствует для чата вне проекта (project-less) — тогда скрываем файловые возможности
  project?: Project;
  onOpenFile?: (path: string) => void;
  // Открыть URL в панели «Чтение» — из кнопки-компаньона у внешней ссылки (флаг link-reader).
  // Отсутствует — MarkdownContent не рисует кнопку вовсе, лента как без фичи
  onOpenReader?: (url: string) => void;
  // Открыть задачу СПРАВА от ленты (split чат|задача в центре воркспейса) — из карточки
  // доклада о выполнении. Отсутствует — рядом места нет (мобила, планшет, чат вне
  // воркспейса), и карточка откроет детали модалкой
  onOpenTaskAside?: (task: Task) => void;
  pendingMessage?: string;
  onPendingMessageSent?: () => void;
  onSessionUpdated?: (session: Session) => void;
  isMobile?: boolean;
  onBack?: () => void;
  onWorkflowRunning?: (active: boolean, sessionId: string) => void;
  onOpenSidebar?: () => void;
  skills?: SkillInfo[];
  // .md-агенты Claude проекта — для единого селектора собеседника и индикации в шапке
  agents?: AgentInfo[];
  attachedFiles: string[];
  onAttachedFilesChange: (files: string[]) => void;
  // Приветственный бабл персоны: показывается в пустом чате вместо обычного empty state
  // (чисто визуально, в бэкенд не отправляется). Как только пойдут реальные сообщения — исчезает.
  greetingBubble?: React.ReactNode;
  // Стиль Islands: чат живёт БЕЗ рамки прямо на холсте (корень прозрачный),
  // а шапка выделена в собственную карточку-остров с зазором снизу
  headerIsland?: boolean;
  // Режим «Стены» (WallColumn): на экране НЕСКОЛЬКО инстансов ChatPanel разом, поэтому
  // глобальные синглтоны одного хозяина (setGitSessionContext, setChatContext,
  // --cc-fab-bottom) не трогаем — иначе инстансы перебивают друг друга, а анмаунт
  // любого сбрасывает контекст всем. Git-бар скрыт (воркспейсный инструмент);
  // шапка чата — штатная (канонический вид), ярлык колонки рисует WallColumn.
  embedded?: boolean;
  // Растущий счётчик «поставь курсор в поле ввода» (колонка стены стала активной)
  composerFocusSignal?: number;
  // Атрибуты перетаскивания для ШАПКИ чата (колонка стены): за неё двигают саму
  // колонку — так же, как за её ярлык. Тащить карточку принято за её верх, и шапка
  // чата — самая заметная его часть.
  headerDragProps?: HTMLAttributes<HTMLDivElement>;
}

// Предел одной загрузки — совпадает с RequestSizeLimit эндпоинта загрузки вложений
// Окно рендера ленты (см. hiddenCount/visibleNodes): в DOM живут только последние
// WINDOW_FIRST узлов — длинные чаты (тысячи элементов, десятки тысяч узлов DOM)
// больше не держат главный поток секундами при открытии. Более ранняя история
// догружается пачками по WINDOW_STEP: кнопкой «Показать предыдущие» или
// прокруткой к самому верху (IntersectionObserver на якоре).
const WINDOW_FIRST = 50;
const WINDOW_STEP = 50;

const MAX_UPLOAD_BYTES = 100 * 1024 * 1024;
const TOO_BIG_MSG = 'Файл больше 100 МБ — такой пока не загрузим';
const UPLOAD_FAIL_MSG = 'Не удалось загрузить файл. Попробуйте ещё раз';

// Фаза работы режима «План» — выводится из ленты, mode и isWaiting (сервер фазу не присылает)
type PlanPhase = 'review' | 'executing' | 'done' | 'replanning' | 'planning' | 'idle' | null;

function derivePlanPhase(items: ChatItem[], mode: Mode, isWaiting: boolean): PlanPhase {
  // «Текущий ход» — от последнего user_message
  let turnStart = -1;
  for (let i = items.length - 1; i >= 0; i--) {
    if (items[i].kind === 'user_message') { turnStart = i; break; }
  }
  const turn = turnStart >= 0 ? items.slice(turnStart) : items;

  // Незакрытый запрос на согласование — на согласовании
  const pendingReview = items.some(it => it.kind === 'plan_review' && !it.resolved);
  if (pendingReview) return 'review';

  // Последний plan_review (по всей ленте) и его позиция
  let lastReviewIdx = -1;
  for (let i = items.length - 1; i >= 0; i--) {
    if (items[i].kind === 'plan_review') { lastReviewIdx = i; break; }
  }
  if (lastReviewIdx >= 0) {
    const lastReview = items[lastReviewIdx] as Extract<ChatItem, { kind: 'plan_review' }>;
    if (lastReview.approved) {
      const hasResultAfter = items.slice(lastReviewIdx + 1).some(it => it.kind === 'result');
      if (hasResultAfter) return 'done';
      if (isWaiting) return 'executing';
    } else if (lastReview.resolved && lastReview.approved === false && isWaiting) {
      return 'replanning';
    }
  }

  if (mode === 'plan' && isWaiting) {
    const reviewInTurn = turn.some(it => it.kind === 'plan_review');
    if (!reviewInTurn) return 'planning';
  }
  if (mode === 'plan') return 'idle';
  return null;
}

// Стабильный кеш-объект хода по ссылке result-элемента. result приходит один раз
// в конце хода и при последующих стрим-дельтах не пересоздаётся, значит запись
// создаётся единожды и переживает любой пересчёт turnMeta. Иначе новая ссылка
// {read,creation} на каждую дельту пробивала React.memo у всех карточек хода.
function memoizedCacheEntry(
  map: WeakMap<ChatItem, { read: number; creation: number }>,
  result: ChatItem,
  read: number,
  creation: number,
): { read: number; creation: number } {
  const cached = map.get(result);
  if (cached) return cached;
  const entry = { read, creation };
  map.set(result, entry);
  return entry;
}

export function ChatPanel({ session, project, onOpenFile, onOpenReader, onOpenTaskAside, pendingMessage, onPendingMessageSent, onSessionUpdated, isMobile, onBack, onWorkflowRunning, onOpenSidebar, skills, agents, attachedFiles, onAttachedFilesChange, greetingBubble, headerIsland, embedded, composerFocusSignal, headerDragProps }: Props) {
  const { items, isWaiting, isJoined, isHistoryLoading, rateLimits, isCompacting, compactNote, workLoop: liveWorkLoop, teamImplement: liveTeamImplement, teamPlanning: liveTeamPlanning, teamWavePulse, promptSuggestion, pending, composerRestore, consumeRestore, send, allowPermission, denyPermission, allowAlways, answerQuestion, respondPlan, respondTeamPlan, respondTeamEscalation, interrupt, compact, toggleThinking, noteCompanionSwitch, cancelPending, preemptForPending } = useSession(session.id, project?.id, (session.participants?.length ?? 0) > 1);
  // Открылся пустой чат (только что создан — своей истории у него нет) — курсор сразу
  // в поле ввода: сюда пришли писать, а не читать. Решение принимаем один раз на чат и
  // только ПОСЛЕ загрузки истории: до неё items пуст у любого чата, и фокус улетал бы
  // и в чат с перепиской. Тач-устройства (pointer: coarse — телефон и планшет; планшет
  // бывает шире порога мобильной раскладки, поэтому гейтим по типу указателя, а не по
  // ширине) не трогаем — фокус поднимает экранную клавиатуру поверх ленты; стену тоже
  // (embedded): там курсор забирает только активная колонка, сигналом composerFocusSignal.
  const isTouch = useIsTouch();
  const [emptyChatFocus, setEmptyChatFocus] = useState(0);
  const focusDecidedFor = useRef<string | null>(null);
  useEffect(() => {
    if (isMobile || isTouch || embedded || isHistoryLoading) return;
    if (focusDecidedFor.current === session.id) return;
    focusDecidedFor.current = session.id;
    if (items.length > 0) return;
    setEmptyChatFocus(n => n + 1);
  }, [session.id, isHistoryLoading, items.length, isMobile, isTouch, embedded]);

  // Цикл «до готово» (флаг work-loop): live-состояние из событий work_loop,
  // до первого события — из Session.workLoop; null — цикл выключен
  const workLoopState = useMemo<WorkLoopState | null>(() => {
    if (liveWorkLoop !== undefined) return liveWorkLoop.active ? liveWorkLoop : null;
    return session.workLoop
      ? { active: true, iteration: session.workLoop.iteration, maxIterations: session.workLoop.maxIterations, phase: session.workLoop.phase }
      : null;
  }, [liveWorkLoop, session.workLoop]);
  const handleToggleWorkLoop = useCallback(async () => {
    try {
      const updated = await api.chats.setWorkLoop(session.id, !workLoopState);
      onSessionUpdated?.(updated);
    } catch (err) {
      showToast('Цикл «до готово»', err instanceof Error ? err.message : 'Не удалось переключить цикл');
      throw err;
    }
  }, [session.id, workLoopState, onSessionUpdated]);

  // Голосовой режим чата: короткий формат ответа (секция промпта + оговорка персоны на
  // бэке) + озвучка ответа. Прайминг аудио прямо в обработчике клика — политика autoplay
  // разрешает воспроизведение только после пользовательского жеста.
  const voiceMode = session.voiceMode === true;
  // Стиль озвучки живёт ЗДЕСЬ, а не в модуле-хранилище: значение читают четыре места в
  // двух компонентах (ветка кнопки и плашка состояния в Composer, гейт стрима и ветка
  // озвучки здесь). Голые функции чтения/записи не реактивны — выбор пункта в меню
  // записал бы localStorage и не перерисовал ни кнопку, ни гейты
  // Стиль НЕ настраивается — выводится из ширины экрана (см. lib/voiceStyle.ts)
  const voiceStyle = voiceStyleFor(isMobile === true);
  const voiceDigest = voiceMode && voiceStyle === VOICE_STYLE_DIGEST;
  // Чат, чей стиль уже выправлен на сервере в этот заход (см. эффект синхронизации ниже)
  const styleSyncedRef = useRef<string | null>(null);
  // Стиль сменился на лету (окно растянули, планшет повернули) — снять гард, чтобы
  // эффект ниже выправил значение на сервере: иначе ход собрался бы в прежнем формате
  const prevStyleRef = useRef(voiceStyle);
  if (prevStyleRef.current !== voiceStyle) {
    prevStyleRef.current = voiceStyle;
    styleSyncedRef.current = null;
  }
  // Фаза озвучки для режима разговора: петля в композере открывает микрофон только когда
  // она вернулась в idle. Ведётся С ТОКЕНОМ вызова (Р12): новый speak внутри себя зовёт
  // stopSpeaking(), и finally предыдущего иначе сбросил бы фазу уже начавшейся озвучки —
  // микрофон открылся бы под играющее аудио
  const [speechPhase, setSpeechPhase] = useState<SpeechPhase>('idle');
  const speechCallRef = useRef(0);
  // personaId — чьим голосом читать; передаёт вызывающий, у которого под рукой ход
  // (колбэк стабильный, без зависимостей — брать персону из замыкания здесь нечем)
  // Чей голос звучит ПРЯМО СЕЙЧАС — захваченная на ход персона. Отсюда питаются оба
  // визуальных эффекта «кто говорит»: кольцо у её аватара в ленте и цвет сияния над
  // композером (см. activeSpeaker). Захват на ход, а не пересчёт по ленте: голос тоже
  // берётся один раз (пакеты синтеза уходят вперёд), и подсветка обязана совпадать с ним
  const [speakingPersonaId, setSpeakingPersonaId] = useState<string | null>(null);
  const startSpeaking = useCallback((text: string, personaId?: string) => {
    const call = ++speechCallRef.current;
    // Синхронно, ДО первого await: в кадре завершения хода петля обязана видеть, что
    // озвучка будет, иначе успеет открыть микрофон ровно под старт синтеза
    setSpeechPhase('willSpeak');
    setSpeakingPersonaId(personaId ?? null);
    void (async () => {
      try {
        setSpeechPhase('speaking');
        await speak(text, personaId, session.id);
      } finally {
        // Токен тот же, что у фазы: поздний finally осиротевшего вызова не должен
        // гасить подсветку уже начавшейся озвучки
        if (speechCallRef.current === call) { setSpeechPhase('idle'); setSpeakingPersonaId(null); }
      }
    })();
  }, []);
  // Прервать озвучку: заодно осиротить текущий вызов, чтобы его finally не трогал фазу
  const stopSpeech = useCallback(() => {
    speechCallRef.current++;
    stopSpeaking();
    setSpeechPhase('idle');
    setSpeakingPersonaId(null);
  }, []);
  // Значение ПРИХОДИТ от композера: запрошенное состояние (PUT ещё в полёте) знает
  // только он — там же живёт петля разговора. Свой ref здесь был вторым источником
  // правды и после провала PUT или смены чата инвертировал режим
  // Выправить стиль на сервере, если он остался от ДРУГОГО устройства. Гейт по voiceMode
  // обязателен: дефолт digest на широком экране расходится со стилем каждого старого чата,
  // и без него PUT (то есть перезапись всего sessions.json) уходил бы на открытие любого
  // чата, включая те, где озвучкой не пользовались. При выключенной озвучке стиль на
  // сервере никого не интересует — секцию промпта по нему не выбирают.
  // Один PUT на открытие чата: onSessionUpdated вернул бы новый объект сессии и эффект
  // пошёл бы по кругу. Ответ намеренно ИГНОРИРУЕМ — поздний ответ, разошедшийся с тапом
  // по кнопке, вернул бы voiceMode прошлого состояния и перевернул её (см. комментарий
  // к handleToggleVoiceMode ниже). Ошибки молча: чинить тут человеку нечего
  useEffect(() => {
    if (!voiceMode) return;
    if (normalizeVoiceStyle(session.voiceStyle) === voiceStyle) return;
    if (styleSyncedRef.current === session.id) return;
    styleSyncedRef.current = session.id;
    void updateChatFields(session, { voiceStyle }).catch(() => {});
  }, [session, voiceMode, voiceStyle]);

  const handleToggleVoiceMode = useCallback(async (next: boolean) => {
    primeAudio();
    // Разбудить аудиоконтекст сигналов в том же жесте: сигнал «нужно решение» может
    // понадобиться и без запуска петли, а вне жеста браузер контекст не пустит
    primeBeep();
    try {
      // Стиль уходит вместе с включением: серверу он нужен уже на первом ходу, а
      // sync-эффект выше сработал бы только на следующем открытии чата
      const updated = await updateChatFields(session, next ? { voiceMode: true, voiceStyle } : { voiceMode: false });
      styleSyncedRef.current = session.id;
      onSessionUpdated?.(updated); // без этого кнопка не перерисуется
      if (!next) stopSpeech();
    } catch (err) {
      showToast('Голосовой режим', err instanceof Error ? err.message : 'Не удалось переключить режим');
      // Прокидываем: без флага на сервере эффект озвучки молчит (он гейтится voiceMode),
      // и петля разговора крутилась бы вхолостую — композер по ошибке её гасит
      throw err;
    }
  }, [session, onSessionUpdated, stopSpeech, voiceStyle]);

  // Режим «Командная реализация»: live-состояние из событий team_implement,
  // до первого события — из Session.teamImplement; null — режим выключен
  const teamImplementState = useMemo<SessionTeamImplement | null>(() => {
    if (liveTeamImplement !== undefined) {
      // Деструктуризация снимает поле active (TeamImplementState → SessionTeamImplement);
      // проверка active сразу использует её, не оставляя неиспользуемую переменную
      const { active, ...rest } = liveTeamImplement;
      if (!active) return null;
      return rest;
    }
    return session.teamImplement ?? null;
  }, [liveTeamImplement, session.teamImplement]);
  // Плашка «Команда готовит план…» в ленте: живёт на стадии планирования и гаснет
  // с карточкой плана/отказа (см. teamPlanningIndicatorVisible)
  const showTeamPlanningIndicator = useMemo(
    () => teamPlanningIndicatorVisible(teamImplementState, items, liveTeamPlanning),
    [teamImplementState, items, liveTeamPlanning],
  );
  const handleToggleTeamImplementAuto = useCallback(async () => {
    if (!teamImplementState) return;
    try {
      const updated = await api.chats.setTeamImplementAuto(session.id, !teamImplementState.autoWaves);
      onSessionUpdated?.(updated);
    } catch (err) {
      showToast('Командная реализация', err instanceof Error ? err.message : 'Не удалось переключить авто-волны');
    }
  }, [session.id, teamImplementState, onSessionUpdated]);
  // Включение режима из карточки механики «Командная реализация» (композер): состав
  // пустой = вся команда проекта, координатора бэкенд берёт из собеседника чата.
  // Ошибку ПРОКИДЫВАЕМ после тоста (M11): композер по ней отменяет отправку вводной —
  // иначе при провале включения текст задачи утекал обычным сообщением в обычный чат
  const handleEnableTeamImplement = useCallback(async (opts: { autoWaves: boolean; executorPersonaIds: string[] }) => {
    try {
      const updated = await api.chats.setTeamImplement(session.id, true, {
        autoWaves: opts.autoWaves,
        executorPersonaIds: opts.executorPersonaIds,
      });
      onSessionUpdated?.(updated);
    } catch (err) {
      showToast('Командная реализация', err instanceof Error ? err.message : 'Не удалось включить режим');
      throw err;
    }
  }, [session.id, onSessionUpdated]);
  const handleDisableTeamImplement = useCallback(async () => {
    try {
      const updated = await api.chats.setTeamImplement(session.id, false);
      onSessionUpdated?.(updated);
      showToast('Командная реализация', 'Режим выключен — чат стал обычным разговором');
    } catch (err) {
      showToast('Командная реализация', err instanceof Error ? err.message : 'Не удалось выключить режим');
    }
  }, [session.id, onSessionUpdated]);
  // «Остановить»: режим остаётся включённым, но новые волны не стартуют — карточку
  // остановки с «Продолжить» публикует бэкенд, она приезжает событием в ленту
  const handleStopTeamImplement = useCallback(async () => {
    try {
      const updated = await api.chats.stopTeamImplement(session.id);
      onSessionUpdated?.(updated);
      showToast('Командная реализация', 'Практика остановлена — новые волны не стартуют');
    } catch (err) {
      showToast('Командная реализация', err instanceof Error ? err.message : 'Не удалось остановить практику');
    }
  }, [session.id, onSessionUpdated]);

  // === Отдельное git worktree чата ===
  // Пока активен чат в worktree, все git-запросы проекта несут его sessionId —
  // бар/панель «Изменения» показывают и мутируют дерево чата, не корень проекта
  useEffect(() => {
    // embedded: git-контекст — глобальный синглтон проекта, на стене им владеет воркспейс
    if (!project || embedded) return;
    setGitSessionContext(project.id, session.worktreePath ? session.id : null);
    return () => setGitSessionContext(project.id, null);
  }, [project, session.id, session.worktreePath, embedded]);
  const [worktreeForceConfirm, setWorktreeForceConfirm] = useState(false);
  // Предупреждение перед сменой дерева (в обе стороны): переезд меняет рабочую папку
  // агента — без объяснения тумблер выглядит «кнопкой-сюрпризом»
  const [worktreeConfirm, setWorktreeConfirm] = useState(false);
  // Имя ветки нового дерева — предзаполняется авто-вариантом (slug имени чата,
  // как посчитает бэкенд), юзер может поправить перед подтверждением
  const [worktreeBranchInput, setWorktreeBranchInput] = useState('');
  const openWorktreeConfirm = useCallback(() => {
    setWorktreeBranchInput(`wt/${slugify(session.name ?? '') || session.id.slice(0, 8)}`);
    setWorktreeConfirm(true);
  }, [session.name, session.id]);
  const handleToggleWorktree = useCallback(async (force = false, branch?: string) => {
    const enabling = !session.worktreePath;
    try {
      const updated = await api.chats.setWorktree(session.id, enabling, branch, force);
      onSessionUpdated?.(updated);
      if (project) {
        // Контекст сменился — статус/стек незапушенных перечитать из нового дерева
        setGitSessionContext(project.id, updated.worktreePath ? updated.id : null);
        void ensureGit(project.id, true);
        void loadUnpushedLog(project.id);
      }
      showToast('Отдельное дерево', enabling
        ? `Чат работает в ветке ${updated.worktreeBranch ?? ''}`.trim()
        : 'Чат вернулся в основное дерево проекта');
    } catch (err) {
      const msg = err instanceof Error ? err.message : 'Не удалось переключить дерево';
      // Гейт незакоммиченных правок: предлагаем принудительное удаление
      if (!enabling && !force && msg.includes('несохранённые изменения')) {
        setWorktreeForceConfirm(true);
        return;
      }
      showToast('Отдельное дерево', msg);
    }
  }, [session.id, session.worktreePath, project, onSessionUpdated]);
  // Окна лимитов подписки (из rate_limit-телеметрии) — для индикатора в бейдже и строки у composer
  const rateWindows = useMemo(() => toRateWindows(rateLimits), [rateLimits]);
  const worstRate = useMemo(() => worstWindow(rateWindows), [rateWindows]);
  // Оценка заполнения контекстного окна — по последнему result-элементу ленты
  const ctxThresholds = useCtxThresholds();
  const ctxEstimate = useMemo(() => estimateContext(items, session.model, ctxThresholds), [items, session.model, ctxThresholds]);
  // Возможности провайдера модели (UI скрывает недоступное)
  const caps = useModelCaps(session.model);
  // Сжимать имеет смысл только когда набралось достаточно ходов (иначе CLI вернёт «not enough messages»)
  const canCompact = useMemo(
    () => caps.supportsCompact && items.filter(it => it.kind === 'result').length >= 2,
    [items, caps.supportsCompact]);
  const online = useOnline();

  // === Персона чата ===
  // Резолвим персону сессии из стора (реактивно — при обновлении списка перечитываем).
  const personasVersion = usePersonasVersion();
  const persona = useMemo(
    () => session.personaId ? getPersonaById(session.personaId) ?? null : null,
    // eslint-disable-next-line react-hooks/exhaustive-deps -- personasVersion — версия внешнего стора: бамп заставляет перечитать getPersonaById (стор нереактивен сам по себе)
    [session.personaId, personasVersion]
  );
  // Имя ассистента чата для строк UI (провайдится в контекст ниже): у чата с персоной —
  // её имя, иначе — имя провайдера модели.
  const asstName = persona?.name || assistantName(session.model);
  useEffect(() => { void ensurePersonasLoaded(); }, []);
  // Участники группового чата (резолв из стора персон); < 2 — обычный чат
  const participantPersonas = useMemo(
    () => (session.participants ?? [])
      .map(id => getPersonaById(id))
      .filter((p): p is Persona => !!p),
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [session.participants, personasVersion]
  );

  // === Кто сейчас говорит ===
  // ОДИН источник на оба эффекта: кольцо у аватара её реплики в ленте и цвет aurora-
  // сияния над композером. Гейт по фазе, а не по одному speakingPersonaId: захваченная
  // персона переживает конец озвучки, и без гейта свет горел бы после того, как смолкло.
  // Персона не резолвится (стор не загружен, чат ведёт голос инстанса) — эффекта нет:
  // цвета взять неоткуда, а серый пульс врал бы про говорящего.
  const activeSpeaker = useMemo(() => {
    if (speechPhase === 'idle' || !speakingPersonaId) return null;
    const p = getPersonaById(speakingPersonaId);
    if (!p) return null;
    return { color: agentDotColor(p.avatar?.color), index: turnVoiceItemIndex(items, speakingPersonaId, session.personaId) };
    // eslint-disable-next-line react-hooks/exhaustive-deps -- personasVersion: стор персон нереактивен, бамп заставляет перечитать getPersonaById
  }, [speechPhase, speakingPersonaId, items, session.personaId, personasVersion]);
  // Значение контекста ленты: подсвечиваемой реплики может не быть вовсе (её ход уже
  // сменился, говорит не автор последней реплики) — тогда светится только композер
  const speakingItem = useMemo(
    () => (activeSpeaker && activeSpeaker.index !== null ? { index: activeSpeaker.index, color: activeSpeaker.color } : null),
    [activeSpeaker]);

  const isGroupChat = participantPersonas.length > 1;
  // Есть ли уже ходы — назначать/менять персону можно только у пустого чата (бэкенд иначе 400)
  // Персоны, доступные в контексте чата (глобальные + этого проекта) — для селектора,
  // пилюль пустого состояния и форка. Грузим всегда: смена собеседника разрешена и по
  // ходу разговора, а на пустом списке селектор не рисуется вовсе — раньше из-за этого
  // в начатом чате без персоны выбрать её было негде.
  const [ctxPersonas, setCtxPersonas] = useState<Persona[]>([]);
  useEffect(() => {
    if (!online) return;
    let alive = true;
    api.personas.list({ scope: 'context', projectId: project?.id })
      .then(list => { if (alive) setCtxPersonas(list); })
      .catch(() => { /* персоны — необязательная фича */ });
    return () => { alive = false; };
  }, [online, project?.id]);

  // Назначить/сменить/снять собеседника чата: персона либо .md-агент — взаимоисключающе
  // (проектная сессия ↔ чат вне проекта — разные эндпоинты). Разрешено и по ходу
  // разговора — тогда в ленту добавляется локальный разделитель «Теперь отвечает: …».
  const handleCompanionChange = useCallback(async (sel: { persona?: Persona | null; agent?: AgentInfo | null }) => {
    const personaId = sel.persona?.id ?? null;
    const agentName = sel.agent?.fileName ?? null;
    try {
      const updated = project
        ? await api.personas.assignPersonaToSession(project.id, session.id, personaId, agentName)
        : await api.personas.assignPersonaToChat(session.id, personaId, agentName);
      onSessionUpdated?.(updated);
      if (items.length > 0) {
        const label = sel.persona ? personaLabel(sel.persona)
          : sel.agent ? sel.agent.name
          : 'обычный ассистент';
        // Прежняя персона «замораживается» как автор уже написанных реплик
        noteCompanionSwitch(label, session.personaId ?? null);
      }
    } catch (e) {
      showToast('Собеседник', e instanceof Error ? e.message : 'Не удалось сменить собеседника', 'info');
    }
  }, [project, session.id, session.personaId, onSessionUpdated, items.length, noteCompanionSwitch]);

  // Обратная совместимость для пилюль «Поговорить с…» пустого состояния (выбор только персоны)
  const handlePersonaChange = useCallback(
    (p: Persona | null) => handleCompanionChange({ persona: p, agent: null }),
    [handleCompanionChange]
  );

  // Групповой чат: создаём НОВЫЙ чат с 2-8 участниками
  // и уводим пользователя в него. Ведущая проектная → сессия текущего проекта,
  // глобальная → чат вне проекта (переход в раздел «Чаты»).
  const handleCreateGroup = useCallback(async (personaIds: string[]) => {
    try {
      const chat = await api.chats.createGroup(personaIds);
      if (chat.projectId) {
        // Сессия в текущем проекте — WorkspacePage откроет её по событию
        window.dispatchEvent(new CustomEvent('cc-open-project-session', { detail: { session: chat } }));
      } else {
        localStorage.setItem('cc_open_chat', chat.id);
        window.dispatchEvent(new CustomEvent('cc-open-chat', { detail: { chatId: chat.id } }));
      }
    } catch (e) {
      showToast('Групповой чат', e instanceof Error ? e.message : 'Не удалось создать групповой чат', 'info');
    }
  }, []);

  // Выбранный .md-агент чата (Session.agentName) — для селектора и индикации в шапке.
  // Если агента нет в списке (файл удалили/вне проекта) — показываем имя как есть.
  const chatAgent = useMemo(
    () => session.agentName
      ? agents?.find(a => a.fileName === session.agentName)
        ?? { name: session.agentName, color: undefined as string | undefined }
      : null,
    [session.agentName, agents]
  );

  // Приветственный пузырь персоны для пустого чата (если у персоны задан greeting).
  // Идёт НАД empty state, не вместо него (ряд персон и пилюли настройки нужны и с
  // приветствием); явный greetingBubble-проп по-прежнему занимает пустую ленту целиком.
  const personaGreeting = useMemo(
    () => (persona && persona.greeting?.trim() ? <PersonaGreeting persona={persona} /> : undefined),
    [persona]
  );


  const [hasCLAUDEmd, setHasCLAUDEmd] = useState<boolean | null>(null);
  useEffect(() => {
    // Для чата вне проекта файлов нет — баннер CLAUDE.md не показываем
    const projectId = project?.id;
    // eslint-disable-next-line react-hooks/set-state-in-effect -- guard: без проекта баннер CLAUDE.md не нужен
    if (!projectId) { setHasCLAUDEmd(false); return; }
    api.files.list(projectId)
      .then(files => setHasCLAUDEmd(files.some(f => !f.isDirectory && f.name === 'CLAUDE.md')))
      .catch(() => setHasCLAUDEmd(true)); // при ошибке не показываем баннер
  }, [project?.id]);

  // Точная стоимость генераций fal.ai: requestId → списанная сумма (для подписи под медиа).
  // Источник — fal_cost-элементы ленты (backend опрашивает billing-events). Дедуп по requestId.
  const falCostByRequest = useMemo(() => {
    const map = new Map<string, number>();
    for (const it of items)
      if (it.kind === 'fal_cost' && !map.has(it.requestId)) map.set(it.requestId, it.costUsd);
    return map;
  }, [items]);

  // Накопительная стоимость fal.ai по сессии: сумма, число генераций, разбивка по моделям.
  const falCostStats = useMemo<FalCostStats>(() => {
    const byModel = new Map<string, { count: number; cost: number }>();
    let total = 0, count = 0;
    const seen = new Set<string>();
    for (const it of items) {
      if (it.kind !== 'fal_cost' || seen.has(it.requestId)) continue;
      seen.add(it.requestId);
      total += it.costUsd;
      count++;
      const key = it.endpointId ?? 'unknown';
      const m = byModel.get(key) ?? { count: 0, cost: 0 };
      m.count++; m.cost += it.costUsd;
      byModel.set(key, m);
    }
    return { total, count, byModel };
  }, [items]);

  // Списанные кредиты glif: jobId → кредиты (для подписи под медиа). Только элементы
  // с известным billing — у остальных credits нет, и в карту они не попадают.
  const glifCostByJob = useMemo(() => {
    const map = new Map<string, number>();
    for (const it of items)
      if (it.kind === 'glif_cost' && typeof it.credits === 'number' && !map.has(it.jobId))
        map.set(it.jobId, it.credits);
    return map;
  }, [items]);

  // Накопительный счётчик генераций glif по сессии: число, разбивка по типам, сумма кредитов.
  const glifGenStats = useMemo(() => computeGlifGenStats(items), [items]);

  // Тип оплаты Claude (подписка/api) — глобальная настройка; влияет на подачу стоимости Claude
  const [claudeBilling, setClaudeBilling] = useState<ClaudeBilling>('subscription');
  useEffect(() => {
    api.settings.get().then(s => { if (s?.claudeBilling) setClaudeBilling(s.claudeBilling); }).catch(() => {});
  }, []);
  // Настройка серверная и общая для всех — менять её вправе только админ (PUT /api/settings
  // закрыт ролью). Обычному пользователю показываем режим, но без переключателя.
  const canEditBilling = (localStorage.getItem('cc_role') || sessionStorage.getItem('cc_role')) === 'admin';
  const changeBilling = useCallback((b: ClaudeBilling) => {
    setClaudeBilling(b);
    // Шлём только своё поле — остальные настройки сервер не трогает (PUT работает как патч)
    api.settings.save({ claudeBilling: b }).catch(() => {});
  }, []);

  const [mode, setMode] = useState<Mode>(session.mode);
  // Выбор режима сразу уезжает в сессию: иначе он жил бы только в состоянии этой вкладки
  // и терялся при уходе со страницы (при возврате ChatPanel перечитывает session.mode).
  // Локальный setMode делаем сразу — переключатель не должен ждать сеть; ход всё равно
  // передаёт режим ещё раз, так что неудачный запрос не оставит расхождения. Бэкенд в
  // SetMode перенастраивает и живой ход на лету (control-протокол set_permission_mode).
  const changeMode = useCallback((m: Mode) => {
    const prev = mode;
    setMode(m);
    api.chats.setMode(session.id, m)
      // Обновляем объект сессии у родителя: он держит его в своём состоянии и при
      // возврате в чат отдаёт обратно пропсом. Без этого сервер уже знает новый режим,
      // а список сессий — ещё старый, и перемонтированная панель показывает прежний.
      .then(updated => onSessionUpdated?.(updated))
      .catch(err => {
        // Бэкенд отклонил смену (например, штаб «Командной реализации» держит план-режим,
        // Э8) — откатываем оптимистичный выбор и объясняем причину, а не молчим
        setMode(prev);
        showToast('Режим чата', err instanceof Error ? err.message : 'Не удалось сменить режим');
      });
  }, [mode, session.id, onSessionUpdated]);

  // Постоянные разрешения чата («Всегда разрешать Bash в этом чате»). Источник правды —
  // сессия с бэка; локальная копия нужна, чтобы список менялся сразу по нажатию, не дожидаясь
  // перезапроса сессии. Поле может отсутствовать вовсе (старый ответ/бэкенд без правки) —
  // тогда список пуст и блок не рисуется.
  const [autoAllowTools, setAutoAllowTools] = useState<string[]>(session.autoAllowTools ?? []);
  const serverAutoAllow = (session.autoAllowTools ?? []).join('\n');
  useEffect(() => {
    setAutoAllowTools(session.autoAllowTools ?? []);
    // eslint-disable-next-line react-hooks/exhaustive-deps -- сверяемся со СТРОКОЙ состава: массив приходит новым объектом на каждый перезапрос сессии и сбрасывал бы список без нужды
  }, [session.id, serverAutoAllow]);

  // «Всегда разрешать X» из карточки запроса: имя инструмента знает только карточка,
  // поэтому она его и передаёт — иначе пришлось бы искать пункт ленты по requestId
  const handleAllowAlways = useCallback((requestId: string, toolName: string) => {
    setAutoAllowTools(prev => (prev.includes(toolName) ? prev : [...prev, toolName]));
    allowAlways(requestId);
  }, [allowAlways]);

  const handleRevokeAutoAllow = useCallback(async (tool: string) => {
    const prev = autoAllowTools;
    setAutoAllowTools(prev.filter(t => t !== tool));
    try {
      // Ответ — обновлённая сессия: отдаём её родителю, иначе он вернёт в пропсе
      // старый список при следующем возврате в чат (та же логика, что у смены режима)
      const updated = await api.sessions.revokeAutoAllow(session.id, tool);
      onSessionUpdated?.(updated);
    } catch (err) {
      setAutoAllowTools(prev);
      showToast('Разрешения чата', err instanceof Error ? err.message : 'Не удалось снять разрешение');
    }
  }, [autoAllowTools, session.id, onSessionUpdated]);

  const [showAttachPicker, setShowAttachPicker] = useState(false);
  // Скролл-механика ленты (прилипание к низу, восстановление позиции, кнопка «вниз») — hooks/useChatScroll
  const {
    bottomRef, scrollRef, contentRef, composerWrapRef, composerH,
    showScrollDown, atBottomRef, handleMessagesScroll, scrollToBottom,
  } = useChatScroll(session.id, items, isHistoryLoading, online);
  // Компенсация перекоса «боковое поле слева против полосы прокрутки справа» — см.
  // lib/chatGutter. В обычном чате она держит колонку сообщений по центру окна; на
  // стене центрировать нечего (лента идёт во всю ширину колонки), там хук только
  // добирает правый паддинг до ширины левого поля. На мобиле поля задаёт разметка.
  useChatGutter(scrollRef, CHAT_MAX_W, isMobile ? 'off' : embedded ? 'pad' : 'center');
  // Композер — нижнее препятствие для круглешка AI: тот остаётся в углу, но ужимается,
  // когда композер доходит до него (замер пересечения — в AiLauncher). Публикуем узел
  // САМОГО композера, а не растянутую обёртку: та шириной во всю область чата, и по ней
  // пересечение выходило истинным всегда — круг оставался ужатым при любом окне.
  const composerObstacleRef = useRef<HTMLDivElement>(null);
  useEffect(() => {
    // embedded: препятствие глобальное, несколько колонок стены перебивали бы друг друга
    if (embedded) return;
    setFabObstacle(composerObstacleRef.current);
    return () => setFabObstacle(null);
    // composerH в зависимостях — им ловим момент, когда композер уже в DOM
    // (первый замер высоты) и ref наконец не пустой
  }, [embedded, composerH]);
  // QA Fold 8: на планшете FAB прижимался к композеру и ужимался (54→36), причём
  // пилюля «Собеседник» резалась сверху. Поднимаем кнопку над композером вместо
  // ужимания — `--cc-fab-bottom = composerH + 12` (12 = зазор). Сбрасывается на 20px
  // при уходе композера. На Стене (embedded) поведение прежнее: глобальный
  // --cc-fab-bottom трогать нельзя — колонки перебивают друг друга.
  useEffect(() => {
    if (embedded) return;
    const root = document.documentElement;
    if (composerH > 0) {
      root.style.setProperty('--cc-fab-bottom', `${composerH + 12}px`);
    } else {
      root.style.setProperty('--cc-fab-bottom', '20px');
    }
    return () => { root.style.setProperty('--cc-fab-bottom', '20px'); };
  }, [composerH, embedded]);
  // Контекст проекта для резолва локальных путей картинок в сообщениях
  const projectCtx = useMemo(() => project ? { id: project.id, rootPath: project.rootPath } : null, [project]);

  // Накопительная стоимость/токены сессии — сумма по всем result-элементам ленты.
  // Источник правды — история (грузится с бэка), поэтому переживает перезагрузку.
  const costStats = useMemo<CostStats>(() => {
    const s: CostStats = { cost: 0, input: 0, output: 0, cacheRead: 0, cacheCreate: 0, turns: 0, results: 0 };
    for (const it of items) {
      if (it.kind !== 'result') continue;
      s.results++;
      if (typeof it.totalCostUsd === 'number') s.cost += it.totalCostUsd;
      if (it.numTurns) s.turns += it.numTurns;
      if (it.usage) {
        s.input += it.usage.inputTokens;
        s.output += it.usage.outputTokens;
        s.cacheRead += it.usage.cacheReadTokens;
        s.cacheCreate += it.usage.cacheCreationTokens;
      }
    }
    return s;
  }, [items]);

  // Браузерные уведомления (только когда вкладка не в фокусе) — нужно решение / ход завершён.
  // Заглушённый чат (notificationsMuted) молчит; счётчики при этом ведём как обычно,
  // иначе снятие мьюта выстрелило бы уведомлением о давно прошедшем событии
  const muted = session.notificationsMuted === true;
  const prevWaitingRef = useRef(false);
  useEffect(() => {
    if (isWaiting && !prevWaitingRef.current && !muted)
      notify('Нужно решение', `${session.name ?? 'Чат'} ждёт вашего ответа`);
    prevWaitingRef.current = isWaiting;
  }, [isWaiting, session.name, muted]);

  const resultCountRef = useRef<number | null>(null);
  useEffect(() => {
    const rc = items.reduce((acc, it) => acc + (it.kind === 'result' ? 1 : 0), 0);
    if (resultCountRef.current !== null && rc > resultCountRef.current && !muted)
      notify(`${asstName}: ход завершён`, `${session.name ?? 'Чат'}`);
    resultCountRef.current = rc;
  }, [items, session.name, asstName, muted]);
  // Озвучка ответа в голосовом режиме — ОТДЕЛЬНЫЙ эффект, не расширение соседнего:
  // тот гейтится notificationsMuted, а мьют браузерных уведомлений к озвучке отношения
  // не имеет. Считаем свой счётчик result: ChatPanel живёт без key (см. комментарий про
  // смену чата ниже), реф между чатами не обнуляется — без сброса по session.id переход
  // из чата с 2 результатами в чат с 5 зачитал бы вслух чужой старый ответ.
  const speakCountRef = useRef<number | null>(null);
  const speakSessionRef = useRef(session.id);
  // Счётчик реплик человека — фактическая граница нового хода для снятия подавления
  // барж-ина: handleSend для этого не годится (queued-ход не рождает user_message,
  // и поздняя дельта СТАРОГО перебитого хода зачитала бы его с начала)
  const userMsgCountRef = useRef<number | null>(null);

  // === Поточная озвучка хода (только режим разговора: voiceMode && handsFreeActive) ===
  // Единственный владелец звука хода в talk-режиме — StreamSpeech: speak() тут не зовётся
  // вовсе (ни на result, ни в фолбэке hitMarkup), что закрывает и дубль озвучки, и
  // конфликт токенов. Гейт handsFreeActive поднимает Composer (петля живёт там).
  const [handsFreeActive, setHandsFreeActive] = useState(false);
  const streamStRef = useRef<TurnStreamState>(TURN_STREAM_INIT);
  const streamRef = useRef<StreamSpeech | null>(null);
  // Сброс на границах хода/чата: новый ход (user_message), прерывание, ошибка, смена
  // чата, загрузка истории (F5 — иначе первый снапшот выглядел бы дельтами). Очередь
  // гасится через stop()
  const resetStreamSpeech = useCallback(() => {
    streamStRef.current = TURN_STREAM_INIT;
    streamRef.current?.stop();
    streamRef.current = null;
  }, []);
  // Барж-ин: озвучка ТЕКУЩЕГО хода погашена перебиванием.
  // Гейтит ОБЕ точки озвучки — пересоздание стрима на дельте и одиночный speak на
  // result: interrupt хода асинхронный, и проскочившие после перебивания дельта или
  // result воскресили бы звук, утащив петлю из слушания обратно в speaking.
  // Снимается на границах нового хода/чата — там же, где сбрасывается стрим
  const bargeSuppressedRef = useRef(false);
  const handleBargeSuppress = useCallback(() => {
    bargeSuppressedRef.current = true;
    stopSpeech();
    resetStreamSpeech();
  }, [stopSpeech, resetStreamSpeech]);

  useEffect(() => { setSpeechToast((text) => showToast('Озвучка', text)); }, []);
  useEffect(() => {
    const switched = speakSessionRef.current !== session.id;
    if (switched) {
      speakSessionRef.current = session.id;
      speakCountRef.current = null; // первая загрузка нового чата — молчим
      userMsgCountRef.current = null;
      stopSpeech();
      resetStreamSpeech();
      bargeSuppressedRef.current = false;
    }
    // Пока история грузится, items пуст у ЛЮБОГО чата: базовая отметка, взятая здесь,
    // равна нулю, и первый же загруженный снапшот выглядел бы как новые ответы —
    // открытие старого чата и F5 зачитывали бы вслух вчерашний ход. Baseline берём
    // с первого ЗАГРУЖЕННОГО снапшота (та же причина, что у emptyChatFocus выше).
    if (isHistoryLoading) {
      speakCountRef.current = null;
      userMsgCountRef.current = null;
      resetStreamSpeech();
      bargeSuppressedRef.current = false;
      return;
    }
    // Подавление барж-ина живёт до СЛЕДУЮЩЕГО хода, и его граница — появление реплики
    // человека в ленте, а не сам факт отправки: у queued-хода user_message придёт позже
    const um = items.reduce((acc, it) => acc + (it.kind === 'user_message' ? 1 : 0), 0);
    if (userMsgCountRef.current !== null && um > userMsgCountRef.current)
      bargeSuppressedRef.current = false;
    userMsgCountRef.current = um;
    const rc = items.reduce((acc, it) => acc + (it.kind === 'result' ? 1 : 0), 0);
    const prev = speakCountRef.current;
    speakCountRef.current = rc;
    if (switched || prev === null) return;
    if (rc <= prev) {
      // Новый ход начался (счётчик result сброшен редьюсером) — резка с нуля,
      // подавление барж-ина осталось у прошлого хода
      if (rc < prev) { resetStreamSpeech(); bargeSuppressedRef.current = false; }
      return;
    }

    // === Пришёл result: закрываем стрим хвостом, а в одиночном режиме — speak() ===
    const stream = streamRef.current;
    const text = turnText(items);
    if (stream) {
      // Хвост ВСЕГДА, даже если cursor не двигался (короткий ответ без точек — весь
      // текст одним куском) — это и есть защита от дубля уже озвученного. Разметка
      // (hitMarkup) закрывается ТОТ ЖЕ путём: санитайзер вычистит код/таблицы из
      // хвоста, отдельный startSpeaking(весь текст) был бы дублем озвученных кусков
      const tail = turnStreamTail(streamStRef.current, items);
      if (tail) stream.enqueue(tail);
      stream.end();
      void stream.done.then(() => {
        // Сброс refs — здесь; фазу снимает сам стрим (onDone-колбэк, единая точка
        // для всех исходов: доиграла очередь, stop(), внешний stopSpeaking())
        if (streamRef.current === stream) {
          streamRef.current = null;
          streamStRef.current = TURN_STREAM_INIT;
        }
      });
      return;
    }
    // Стрим не создавался (гейт talk-режима не прошёл ни разу) — старый путь: озвучка
    // всего текста хода одним speak(). Гейты одиночного режима прежние.
    // Перебитый ход сюда попадает с уже сброшенным стримом — молчит и он
    if (bargeSuppressedRef.current) return;
    if (!voiceMode) return;
    // Цикл «до готово» (Р8): отличить финальный result от промежуточного в момент события
    // нельзя, а читать вслух каждую итерацию — мусор
    if (workLoopState) return;
    // Стиль digest: вслух идёт ТОЛЬКО выжимка из блока <voice> в конце ответа. Полный текст
    // хода здесь не озвучивается никогда — иначе синтезатор зачитает код и таблицы.
    // Извлечение синхронное и до первого await (дисциплина токена speechCallRef)
    if (voiceDigest) {
      const digest = extractVoiceDigest(text);
      // Модель забыла маркер — читаем ПЕРВЫЕ ТРИ ПРЕДЛОЖЕНИЯ, чтобы не молчать вовсе.
      // Именно предложения, а не первую строку: ответ одним абзацем без переводов строки
      // уехал бы в синтез целиком — ровно то поведение, от которого этот стиль и уходит
      const spoken = digest ?? splitSentences(sanitizeForSpeech(text)).slice(0, 3).join(' ');
      if (!digest) talkDiag('digest: ход без маркера <voice>', { fallback: spoken.length });
      if (!spoken.trim()) { showToast('Озвучка', 'В ответе нечего озвучить'); return; }
      // eslint-disable-next-line react-hooks/set-state-in-effect -- см. ниже: фаза выставляется синхронно в этом же кадре (Р12)
      startSpeaking(spoken, turnVoicePersonaId(items, session.personaId));
      return;
    }
    // eslint-disable-next-line react-hooks/set-state-in-effect -- фаза озвучки обязана выставиться синхронно в этом же кадре (Р12)
    if (text) startSpeaking(text, turnVoicePersonaId(items, session.personaId));
  }, [items, session.id, session.personaId, voiceMode, voiceDigest, workLoopState, isHistoryLoading, startSpeaking, stopSpeech, resetStreamSpeech]);

  // Эффект стриминга: режет нарастающий текст хода на предложения и озвучивает их
  // по мере появления, не дожидаясь result. tool_use/thinking_delta ничего не делают —
  // курсор абсолютный, конкатенация turnText продолжается сама (озвучка вслух во
  // время работы инструмента — цель фичи)
  useEffect(() => {
    if (!voiceMode || !handsFreeActive) return;
    // Явная проверка стиля, а не расчёт на «в digest петли не бывает»: один промах — и
    // стрим зачитает вслух полный ответ с кодом и таблицами. Заодно это держит инвариант
    // «единственный владелец звука в talk — StreamSpeech»: стрим и ветка digest выше
    // не пересекаются никогда
    if (voiceStyle !== VOICE_STYLE_TALK) return;
    if (bargeSuppressedRef.current) return; // ход перебит — дельты больше не озвучиваем
    const r = turnStreamChunks(streamStRef.current, items);
    if (r.off) { streamStRef.current = { ...streamStRef.current, off: true }; return; }
    streamStRef.current = { ...streamStRef.current, cursor: r.cursor };
    if (r.chunks.length === 0) return;
    if (!streamRef.current) {
      // Первый кусок хода: стрим создаётся здесь, фаза — синхронно ДО await (Р12),
      // иначе петля успела бы открыть микрофон под старт синтеза. Возврат в idle —
      // только onDone-колбэк стрима: он срабатывает при любом исходе (очередь
      // доиграла / stop() / внешний stopSpeaking) ровно один раз
      const call = ++speechCallRef.current;
      const voiceId = turnVoicePersonaId(items, session.personaId);
      const s = startStreamSpeak(() => {
        if (speechCallRef.current !== call) return; // осиротели — фазу трогать нельзя
        if (streamRef.current !== s) return; // стрим уже сброшен (новый ход/чат)
        streamRef.current = null;
        streamStRef.current = TURN_STREAM_INIT;
        // eslint-disable-next-line react-hooks/set-state-in-effect -- финал озвучки: микрофон петли открывается именно отсюда
        setSpeechPhase('idle');
        // eslint-disable-next-line react-hooks/set-state-in-effect -- вместе с фазой: подсветка говорящей гаснет ровно тогда, когда смолкает голос
        setSpeakingPersonaId(null);
        // Голос захватывается ОДИН раз на весь ход: пакеты синтезируются заранее
        // (prefetch), и смена голоса посреди хода выбросила бы оплаченный пакет
      }, voiceId, session.id);
      streamRef.current = s;
      // eslint-disable-next-line react-hooks/set-state-in-effect -- см. startSpeaking: та же дисциплина токена
      setSpeechPhase('willSpeak');
      setSpeechPhase('speaking');
      // eslint-disable-next-line react-hooks/set-state-in-effect -- подсветка говорящей зажигается тем же кадром, что и фаза
      setSpeakingPersonaId(voiceId ?? null);
    }
    for (const c of r.chunks) streamRef.current!.enqueue(c);
  }, [items, voiceMode, voiceStyle, handsFreeActive, session.personaId]);

  // Уход со страницы/размонтирование — озвучка не должна пережить чат
  useEffect(() => () => stopSpeaking(), []);

  const pendingRef = useRef<string | undefined>(pendingMessage);
  pendingRef.current = pendingMessage;
  // «Свежие» значения для стабильных колбэков (useCallback без лишних пересозданий):
  // читаются только в обработчиках/эффектах, синхронизируются после каждого коммита
  const itemsRef = useRef(items);
  const modeRef = useRef(mode);
  useEffect(() => {
    itemsRef.current = items;
    modeRef.current = mode;
  });

  // Режим при смене чата и при composer_restore — ЕДИНАЯ точка (вариант B из ревью
  // 7f6c5eaf): Composer восстанавливает только текст/вложения, режим ставим здесь,
  // чтобы два эффекта не спорили за setMode. Гейт «черновик важнее restore» читает тот
  // же источник, что и гейт текста в Composer — getDraft текущего чата: mode и текст
  // всегда согласованы (оба из restore либо оба от текущего чата); раньше здесь читали
  // черновик уходящего чата, а Composer — живой lastTextRef, и в окне между вводом и
  // автосохранением условия расходились (режим восстановлен, текст — нет).
  // Сброс при смене чата: ChatPanel переиспользуется между сессиями (стоит без key), а
  // useState читает session.mode только при первом монтировании — без сброса в новый чат
  // «утекает» режим предыдущей вкладки. Зависимость — session.id, не session.mode: иначе
  // серверный апдейт в той же вкладке перебил бы локальный выбор.
  const modeSessionRef = useRef(session.id);
  useEffect(() => {
    const switched = modeSessionRef.current !== session.id;
    modeSessionRef.current = session.id;
    const r = composerRestore;
    const restored = r && r.seq !== 0 && r.text != null && !getDraft(session.id).trim()
      ? MODES.find(v => v === r.mode) : undefined;
    const target = restored && !isDangerMode(restored) ? restored : null;
    if (target) {
      // Через changeMode (с пушем на сервер) — как раньше делал Composer через
      // onModeChange; гард от повторного PUT, если режим уже совпадает
      if (modeRef.current !== target) changeMode(target);
    } else if (switched) {
      setMode(session.mode);
    }
    // Гасим разовую команду: к этому моменту её отработали оба владельца — Composer
    // (текст/вложения) и этот эффект (режим). Порядок гарантирован React: passive-эффекты
    // идут снизу вверх, поэтому эффект Composer как ребёнка выполняется раньше, а оба
    // читают ЗАХВАЧЕННОЕ в своём рендере значение — гашение их не обкрадывает. Без него
    // composerRestore жил бы в per-session сторе и подставлял уже отправленный текст
    // при каждом возврате в чат. Перезапуск этого эффекта после гашения безвреден:
    // r === null, switched === false → ветки не срабатывают.
    if (r) consumeRestore();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [session.id, composerRestore?.seq]);

  // Для монотонного счётчика фаз workflow — не прыгать назад когда total растёт
  const workflowPhaseRef = useRef<{ wfId: string; phasesDone: number }>({ wfId: '', phasesDone: 0 });

  // Автоотправка первого сообщения сразу после присоединения к сессии.
  // mode/onPendingMessageSent — через ref: эффект должен выстрелить один раз при join,
  // а не перезапускаться при смене режима или пересоздании колбэка родителя
  const onPendingSentRef = useRef(onPendingMessageSent);
  useEffect(() => { onPendingSentRef.current = onPendingMessageSent; });
  // Реагируем и на смену pendingMessage: в УЖЕ открытом чате (isJoined) новое отложенное
  // сообщение иначе не отправится (эффект бы не перезапустился). Guard pendingRef — от
  // повторов (после отправки ref=undefined, а сброс pendingMessage родителем сюда же вернёт no-op)
  useEffect(() => {
    if (isJoined && pendingRef.current) {
      const msg = pendingRef.current;
      pendingRef.current = undefined;
      onPendingSentRef.current?.();
      send(msg, [], modeRef.current);
    }
  }, [isJoined, send, pendingMessage]);

  const handleSend = async (text: string, _attachments?: string[], opts?: { auto?: boolean }) => {
    // Новый вопрос обрывает чтение предыдущего ответа. Прайминг здесь — второе место
    // (первое в тумблере): режим персистится на чате, и «включил вчера — надиктовал
    // сегодня» иначе упрётся в autoplay-политику браузера
    stopSpeech();
    if (voiceMode) primeAudio();
    // Авто-обвязка «Обсудить с командой» вложений не несёт — берём только при ручной отправке
    if (opts?.auto) {
      atBottomRef.current = true;
      await send(text, [], mode, { auto: true });
      return;
    }
    if (!text.trim() && attachedFiles.length === 0) return;
    const paths = [...attachedFiles];
    onAttachedFilesChange([]);
    atBottomRef.current = true; // своё сообщение — прыгаем вниз и снова прилипаем
    await send(text, paths, mode);
  };

  // Git-бар «Зафиксировать»: делегируем коммит текущему чату (Claude сам вызовет git
  // и придумает сообщение). Общая обвязка для обоих режимов («своё» / «всё»):
  // подтягиваем стиль сообщения из панели «Изменения» (effective — тот же, что у
  // ✨-генерации, чтобы делегированный коммит и кнопка ✨ давали один формат) и шлём
  // агенту готовый промпт. buildBody получает суффикс со стилем и собирает текст.
  const commitViaChat = useCallback((buildBody: (style: string) => string) => {
    if (!project) return;
    const pid = project.id;
    void (async () => {
      let style = '';
      try {
        const info = await api.git.getCommitPrompt(pid);
        if (info.effective?.trim()) style = `\n\nОформи сообщение коммита строго по этому стилю:\n${info.effective.trim()}`;
      } catch { /* офлайн/нет промпта — коммитим по общим правилам */ }
      atBottomRef.current = true;
      await send(buildBody(style), [], mode);
    })();
  }, [project, send, mode, atBottomRef]);

  // «Только этот чат»: просим закоммитить свои правки, не захватывая чужие.
  // Список файлов НЕ подставляем — он брался из ленты file_changed, а туда попадают
  // и внешние правки (external: человек в IDE, форматтер, другая сессия), помеченные
  // именно чтобы отличать «не наше». Подмешанные в промпт, они выглядели инструкцией
  // закоммитить чужую работу — Claude сам сверься с git diff по ходу диалога.
  const handleCommitOwn = useCallback(() => {
    commitViaChat(style =>
      `Зафиксируй (git commit) изменения, сделанные в рамках этого чата — только то, что ты правил в этом диалоге, не затрагивая остальные изменения рабочего дерева. Сам придумай осмысленное сообщение коммита по сути изменений.${style}`);
  }, [commitViaChat]);

  // «Всё рабочее дерево»: коммитим все незафиксированные изменения без ограничения
  // диалогом (staged + unstaged, включая правки не из этого чата).
  const handleCommitAll = useCallback(() => {
    commitViaChat(style =>
      `Зафиксируй (git commit) все незафиксированные изменения рабочего дерева. Сам придумай осмысленное сообщение коммита по сути изменений.${style}`);
  }, [commitViaChat]);

  // Загрузка файлов с устройства — один эндпоинт для обоих типов чата: сервер кладёт файл
  // в рабочую папку сессии (для worktree-чата — в неё же, а не в корень проекта) и сам
  // сохраняет оригинальное имя, уникальность даёт подпапка. Путь из ответа — во вложения.
  const chatFileInputRef = useRef<HTMLInputElement>(null);
  const handleChatUpload = useCallback(async (files: File[]) => {
    const added: string[] = [];
    for (const file of files) {
      if (file.size > MAX_UPLOAD_BYTES) { showToast('Вложение', TOO_BIG_MSG); continue; }
      try {
        const { path } = await api.chats.uploadFile(session.id, file);
        added.push(path);
      } catch { showToast('Вложение', UPLOAD_FAIL_MSG); }
    }
    if (added.length) onAttachedFilesChange([...attachedFiles, ...added]);
  }, [session.id, attachedFiles, onAttachedFilesChange]);

  // Единая точка загрузки с устройства (вставка, перетаскивание, кнопка пикера):
  // гейт по зрению модели сужен до картинок — pdf и документы claude читает с диска
  // и без поддержки image-блоков. Промис нужен пикеру: по нему живёт его индикатор загрузки
  const handleComposerFiles = useCallback(async (files: File[]) => {
    let list = files;
    if (!caps.supportsImages) {
      list = list.filter(f => !f.type.startsWith('image/'));
      if (list.length < files.length) showToast('Вложение', 'Эта модель не понимает картинки — вложение пропущено');
      if (!list.length) return;
    }
    await handleChatUpload(list);
  }, [caps.supportsImages, handleChatUpload]);

  const handleHint = (hint: string) => {
    atBottomRef.current = true;
    send(hint, [], mode);
  };

  // Стабильный (items/mode — через ref), чтобы React.memo элементов ленты работал
  const handleRetry = useCallback(() => {
    const lastUser = [...itemsRef.current].reverse().find(it => it.kind === 'user_message');
    if (lastUser && lastUser.kind === 'user_message') { atBottomRef.current = true; send(lastUser.text, lastUser.attachedPaths ?? [], modeRef.current); }
  }, [send, atBottomRef]);

  // Миграция чата на другого провайдера (кнопка карточки provider_limit при исчерпании
  // лимита): сервер перевозит транскрипт, событие provider_switched гасит карточку и
  // рисует разделитель; ошибку показывает сама карточка. subscriptionKey задан только у
  // опций «аккаунт пула» (kind='subscription') — явный выбор подписки вместо автовыбора
  const handleMigrateProvider = useCallback(async (model: string, subscriptionKey?: string) => {
    const updated = await api.chats.migrateProvider(session.id, model, subscriptionKey);
    onSessionUpdated?.(updated);
  }, [session.id, onSessionUpdated]);

  // Смена модели из полосы контролов композера. В рамках одного провайдера — обычный
  // update; смена провайдера у НАЧАТОГО чата упирается в guard (транскрипт живёт у
  // эндпоинта), поэтому идёт миграцией — тот же путь, что в «Настройках чата».
  // У ещё не начатого чата (нет claudeSessionId) update проходит и на чужого провайдера.
  const handleModelChange = useCallback(async (model: string) => {
    // Выбор пути (update или миграция) — в planModelChange, под тестом: там же разворот
    // пункта «По умолчанию» в конкретную модель назначения места. В update уходит
    // исходное значение — пустая строка сбрасывает Model=null в рамках того же провайдера.
    const plan = planModelChange(model, session);
    const payload = { model };
    try {
      const updated = plan.kind === 'migrate'
        ? await api.chats.migrateProvider(session.id, plan.model)
        : session.projectId
          ? await api.sessions.update(session.projectId, session.id, payload)
          : await api.chats.update(session.id, payload);
      onSessionUpdated?.(updated);
    } catch (err) {
      showToast('Модель', err instanceof Error ? err.message : 'Не удалось сменить модель');
    }
  }, [session.id, session.model, session.projectId, session.claudeSessionId, onSessionUpdated]);

  // Усилие рассуждения из полосы контролов. Тот же контракт, что у модели: пустая
  // строка — сброс на дефолт CLI, null бэкенд трактует как «поле не менять».
  const handleEffortChange = useCallback(async (effort: string) => {
    try {
      const updated = session.projectId
        ? await api.sessions.update(session.projectId, session.id, { effort })
        : await api.chats.update(session.id, { effort });
      onSessionUpdated?.(updated);
    } catch (err) {
      showToast('Усилие', err instanceof Error ? err.message : 'Не удалось сменить усилие');
    }
  }, [session.id, session.projectId, onSessionUpdated]);

  // Режим «План» — персистентный: после одобрения остаёмся в нём (следующие задачи тоже
  // планируются). Исполнение именно этого плана гарантирует backend (один ход без plan-режима).
  const handleRespondPlan = useCallback((requestId: string, approve: boolean, feedback?: string) => {
    respondPlan(requestId, approve, feedback);
  }, [respondPlan]);

  // Обвязка карточки плана «Командной реализации»: состояние режима + действия.
  // Решение (в т.ч. edit — правка плана текстом) уходит в хаб, координатору ход не
  // выдаётся: сервер сам гасит карточку и пересобирает план. Контекст, а не пропы —
  // карточка лежит глубоко в ленте.
  const handleRespondTeamPlan = useCallback((planId: string, decision: TeamPlanDecision,
    subtaskId?: string, executorPersonaId?: string, feedback?: string) => {
    respondTeamPlan(planId, decision, subtaskId, executorPersonaId, feedback).catch(err => {
      showToast('Командная реализация', err instanceof Error ? err.message : 'Не удалось отправить решение по плану');
    });
  }, [respondTeamPlan]);
  const teamPlanCtx = useMemo<TeamPlanChatContext | null>(() => teamImplementState ? {
    autoWaves: teamImplementState.autoWaves,
    waveNumber: teamImplementState.waveNumber,
    planCardId: teamImplementState.planCardId ?? null,
    executorPersonaIds: teamImplementState.executorPersonaIds,
    onRespond: handleRespondTeamPlan,
  } : null, [teamImplementState, handleRespondTeamPlan]);

  // Обвязка карточек остановки (Э4): решение уходит в хаб, карточка гаснет.
  // Контекст живёт, пока включён режим — в выключенном чате карточки только читаются
  const handleRespondTeamEscalation = useCallback((escalationId: string, actionId?: string, comment?: string) => {
    respondTeamEscalation(escalationId, actionId, comment).catch(err => {
      showToast('Командная реализация', err instanceof Error ? err.message : 'Не удалось отправить решение');
    });
  }, [respondTeamEscalation]);
  const teamEscalationCtx = useMemo<TeamEscalationChatContext | null>(() => teamImplementState
    ? { onRespond: handleRespondTeamEscalation }
    : null, [teamImplementState, handleRespondTeamEscalation]);

  // Откат файла — стабильный колбэк для карточек file_changed в ленте. Действие бьёт
  // по git checkout HEAD и стирает ЛЮБЫЕ несохранённые правки файла (не только модели),
  // поэтому спрашивает подтверждение, а не бьёт сразу
  const projectId = project?.id;
  const [revertPath, setRevertPath] = useState<string | null>(null);
  const handleRevert = useCallback((path: string) => {
    setRevertPath(path);
  }, []);
  const confirmRevert = useCallback(() => {
    const path = revertPath;
    setRevertPath(null);
    if (projectId && path) api.files.revert(projectId, path);
  }, [projectId, revertPath]);

  // Индекс последнего result — у него показываем плашку токенов/времени, у прошлых скрываем
  const lastResultIndex = useMemo(
    () => items.reduce((acc, it, i) => (it.kind === 'result' ? i : acc), -1),
    [items]
  );

  // Отметка «Ход остановлен пользователем», у которой ещё показываем «Повторить»
  // (см. retryableInterruptedIndex): ниже неё разговор не продолжился
  const retryInterruptedIdx = useMemo(() => retryableInterruptedIndex(items), [items]);

  // Есть ли в чате переписка — по загруженной ленте (надёжнее session.messageCount, который
  // у активной проектной сессии не синхронизируется по realtime). Управляет показом кнопок
  // «Итог сессии» и «Задачи из чата» в шапке.
  const hasMessages = useMemo(() => items.some(it => it.kind === 'user_message'), [items]);

  // Краткий хвост переписки (последние текстовые реплики) — для локального ранжирования
  // действий чата («Извлечь задачи», «Итог сессии») по реальному содержанию диалога.
  const chatTail = useMemo(() => {
    const parts: string[] = [];
    for (const it of items.slice(-10)) {
      const t = (it as { text?: string }).text;
      if (typeof t === 'string' && t.trim()) parts.push(t.trim());
    }
    return parts.slice(-6).join('\n---\n').slice(0, 1500);
  }, [items]);

  // Сообщаем AI-палитре, что чат открыт (переписка + хвост) — чтобы действия чата были
  // доступны и в проектных чатах, где активная сессия не отражается в nav.
  useEffect(() => {
    // embedded: контекст AI-палитры один на приложение — стена его не трогает
    if (embedded) return;
    // personaId — резолверу релевантной персоны (аватары AI-хаба вне чата)
    setChatContext(true, hasMessages, chatTail, session.personaId);
    return () => setChatContext(false, false);
  }, [hasMessages, chatTail, session.personaId, embedded]);

  // Триггер «завершение хода Claude»: по переходу isWaiting → false просим AI-хаб
  // переоценить контекст (уместно ли извлечь задачи, собрать итог в заметку и т.п.).
  const prevWaiting = useRef(isWaiting);
  useEffect(() => {
    if (prevWaiting.current && !isWaiting) window.dispatchEvent(new Event(AI_RECOMPUTE_EVENT));
    prevWaiting.current = isWaiting;
  }, [isWaiting]);

  // Фаза режима «План» (для контекстного индикатора и подписи WaitingIndicator)
  const planPhase = useMemo(() => derivePlanPhase(items, mode, isWaiting), [items, mode, isWaiting]);
  const planningKind = planPhase === 'planning' ? 'planning' : planPhase === 'replanning' ? 'replanning' : undefined;

  // Дерево ХОДА (turnWorktree с бэка + EnterWorktree первого хода, см. lib/turnWorktree) —
  // git-бар и разделитель в ленте считают его отсюда, а не из Session.worktreePath
  const turnTree = useMemo(() => computeTurnTree(items), [items]);
  // Индексы session_started, видимые в ленте разделителем «ход в дереве агента»/«ход
  // вернулся в проект» (остальные session_started прозрачны для группировки, см. isInvisible)
  const turnBoundaries = useMemo(() => sessionStartedBoundaries(items), [items]);
  // Активный корень дерева для показа путей: дерево хода сильнее дерева чата
  // (см. ChatTreePathContext) — приоритет как у самого turnTree/git-бара.
  const treePathCtx = useMemo(() => turnTree?.path ?? session.worktreePath ?? null, [turnTree, session.worktreePath]);

  // Активный workflow — сырой прогресс фаз (чистая функция от ленты, без мутаций)
  const rawWorkflowInfo = useMemo(() => {
    for (let i = items.length - 1; i >= 0; i--) {
      const it = items[i];
      if (it.kind !== 'tool_use') continue;
      const wf = it as ToolUseItem;
      if (wf.name.toLowerCase() !== 'workflow' || wf.workflowDone === true
        || wf.workflowAborted === true || wf.bgDone === true) continue;
      // Workflow уходит в фон и возвращает result («launched in background») СРАЗУ —
      // по result судить о завершённости нельзя. Явный workflowDone от ватчера (boolean)
      // авторитетен: false = идёт, даже если все агенты волны isDone (пауза между волнами,
      // сервер держит 45с-выдержку). Эвристика по агентам — фолбэк без флага.
      const running = wf.result === undefined
        || typeof wf.workflowDone === 'boolean'
        || (wf.workflowAgents?.some(a => a.isDone !== true) ?? false);
      if (!running) continue;
      const meta = parseWorkflowMeta(wf.input);
      const phases = meta?.phases ?? [];
      if (phases.length === 0) return { wfId: wf.id, rawPhasesDone: 0, phasesTotal: 0 };
      const serverAgents = wf.workflowAgents;
      const transcriptDone = serverAgents?.filter(a => a.isDone).length ?? 0;
      const transcriptTotal = serverAgents?.length ?? 0;
      const rawPhasesDone = transcriptTotal > 0
        ? Math.min(Math.floor((transcriptDone / transcriptTotal) * phases.length), phases.length - 1)
        : 0;
      return { wfId: wf.id, rawPhasesDone, phasesTotal: phases.length };
    }
    return null;
  }, [items]);

  // Монотонный максимум фаз: когда агенты новой фазы появляются, total растёт и
  // пропорция временно падает — счётчик не должен прыгать назад. Ref мутируем
  // в эффекте (после коммита), в рендере только читаем.
  useEffect(() => {
    if (!rawWorkflowInfo) return;
    const ref = workflowPhaseRef.current;
    if (ref.wfId !== rawWorkflowInfo.wfId) { ref.wfId = rawWorkflowInfo.wfId; ref.phasesDone = 0; }
    ref.phasesDone = Math.max(ref.phasesDone, rawWorkflowInfo.rawPhasesDone);
  }, [rawWorkflowInfo]);

  // Для индикатора в тулбаре и нотификации родителя: raw против запомненного максимума
  const activeWorkflowInfo = rawWorkflowInfo
    ? {
        phasesDone: Math.max(
          rawWorkflowInfo.rawPhasesDone,
          workflowPhaseRef.current.wfId === rawWorkflowInfo.wfId ? workflowPhaseRef.current.phasesDone : 0,
        ),
        phasesTotal: rawWorkflowInfo.phasesTotal,
      }
    : null;

  const isWorkflowRunning = activeWorkflowInfo !== null;
  useEffect(() => {
    onWorkflowRunning?.(isWorkflowRunning, session.id);
  }, [isWorkflowRunning, onWorkflowRunning, session.id]);

  // Последняя запущенная в чате механика «Обсудить с командой» — детект по тексту хода
  // (как бейдж в ленте). Пишем в стор ретроактивно: подтягивает бейдж в шапку и на
  // карточку в списке чатов даже для чатов, где механику запускали до появления фичи.
  const lastMechanic = useMemo(() => {
    for (let i = items.length - 1; i >= 0; i--) {
      const it = items[i];
      if (it.kind !== 'user_message') continue;
      const m = detectTeamMechanic(it.text);
      if (m) return m;
    }
    return null;
  }, [items]);
  useEffect(() => {
    if (lastMechanic) setLastMechanic(session.id, lastMechanic);
  }, [lastMechanic, session.id]);

  // Единое условие показа WaitingIndicator — только по isWaiting из редьюсера (реагирует
  // на result/error/exited/status_changed/отправку — консистентно с реальной активностью).
  // fallback на starting: при холодном старте чата isWaiting ещё false, а сессия стартует.
  // Без этого индикатор не появился бы до первого status_changed:working.
  // Не используем session.status === 'working': это внешний пропс, который может застрять
  // stale после переподключения SignalR и давать ложный positive.
  const sessionBusy = isWaiting || (session.status === 'starting' && items.length === 0);
  const showWaiting =
    items.length > 0
    && sessionBusy;
  // Ждёт ответа от пользователя (permission_request / ask_question) — для режима текста
  const awaitingResponse = items.some(it =>
    (it.kind === 'permission_request' || it.kind === 'ask_question') && !it.resolved
  );
  // Звуковой сигнал «нужно твоё решение» в голосовом режиме: карточку на экране человек
  // не видит — он либо идёт с телефоном в кармане, либо слушает ответ. Сигнал живёт здесь,
  // а не в петле, чтобы звучать и с выключенной петлёй (режим включён — значит слушают).
  // Петля этот же случай доозвучивает фразой, с задержкой на длину сигнала
  const prevAwaitingRef = useRef(false);
  useEffect(() => {
    const was = prevAwaitingRef.current;
    prevAwaitingRef.current = awaitingResponse;
    // В digest не пищим: сигнал заведён для разговора, где человек не смотрит на экран,
    // а здесь карточку с вопросом он видит глазами
    if (awaitingResponse && !was && voiceMode && !voiceDigest) needAnswer();
  }, [awaitingResponse, voiceMode, voiceDigest]);

  // Номера версий plan_review: счётчик с последнего user_message включительно (1, 2, …).
  // Также помечаем, был ли в текущем ходе отклонённый план — тогда показываем бейдж даже для v1.
  const planVersions = useMemo(() => {
    let counter = 0;
    let rejectedSeen = false;
    const result = new Map<number, { version: number; hadRejected: boolean }>();
    items.forEach((it, i) => {
      if (it.kind === 'user_message') { counter = 0; rejectedSeen = false; }
      if (it.kind === 'plan_review') {
        counter++;
        result.set(i, { version: counter, hadRejected: rejectedSeen });
        if (it.resolved && it.approved === false) rejectedSeen = true;
      }
    });
    return result;
  }, [items]);

  // Индекс последнего одобренного plan_review и конец «зоны реализации» (до следующего
  // user_message или result) — действия в этой зоне оборачиваем success-коннектором.
  const execZone = useMemo(() => {
    let approvedIdx = -1;
    for (let i = items.length - 1; i >= 0; i--) {
      const it = items[i];
      if (it.kind === 'plan_review' && it.resolved && it.approved) { approvedIdx = i; break; }
      if (it.kind === 'user_message') break;
    }
    if (approvedIdx < 0) return null;
    let endIdx = items.length;
    for (let i = approvedIdx + 1; i < items.length; i++) {
      if (items[i].kind === 'user_message' || items[i].kind === 'result') { endIdx = i; break; }
    }
    return { start: approvedIdx + 1, end: endIdx };
  }, [items]);

  // Индекс последнего одобренного плана во всей ленте — только у него показываем
  // подсказку «Перейти в Авто» (у старых одобренных планов она неактуальна)
  const lastApprovedPlanIdx = useMemo(() => {
    for (let i = items.length - 1; i >= 0; i--) {
      const it = items[i];
      if (it.kind === 'plan_review' && it.resolved && it.approved) return i;
    }
    return -1;
  }, [items]);

  // Todo через TaskCreate/TaskUpdate инкрементальны (в отличие от TodoWrite с полным
  // списком), а CLI ведёт ОДИН список на сессию — поэтому лента режется на пачки
  // (computeTodoBatches): карточка чек-листа рисуется на последнем вызове КАЖДОЙ пачки
  // со своим составом. Раньше карточка была одна на весь чат, и прошлые планы, сколько бы
  // их ни было за сессию, в истории не показывались вовсе.
  const todoBatches = useMemo(() => computeTodoBatches(items), [items]);
  // индекс последнего вызова пачки → её список (для рендера карточки на этом месте)
  const batchByIndex = useMemo(
    () => new Map(todoBatches.filter(b => b.lastIndex >= 0).map(b => [b.lastIndex, b.todos])),
    [todoBatches]);
  // Текущая пачка — для пилюли прогресса и подписи «на каком я шаге». useMemo обязателен:
  // список уходит в зависимости renderItem, и новый массив на каждый рендер сбрасывал бы
  // мемоизацию всей ленты
  const taskTodos = useMemo(
    () => (todoBatches.length ? todoBatches[todoBatches.length - 1].todos : []),
    [todoBatches]);


  // Снимок промпта и размер контекста ДЛЯ КАЖДОГО индекса ленты: у постов ассистента
  // своего snapshotId нет — он лежит на сообщении, которым начался ход, а contextTokens
  // приходит только в result в конце хода. Оба разносим по ходу одним проходом:
  // сообщение человека открывает ход, result его закрывает. ChatItemView списка items
  // не видит, поэтому считаем здесь и отдаём пропами.
  // ref-хранилище кеш-объектов хода по ссылке result-элемента — переживает пересчёт
  // turnMeta на каждой стрим-дельте, сохраняя стабильные ссылки для React.memo
  const cacheByResultRef = useRef<WeakMap<ChatItem, { read: number; creation: number }>>(new WeakMap());

  const turnMeta = useMemo(() => {
    const snapshots: (string | undefined)[] = new Array(items.length);
    const contextTokens: (number | undefined)[] = new Array(items.length);
    let currentSnapshot: string | undefined;
    // Границы хода: от user_message до его result. Идём вперёд за снимком, назад — за токенами
    for (let i = 0; i < items.length; i++) {
      const it = items[i];
      if (it.kind === 'user_message') currentSnapshot = it.promptSnapshotId;
      snapshots[i] = currentSnapshot;
    }
    // Кэш промптов из usage того же result — единственные точные числа про кэш.
    // Объект берём из WeakMap по ссылке result: он не пересоздаётся на дельту,
    // иначе React.memo на ChatItemView пробивался бы у всех карточек хода.
    const cacheByResult = cacheByResultRef.current;
    const cache: ({ read: number; creation: number } | undefined)[] = new Array(items.length);
    let currentTokens: number | undefined;
    let currentCache: { read: number; creation: number } | undefined;
    for (let i = items.length - 1; i >= 0; i--) {
      const it = items[i];
      if (it.kind === 'result') {
        currentTokens = it.contextTokens;
        currentCache = it.usage
          ? memoizedCacheEntry(cacheByResult, it, it.usage.cacheReadTokens, it.usage.cacheCreationTokens)
          : undefined;
      }
      contextTokens[i] = currentTokens;
      cache[i] = currentCache;
    }
    return { snapshots, contextTokens, cache };
  }, [items]);

  // Краткий контекст чата для командной механики «Панель экспертов» (attachContext):
  // последние ~6 реплик диалога (пользователь + ассистент), каждая обрезана до 300 символов
  const chatContext = useMemo(() => {
    const parts: string[] = [];
    for (const it of items) {
      if (it.kind === 'user_message' && !it.systemDirective && !it.staffNote) parts.push(`Пользователь: ${it.text}`);
      else if (it.kind === 'text' && !it.parentToolUseId) parts.push(`Ассистент: ${it.text}`);
    }
    const tail = parts.slice(-6).map(t => t.length > 300 ? t.slice(0, 300) + '…' : t);
    return tail.length > 0 ? tail.join('\n') : undefined;
  }, [items]);

  // === Мост в командные механики ===
  // Маркеры <team-mechanic/> в текстах ассистента → карточки предложений. Дедуп «одна
  // механика — одна карточка на чат» сохраняется, но карточку несёт ПОСЛЕДНЕЕ
  // предложение каждой механики, а не первое: при повторном маркере карточка
  // «переезжает» к актуальной реплике и берёт свежий topic. Это чинит сценарий, где
  // после уточнений пользователя модель повторяет предложение — раньше дедуп
  // закреплял карточку у самого старого маркера, который к тому моменту был погашен
  // ходом пользователя, и запустить механику становилось невозможно.
  const mechanicOffers = useMemo(() => buildMechanicOffers(items), [items]);
  // «Запущено» и «запуск провалился» считаем по ИНДЕКСУ карточки, симметрично declined.
  // Раньше launchedMechanics был Set<TeamMechanicId> и смотрел по всей ленте: в чате, где
  // механика уже запускалась (например, штаб командной реализации), каждое новое
  // предложение той же механики рождалось мёртвым — «Запущено» ещё до всякого клика.
  // hasLaunchedAfter/hasFailedLaunchAfter ограничены окном ПОСЛЕ карточки, поэтому
  // прошлые запуски невидимы для новой карточки.
  const [clickedOfferIndices, setClickedOfferIndices] = useState<ReadonlySet<number>>(new Set());
  const launchedByIndex = useMemo(() => {
    const s = new Set<number>(clickedOfferIndices);
    for (const [i, offer] of mechanicOffers) {
      if (hasLaunchedAfter(items, i, offer.id)) s.add(i);
    }
    // implementMode текстом не детектится (обычное сообщение) — «запущено» = режим включён
    if (teamImplementState) {
      for (const [i, offer] of mechanicOffers) {
        if (offer.id === 'implementMode') s.add(i);
      }
    }
    return s;
  }, [items, mechanicOffers, clickedOfferIndices, teamImplementState]);
  // Ход с командой ушёл, но result не пришёл — error в ленте раньше result'а. Снимаем
  // «Запущено» и возвращаем кнопку «Повторить» той же темой. Симметрично launched:
  // только окно ПОСЛЕ карточки; прошлые ошибки чужих ходов нашу карточку не гасят.
  const failedByIndex = useMemo(() => {
    const s = new Set<number>();
    for (const i of launchedByIndex) {
      if (hasFailedLaunchAfter(items, i)) s.add(i);
    }
    return s;
  }, [items, launchedByIndex]);
  // «Отказались»: после карточки диалог пошёл дальше (новый живой ход пользователя),
  // а механику так и не запустили — кнопку гасим с подписью, чтобы спустя время не
  // купить случайным кликом дорогой прогон. Индексы карточек, не id: дедуп «одна
  // механика — одна карточка» уже гарантирует взаимно-однозначность. По симметрии с
  // launchedByIndex declined считается по индексам — без глобального флага по id.
  const declinedMechanicOffers = useMemo(() => {
    const s = new Set<number>();
    for (const [i] of mechanicOffers) {
      if (launchedByIndex.has(i)) continue;
      if (hasUserTurnAfter(items, i)) s.add(i);
    }
    return s;
  }, [items, mechanicOffers, launchedByIndex]);

  // Найти индекс user_message, запускающего механику ПОСЛЕ карточки — для скролла.
  // Симметрично hasLaunchedAfter: ищем живой ход с командой механики после offerIndex.
  // Служебные ходы и авто-продолжения пропускаем (они не считались запуском для launched).
  const findLaunchedIndex = useCallback((items: readonly ChatItem[], offerIndex: number, offerId: TeamMechanicId): number | null => {
    for (let j = offerIndex + 1; j < items.length; j++) {
      const it = items[j];
      if (it.kind !== 'user_message') continue;
      if (it.systemDirective || it.staffNote || it.auto) continue;
      if (detectTeamMechanic(it.text) === offerId) return j;
    }
    return null;
  }, []);

  // Скролл к user_message с командой механики ПОСЛЕ карточки — для клика по статусу
  // «Запущено». Элементы ленты помечены data-feed-index, поиск идёт по контейнеру ленты.
  // Вызывается из TeamMechanicOfferCard при клике по статусу.
  const scrollToMechanicLaunch = useCallback((offerIndex: number, offerId: TeamMechanicId) => {
    const targetIdx = findLaunchedIndex(items, offerIndex, offerId);
    if (targetIdx == null) return;
    const root = scrollRef.current;
    const node = root?.querySelector<HTMLElement>(`[data-feed-index="${targetIdx}"]`);
    node?.scrollIntoView({ behavior: 'smooth', block: 'center' });
  }, [items, findLaunchedIndex]);

  // Открытые карточки остановки (есть неотвеченная team_escalation): закреплённая полоса
  // над композером показывает самую свежую (последнюю по индексу — чем ниже, тем позже),
  // остальные — счётчиком. Без открытых карточек полоса не рисуется. Карточки-плана и
  // вопрос человеку НЕ считаются открытыми остановками: у них своя логика видимости
  const openEscalations = useMemo(() => findOpenEscalations(items), [items]);
  const topEscalation = openEscalations[openEscalations.length - 1] ?? null;
  // jumpToEscalation объявлен ниже — после renderedItems, от которого зависит
  // (порядок определения в JS важен — иначе ReferenceError)

  const runTeamMechanic = useCallback(async (offer: TeamMechanicOffer, offerIndex: number) => {
    setClickedOfferIndices(prev => new Set(prev).add(offerIndex));
    try {
      // «Командная реализация» — режим чата: включается REST-ом ДО отправки темы
      if (offer.id === 'implementMode') {
        const updated = await api.chats.setTeamImplement(session.id, true, {
          autoWaves: DEFAULT_TEAM_SETTINGS.modeAutoWaves,
          executorPersonaIds: [],
        });
        onSessionUpdated?.(updated);
      }
      atBottomRef.current = true;
      await send(buildTeamTurnText(offer.id, offer.topic, DEFAULT_TEAM_SETTINGS, chatContext), [], modeRef.current);
    } catch (err) {
      showToast('Командные механики', err instanceof Error ? err.message : 'Не удалось запустить механику');
      setClickedOfferIndices(prev => { const n = new Set(prev); n.delete(offerIndex); return n; });
    }
  }, [session.id, send, chatContext, onSessionUpdated, atBottomRef]);

  // === Каркас проекта (знакомство v2, п.4) ===
  // Маркеры <project-preset key="…"/> в текстах ассистента → карточка с кнопками
  // «Создать» / «Не нужно». Состояние кнопок берём с DTO проекта: «pending» — можно
  // применить/отказаться; «<ключ>» — каркас уже создан; «none» — человек отказался;
  // null — проект создан до фичи, к каркасу возвращаться не нужно. Локальный override
  // поверх DTO нужен на случай быстрого клика — POST уходит асинхронно, а кнопка должна
  // погаснуть/сменить подпись уже сейчас, чтобы пользователь не нажал второй раз.
  const presetOffers = useMemo(() => buildProjectPresetOffer(items), [items]);
  const [presetOverride, setPresetOverride] = useState<string | null>(null);
  // Inline-ошибка последнего клика (4xx) и «занято» — отдельные флаги, чтобы кнопка
  // не перерисовывала «Каркас создан» до того, как сервер подтвердит.
  const [presetBusy, setPresetBusy] = useState(false);
  const [presetError, setPresetError] = useState<string | null>(null);
  const [presetNote, setPresetNote] = useState<string | null>(null);
  // Без `?? null`: иначе «DTO ещё не приехал» (project === undefined) и «проект до
  // фичи» (project.presetKey === null) не различить — оба значения стянутся в null
  // и попадут в ветку активной кнопки (старый проект → 409 на клике). Хелпер
  // resolvePresetCardState ниже трактует и null, и undefined одинаково — `hidden`.
  const effectivePresetKey = presetOverride ?? project?.presetKey;
  const applyPreset = useCallback(async (key: string) => {
    if (!project) return;
    if (presetBusy) return;
    setPresetBusy(true);
    setPresetError(null);
    try {
      const report = await api.projects.applyPreset(project.id, key);
      // Сразу отражаем успех на DTO — пользователь увидит «Каркас создан» ещё до того,
      // как WorkspacePage обновит объект проекта после рефреша
      setPresetOverride(key);
      // Краткий итог для подписи в карточке, если бэк что-то пропустил (например, файл занят)
      const skipped = report.skipped?.length ?? 0;
      const created = report.created?.length ?? 0;
      if (skipped > 0) {
        setPresetNote(`Каркас применён: ${created} новых, ${skipped} пропущено (уже есть в проекте).`);
      } else {
        setPresetNote(null);
      }
      // Доклад в ленту: обычный ход с текстом, НЕ systemDirective — тот уходит в обход
      // очереди (SessionManager:2475,2595) и рвёт цикл. Берём текст кортко —
      // детали (что создали, что пропустили) уже в карточке.
      const reportText = skipped > 0
        ? 'Готово: создала каркас. Часть файлов уже была в проекте — их не трогала.'
        : 'Готово: создала каркас. Правила лежат в CLAUDE.md проекта — их потом можно поправить.';
      atBottomRef.current = true;
      await send(reportText, [], modeRef.current);
    } catch (err) {
      const message = err instanceof Error ? err.message : 'Не удалось применить каркас';
      setPresetError(message);
      // 409 — каркас уже применён/отклонён/проект до фичи: после этого DTO и так
      // покажет финальное состояние, не блокируем
      setPresetOverride(effectivePresetKey ?? null);
    } finally {
      setPresetBusy(false);
    }
  }, [project, presetBusy, send, atBottomRef, effectivePresetKey]);
  const declinePreset = useCallback(async () => {
    if (!project) return;
    if (presetBusy) return;
    setPresetBusy(true);
    setPresetError(null);
    try {
      await api.projects.applyPreset(project.id, 'none');
      setPresetOverride('none');
      // Доклад в ленту тем же путём, что и applyPreset: обычный ход с текстом,
      // а не systemDirective — тот рвёт цикл. Без этого хода модель не узнаёт, что
      // человек отказался, и сценарий знакомства повиснет на ожидании команды.
      atBottomRef.current = true;
      await send('Каркас не нужен — папку оставляем как есть.', [], modeRef.current);
    } catch (err) {
      setPresetError(err instanceof Error ? err.message : 'Не удалось зафиксировать отказ');
    } finally {
      setPresetBusy(false);
    }
  }, [project, presetBusy, send, atBottomRef]);

  // Состояние карточки — функция от серверного `presetKey`. Логика вынесена в
  // `resolvePresetCardState` (чистая функция, покрыта тестом): «pending» с
  // маркером в ленте → кнопки живые; «pending» без маркера или «null» (проект до
  // фичи / DTO не приехал) → карточка скрыта, активной кнопки нет.
  const presetCardState: PresetCardState = useMemo(
    () => resolvePresetCardState(effectivePresetKey, presetOffers.size > 0),
    [effectivePresetKey, presetOffers.size],
  );

  // Единый рендер одного элемента ленты (используется в основном рендере и в доке).
  // useCallback + React.memo на ChatItemView: при дописывании ленты неизменившиеся
  // элементы не перерендериваются (все пропсы-функции стабильны).
  const renderItem = useCallback((item: ChatItem, i: number,
    extras?: {
      agentActivity?: ActivityEntry[];
      agentRenderChild?: (item: ChatItem, idx: number) => React.ReactNode;
    }) => (
    <ChatItemView
      key={itemKey(item, i)}
      item={item}
      index={i}
      online={online}
      streaming={isWaiting && i === items.length - 1}
      isLastResult={i === lastResultIndex}
      planPill={!showWaiting && i === lastResultIndex && taskTodos.length > 0
        ? <TurnPlanPill todos={taskTodos} />
        : undefined}
      canRetryInterrupted={i === retryInterruptedIdx}
      onToggleThinking={toggleThinking}
      onAllowPermission={allowPermission}
      onDenyPermission={denyPermission}
      onAllowAlways={handleAllowAlways}
      onAnswerQuestion={answerQuestion}
      onRespondPlan={handleRespondPlan}
      planVersion={planVersions.get(i)?.version}
      planShowBadge={!!planVersions.get(i) && (planVersions.get(i)!.version > 1 || planVersions.get(i)!.hadRejected)}
      planShowSwitch={i === lastApprovedPlanIdx && mode === 'plan'}
      onSwitchMode={changeMode}
      onOpenFile={onOpenFile}
      onRevert={project ? handleRevert : undefined}
      onRetry={handleRetry}
      onInterrupt={interrupt}
      onMigrateProvider={handleMigrateProvider}
      taskPlan={batchByIndex.get(i)}
      agentActivity={extras?.agentActivity}
      agentRenderChild={extras?.agentRenderChild}
      turnBoundaryKind={item.kind === 'session_started' ? turnBoundaries.get(i) : undefined}
      teamMechanicOffer={item.kind === 'text' && mechanicOffers.has(i)
        ? (() => {
            const offer = mechanicOffers.get(i)!;
            const launched = launchedByIndex.has(i);
            return {
              offer,
              launched,
              // failed важно только при launched: пока карточка не запущена, ошибок нет
              failed: launched && failedByIndex.has(i),
              declined: declinedMechanicOffers.has(i),
              onRun: () => void runTeamMechanic(offer, i),
              onScrollToLaunch: () => scrollToMechanicLaunch(i, offer.id),
              // Перезапуск той же темой — только если запуск провалился (есть failedByIndex)
              onRerun: launched && failedByIndex.has(i) ? () => void runTeamMechanic(offer, i) : undefined,
            };
          })()
        : undefined}
      projectPresetOffer={item.kind === 'text' && presetOffers.has(i) && presetCardState.mode !== 'hidden'
        ? {
            state: presetCardState,
            pendingKey: presetOffers.get(i)?.key,
            appliedNote: presetNote,
            error: presetError,
            busy: presetBusy,
            onApply: (key) => void applyPreset(key),
            onDecline: () => void declinePreset(),
          }
        : undefined}
      promptSnapshotId={turnMeta.snapshots[i]}
      turnContextTokens={turnMeta.contextTokens[i]}
      turnCache={turnMeta.cache[i]}
    />
  ), [
    online, isWaiting, items.length, lastResultIndex, retryInterruptedIdx, toggleThinking, allowPermission,
    denyPermission, handleAllowAlways, answerQuestion, handleRespondPlan, planVersions,
    lastApprovedPlanIdx, mode, onOpenFile, project, handleRevert, handleRetry,
    interrupt, handleMigrateProvider, batchByIndex, showWaiting, taskTodos, changeMode, turnBoundaries,
    mechanicOffers, launchedByIndex, failedByIndex, declinedMechanicOffers, runTeamMechanic, scrollToMechanicLaunch,
    presetOffers, presetCardState, presetNote, presetError, presetBusy, applyPreset, declinePreset,
    turnMeta,
  ]);

  // Блок действий: подряд идущие карточки инструментов + изменения файлов объединяем
  // в один контур (внешние линии сверху/снизу + разделители между соседями). Стопку не
  // рвут ни file_changed, ни размышления между действиями, ни невидимые элементы —
  // как только агент пошёл дальше (следующий видимый элемент после группы), весь блок
  // (включая одиночное действие) сворачивается в строку «N действий».
  // Карта видимого медиа tool-блоков: сквозной дедуп по URL через всю ленту
  // (glif: project_update + view_media одного файла — один MediaBlock, выживает галерея).
  // Extract кэширован по ссылке на элемент, поэтому пересчёт на стрим-дельту дёшев
  const mediaVisibility = useMemo(() => buildMediaVisibility(items), [items]);

  // QA Fold 8: ошибки прошлых дней (ts < сегодня) склеиваем в error_group ПО ДНЯМ —
  // иначе красные баннеры «Session failed 13.08» плодятся в ленте и теснят живое.
  // Сегодняшние проходят как раньше (полноразмерный баннер). Одиночная ошибка
  // прошлого дня — тоже группа, но ErrorGroupView рисует её одной компактной строкой
  // без ката. «День» считаем по локальному времени (то, что видит пользователь в шапке
  // ленты). Ошибка без ts считается свежей (сегодняшней) — на бэкенде он должен быть,
  // но на всякий случай не теряем её.
  //
  // Группируем ВСЕ ошибки дня, а не только идущие подряд: каждая падает своим ходом, и
  // между ними всегда стоят сообщение пользователя и result следующего хода — по соседству
  // серия не собиралась бы никогда (round 2: 3 ошибки 13.08 остались тремя баннерами).
  // Карточка группы встаёт на место ПЕРВОЙ ошибки дня, остальные из ленты уходят в кат.
  //
  // Схлопывать массив ленты НЕЛЬЗЯ: индексы элементов — ключ к turnMeta, turnBoundaries,
  // batchByIndex и execZone, и укороченный display сдвинул бы всю метаинформацию после
  // первой же группы. Поэтому группа живёт картой «индекс первой ошибки дня → группа»,
  // а остальные ошибки гасятся набором индексов — как suppressedByWorkflow.
  const errorGroups = useMemo(() => {
    const startOfToday = (() => {
      const d = new Date(); d.setHours(0, 0, 0, 0); return d.getTime();
    })();
    const dayKey = (ms: number) => {
      const d = new Date(ms); d.setHours(0, 0, 0, 0); return d.getTime();
    };
    // День → индекс первой ошибки этого дня в ленте
    const anchorByDay = new Map<number, number>();
    const at = new Map<number, Extract<ChatItem, { kind: 'error_group' }>>();
    const suppressed = new Set<number>();
    items.forEach((it, idx) => {
      if (it.kind !== 'error' || typeof it.ts !== 'number' || it.ts >= startOfToday) return;
      const dk = dayKey(it.ts);
      const anchor = anchorByDay.get(dk);
      if (anchor === undefined) {
        anchorByDay.set(dk, idx);
        at.set(idx, { kind: 'error_group', date: dk, items: [it] });
      } else {
        at.get(anchor)!.items.push(it);
        suppressed.add(idx);
      }
    });
    return { at, suppressed };
  }, [items]);

  // «Командная реализация»: ход координатора — ход самого чата (он же персона чата),
  // поэтому он показывается обычной репликой этой персоны, как любой её ответ.
  // Свёртка в CoordinatorTurnCard была нужна, пока автор выглядел чужим — теперь,
  // когда координатор = персона чата, отдельная карточка лишь маскировала реального
  // автора. Служебный шум (⚑ staffNote штаба) гасится здесь же, набором
  // suppressedByTeamNoise — см. выше.

  // Окно рендера ленты и перевод item→узел работают на одном массиве: useMemo
  // ниже возвращает Array<{ node, start }> со склеенными блоками действий И
  // exec-зоной (каждый склеенный блок — один узел со start = start первого). Скрытые
  // узлы, видимые узлы, и itemIdxToNodePos(jumpToEscalation) смотрят на этот же
  // массив — одна координата, рассинхронить нельзя
  type RenderedNode = { node: React.ReactNode; start: number };

  // Группировка — O(n) с постройкой карт по всей ленте (useMemo).
  const renderedItems = useMemo((): RenderedNode[] => {
    // Display-лента = сама items: индексы обязаны совпадать с items (по ним ходят
    // turnMeta, turnBoundaries, batchByIndex, execZone). Ошибки прошлых дней рисуются
    // группой через errorGroups — карту «индекс → error_group» и набор гашеных индексов.
    const display = items;
    // Последний вызов каждой пачки (batchByIndex) исключаем из блока действий, как и TodoWrite:
    // на его месте рисуется отдельная карточка чек-листа, ей не место внутри контура
    // Выкатка прода исключена из блока действий по той же причине, что и workflow:
    // на её месте стоит карточка хода выкатки, а не строка инструмента
    const isTool = (it: ChatItem, idx: number) => it.kind === 'tool_use' && it.name !== 'TodoWrite' && !batchByIndex.has(idx) && !it.parentToolUseId && it.name.toLowerCase() !== 'workflow' && !isDeployStart(it.name);
    const inBlock = (it: ChatItem, idx: number) => isTool(it, idx) || it.kind === 'file_changed';
    // Ссылка на родителя есть у tool_use и у текста/thinking сабагента
    const parentOf = (it: ChatItem): string | undefined =>
      it.kind === 'tool_use' || it.kind === 'text' || it.kind === 'thinking' ? it.parentToolUseId : undefined;
    // Карта детей: ВСЕ parented-элементы (инструменты + текст/thinking сабагента)
    // в порядке ленты, с глобальными индексами для renderChild
    const childrenByParentId = new Map<string, ActivityEntry[]>();
    items.forEach((it, k) => {
      const pid = parentOf(it);
      if (!pid) return;
      const arr = childrenByParentId.get(pid) ?? [];
      arr.push({ item: it, idx: k });
      childrenByParentId.set(pid, arr);
    });
    // Элементы, которые рендерятся внутри WorkflowBlockView (субагенты, их инструменты
    // и текст). Наборы — по ссылке: у text/thinking нет id, а ссылки стабильны в проходе.
    const suppressedByWorkflow = new Set<ChatItem>();
    for (const it of items) {
      if (it.kind !== 'tool_use' || it.name.toLowerCase() !== 'workflow') continue;
      for (const e of (childrenByParentId.get(it.id) ?? [])) {
        suppressedByWorkflow.add(e.item);
        if (e.item.kind === 'tool_use')
          for (const g of (childrenByParentId.get(e.item.id) ?? [])) suppressedByWorkflow.add(g.item);
      }
    }
    // Служебный шум режима «Командная реализация» (плашки ⚑ staffNote штаба):
    // гасим как suppressed-набор, чтобы индексы display не съезжали и прыжки по
    // data-feed-index (закреплённая полоса эскалации, scrollToMechanicLaunch) остались
    // корректными. Вне режима штаба набор пуст — обычный чат не меняется.
    //
    // Гасим ТОЛЬКО по staffNote: каждый штабный триггер его несёт (TeamStaffNotes
    // из TeamWaveService/SessionManager/TaskExecutionService), а доклады исполнителей
    // без персоны приходят с auto=true и без staffNote — их трогать нельзя: они
    // попадают в ленту и должны остаться видимыми, иначе реплей истории после F5
    // даст расхождение (в историю пишется StoredUserMessage БЕЗ auto). systemDirective
    // сюда не входит: цикл «до готово» не относится к КР-механике и в КР-чате не
    // встречается.
    //
    // Гейт teamImplementState — осознанный: при выключении режима все ранее скрытые
    // ⚑-плашки возвращаются в ленту обратно. Иначе user_message со staffNote=true
    // остался бы подавленным и в обычном чате (это не тот вид подавления, что
    // принадлежит архиву/истории). Эффект «при выключении вернулось» согласован
    // с поведением других UI-флагов режима — их смена тоже не отзывается задним
    // числом, потому что они пересобираются через те же команды
    const suppressedByTeamNoise = new Set<number>();
    if (teamImplementState) {
      for (let k = 0; k < display.length; k++) {
        const it = display[k];
        if (it.kind !== 'user_message') continue;
        if (it.staffNote) suppressedByTeamNoise.add(k);
      }
    }
    // Дети top-level agent-вызовов рендерятся inline под родителем в блоке действий:
    // при параллельных агентах инструменты приходят вперемешку, и без группировки по родителю
    // все sub-tool строки сливаются в один безымянный блок.
    const suppressedByAgentParent = new Set<ChatItem>();
    for (const it of items) {
      if (it.kind !== 'tool_use' || !!it.parentToolUseId || it.name.toLowerCase() === 'workflow') continue;
      for (const e of (childrenByParentId.get(it.id) ?? [])) {
        if (!suppressedByWorkflow.has(e.item)) suppressedByAgentParent.add(e.item);
      }
    }
    // Дочерние элементы субагента (не-Workflow, не inline) — рисуем единой линией-коннектором слева
    const isSubItem = (it: ChatItem) => !!parentOf(it) && !suppressedByWorkflow.has(it) && !suppressedByAgentParent.has(it);
    // Узлы ленты с пометкой стартового индекса — нужно для обёртки success-коннектором
    const nodes: RenderedNode[] = [];
    const pushNode = (node: React.ReactNode, start: number) => nodes.push({ node, start });
    let i = 0;
    let prevNodeWasBlock = false;
    while (i < display.length) {
      // Ошибки прошлых дней: все ошибки одного дня рисуются одной группой на месте
      // первой из них, остальные пропускаем (индексы при этом не съезжают)
      if (errorGroups.suppressed.has(i)) { i++; continue; }
      const errGroup = errorGroups.at.get(i);
      if (errGroup) {
        pushNode(<div key={`sp-${i}`} style={{ marginTop: 3 }}>{renderItem(errGroup, i)}</div>, i);
        i++; prevNodeWasBlock = false; continue;
      }
      // Workflow-блок рендерим специальным компонентом. agents — стрим-субагенты
      // (tool_use-дети воркфлоу); их полный поток отдаёт карта childrenByParentId
      if (display[i].kind === 'tool_use' && (display[i] as ToolUseItem).name.toLowerCase() === 'workflow') {
        const wf = display[i] as ToolUseItem;
        const wfAgents = (childrenByParentId.get(wf.id) ?? [])
          .filter(e => e.item.kind === 'tool_use').map(e => e.item as ToolUseItem);
        pushNode(<WorkflowBlockView key={`wf-${wf.id}`} workflow={wf} agents={wfAgents} childrenByParentId={childrenByParentId} onOpenFile={onOpenFile} />, i);
        i++; prevNodeWasBlock = false; continue;
      }
      // Выкатка прода (ADR-010) — тем же приёмом: на месте вызова инструмента стоит
      // карточка хода выкатки, deployId она берёт из его result
      if (display[i].kind === 'tool_use' && isDeployStart((display[i] as ToolUseItem).name)) {
        const dep = display[i] as ToolUseItem;
        pushNode(
          <div key={`dep-${dep.id}`} style={{ marginTop: 3 }}>
            <DeployProgressCard item={dep} sessionId={session.id} online={online} onOpenFile={onOpenFile} />
          </div>,
          i,
        );
        i++; prevNodeWasBlock = false; continue;
      }
      // Служебный шум КР (⚑ staffNote штаба) — гасим, чтобы лента показывала
      // координатора обычными репликами. Индекс при этом не съезжает, data-feed-index
      // остальных элементов не страдает.
      // Наборы подавления дизъюнктны по kind: suppressedByTeamNoise — только
      // user_message со staffNote, suppressedByWorkflow/agentParent — только
      // элементы с parentToolUseId (childrenByParentId/WorkflowBlockView),
      // errorGroups — только error/error_group. Поэтому порядок проверок на
      // корректность склейки блоков не влияет: блок действий собирает
      // НЕподавленные соседние tool_use, а если подавленный элемент попал
      // в группу, его пропустит isSubItem/isInvisible/isSuppressed внутри
      // lookahead-цикла ниже. Гашение здесь нужно только чтобы САМОМУ
      // подавленному элементу не выделился data-feed-index — иначе баннер
      // «К карточке» найдёт его в DOM как обычный и прыгнет не туда
      if (suppressedByTeamNoise.has(i)) { i++; continue; }
      // Элементы, отрисованные внутри WorkflowBlockView или inline под родителем-агентом,
      // в основной ленте пропускаем (любой kind: инструменты, текст, thinking)
      if (suppressedByWorkflow.has(display[i]) || suppressedByAgentParent.has(display[i])) {
        i++; continue;
      }
      if (isSubItem(display[i])) {
        const start = i;
        const sub: Array<[ChatItem, number]> = [];
        while (i < display.length && isSubItem(display[i])) { sub.push([display[i], i]); i++; }
        // Один контейнер с borderLeft на всю стопку дочерних → линия не прерывается gap'ом ленты
        const subDiv = (
          <div key={`sub-${itemKey(sub[0][0], start)}`} style={{ marginLeft: 8, paddingLeft: 14, borderLeft: `2px solid ${C.border}` }}>
            {sub.map(([it, idx], gi) => (
              <div key={itemKey(it, idx)} style={gi === 0 ? undefined : { borderTop: `1px solid ${C.bgInset}` }}>{renderItem(it, idx)}</div>
            ))}
          </div>
        );
        if (prevNodeWasBlock && nodes.length > 0) {
          // Прижать к шапке: объединяем дочерние инструменты с предшествующим блоком без gap
          const prev = nodes[nodes.length - 1];
          nodes[nodes.length - 1] = {
            node: <Fragment key={`merged-${prev.start}`}>{prev.node}{subDiv}</Fragment>,
            start: prev.start,
          };
        } else {
          pushNode(subDiv, start);
        }
        prevNodeWasBlock = false;
      } else if (inBlock(display[i], i)) {
        const start = i;
        const slice: Array<[ChatItem, number]> = [];
        // Прозрачные для группировки: рендерятся в null и не должны рвать стопку действий.
        // session_started на «границе» дерева хода (turnBoundaries) — исключение: он
        // теперь ВИДИМ (разделитель «ход в дереве агента»/«ход вернулся в проект»)
        // и обязан рвать стопку действий, как любой другой видимый элемент
        const isInvisible = (it: ChatItem, idx: number) =>
          (it.kind === 'session_started' && !turnBoundaries.has(idx)) || it.kind === 'resumed' || it.kind === 'fal_cost' || it.kind === 'glif_cost';
        // Размышления верхнего уровня прячем внутрь группы, если они стоят МЕЖДУ действиями
        const isThought = (it: ChatItem) => (it.kind === 'thinking' && !it.parentToolUseId) || it.kind === 'redacted_thinking';
        // isSuppressed включает и гашение штабного шума по индексу — иначе
        // блок действий соберёт соседей вокруг подавленных элементов и прыжки
        // по data-feed-index поплывут
        const isSuppressed = (it: ChatItem, idx: number) =>
          suppressedByTeamNoise.has(idx)
          || suppressedByWorkflow.has(it)
          || suppressedByAgentParent.has(it);
        while (i < display.length) {
          if (isSuppressed(display[i], i) || isInvisible(display[i], i)) { i++; continue; }
          if (inBlock(display[i], i)) { slice.push([display[i], i]); i++; continue; }
          if (isThought(display[i])) {
            // Lookahead: впитываем размышления, только если дальше идёт ещё действие —
            // размышление перед финальным ответом остаётся видимой строкой над ним
            let j = i;
            while (j < display.length && (isThought(display[j]) || isInvisible(display[j], j) || isSuppressed(display[j], j))) j++;
            if (j < display.length && inBlock(display[j], j)) {
              for (; i < j; i++) if (isThought(display[i])) slice.push([display[i], i]);
              continue;
            }
          }
          break;
        }
        // Один контур: инструменты и изменения файлов — компактными строками (в т.ч. одиночные).
        // Для agent-вызовов с детьми сразу рисуем детей inline под родителем — иначе при параллельных
        // агентах все их инструменты сливаются в один безымянный блок после шапки.
        // Вызов агента (Task/Agent) в свёрнутой шапке ведёт себя как изменения файлов:
        // виден в summary, на своём месте при раскрытии и НЕ входит в счётчик «N действий»
        const isAgentEntry = (it: ChatItem) =>
          it.kind === 'tool_use' && (it.name.toLowerCase() === 'task' || it.name.toLowerCase() === 'agent');
        // Медиа-результаты (сгенерированные fal.ai картинки/видео) тоже не прячем в свёртку:
        // карточка с изображением и футером ведёт себя как агенты — видна в summary,
        // на своём месте при раскрытии и не входит в счётчик «N действий».
        // Считаем по карте дедупа: блок, чьё медиа целиком скрыто (glif project_update
        // после media_view), — обычная строка инструмента
        const isMediaEntry = (it: ChatItem) =>
          it.kind === 'tool_use' && !it.isError && typeof it.result === 'string'
          && (mediaVisibility.get(it.id)?.length ?? 0) > 0;
        // Карточка «Задача создана» (tasks_create) — тоже не прячется в свёртку;
        // ошибочный вызов деградирует в обычную строку инструмента и сворачивается как все
        const isTaskCardEntry = (it: ChatItem) => it.kind === 'tool_use' && !it.isError && isTasksCreate(it.name);
        // HTML-виджет (widget_show) — визуальный результат для пользователя, как медиа:
        // всегда виден в ленте, в свёртку не прячется. Ошибка — обычная строка
        // инструмента, сворачивается как все
        const isWidgetEntry = (it: ChatItem) =>
          it.kind === 'tool_use' && !it.isError && isWidgetShow(it.name);
        const isPinnedEntry = (it: ChatItem) => isAgentEntry(it) || isMediaEntry(it) || isTaskCardEntry(it) || isWidgetEntry(it);
        const toolCount = slice.filter(([it]) => it.kind === 'tool_use' && !isPinnedEntry(it)).length;
        // Группа завершена, как только после неё появился следующий видимый элемент
        // (текст ассистента, запрос разрешения, result, error…) — конца хода не ждём.
        // Хвостовые размышления не сигнал: они могут впитаться в группу при следующем
        // действии, и группа мигала бы свернулась/раскрылась на каждом межшаговом thinking.
        let after = i;
        while (after < display.length && (isThought(display[after]) || isInvisible(display[after], after) || isSuppressed(display[after], after))) after++;
        // Последняя группа сворачивается и когда после неё ещё нет видимого элемента,
        // но ход уже завершён (сессия не работает): иначе действия последнего диалога
        // оставались бы раскрытыми в отличие от всех предыдущих групп.
        const isGroupDone = after < display.length || !sessionBusy;
        // Изменения файлов не теряются при сворачивании: в свёрнутой шапке — те же плашки
        // (дедуп по пути, +N/−N событий суммируются), при раскрытии они на своих местах
        const fileAgg = new Map<string, Extract<ChatItem, { kind: 'file_changed' }>>();
        for (const [it] of slice) {
          if (it.kind !== 'file_changed') continue;
          const prev = fileAgg.get(it.path);
          // external — по И: если хоть один вклад был от модели этого чата, строка в целом не «чужая»
          fileAgg.set(it.path, prev
            ? { ...it, added: prev.added + it.added, removed: prev.removed + it.removed, external: (prev.external ?? false) && (it.external ?? false) }
            : it);
        }
        // Единый рендер элемента группы — и в раскрытом виде (children), и в свёрнутой
        // шапке (summary, куда попадают агенты и агрегированные изменения файлов)
        const renderGroupEntry = (it: ChatItem, idx: number, topBorder: boolean) => {
          // Финальный текст сабагента из транскрипта дублирует тело ответа (tool_result) —
          // после завершения в активности его не показываем (ответ рендерит сама карточка)
          const answerBody = it.kind === 'tool_use' && typeof it.result === 'string'
            ? splitAgentResultTail(it.result).body.trim() : null;
          const inlineChildren: ActivityEntry[] = it.kind === 'tool_use'
            ? (childrenByParentId.get(it.id) ?? []).filter(e => !suppressedByWorkflow.has(e.item)
                && !(answerBody !== null && e.item.kind === 'text' && e.item.text.trim() === answerBody))
            : [];
          // Консультация персоны-сабагента: активность рендерится СЕКЦИЕЙ ВНУТРИ
          // карточки (PersonaTaskView), внешняя плашка «N действий» не нужна
          const isPersonaTask = it.kind === 'tool_use' && inlineChildren.length > 0
            && !!findConsultedPersona(it, getPersonasSnapshot(), project?.id ?? null);
          return (
            <Fragment key={itemKey(it, idx)}>
              <div data-feed-index={idx} style={topBorder ? { borderTop: `1px solid ${C.bgInset}` } : undefined}>
                {it.kind === 'file_changed'
                  ? <FileChangedRow item={it} online={online} onOpenFile={onOpenFile} onRevert={project ? handleRevert : undefined} />
                  : renderItem(it, idx, isPersonaTask
                      ? { agentActivity: inlineChildren, agentRenderChild: renderItem }
                      : undefined)}
              </div>
              {inlineChildren.length > 0 && !isPersonaTask && (
                <AgentActionsBlock
                  entries={inlineChildren}
                  renderChild={renderItem}
                />
              )}
            </Fragment>
          );
        };
        // В свёрнутой шапке видны агенты и медиа-карточки (в порядке ленты) и агрегированные плашки файлов
        const agentSummary = slice.filter(([it]) => isPinnedEntry(it))
          .map(([it, idx]) => renderGroupEntry(it, idx, true));
        const filesSummary = [...fileAgg.values()].map(f => (
          <div key={`fsum-${f.path}`} style={{ borderTop: `1px solid ${C.bgInset}` }}>
            <FileChangedRow item={f} online={online} onOpenFile={onOpenFile} onRevert={project ? handleRevert : undefined} />
          </div>
        ));
        const summaryNodes = [...agentSummary, ...filesSummary];
        pushNode(
          <ToolGroupBlock key={`grp-${itemKey(slice[0][0], start)}`} isGroupDone={isGroupDone} toolCount={toolCount} summary={summaryNodes.length > 0 ? summaryNodes : undefined}>
            {slice.map(([it, idx], gi) => renderGroupEntry(it, idx, gi !== 0))}
          </ToolGroupBlock>,
          start
        );
        prevNodeWasBlock = true;
      } else {
        const item = display[i];
        const kind = item.kind;
        const node = renderItem(item, i);
        // Якорь прыжка ОБЯЗАН иметь бокс: scrollIntoView в Blink на элементах без
        // layout-объекта (display:contents, span-обёртки) выходит сразу и не скроллит,
        // а outline/background .escalation-flash рисовать не на чем. Поэтому любые
        // карточки, к которым прыгает закреплённая полоса «Практика ждёт вашего решения»
        // и сам ChatPanel через scrollToMechanicLaunch/scrollToEscalation, идут через
        // ветку `<div ... data-feed-index>` ниже, а не через последнюю <span>
        // display:contents
        const needsTopSpacing = kind === 'text' || kind === 'user_message' || kind === 'result'
          || kind === 'error' || kind === 'error_group'
          || kind === 'team_escalation' || kind === 'team_plan'
          || kind === 'ask_question' || kind === 'permission_request'
          || kind === 'plan_review';
        // Вправо идёт ТОЛЬКО настоящий пузырь пользователя. Плашки-разделители
        // (staffNote, systemDirective, авто-слэш-команды) и карточки viaAgent/auto
        // центрируются по колонке — как и result-узлы рядом.
        const isUserBubble = kind === 'user_message'
          && !item.staffNote
          && !item.systemDirective
          && !item.viaAgent
          && !item.auto;
        pushNode(
          isUserBubble
            ? <div key={`sp-${i}`} data-feed-index={i} style={{ marginTop: 3, display: 'flex', justifyContent: 'flex-end' }}>{node}</div>
            : (kind === 'user_message' || kind === 'result')
              ? <div key={`sp-${i}`} data-feed-index={i} style={{ marginTop: 3, display: 'flex', justifyContent: 'center' }}>{node}</div>
              : needsTopSpacing
                ? <div key={`sp-${i}`} data-feed-index={i} style={{ marginTop: 3 }}>{node}</div>
                : <Fragment key={`sp-${i}`}><span data-feed-index={i} style={{ display: 'contents' }}>{node}</span></Fragment>,
          i
        );
        i++;
        prevNodeWasBlock = false;
      }
    }

    // success-коннектор: непрерывные узлы из «зоны реализации» (после одобренного плана)
    // оборачиваем в одну левую зелёную линию — «эти правки реализуют план».
    // Склейка ОСТАЁТСЯ в виде одного узла со start = start первого узла группы:
    // jumpToEscalation и окно работают в координатах этого массива, и смешение
    // «узлов внутри зоны» с «узлами снаружи» ломает обе координаты. То есть
    // renderedItems — единственный источник правды: скрытые узлы, видимые узлы,
    // и перевод itemIdxToNodePos смотрят на один и тот же массив
    if (!execZone) return nodes;
    const result: RenderedNode[] = [];
    let j = 0;
    while (j < nodes.length) {
      const inZone = (n: { start: number }) => n.start >= execZone.start && n.start < execZone.end;
      if (inZone(nodes[j])) {
        const group: React.ReactNode[] = [];
        const groupStart = nodes[j].start;
        while (j < nodes.length && inZone(nodes[j])) { group.push(nodes[j].node); j++; }
        result.push({
          start: groupStart,
          node: (
            <div key={`exec-${groupStart}`} style={{ marginLeft: 8, paddingLeft: 14, borderLeft: `3px solid ${C.success}`, display: 'flex', flexDirection: 'column', gap: 3, marginTop: -3 }}>
              {group}
            </div>
          ),
        });
      } else {
        result.push(nodes[j]); j++;
      }
    }
    return result;
    // personasVersion: findConsultedPersona матчит по стору персон — после его загрузки
    // карточки консультаций пересобираются с активностью внутри
    // eslint-disable-next-line react-hooks/exhaustive-deps -- personasVersion — намеренный cache-bust: пересборка карточек после загрузки стора персон
  }, [items, renderItem, batchByIndex, execZone, online, onOpenFile, project, handleRevert, personasVersion, sessionBusy, turnBoundaries, mediaVisibility, errorGroups, teamImplementState, session.id]);

  // Прыжок из баннера к карточке: лента режется окном (WINDOW_FIRST=50), и нужный
  // узел за пределами видимой области физически отсутствует в DOM — простой
  // querySelector+scrollIntoView вернёт null. idx — индекс item; сначала переводим
  // его в позицию узла (itemIdxToNodePos), раздвигаем окно, и уже после ререндера —
  // скроллим и подсвечиваем целевой item по data-feed-index (он адресует item,
  // не узел — это правильно: scrollIntoView попадает ровно в ту карточку, к которой
  // прыгаем). Поиск идёт внутри scrollRef, а не document: у ChatPanel бывает режим
  // embedded, и две ленты в DOM одновременно. deps по renderedItems: ref не нужен,
  // потому что hiddenCount (set внутри) — отдельный источник, а nodes/idx идут
  // аргументами. callback стабилен, пока не пересобран useMemo
  const jumpToEscalation = useCallback((idx: number) => {
    const nodePos = itemIdxToNodePos(renderedItems, idx);
    setHiddenCount((h) => computeJumpHidden(nodePos, h, renderedItems.length, WINDOW_FIRST));
    // После ререндера — querySelector по data-feed-index={idx} (item-индекс).
    // Повтор на следующем кадре: если React не успел закоммитить обновлённый срез
    // к первому rAF (StrictMode, ререндер по другим setState), querySelector вернёт
    // null и клик снова станет молчаливым no-op. Дешёвая страховка — один повтор
    const flash = (): boolean => {
      const node = scrollRef.current?.querySelector<HTMLElement>(`[data-feed-index="${idx}"]`);
      if (!node) return false;
      node.scrollIntoView({ behavior: 'smooth', block: 'center' });
      node.classList.add('escalation-flash');
      window.setTimeout(() => node.classList.remove('escalation-flash'), 1500);
      return true;
    };
    if (!flash()) requestAnimationFrame(flash);
  }, [renderedItems, scrollRef]);

  // Окно рендера ленты: монтируем только хвост, скрывая ведущие узлы. Состояние —
  // число СКРЫТЫХ сверху УЗЛОВ (hiddenCount), а не «сколько показано»: при стриминге
  // новых сообщений хвост растёт сам, а позиция чтения в середине окна не прыгает.
  // Узел = элемент renderedItems (см. useMemo выше): одиночный item или склеенный
  // блок действий, или сводка exec-зоны — все они считаются одной записью с одним
  // start. null = «по умолчанию» (показать последние WINDOW_FIRST) — до первого действия
  // пользователя окно следует за концом ленты.
  const [hiddenCount, setHiddenCount] = useState<number | null>(null);
  // eslint-disable-next-line react-hooks/set-state-in-effect -- сброс окна при смене чата: панель переиспользуется между сессиями (без key), как и mode выше
  useEffect(() => { setHiddenCount(null); }, [session.id]);
  const hidden = Math.min(
    hiddenCount ?? Math.max(0, renderedItems.length - WINDOW_FIRST),
    Math.max(0, renderedItems.length - 1),
  );
  const visibleNodes = useMemo(
    // map в ReactNode[]: всё, что ниже (Provider-цепочка → {visibleNodes}), ждёт
    // рендер-детей, а не структуру {node, start}. После смены типа renderedItems
    // единая точка правды: hidden/visibleNodes/jumpToEscalation смотрят на один массив
    () => (hidden > 0 ? renderedItems.slice(hidden) : renderedItems).map(n => n.node),
    [renderedItems, hidden],
  );

  // Подгрузка более ранних сообщений. Компенсация scrollTop на высоту вставленного
  // блока — layout-эффектом до кадра, чтобы видимая часть ленты не сдвигалась
  // (иначе каждое раскрытие давало бы скачок контента и рос CLS).
  const prependCompRef = useRef<{ h: number; top: number } | null>(null);
  const showEarlier = useCallback(() => {
    const el = scrollRef.current;
    if (el) prependCompRef.current = { h: el.scrollHeight, top: el.scrollTop };
    setHiddenCount(h => {
      const cur = h ?? Math.max(0, renderedItems.length - WINDOW_FIRST);
      return Math.max(0, cur - WINDOW_STEP);
    });
  }, [renderedItems.length, scrollRef]);
  useLayoutEffect(() => {
    const p = prependCompRef.current;
    if (!p) return;
    prependCompRef.current = null;
    const el = scrollRef.current;
    if (el) el.scrollTop = p.top + (el.scrollHeight - p.h);
  }, [hidden, scrollRef]);

  // Авто-догрузка при прокрутке к верху: якорь-сентинел перед первым видимым узлом.
  // После вставки пачки компенсация уносит сентинел выше вьюпорта, поэтому цепочки
  // «загрузилось всё сразу» нет — каждая порция требует нового подхода к верху.
  const topSentinelRef = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const sentinel = topSentinelRef.current;
    const root = scrollRef.current;
    if (!sentinel || !root || hidden <= 0) return;
    const io = new IntersectionObserver(
      entries => { if (entries.some(e => e.isIntersecting)) showEarlier(); },
      // небольшой верхний запас — пачка подгружается чуть раньше касания края
      { root, rootMargin: '240px 0px 0px 0px' },
    );
    io.observe(sentinel);
    return () => io.disconnect();
  }, [hidden, showEarlier, scrollRef]);

  // Подпал цветом проекта под верхом чата (см. слой в разметке ниже)
  const projectWash = projectTopWash(project);

  const headerBar = (
    <ChatHeaderBar
      island={headerIsland}
      compact={embedded}
      session={session}
      project={project}
      hasMessages={hasMessages}
      online={online}
      cost={costStats}
      falCost={falCostStats}
      glifCost={glifGenStats}
      billing={claudeBilling}
      onBillingChange={canEditBilling ? changeBilling : undefined}
      rateWindows={rateWindows}
      isMobile={isMobile}
      onBack={onBack}
      activeWorkflow={activeWorkflowInfo ?? undefined}
      lastMechanic={lastMechanic}
      onOpenSidebar={onOpenSidebar}
      ctxEstimate={ctxEstimate}
      isWaiting={isWaiting}
      isCompacting={isCompacting}
      canCompact={canCompact}
      compactNote={compactNote}
      onCompact={compact}
      persona={persona}
      personaZoneName={project?.name ?? null}
      agent={persona ? null : chatAgent}
      participants={isGroupChat ? participantPersonas : null}
      onSessionUpdated={onSessionUpdated}
    />
  );

  return (
    <AssistantNameContext.Provider value={asstName}>
    <PersonaContext.Provider value={persona}>
    {/* embedded (колонка стены) — тоже прозрачный: подложку даёт стеклянный остров
        колонки, а собственный плотный фон закрывал бы дудл-холст под ней */}
    <div style={{
      display: 'flex', flexDirection: 'column', height: '100%', position: 'relative',
      background: headerIsland || embedded ? 'transparent' : C.bgMain,
    }}>
      {/* Подпал цветом проекта под верхом чата — ПО ШИРИНЕ ЛЕНТЫ, а не всего центра:
          растянутый на всю ширину он читался бы как фон экрана, а не как метка этого
          чата. На стене подпал рисует сам остров колонки (там он и есть карточка) */}
      {!embedded && projectWash && (
        <div style={{
          position: 'absolute', top: 0, left: '50%', transform: 'translateX(-50%)',
          width: '100%', maxWidth: CHAT_MAX_W, height: 96,
          backgroundImage: projectWash, pointerEvents: 'none',
          // Верхние углы скруглены — подпал читается как продолжение карточки
          // чата, а не как прямоугольная плашка, наклеенная поверх
          borderTopLeftRadius: R.xxl, borderTopRightRadius: R.xxl,
        }} />
      )}
      {/* В режиме headerIsland шапка сама рисует себя hero-вариантом прямо на
          холсте (ChatHeaderBar, ветка island) — обёртки не нужно. На стене
          (embedded) шапка тоже штатная — канонический вид чата; над ней колонка
          рисует свою тонкую полосу-ярлык (проект + zoom), это не дубль шапки.
          headerDragProps — шапка работает второй ручкой перетаскивания колонки */}
      {headerDragProps ? <div {...headerDragProps}>{headerBar}</div> : headerBar}

      {/* Сообщения (нижний отступ = высота плавающего composer + зазор).
          Прокручивается НЕ вся ширина области, а колонка сообщений: иначе полоса
          прокрутки рисуется по краю широкого центра и на большом экране висит
          в сотне пикселей от текста, рядом с кнопкой «вниз». Внешняя обёртка
          только центрирует колонку и сама не скроллится. */}
      <div style={{ flex: 1, minHeight: 0, display: 'flex', justifyContent: 'center' }}>
      <div ref={scrollRef} onScroll={handleMessagesScroll} data-selection-scope="chat" data-selection-target="[data-selection-doc]" data-selection-priority="1" style={{ flex: 1, minWidth: 0,
        // Область прокрутки = боковое поле слева + колонка сообщений +
        // место под полосу справа. Полоса идёт вплотную к правому краю сообщений (не
        // по краю широкого центра), а сама колонка остаётся по центру окна — иначе она
        // разошлась бы с композером, который центрируется отдельно. Ширину коробки и
        // компенсацию перекоса считает useChatGutter (они зависят от ширины полосы, а
        // её приходится мерить) и кладёт в переменные, которые читают эти два свойства.
        // embedded (колонка стены): колонка узкая, центрировать ленту внутри неё
        // незачем — прокрутка идёт по ВСЕЙ ширине, и полоса встаёт у правого края
        // самой колонки, как в любом списке. Компенсация перекоса тут не нужна.
        maxWidth: isMobile ? CHAT_MAX_W + CHAT_GUTTER_MOBILE * 2 : embedded ? undefined : `var(${VAR_W}, ${CHAT_MAX_W + CHAT_GUTTER_L}px)`,
        marginRight: isMobile || embedded ? undefined : `var(${VAR_SHIFT}, 0px)`,
        paddingLeft: isMobile ? CHAT_GUTTER_MOBILE : CHAT_GUTTER_L,
        scrollbarGutter: isMobile ? undefined : 'stable',
        // Справа: в обычном чате поле даёт зарезервированное место под полосу плюс
        // внешний отступ-компенсация (колонка остаётся по центру окна). На стене
        // компенсировать некуда, поэтому остаток до CHAT_GUTTER_L добирается
        // паддингом — величину считает useChatGutter замером полосы.
        overflowY: 'auto', overflowX: 'hidden', position: 'relative', paddingTop: isMobile ? 16 : 20,
        paddingRight: isMobile ? CHAT_GUTTER_MOBILE : embedded ? `var(${VAR_PAD_R}, 0px)` : 0, paddingBottom: 8,
        // Лента заканчивается НАД композером, а не подлезает под него: раньше это был
        // paddingBottom, и контент прокручивался в прозрачных промежутках композера
        // (между карточкой ввода и полосой кнопок). marginBottom ужимает саму область
        // прокрутки, поэтому overflow обрезает сообщения по её нижней границе.
        marginBottom: composerH }}><div ref={contentRef} style={{ display: 'flex', flexDirection: 'column', gap: 2, width: '100%', maxWidth: CHAT_MAX_W, margin: '0 auto' }}>
        {/* Спиннер загрузки истории */}
        {items.length === 0 && isHistoryLoading && (
          <div style={{
            position: 'absolute', inset: 0,
            display: 'flex', alignItems: 'center', justifyContent: 'center',
          }}>
            <div className="tool-spinner" style={{ width: 22, height: 22, borderWidth: 2.5 }} />
          </div>
        )}

        {/* Empty state: приветствие персоны (если задано) + сам empty state с рядом
            персон «Поговорить с…» и пилюлями настройки будущего чата. Собственный
            greetingBubble (гейт онбординга) заменяет пустую ленту целиком */}
        {items.length === 0 && !isHistoryLoading && online && (
          greetingBubble ?? (
            <>
              {personaGreeting}
              <ChatEmptyState hasProject={!!project} hasCLAUDEmd={hasCLAUDEmd} onHint={handleHint}
                session={session} project={project} onSessionUpdated={onSessionUpdated} isMobile={isMobile}
                personas={ctxPersonas} selectedPersonaId={session.personaId} onPickPersona={handlePersonaChange}
                compact={embedded} greetingAbove={!!personaGreeting} />
            </>
          )
        )}

        {/* Окно рендера: якорь авто-догрузки и кнопка «Показать предыдущие» —
            видны, только пока сверху ленты остаются скрытые узлы */}
        {hidden > 0 && (
          <>
            {/* 1px-якорь для IntersectionObserver: пересечение с верхом ленты = догрузить пачку */}
            <div ref={topSentinelRef} style={{ height: 1 }} />
            <div style={{ display: 'flex', justifyContent: 'center', paddingTop: SP.xxs, paddingBottom: SP.sm }}>
              <Button
                variant="ghostFilled"
                size={isMobile ? 'md' : 'sm'}
                pill
                leftIcon={<ArrowUp size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
                onClick={showEarlier}
              >
                Показать предыдущие ({Math.min(WINDOW_STEP, hidden)})
              </Button>
            </div>
          </>
        )}

        <FalCostContext.Provider value={falCostByRequest}><GlifCostContext.Provider value={glifCostByJob}><MediaVisibilityContext.Provider value={mediaVisibility}><ChatProjectContext.Provider value={projectCtx}><ChatTreePathContext.Provider value={treePathCtx}><ChatSessionContext.Provider value={session.id}><ChatOpenFileContext.Provider value={onOpenFile ?? null}><ChatOpenReaderContext.Provider value={onOpenReader ?? null}><ChatOpenTaskContext.Provider value={onOpenTaskAside ?? null}><TeamPlanContext.Provider value={teamPlanCtx}><TeamEscalationContext.Provider value={teamEscalationCtx}><SpeakingItemContext.Provider value={speakingItem}>{visibleNodes}</SpeakingItemContext.Provider></TeamEscalationContext.Provider></TeamPlanContext.Provider></ChatOpenTaskContext.Provider></ChatOpenReaderContext.Provider></ChatOpenFileContext.Provider></ChatSessionContext.Provider></ChatTreePathContext.Provider></ChatProjectContext.Provider></MediaVisibilityContext.Provider></GlifCostContext.Provider></FalCostContext.Provider>

        {/* Карточка «Готовит план…»: стадия планирования идёт минутами (потолок
            планировщика 300с), и молчащая лента читалась как «всё встало» (прод 2026-08-04).
            Теперь — карточка той же формы, что PersonaConsultCard: аватар/имя/цвет
            планировщика вместо безличной плашки. Персона приходит из события team_planning
            (бэкенд прокидывает ResolvePlanner), фолбэк — резолв по teamImplementState и
            персоне чата — на случай старого события без personaId. Гаснет сама по
            teamPlanningIndicatorVisible */}
        {showTeamPlanningIndicator && (() => {
          const plannerId = resolvePlannerPersonaId(teamImplementState, liveTeamPlanning?.personaId, session.personaId);
          // eslint-disable-next-line react-hooks/rules-of-hooks -- getPersonaById читает нереактивный стор; personasVersion нужен, чтобы бамп заставил пересчитать (deps через key в ChatPanel)
          const plannerPersona = plannerId ? getPersonaById(plannerId) : null;
          return (
            <TeamPlanningIndicator
              startedAt={liveTeamPlanning?.startedAt}
              persona={plannerPersona}
            />
          );
        })()}

        {online && showWaiting && (
          // Индикатор стоит В ПОТОКЕ, по левому краю сообщений — как аватар обычной
          // строки. Раньше его выносили наружу отрицательным отступом (-38), и под это
          // держали жёлоб 52px слева от ленты: в узкой колонке стены домик лез на рамку,
          // а текст ленты уезжал вправо. Кольца «Эхо» выступают за аватар примерно на
          // 12px (scale 1.85 от 28) — бокового поля ленты (CHAT_GUTTER_L / на мобиле
          // CHAT_GUTTER_MOBILE + мельче размах, media query в index.css) на них хватает,
          // клип области прокрутки (overflow-x: hidden) пульс не режет.
          //
          // Пилюля плана — в ЭТОЙ ЖЕ строке, справа, и живёт независимо от хода: индикатор
          // гаснет по концу хода, а прогресс нужно смотреть как раз в паузе. Общая строка
          // держит её на одном месте в обоих состояниях, без прыжка при старте/конце хода.
          <div style={{ marginTop: 5, display: 'flex', alignItems: 'center', gap: 10 }}>
            <WaitingIndicator planning={planningKind} awaitingResponse={awaitingResponse} />
            <div style={{ marginLeft: 'auto', minWidth: 0, display: 'flex' }}>
              <TurnPlanPill todos={taskTodos} />
            </div>
          </div>
        )}

        {/* Сообщения агентов, ждущие конца хода: в самом низу ленты — они придут следующими.
            Живут только в памяти сервера, в истории их нет; крестик снимает доставку. */}
        {/* Перебой предлагаем только когда ход реально идёт: без него прерывать нечего,
            а кнопка на стоящей очереди читалась бы как сломанная */}
        <PendingMessageList items={pending} isMobile={isMobile}
          onCancel={online ? cancelPending : undefined}
          onPreempt={online && isWaiting ? preemptForPending : undefined} />

        {/* Баннер прерванной сессии — в конце ленты, после истории. Возобновление — НА МЕСТЕ:
            обычный ход «Продолжи» в эту же сессию (бэкенд резюмирует транскрипт через --resume),
            чат сохраняет имя, историю и связь с родительским. Пока ход идёт (isWaiting), баннер
            скрыт — статус orphaned снимет status_changed от сервера. */}
        {session.status === 'orphaned' && !isHistoryLoading && !isWaiting && (() => {
          const hasPending = items.some(it =>
            (it.kind === 'ask_question' || it.kind === 'permission_request' || it.kind === 'plan_review')
            && !it.resolved
          );
          const BannerIcon = hasPending ? CircleHelp : RotateCw;
          return (
            <div style={{
              alignSelf: 'center', width: '100%', maxWidth: 440,
              display: 'flex', flexDirection: 'column', alignItems: 'center', gap: 14,
              padding: '26px 24px', marginTop: 28,
              background: C.bgPanel, borderRadius: 16,
              border: `1px solid ${C.border}`, boxShadow: SHADOW.card,
            }}>
              {/* Иконка в тёплом кружке — статус паузы, а не ошибка */}
              <div style={{
                width: 46, height: 46, borderRadius: R.full, flexShrink: 0,
                display: 'flex', alignItems: 'center', justifyContent: 'center',
                background: C.accentSoft, color: C.accent,
              }}>
                <BannerIcon size={22} strokeWidth={2} />
              </div>
              <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', gap: 5 }}>
                <span style={{ fontSize: 15, fontWeight: 600, color: C.textHeading, textAlign: 'center' }}>
                  {hasPending ? 'Чат ждёт вашего ответа' : 'Чат приостановлен'}
                </span>
                <span style={{ fontSize: 13, lineHeight: 1.5, color: C.textSecondary, textAlign: 'center', maxWidth: 340 }}>
                  {hasPending
                    ? `Сервер перезапустился, не дождавшись вашего ответа — запрос устарел. Продолжите диалог, и ${asstName} при необходимости спросит снова.`
                    : `Диалог прервался при перезапуске сервера. ${asstName} продолжит с того же места.`}
                </span>
              </div>
              <button
                onClick={() => { atBottomRef.current = true; void send('Продолжи', [], mode); }}
                style={{
                  display: 'inline-flex', alignItems: 'center', gap: 7,
                  padding: '9px 22px', borderRadius: R.lg, fontSize: 13, fontWeight: 600,
                  background: C.accent, border: 'none', cursor: 'pointer', color: C.onAccent,
                  boxShadow: SHADOW.button, transition: 'opacity 0.12s',
                }}
                onMouseEnter={e => { e.currentTarget.style.opacity = '0.88'; }}
                onMouseLeave={e => { e.currentTarget.style.opacity = '1'; }}
              >
                <RotateCw size={15} strokeWidth={2.2} />
                Возобновить
              </button>
            </div>
          );
        })()}

        <div ref={bottomRef} />
      </div></div>
      </div>

      {/* Плавающая кнопка «вниз» — появляется, когда лента отлистана вверх.
          Геометрия повторяет композер (те же padding и CHAT_MAX_W по центру), поэтому
          кнопка встаёт над кнопкой отправки — у правого края КОЛОНКИ ЧТЕНИЯ, а не
          контейнера панели: в разделах «Проекты»/«Чаты» контейнер тянется до рельсы
          панелей, и привязка к его краю уносила кнопку в пустоту сбоку от ленты.
          По вертикали — прямо над композером: круглешок AI приклеен к углу ЭКРАНА и на
          подошедший композер отвечает ужиманием, а не подъёмом, так что уступать ему
          место не надо. */}
      {showScrollDown && (
        <div style={{
          position: 'absolute', left: 0, right: 0, bottom: composerH + 14,
          padding: isMobile ? `0 ${CHAT_GUTTER_MOBILE}px` : `0 ${CHAT_GUTTER_L}px`,
          pointerEvents: 'none', zIndex: 15, transition: 'bottom 0.3s ease',
        }}>
          <div style={{ maxWidth: CHAT_MAX_W, margin: '0 auto', display: 'flex', justifyContent: 'flex-end' }}>
            <button
              onClick={scrollToBottom}
              title="Вниз чата"
              style={{
                // Служебная прокрутка — нейтральная (не accent), чтобы единственным
                // акцентом в углу оставался круглешок AI.
                pointerEvents: 'auto',
                width: 44, height: 44, borderRadius: '50%',
                border: `1px solid ${C.border}`,
                background: C.bgCard, color: C.textSecondary, cursor: 'pointer',
                display: 'flex', alignItems: 'center', justifyContent: 'center',
                boxShadow: SHADOW.card,
              }}
            >
              <ArrowDown size={22} strokeWidth={2.2} />
            </button>
          </div>
        </div>
      )}

      {/* Composer — плавающий над лентой; фон прозрачный, контент виден под/вокруг него */}
      <div ref={composerWrapRef} style={{
        position: 'absolute', left: 0, right: 0, bottom: 0,
        // Снизу воздуха нет, когда чат стоит прямо на холсте (headerIsland): его
        // даёт padding холста (ISLAND.pad), и губа композера встаёт на одну линию
        // с нижней кромкой соседних островов. Внутри острова (split чат|файл)
        // отступ нужен — у Island своего padding нет, композер лёг бы на рамку.
        padding: isMobile ? `0 ${CHAT_GUTTER_MOBILE}px 12px` : `0 ${CHAT_GUTTER_L}px ${headerIsland ? 0 : 18}px`,
        pointerEvents: 'none',
      }}>
        {/* Именно этот узел — препятствие для круглешка AI: у него РЕАЛЬНАЯ геометрия
            композера (ограничен CHAT_MAX_W и центрирован). Внешняя обёртка растянута
            left:0/right:0, и замер по ней всегда давал пересечение с углом кнопки —
            круг был ужат даже когда композер визуально далеко */}
        <div ref={composerObstacleRef} style={{ maxWidth: CHAT_MAX_W, margin: '0 auto', pointerEvents: 'auto' }}>
          {mode === 'bypass' && (
            <div style={{
              display: 'flex', alignItems: 'center', gap: 7, marginBottom: 6, padding: '6px 12px',
              borderRadius: R.lg, background: C.dangerBg, color: C.danger, fontSize: 12, fontWeight: 600,
            }}>
              <span style={{ display: 'flex' }}><ModeIcon mode="bypass" /></span>
              Режим «Без ограничений» — {asstName} действует без подтверждений
            </div>
          )}
          {/* Git-бар над композером (только проектный чат на десктопе): ветка/worktree
              чата, дерево текущего хода, суммарный diff и кнопки «Зафиксировать»/
              «Опубликовать». Правой панели «Изменения» на мобиле нет — отсюда гейт
              !isMobile; на мобиле о дереве хода сообщает только отметка в ленте. */}
          {project && !isMobile && !embedded && <ProjectGitBar project={project} session={session} turnTree={turnTree} turnTreeLive={isWaiting} onCommitOwn={handleCommitOwn} onCommitAll={handleCommitAll} />}
          {/* Подъём композера над лентой даёт сама белая карточка (Composer), а не эта
              обёртка: полоса контролов вынесена из карточки, и тень на обёртке рисовала
              серый ореол вокруг пустой области под ней и полоску над полем ввода. */}
          <div>
          <input
            ref={chatFileInputRef}
            type="file"
            multiple
            style={{ display: 'none' }}
            onChange={e => { const fs = Array.from(e.target.files ?? []); e.target.value = ''; if (fs.length) handleChatUpload(fs); }}
          />
          {/* Закреплённая полоса «практика ждёт вашего решения»: пока в ленте есть
              открытая карточка эскалации, человек видит её над композером, даже если
              её уже унесло вверх потоком докладов. Клик раздвигает окно ленты,
              скроллит к карточке и мягко подсвечивает (.escalation-flash в index.css) */}
          {topEscalation && (
            <EscalationStickyBanner
              top={topEscalation}
              others={openEscalations.length - 1}
              onJump={jumpToEscalation}
            />
          )}
          <Composer
            // key по чату: Composer полностью перемонтируется при смене сессии, поэтому
            // его внутренний стейт (текст черновика, teamMech/teamOpen/teamSettings,
            // открытые меню и пр.) не течёт между чатами. Текст восстанавливается из
            // стора черновиков (getDraft) уже под новый sessionId.
            key={session.id}
            sessionId={session.id}
            voicePersonaId={session.personaId ?? undefined}
            offline={!online}
            onSend={handleSend}
            onStop={interrupt}
            // В проекте — пикер файлов проекта; вне проекта — загрузка файла с устройства
            onAttach={project ? (() => setShowAttachPicker(true)) : (() => chatFileInputRef.current?.click())}
            isGenerating={isWaiting}
            mode={mode}
            onModeChange={changeMode}
            planAvailable={caps.supportsPlanMode}
            autoAllowTools={autoAllowTools}
            onRevokeAutoAllow={handleRevokeAutoAllow}
            attachments={attachedFiles}
            onRemoveAttachment={path => onAttachedFilesChange(attachedFiles.filter(p => p !== path))}
            onAttachFiles={files => void handleComposerFiles(files)}
            isMobile={isMobile}
            skills={skills}
            personas={ctxPersonas}
            agents={agents ?? []}
            selectedPersona={persona}
            selectedAgentName={session.agentName ?? null}
            onCompanionChange={handleCompanionChange}
            canPickCompanion={online}
            model={session.model}
            onModelChange={handleModelChange}
            chatStarted={!!session.claudeSessionId}
            effort={session.effort}
            onEffortChange={caps.supportsEffort ? handleEffortChange : undefined}
            hasMessages={items.length > 0}
            participantIds={session.participants}
            onCreateGroup={handleCreateGroup}
            workLoop={workLoopState}
            onToggleWorkLoop={handleToggleWorkLoop}
            teamImplement={teamImplementState}
            teamWavePulse={teamWavePulse}
            onToggleTeamImplementAuto={teamImplementState ? handleToggleTeamImplementAuto : undefined}
            onDisableTeamImplement={teamImplementState ? handleDisableTeamImplement : undefined}
            onStopTeamImplement={teamImplementState ? handleStopTeamImplement : undefined}
            onEnableTeamImplement={handleEnableTeamImplement}
            isProjectChat={!!project}
            // Онбординг-интервью: команды ещё нет — «Обсудить с командой» скрываем
            onboarding={!!session.onboardingKind}
            worktreeBranch={session.worktreeBranch}
            onToggleWorktree={project ? openWorktreeConfirm : undefined}
            voiceMode={voiceMode}
            onToggleVoiceMode={handleToggleVoiceMode}
            voiceStyle={voiceStyle}
            // Вопрос модели выводит режим разговора из петли: голосом на разрешение
            // не ответишь. Именно awaitingResponse, а НЕ isWaiting (тот = «ход идёт»
            // и уходит выше как isGenerating)
            awaitingResponse={awaitingResponse}
            speechPhase={speechPhase}
            onHandsFreeActiveChange={setHandsFreeActive}
            onStopSpeech={stopSpeech}
            onBargeSuppress={handleBargeSuppress}
            chatContext={chatContext}
            promptSuggestion={promptSuggestion}
            rateWindow={worstRate}
            restore={composerRestore}
            onReplaceAttachments={onAttachedFilesChange}
            // Два источника «поставь курсор в поле»: колонка стены стала активной и
            // открылся пустой чат. Оба счётчика монотонны, поэтому сумма растёт от любого
            // из них; 0 = сигнала не было (Composer такое значение игнорирует).
            focusSignal={(composerFocusSignal ?? 0) + emptyChatFocus}
            // Цвет сияния при озвучке: сперва цвет говорящей персоны (тот же, что у кольца
            // её аватара в ленте), иначе фирменный цвет проекта — «голос проекта»;
            // ни того, ни другого нет — оранжевый токен внутри композера
            auroraColorHex={activeSpeaker?.color ?? (project ? projectMainColor(project) : undefined)}
          />
          </div>
        </div>
      </div>

      {/* Пикер вложений — только при наличии проекта */}
      {project && showAttachPicker && (
        <AttachPicker
          projectId={project.id}
          selected={attachedFiles}
          onToggle={path => onAttachedFilesChange(
            attachedFiles.includes(path) ? attachedFiles.filter(p => p !== path) : [...attachedFiles, path]
          )}
          onClose={() => setShowAttachPicker(false)}
          onUpload={handleComposerFiles}
        />
      )}

      {/* Предупреждение перед сменой дерева: что именно произойдёт с чатом и файлами */}
      {worktreeConfirm && (
        <Modal
          width={480}
          onClose={() => setWorktreeConfirm(false)}
          title={session.worktreePath ? 'Вернуть чат в проект?' : 'Перевести чат в отдельное дерево?'}
          footer={
            <ModalActions
              confirmLabel={session.worktreePath ? 'Вернуть' : 'Перевести'}
              onConfirm={() => {
                setWorktreeConfirm(false);
                void handleToggleWorktree(false, session.worktreePath ? undefined : (worktreeBranchInput.trim() || undefined));
              }}
              onCancel={() => setWorktreeConfirm(false)}
            />
          }
        >
          <div style={{ fontSize: 13, color: C.textSecondary, lineHeight: 1.55, display: 'flex', flexDirection: 'column', gap: 12 }}>
            {session.worktreePath ? (
              <div>
                Чат вернётся к файлам проекта, папка дерева будет удалена.
                Ветка <span style={{ fontFamily: 'monospace' }}>{session.worktreeBranch}</span> с коммитами останется —
                её можно влить позже.
              </div>
            ) : (
              <>
                <div>
                  Чат продолжится в отдельной копии проекта на своей ветке —
                  основные файлы не изменятся, разговор не прервётся.
                </div>
                <label style={{ display: 'flex', flexDirection: 'column', gap: 5 }}>
                  <span style={{ fontSize: 12, color: C.textMuted }}>Ветка</span>
                  <input
                    value={worktreeBranchInput}
                    onChange={e => setWorktreeBranchInput(e.target.value)}
                    spellCheck={false}
                    style={{
                      height: 32, padding: '0 10px', fontFamily: 'monospace', fontSize: 12.5,
                      border: `1px solid ${C.border}`, borderRadius: 8, background: C.bgWhite,
                      color: C.textPrimary, outline: 'none',
                    }}
                  />
                </label>
              </>
            )}
          </div>
        </Modal>
      )}

      {/* Откат файла из карточки file_changed: git checkout HEAD стирает любые несохранённые
          правки файла, не только модели — предупреждаем честно перед действием */}
      {revertPath && (
        <ConfirmDialog
          title="Вернуть файл к последнему коммиту?"
          subtitle="Файл вернётся к состоянию последнего коммита. Все незафиксированные правки — и Claude, и ваши — пропадут без возможности восстановить."
          confirmLabel="Вернуть к коммиту"
          confirmVariant="danger"
          onConfirm={confirmRevert}
          onCancel={() => setRevertPath(null)}
        />
      )}

      {/* Выключение worktree при несохранённых правках: подтверждение принудительного удаления */}
      {worktreeForceConfirm && (
        <Modal
          width={460}
          onClose={() => setWorktreeForceConfirm(false)}
          title="В дереве есть несохранённые правки"
          footer={
            <ModalActions
              confirmLabel="Удалить с правками"
              confirmVariant="danger"
              onConfirm={() => { setWorktreeForceConfirm(false); void handleToggleWorktree(true); }}
              onCancel={() => setWorktreeForceConfirm(false)}
            />
          }
        >
          <div style={{ fontSize: 13, color: C.textSecondary, lineHeight: 1.5 }}>
            Они будут потеряны. Чтобы сохранить — сначала зафиксируй их в git-баре
            (коммиты ветки {session.worktreeBranch} не пропадут).
          </div>
        </Modal>
      )}
    </div>
    </PersonaContext.Provider>
    </AssistantNameContext.Provider>
  );
}
