import { useState, useRef, useEffect, useMemo, useCallback, Fragment } from 'react';
import type { Project, Session, ChatItem, SkillInfo, AgentInfo, ClaudeBilling, Persona } from '../types';
import { useSession } from '../hooks/useSession';
import { usePersonasVersion, getPersonaById, ensurePersonasLoaded, personaLabel } from '../lib/personas';
import { showToast } from '../lib/toast';
import { PersonaGreeting } from '../features/personas/PersonaGreeting';
import { countFiles, computeTodos } from '../hooks/useSessionArtifacts';
import { useChatScroll } from '../hooks/useChatScroll';
import { useOnline } from '../hooks/useOnline';
import { api } from '../lib/api';
import { parseWorkflowMeta } from '../lib/workflowMeta';
import { toRateWindows, worstWindow } from '../lib/rateLimit';
import { estimateContext } from '../lib/context';
import { useCtxThresholds } from '../lib/contextPrefs';
import { notify } from '../lib/notify';
import { type Mode, ModeIcon } from '../lib/modes';
import { useModelCaps, assistantName } from '../lib/models';
import { Composer } from './Composer';
import { EditSessionDialog } from './EditSessionDialog';
import { C, R, SHADOW, CHAT_MAX_W } from '../lib/design';
import { useFeature, FLAGS } from '../lib/featureFlags';
import { setChatContext } from '../lib/ai/chatContext';
import { ChatHeaderBar, RateLimitBar, type CostStats, type FalCostStats } from './chat/ChatHeaderBar';
import { ChatProjectContext, FalCostContext, AssistantNameContext, PersonaContext } from './chat/contexts';
import { WaitingIndicator } from './chat/WaitingIndicator';
import { ChatEmptyState } from './chat/EmptyState';
import { AttachPicker } from './chat/AttachPicker';
import { ToolGroupBlock, AgentActionsBlock, itemKey } from './chat/timeline';
import { ChatItemView, FileChangedRow } from './chat/ChatItemView';
import { type ToolUseItem } from './chat/ToolUseView';
import { WorkflowBlockView } from './chat/WorkflowBlockView';

interface Props {
  session: Session;
  // Отсутствует для чата вне проекта (project-less) — тогда скрываем файловые возможности
  project?: Project;
  onOpenFile?: (path: string) => void;
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
  onResume?: (message?: string) => void;
  // Тумблер панели «Артефакты сессии» в шапке (приходит только при включённом фич-флаге)
  artifactsOpen?: boolean;
  onToggleArtifacts?: () => void;
  // Приветственный бабл персоны: показывается в пустом чате вместо обычного empty state
  // (чисто визуально, в бэкенд не отправляется). Как только пойдут реальные сообщения — исчезает.
  greetingBubble?: React.ReactNode;
}

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

export function ChatPanel({ session, project, onOpenFile, pendingMessage, onPendingMessageSent, onSessionUpdated, isMobile, onBack, onWorkflowRunning, onOpenSidebar, skills, agents, attachedFiles, onAttachedFilesChange, onResume, artifactsOpen, onToggleArtifacts, greetingBubble }: Props) {
  const { items, isWaiting, isJoined, isHistoryLoading, rateLimits, isCompacting, compactNote, send, allowPermission, denyPermission, allowAlways, answerQuestion, respondPlan, interrupt, compact, toggleThinking, noteCompanionSwitch } = useSession(session.id, project?.id, (session.participants?.length ?? 0) > 1);
  // Окна лимитов подписки (из rate_limit-телеметрии) — для индикатора в бейдже и строки у composer
  const rateWindows = useMemo(() => toRateWindows(rateLimits), [rateLimits]);
  const worstRate = useMemo(() => worstWindow(rateWindows), [rateWindows]);
  // Оценка заполнения контекстного окна — по последнему result-элементу ленты
  const ctxThresholds = useCtxThresholds();
  const ctxEstimate = useMemo(() => estimateContext(items, session.model, ctxThresholds), [items, session.model, ctxThresholds]);
  // Возможности провайдера модели (UI скрывает недоступное)
  const caps = useModelCaps(session.model);
  // Имя ассистента сессии для строк UI (провайдится в контекст ниже)
  const asstName = assistantName(session.model);
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
    [session.personaId, personasVersion]
  );
  useEffect(() => { void ensurePersonasLoaded(); }, []);
  // Участники группового чата (резолв из стора персон); < 2 — обычный чат
  const participantPersonas = useMemo(
    () => (session.participants ?? [])
      .map(id => getPersonaById(id))
      .filter((p): p is Persona => !!p),
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [session.participants, personasVersion]
  );
  const isGroupChat = participantPersonas.length > 1;
  // Есть ли уже ходы — назначать/менять персону можно только у пустого чата (бэкенд иначе 400)
  const chatEmpty = items.length === 0;
  // Персоны, доступные в контексте чата (глобальные + этого проекта) — для селектора,
  // пилюль пустого состояния и форка. Грузим лениво, пока чат пуст либо ведётся персоной.
  const [ctxPersonas, setCtxPersonas] = useState<Persona[]>([]);
  useEffect(() => {
    if (!online) return;
    if (!chatEmpty && !persona) return; // список нужен только для пустого чата или форка
    let alive = true;
    api.personas.list({ scope: 'context', projectId: project?.id })
      .then(list => { if (alive) setCtxPersonas(list); })
      .catch(() => { /* персоны — необязательная фича */ });
    return () => { alive = false; };
  }, [online, chatEmpty, persona, project?.id]);

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
  }, [project, session.id, onSessionUpdated, items.length, noteCompanionSwitch]);

  // Обратная совместимость для пилюль «Поговорить с…» пустого состояния (выбор только персоны)
  const handlePersonaChange = useCallback(
    (p: Persona | null) => handleCompanionChange({ persona: p, agent: null }),
    [handleCompanionChange]
  );

  // Групповой чат (флаг persona-group-chats): создаём НОВЫЙ чат с 2-4 участниками
  // и уводим пользователя в него. Ведущая проектная → сессия текущего проекта,
  // глобальная → чат вне проекта (переход в раздел «Чаты»).
  const groupChatsOn = useFeature(FLAGS.personaGroupChats);
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
  // Явный greetingBubble-проп имеет приоритет.
  const personaGreeting = useMemo(
    () => (persona && persona.greeting?.trim() ? <PersonaGreeting persona={persona} /> : undefined),
    [persona]
  );
  const effectiveGreeting = greetingBubble ?? personaGreeting;

  // Число изменённых файлов — для бейджа на кнопке «Артефакты» (только когда тумблер проброшен)
  const artifactFileCount = useMemo(
    () => onToggleArtifacts && project ? countFiles(items, project.rootPath) : 0,
    [onToggleArtifacts, items, project]
  );

  const [hasCLAUDEmd, setHasCLAUDEmd] = useState<boolean | null>(null);
  useEffect(() => {
    // Для чата вне проекта файлов нет — баннер CLAUDE.md не показываем
    if (!project) { setHasCLAUDEmd(false); return; }
    api.files.list(project.id)
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

  // Тип оплаты Claude (подписка/api) — глобальная настройка; влияет на подачу стоимости Claude
  const [claudeBilling, setClaudeBilling] = useState<ClaudeBilling>('subscription');
  useEffect(() => {
    api.settings.get().then(s => { if (s?.claudeBilling) setClaudeBilling(s.claudeBilling); }).catch(() => {});
  }, []);
  const changeBilling = useCallback((b: ClaudeBilling) => {
    setClaudeBilling(b);
    // Сохраняем, не затирая остальные настройки
    api.settings.get().then(s => api.settings.save({ ...s, claudeBilling: b })).catch(() => {});
  }, []);

  const [mode, setMode] = useState<Mode>(session.mode);
  const [showAttachPicker, setShowAttachPicker] = useState(false);
  const [showEdit, setShowEdit] = useState(false);
  // Скролл-механика ленты (прилипание к низу, восстановление позиции, кнопка «вниз») — hooks/useChatScroll
  const {
    bottomRef, scrollRef, contentRef, composerWrapRef, composerH,
    showScrollDown, atBottomRef, handleMessagesScroll, scrollToBottom,
  } = useChatScroll(session.id, items, isHistoryLoading, online);
  // FAB AI-хаба должен вставать НАД композером (иначе налезает на композер и кнопку
  // «вниз»): пробрасываем высоту композера в глобальную CSS-переменную, читаемую FAB.
  const aiAssistOn = useFeature(FLAGS.aiAssist);
  useEffect(() => {
    if (!aiAssistOn) return;
    const root = document.documentElement;
    root.style.setProperty('--cc-fab-bottom', `${composerH + 12}px`);
    return () => { root.style.setProperty('--cc-fab-bottom', '20px'); };
  }, [aiAssistOn, composerH]);
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

  // Браузерные уведомления (только когда вкладка не в фокусе) — нужно решение / ход завершён
  const prevWaitingRef = useRef(false);
  useEffect(() => {
    if (isWaiting && !prevWaitingRef.current)
      notify('Нужно решение', `${session.name ?? 'Чат'} ждёт вашего ответа`);
    prevWaitingRef.current = isWaiting;
  }, [isWaiting, session.name]);

  const resultCountRef = useRef<number | null>(null);
  useEffect(() => {
    const rc = items.reduce((acc, it) => acc + (it.kind === 'result' ? 1 : 0), 0);
    if (resultCountRef.current !== null && rc > resultCountRef.current)
      notify(`${asstName} закончил`, `${session.name ?? 'Чат'}: ход завершён`);
    resultCountRef.current = rc;
  }, [items, session.name, asstName]);
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

  // Для монотонного счётчика фаз workflow — не прыгать назад когда total растёт
  const workflowPhaseRef = useRef<{ wfId: string; phasesDone: number }>({ wfId: '', phasesDone: 0 });

  // Автоотправка первого сообщения сразу после присоединения к сессии.
  // mode/onPendingMessageSent — через ref: эффект должен выстрелить один раз при join,
  // а не перезапускаться при смене режима или пересоздании колбэка родителя
  const onPendingSentRef = useRef(onPendingMessageSent);
  useEffect(() => { onPendingSentRef.current = onPendingMessageSent; });
  useEffect(() => {
    if (isJoined && pendingRef.current) {
      const msg = pendingRef.current;
      pendingRef.current = undefined;
      onPendingSentRef.current?.();
      send(msg, [], modeRef.current);
    }
  }, [isJoined, send]);

  const handleSend = async (text: string) => {
    if (!text.trim() && attachedFiles.length === 0) return;
    const paths = [...attachedFiles];
    onAttachedFilesChange([]);
    atBottomRef.current = true; // своё сообщение — прыгаем вниз и снова прилипаем
    await send(text, paths, mode);
  };

  // Загрузка вставленных/перетащенных картинок в проект → относительные пути в attachedFiles.
  // Бэкенд по расширению отправит их claude как image-блоки base64.
  const handleAttachImages = useCallback(async (files: File[]) => {
    // Вне проекта файлы никуда не грузим — вложения недоступны
    if (!project) return;
    const dir = '.cc-attachments';
    try { await api.files.mkdir(project.id, dir); } catch { /* папка уже есть */ }
    const added: string[] = [];
    for (const file of files) {
      const extFromType = file.type === 'image/jpeg' ? '.jpg' : file.type === 'image/gif' ? '.gif'
        : file.type === 'image/webp' ? '.webp' : '.png';
      const ext = file.name.match(/\.[a-z0-9]+$/i)?.[0] ?? extFromType;
      const unique = `paste-${Date.now()}-${Math.random().toString(36).slice(2, 8)}${ext}`;
      try {
        await api.files.upload(project.id, new File([file], unique, { type: file.type }), dir);
        added.push(`${dir}/${unique}`);
      } catch { /* пропускаем неудачную загрузку */ }
    }
    if (added.length) onAttachedFilesChange([...attachedFiles, ...added]);
  }, [project, attachedFiles, onAttachedFilesChange]);

  // Загрузка файла с устройства для чата вне проекта — грузим в рабочую папку чата,
  // относительный путь добавляем во вложения. Используется и кнопкой «прикрепить», и paste/drop.
  const chatFileInputRef = useRef<HTMLInputElement>(null);
  const handleChatUpload = useCallback(async (files: File[]) => {
    const added: string[] = [];
    for (const file of files) {
      try {
        const { path } = await api.chats.uploadFile(session.id, file);
        added.push(path);
      } catch { /* пропускаем неудачную загрузку */ }
    }
    if (added.length) onAttachedFilesChange([...attachedFiles, ...added]);
  }, [session.id, attachedFiles, onAttachedFilesChange]);

  const handleHint = (hint: string) => {
    atBottomRef.current = true;
    send(hint, [], mode);
  };

  // Стабильный (items/mode — через ref), чтобы React.memo элементов ленты работал
  const handleRetry = useCallback(() => {
    const lastUser = [...itemsRef.current].reverse().find(it => it.kind === 'user_message');
    if (lastUser && lastUser.kind === 'user_message') { atBottomRef.current = true; send(lastUser.text, lastUser.attachedPaths ?? [], modeRef.current); }
  }, [send]);

  // Режим «План» — персистентный: после одобрения остаёмся в нём (следующие задачи тоже
  // планируются). Исполнение именно этого плана гарантирует backend (один ход без plan-режима).
  const handleRespondPlan = useCallback((requestId: string, approve: boolean, feedback?: string) => {
    respondPlan(requestId, approve, feedback);
  }, [respondPlan]);

  // Откат файла — стабильный колбэк для карточек file_changed в ленте
  const projectId = project?.id;
  const handleRevert = useCallback((path: string) => {
    if (projectId) api.files.revert(projectId, path);
  }, [projectId]);

  // Индекс последнего result — у него показываем плашку токенов/времени, у прошлых скрываем
  const lastResultIndex = useMemo(
    () => items.reduce((acc, it, i) => (it.kind === 'result' ? i : acc), -1),
    [items]
  );

  // Есть ли в чате переписка — по загруженной ленте (надёжнее session.messageCount, который
  // у активной проектной сессии не синхронизируется по realtime). Управляет показом кнопок
  // «Итог сессии» и «Задачи из чата» в шапке.
  const hasMessages = useMemo(() => items.some(it => it.kind === 'user_message'), [items]);

  // Сообщаем AI-палитре, что чат открыт (и есть ли переписка) — чтобы действия чата
  // были доступны и в проектных чатах, где активная сессия не отражается в nav.
  useEffect(() => {
    setChatContext(true, hasMessages);
    return () => setChatContext(false, false);
  }, [hasMessages]);

  // Фаза режима «План» (для контекстного индикатора и подписи WaitingIndicator)
  const planPhase = useMemo(() => derivePlanPhase(items, mode, isWaiting), [items, mode, isWaiting]);
  const planningKind = planPhase === 'planning' ? 'planning' : planPhase === 'replanning' ? 'replanning' : undefined;

  // Активный workflow — сырой прогресс фаз (чистая функция от ленты, без мутаций)
  const rawWorkflowInfo = useMemo(() => {
    for (let i = items.length - 1; i >= 0; i--) {
      const it = items[i];
      if (it.kind !== 'tool_use') continue;
      const wf = it as ToolUseItem;
      if (wf.name.toLowerCase() !== 'workflow' || wf.workflowDone === true || wf.result !== undefined) continue;
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

  // Единое условие показа WaitingIndicator — синхронизировано с флагом активности на карточке.
  // session.status покрывает случай когда isWaiting ещё не обновился (перезагрузка, переключение чата).
  // items.length > 0: не показывать спиннер на пустом чате до первого сообщения.
  const showWaiting =
    items.length > 0
    && (isWaiting || session.status === 'working' || session.status === 'starting')
    && !items.some(it => (it.kind === 'permission_request' || it.kind === 'plan_review' || it.kind === 'ask_question') && !it.resolved);

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
  // списком) — карточку чек-листа рисуем один раз, на последнем task-вызове ленты:
  // там агрегат computeTodos отражает актуальное состояние всего списка
  const lastTaskIdx = useMemo(() => {
    for (let i = items.length - 1; i >= 0; i--) {
      const it = items[i];
      if (it.kind === 'tool_use' && !it.parentToolUseId && (it.name === 'TaskCreate' || it.name === 'TaskUpdate')) return i;
    }
    return -1;
  }, [items]);
  const taskTodos = useMemo(() => (lastTaskIdx >= 0 ? computeTodos(items) : []), [items, lastTaskIdx]);

  // «Продолжить обсуждение» из карточки совещания: краткий итог уходит обычным сообщением
  const handleMeetingContinue = useCallback((text: string) => {
    atBottomRef.current = true;
    void send(text, [], mode);
  }, [send, mode]);
  // Отмена идущего совещания (карточка знает только о факте, id чата — тут)
  const handleMeetingCancel = useCallback(() => {
    api.chats.cancelMeeting(session.id)
      .catch(() => showToast('Совещание', 'Не удалось отменить совещание', 'info'));
  }, [session.id]);

  // Единый рендер одного элемента ленты (используется в основном рендере и в доке).
  // useCallback + React.memo на ChatItemView: при дописывании ленты неизменившиеся
  // элементы не перерендериваются (все пропсы-функции стабильны).
  const renderItem = useCallback((item: ChatItem, i: number) => (
    <ChatItemView
      key={itemKey(item, i)}
      item={item}
      index={i}
      online={online}
      streaming={isWaiting && i === items.length - 1}
      isLastResult={i === lastResultIndex}
      onToggleThinking={toggleThinking}
      onAllowPermission={allowPermission}
      onDenyPermission={denyPermission}
      onAllowAlways={allowAlways}
      onAnswerQuestion={answerQuestion}
      onRespondPlan={handleRespondPlan}
      planVersion={planVersions.get(i)?.version}
      planShowBadge={!!planVersions.get(i) && (planVersions.get(i)!.version > 1 || planVersions.get(i)!.hadRejected)}
      planShowSwitch={i === lastApprovedPlanIdx && mode === 'plan'}
      onSwitchMode={setMode}
      onOpenFile={onOpenFile}
      onRevert={project ? handleRevert : undefined}
      onRetry={handleRetry}
      onInterrupt={interrupt}
      taskPlan={i === lastTaskIdx && taskTodos.length > 0 ? taskTodos : undefined}
      onSendMessage={handleMeetingContinue}
      onMeetingCancel={handleMeetingCancel}
    />
  ), [
    online, isWaiting, items.length, lastResultIndex, toggleThinking, allowPermission,
    denyPermission, allowAlways, answerQuestion, handleRespondPlan, planVersions,
    lastApprovedPlanIdx, mode, onOpenFile, project, handleRevert, handleRetry,
    interrupt, lastTaskIdx, taskTodos, handleMeetingContinue, handleMeetingCancel,
  ]);

  // Блок действий: подряд идущие карточки инструментов + изменения файлов объединяем
  // в один контур (внешние линии сверху/снизу + разделители между соседями), чтобы
  // file_changed между инструментами не разрывал стопку. Контур рисуем только если в
  // блоке есть хотя бы один инструмент; одиночные file_changed остаются обычными карточками.
  // Группировка — O(n) с постройкой карт по всей ленте, поэтому в useMemo от items.
  const renderedItems = useMemo(() => {
    // Последний task-вызов (lastTaskIdx) исключаем из блока действий, как и TodoWrite:
    // на его месте рисуется отдельная карточка чек-листа, ей не место внутри контура
    const isTool = (it: ChatItem, idx: number) => it.kind === 'tool_use' && it.name !== 'TodoWrite' && idx !== lastTaskIdx && !it.parentToolUseId && it.name.toLowerCase() !== 'workflow';
    const inBlock = (it: ChatItem, idx: number) => isTool(it, idx) || it.kind === 'file_changed';
    // Строим карту дочерних tool_use для Workflow-блоков
    const childrenByParentId = new Map<string, ToolUseItem[]>();
    for (const it of items) {
      if (it.kind === 'tool_use' && it.parentToolUseId) {
        const arr = childrenByParentId.get(it.parentToolUseId) ?? [];
        arr.push(it as ToolUseItem);
        childrenByParentId.set(it.parentToolUseId, arr);
      }
    }
    // ID субагентов и их инструментов, которые рендерятся внутри WorkflowBlockView
    const suppressedByWorkflow = new Set<string>();
    for (const it of items) {
      if (it.kind !== 'tool_use' || it.name.toLowerCase() !== 'workflow') continue;
      for (const agent of (childrenByParentId.get(it.id) ?? [])) {
        suppressedByWorkflow.add(agent.id);
        for (const tool of (childrenByParentId.get(agent.id) ?? [])) suppressedByWorkflow.add(tool.id);
      }
    }
    // Дети top-level agent-вызовов рендерятся inline под родителем в блоке действий:
    // при параллельных агентах инструменты приходят вперемешку, и без группировки по родителю
    // все sub-tool строки сливаются в один безымянный блок.
    const suppressedByAgentParent = new Set<string>();
    const idxMap = new Map<string, number>();
    items.forEach((it, k) => { if (it.kind === 'tool_use') idxMap.set(it.id, k); });
    for (const it of items) {
      if (it.kind !== 'tool_use' || !!it.parentToolUseId || it.name.toLowerCase() === 'workflow') continue;
      for (const child of (childrenByParentId.get(it.id) ?? [])) {
        if (!suppressedByWorkflow.has(child.id)) suppressedByAgentParent.add(child.id);
      }
    }
    // Дочерние вызовы субагента (не-Workflow, не inline) — рисуем единой линией-коннектором слева
    const isSubTool = (it: ChatItem) => it.kind === 'tool_use' && !!it.parentToolUseId && !suppressedByWorkflow.has(it.id) && !suppressedByAgentParent.has(it.id);
    // Узлы ленты с пометкой стартового индекса — нужно для обёртки success-коннектором
    const nodes: Array<{ node: React.ReactNode; start: number }> = [];
    const pushNode = (node: React.ReactNode, start: number) => nodes.push({ node, start });
    let i = 0;
    let prevNodeWasBlock = false;
    while (i < items.length) {
      // Workflow-блок рендерим специальным компонентом
      if (items[i].kind === 'tool_use' && (items[i] as ToolUseItem).name.toLowerCase() === 'workflow') {
        const wf = items[i] as ToolUseItem;
        pushNode(<WorkflowBlockView key={`wf-${wf.id}`} workflow={wf} agents={childrenByParentId.get(wf.id) ?? []} childrenByParentId={childrenByParentId} onOpenFile={onOpenFile} />, i);
        i++; prevNodeWasBlock = false; continue;
      }
      // Субагенты Workflow и их инструменты рендерятся внутри WorkflowBlockView
      if (items[i].kind === 'tool_use' && suppressedByWorkflow.has((items[i] as ToolUseItem).id)) {
        i++; continue;
      }
      // Дочерние инструменты top-level агентов рендерятся inline под родителем — пропускаем здесь
      if (items[i].kind === 'tool_use' && suppressedByAgentParent.has((items[i] as ToolUseItem).id)) {
        i++; continue;
      }
      if (isSubTool(items[i])) {
        const start = i;
        const sub: Array<[ChatItem, number]> = [];
        while (i < items.length && isSubTool(items[i])) { sub.push([items[i], i]); i++; }
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
      } else if (inBlock(items[i], i)) {
        const start = i;
        const slice: Array<[ChatItem, number]> = [];
        while (i < items.length && inBlock(items[i], i)) { slice.push([items[i], i]); i++; }
        // Один контур: инструменты и изменения файлов — компактными строками (в т.ч. одиночные).
        // Для agent-вызовов с детьми сразу рисуем детей inline под родителем — иначе при параллельных
        // агентах все их инструменты сливаются в один безымянный блок после шапки.
        const toolCount = slice.filter(([it]) => it.kind === 'tool_use').length;
        let isGroupDone = false;
        for (let k = i; k < items.length; k++) {
          if (items[k].kind === 'user_message') break;
          if (items[k].kind === 'result') { isGroupDone = true; break; }
        }
        pushNode(
          <ToolGroupBlock key={`grp-${itemKey(slice[0][0], start)}`} isGroupDone={isGroupDone} toolCount={toolCount}>
            {slice.map(([it, idx], gi) => {
              const inlineChildren = it.kind === 'tool_use'
                ? (childrenByParentId.get(it.id) ?? []).filter(c => !suppressedByWorkflow.has(c.id))
                : [];
              return (
                <Fragment key={itemKey(it, idx)}>
                  <div style={gi === 0 ? undefined : { borderTop: `1px solid ${C.bgInset}` }}>
                    {it.kind === 'file_changed'
                      ? <FileChangedRow item={it} online={online} onOpenFile={onOpenFile} onRevert={project ? handleRevert : undefined} />
                      : renderItem(it, idx)}
                  </div>
                  {inlineChildren.length > 0 && (
                    <AgentActionsBlock
                      items={inlineChildren}
                      renderChild={renderItem}
                      idxMap={idxMap}
                    />
                  )}
                </Fragment>
              );
            })}
          </ToolGroupBlock>,
          start
        );
        prevNodeWasBlock = true;
      } else {
        const kind = items[i].kind;
        const node = renderItem(items[i], i);
        const needsTopSpacing = kind === 'text' || kind === 'user_message' || kind === 'result' || kind === 'error';
        pushNode(
          kind === 'user_message'
            ? <div key={`sp-${i}`} style={{ marginTop: 12, display: 'flex', justifyContent: 'flex-end' }}>{node}</div>
            : kind === 'result'
              ? <div key={`sp-${i}`} style={{ marginTop: 12, display: 'flex', justifyContent: 'center' }}>{node}</div>
              : needsTopSpacing
                ? <div key={`sp-${i}`} style={{ marginTop: 12 }}>{node}</div>
                : node,
          i
        );
        i++;
        prevNodeWasBlock = false;
      }
    }

    // success-коннектор: непрерывные узлы из «зоны реализации» (после одобренного плана)
    // оборачиваем в одну левую зелёную линию — «эти правки реализуют план».
    if (!execZone) return nodes.map(n => n.node);
    const result: React.ReactNode[] = [];
    let j = 0;
    while (j < nodes.length) {
      const inZone = (n: { start: number }) => n.start >= execZone.start && n.start < execZone.end;
      if (inZone(nodes[j])) {
        const group: React.ReactNode[] = [];
        const groupStart = nodes[j].start;
        while (j < nodes.length && inZone(nodes[j])) { group.push(nodes[j].node); j++; }
        result.push(
          <div key={`exec-${groupStart}`} style={{ marginLeft: 8, paddingLeft: 14, borderLeft: `3px solid ${C.success}`, display: 'flex', flexDirection: 'column', gap: 6, marginTop: -6 }}>
            {group}
          </div>
        );
      } else {
        result.push(nodes[j].node); j++;
      }
    }
    return result;
  }, [items, renderItem, lastTaskIdx, execZone, online, onOpenFile, project, handleRevert]);

  return (
    <AssistantNameContext.Provider value={asstName}>
    <PersonaContext.Provider value={persona}>
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%', position: 'relative', background: C.bgMain }}>
      <ChatHeaderBar
        session={session}
        project={project}
        hasMessages={hasMessages}
        online={online}
        cost={costStats}
        falCost={falCostStats}
        billing={claudeBilling}
        onBillingChange={changeBilling}
        rateWindows={rateWindows}
        onOpenSettings={() => setShowEdit(true)}
        isMobile={isMobile}
        onBack={onBack}
        activeWorkflow={activeWorkflowInfo ?? undefined}
        onOpenSidebar={onOpenSidebar}
        artifactsOpen={artifactsOpen}
        onToggleArtifacts={onToggleArtifacts}
        artifactFileCount={artifactFileCount}
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

      {/* Сообщения (нижний отступ = высота плавающего composer + зазор) */}
      <div ref={scrollRef} onScroll={handleMessagesScroll} style={{ flex: 1, overflowY: 'auto', overflowX: 'hidden', position: 'relative', paddingTop: isMobile ? 16 : 20, paddingLeft: isMobile ? 12 : 24, paddingRight: isMobile ? 12 : 24, paddingBottom: composerH + 8 }}><div ref={contentRef} style={{ display: 'flex', flexDirection: 'column', gap: 6, width: '100%', maxWidth: CHAT_MAX_W, margin: '0 auto' }}>
        {/* Спиннер загрузки истории */}
        {items.length === 0 && isHistoryLoading && (
          <div style={{
            position: 'absolute', inset: 0,
            display: 'flex', alignItems: 'center', justifyContent: 'center',
          }}>
            <div className="tool-spinner" style={{ width: 22, height: 22, borderWidth: 2.5 }} />
          </div>
        )}

        {/* Empty state: для персоны с приветствием — её бабл, иначе обычный empty state
            (с рядом персон «Поговорить с…») */}
        {items.length === 0 && !isHistoryLoading && online && (
          effectiveGreeting ?? (
            <ChatEmptyState hasProject={!!project} hasCLAUDEmd={hasCLAUDEmd} onHint={handleHint}
              session={session} onSessionUpdated={onSessionUpdated} isMobile={isMobile}
              personas={ctxPersonas} selectedPersonaId={session.personaId} onPickPersona={handlePersonaChange} />
          )
        )}

        <FalCostContext.Provider value={falCostByRequest}><ChatProjectContext.Provider value={projectCtx}>{renderedItems}</ChatProjectContext.Provider></FalCostContext.Provider>

        {online && showWaiting && (
          <WaitingIndicator planning={planningKind} />
        )}

        {/* Баннер прерванной сессии — в конце ленты, после истории */}
        {session.status === 'orphaned' && !isHistoryLoading && (() => {
          const hasPending = items.some(it =>
            (it.kind === 'ask_question' || it.kind === 'permission_request' || it.kind === 'plan_review')
            && !it.resolved
          );
          return (
            <div style={{
              display: 'flex', flexDirection: 'column', alignItems: 'center', gap: 10,
              padding: '20px 16px', marginTop: 8,
              background: C.bgPanel, borderRadius: 12,
              border: `1px solid ${C.border}`,
            }}>
              <span style={{ fontSize: 13, color: C.textSecondary, textAlign: 'center' }}>
                {hasPending
                  ? 'Чат ожидал вашего ответа. После возобновления ответьте на незакрытый запрос.'
                  : `Чат был прерван при перезапуске сервера. ${asstName} продолжит с того же места.`}
              </span>
              {onResume && (
                <button
                  onClick={() => onResume(hasPending ? undefined : 'Продолжи')}
                  style={{
                    padding: '8px 20px', borderRadius: 10, fontSize: 13, fontWeight: 600,
                    background: C.accent, border: 'none', cursor: 'pointer', color: C.onAccent,
                  }}
                  onMouseEnter={e => { e.currentTarget.style.opacity = '0.88'; }}
                  onMouseLeave={e => { e.currentTarget.style.opacity = '1'; }}
                >
                  Возобновить
                </button>
              )}
            </div>
          );
        })()}

        <div ref={bottomRef} />
      </div></div>

      {/* Плавающая кнопка «вниз» — появляется, когда лента отлистана вверх */}
      {showScrollDown && (
        <button
          onClick={scrollToBottom}
          title="Вниз чата"
          style={{
            // Когда включён AI-хаб, поднимаем кнопку «вниз» выше FAB (над композером) с зазором.
            // Служебная прокрутка — нейтральная (не accent), чтобы единственным акцентом в углу был FAB.
            position: 'absolute', right: isMobile ? 16 : 20, bottom: composerH + 14 + (aiAssistOn ? 64 : 0),
            width: 44, height: 44, borderRadius: '50%',
            border: `1px solid ${C.border}`,
            background: C.bgCard, color: C.textSecondary, cursor: 'pointer',
            display: 'flex', alignItems: 'center', justifyContent: 'center',
            boxShadow: SHADOW.card, zIndex: 15,
          }}
        >
          <svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round">
            <path d="M12 5v14" /><path d="m19 12-7 7-7-7" />
          </svg>
        </button>
      )}

      {/* Composer — плавающий над лентой; фон прозрачный, контент виден под/вокруг него */}
      <div ref={composerWrapRef} style={{
        position: 'absolute', left: 0, right: 0, bottom: 0,
        padding: isMobile ? '0 12px 12px' : '0 24px 18px',
        pointerEvents: 'none',
      }}>
        <div style={{ maxWidth: CHAT_MAX_W, margin: '0 auto', pointerEvents: 'auto' }}>
          {mode === 'bypass' && (
            <div style={{
              display: 'flex', alignItems: 'center', gap: 7, marginBottom: 6, padding: '6px 12px',
              borderRadius: R.lg, background: C.dangerBg, color: C.danger, fontSize: 12, fontWeight: 600,
            }}>
              <span style={{ display: 'flex' }}><ModeIcon mode="bypass" /></span>
              Режим «Без ограничений» — {asstName} действует без подтверждений
            </div>
          )}
          {/* Вариант В: строка-предупреждение о лимите подписки у места отправки (warning/rejected) */}
          {worstRate && worstRate.level !== 'normal' && <RateLimitBar w={worstRate} />}
          <div style={{ borderRadius: 14, boxShadow: SHADOW.dropdown }}>
          <input
            ref={chatFileInputRef}
            type="file"
            multiple
            style={{ display: 'none' }}
            onChange={e => { const fs = Array.from(e.target.files ?? []); e.target.value = ''; if (fs.length) handleChatUpload(fs); }}
          />
          <Composer
            sessionId={session.id}
            offline={!online}
            onSend={handleSend}
            onStop={interrupt}
            // В проекте — пикер файлов проекта; вне проекта — загрузка файла с устройства
            onAttach={project ? (() => setShowAttachPicker(true)) : (() => chatFileInputRef.current?.click())}
            isGenerating={isWaiting}
            mode={mode}
            onModeChange={setMode}
            planAvailable={caps.supportsPlanMode}
            attachments={attachedFiles}
            onRemoveAttachment={path => onAttachedFilesChange(attachedFiles.filter(p => p !== path))}
            onAttachImages={project ? handleAttachImages : handleChatUpload}
            isMobile={isMobile}
            skills={skills}
            personas={ctxPersonas}
            agents={agents ?? []}
            selectedPersona={persona}
            selectedAgentName={session.agentName ?? null}
            onCompanionChange={handleCompanionChange}
            canPickCompanion={online}
            hasMessages={items.length > 0}
            participantIds={session.participants}
            onCreateGroup={groupChatsOn ? handleCreateGroup : undefined}
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
        />
      )}

      {/* Настройки чата */}
      {showEdit && (
        <EditSessionDialog
          session={session}
          onSaved={s => onSessionUpdated?.(s)}
          onClose={() => setShowEdit(false)}
        />
      )}
    </div>
    </PersonaContext.Provider>
    </AssistantNameContext.Provider>
  );
}
