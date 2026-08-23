import { useEffect, useMemo, useState, useRef } from 'react';
import { FilterX, ChevronUp, ChevronDown, MessageCircle } from 'lucide-react';
import type { Project, ProjectTag, Session } from '../types';
import { api } from '../lib/api';
import { onMessage, onReconnected } from '../lib/signalr';
import { useOnline } from '../hooks/useOnline';
import { C, GROUP_COLORS, MODAL_W, R } from '../lib/design';
import { Modal, ModalActions } from './ui';
import { usePersonas, usePersonasVersion } from '../lib/personas';
import { createChatWithContextPersona } from '../lib/defaultPersona';
import { showToast } from '../lib/toast';
import { useFeature, FLAGS } from '../lib/featureFlags';
import { ChatFilterResetActions } from './FilterBar';
import { ChatListToolbar } from './ChatListToolbar';
import { EmptyState } from './ui';
import { useChatFilters, useSanitizePersonaFilter, matchChatFilter, isDefaultFilters, defaultChatFiltersKeepingView, buildHiddenReason } from '../lib/chatFilters';
import { buildChatTreeRows, splitChatTreeByRoots, useTreeCollapse } from '../lib/chatTree';
import { useAgentsPresence } from '../lib/agentsPresence';
import { useLastMechanicVersion } from '../lib/lastMechanic';
import { ChatCard } from './ChatCard';
import { ChatTreeBranch, nestTreeRows } from './ChatTreeRow';
import { ChatGroupingDnd } from './ChatGroupingDnd';
import { ListDateDivider } from './ListDateDivider';
import { groupChats, groupByTags, sortChatsFlat, chatTagsSorted, type TagChatGroup } from '../lib/chatGroups';
import { tagColor } from '../lib/tagRegistry';
import { TagAssignMenu } from './TagChip';
import { WALL_DRAG_TYPE } from '../features/wall/WallDock';

interface Props {
  project: Project;
  activeSession: Session | null;
  onSelect: (session: Session, firstMessage?: string, autoSelect?: boolean) => void;
  onSessionUpdated?: (session: Session) => void;
  onSessionsChanged?: (count: number) => void;
  // Список опустел (удалён последний чат) — центр показывает пустое состояние,
  // а не автосоздаёт новый чат. Владелец сбрасывает activeSession в null.
  onCleared?: () => void;
  isMobile?: boolean;
  workflowRunningFor?: string;
  // Реестр общих тегов изменён (reorder ▲▼, создание тега из меню маркировки) —
  // владельцу (WorkspacePage) обновить project.tagRegistry. Не задан — SessionList
  // держит реестр сам (optimistic state поверх props)
  onTagsReorder?: (registry: ProjectTag[]) => void;
  // «На стену»: пункт в меню карточки; в ПЛОСКОМ режиме карточки ещё и
  // перетаскиваются на док стены (в Иерархии нативный drag сломал бы dnd-kit вложения).
  // Не задан — механики нет (мобила, флаг выключен).
  onAddToWall?: (s: Session) => void;
}

// Кнопка порядка ▲▼ у заголовка секции тегов
const orderBtnStyle = (disabled: boolean): React.CSSProperties => ({
  display: 'flex', alignItems: 'center', justifyContent: 'center',
  width: 20, height: 20, padding: 0,
  border: 'none', borderRadius: R.sm, cursor: disabled ? 'default' : 'pointer',
  background: 'transparent', color: disabled ? C.border : C.textMuted,
});

export function SessionList({ project, activeSession, onSelect, onSessionUpdated, onSessionsChanged, onCleared, isMobile = false, workflowRunningFor, onTagsReorder, onAddToWall }: Props) {
  const online = useOnline();
  // Подписка на стор персон — перерисоваться, когда список подгрузится (аватары сессий персон)
  usePersonasVersion();
  // Подписка на стор механик — перерисовать список при запуске новой механики
  useLastMechanicVersion();
  const personas = usePersonas();
  const [sessions, setSessions] = useState<Session[]>([]);
  // Список приехал с сервера хотя бы раз. До этого о числе чатов молчим: пустой
  // стартовый массив — не «чатов нет», а «ещё не знаем», и владелец, который по
  // этому числу решает показывать ли панель, схлопнул бы её сразу после открытия
  const [loaded, setLoaded] = useState(false);
  const [deleteTarget, setDeleteTarget] = useState<Session | null>(null);
  // Карточка под курсором — на ней показываем действия (на тач-устройствах hover нет, там действия видны всегда)
  const [hoveredId, setHoveredId] = useState<string | null>(null);
  const initializedRef = useRef(false);
  // Свежие activeSession/onSelect для обработчика chat_deleted (realtime-подписка живёт дольше рендера)
  const activeRef = useRef(activeSession);
  useEffect(() => { activeRef.current = activeSession; }, [activeSession]);
  const onSelectRef = useRef(onSelect);
  useEffect(() => { onSelectRef.current = onSelect; });
  const onClearedRef = useRef(onCleared);
  useEffect(() => { onClearedRef.current = onCleared; });

  // === Реестр общих тегов проекта ===
  // Optimistic state поверх project.tagRegistry: reorder/создание видны сразу, ответ
  // PUT (с нормализованным order) заменяет state; владелец может подхватить через
  // onTagsReorder и обновить project — тогда sync-эффект применит ту же правку.
  const [registry, setRegistry] = useState<ProjectTag[]>(() => project.tagRegistry ?? []);
  // eslint-disable-next-line react-hooks/set-state-in-effect -- синхронизация оптимистичного реестра тегов с project.tagRegistry
  useEffect(() => { setRegistry(project.tagRegistry ?? []); }, [project.id, project.tagRegistry]);
  // Открытое меню маркировки: чат + якорь кнопки (fixed-позиция, скролл списка закрывает)
  const [tagMenu, setTagMenu] = useState<{ sessionId: string; anchor: DOMRect } | null>(null);

  const persistRegistry = (next: ProjectTag[]) => {
    const prev = registry;
    setRegistry(next);
    api.projects.updateTags(project.id, next)
      .then(p => {
        const fresh = p.tagRegistry ?? next;
        setRegistry(fresh);
        onTagsReorder?.(fresh);
      })
      .catch(() => setRegistry(prev));
  };

  // Перестановка тега в реестре кнопками ▲▼ у заголовка секции (бэк нормализует order
  // по позиции массива — достаточно переставить элементы)
  // TODO: HTML5 drag-and-drop секций (по макету — drag-handle слева от заголовка)
  const moveTag = (tag: ProjectTag, dir: -1 | 1) => {
    const i = registry.findIndex(t => t.name.toLowerCase() === tag.name.toLowerCase());
    const j = i + dir;
    if (i < 0 || j < 0 || j >= registry.length) return;
    const next = [...registry];
    [next[i], next[j]] = [next[j], next[i]];
    persistRegistry(next.map((t, idx) => ({ ...t, order: idx })));
  };

  // Записать теги чата: optimistic в список (и активную сессию владельца), PUT, откат при сбое
  const setSessionTags = (s: Session, tags: string[]) => {
    const prevTags = s.tags ?? [];
    const optimistic = { ...s, tags };
    setSessions(prev => prev.map(x => (x.id === s.id ? optimistic : x)));
    if (activeSession?.id === s.id) onSessionUpdated?.(optimistic);
    api.sessions.update(project.id, s.id, { tags })
      .then(updated => handleSessionUpdated(updated))
      .catch(() => {
        setSessions(prev => prev.map(x => (x.id === s.id ? { ...x, tags: prevTags } : x)));
      });
  };

  // Переименование из карточки списка: ответ раскладывается тем же путём, что и теги —
  // в список и (если чат открыт) владельцу, чтобы шапка панели не отстала
  const renameSession = (s: Session, name: string) =>
    api.sessions.update(project.id, s.id, { name }).then(updated => handleSessionUpdated(updated));

  const toggleTag = (s: Session, name: string) => {
    const tags = s.tags ?? [];
    const has = tags.some(t => t.toLowerCase() === name.toLowerCase());
    setSessionTags(s, has ? tags.filter(t => t.toLowerCase() !== name.toLowerCase()) : [...tags, name]);
  };

  // Новый тег из меню маркировки: в реестр (цвет — следующий из палитры по кругу)
  // и сразу на чат. Пустой реестр — нормальный старт, первый тег создаётся здесь.
  const createTag = (s: Session, name: string) => {
    const color = GROUP_COLORS[registry.length % GROUP_COLORS.length];
    persistRegistry([...registry, { name, order: registry.length, color }]);
    if (!(s.tags ?? []).some(t => t.toLowerCase() === name.toLowerCase())) {
      setSessionTags(s, [...(s.tags ?? []), name]);
    }
  };

  useEffect(() => { if (loaded) onSessionsChanged?.(sessions.length); }, [loaded, sessions.length, onSessionsChanged]);

  // Кнопка десктопного чата видна, когда фича включена у человека И грань включена
  // в этом проекте: без второй половины оси сервер откажет, а кнопка врала бы
  const desktopReady = useFeature(FLAGS.desktopAgent) && project.desktopAgentEnabled === true;

  const createNew = async (): Promise<Session> => {
    // Чат создаётся от лица дефолт-персоны проекта
    const s = await createChatWithContextPersona(project, { mode: 'auto' });
    // Чужую (глобальную) сессию в список этого проекта не добавляем — поллинг сам синхронит
    if (s.projectId === project.id) setSessions(prev => [s, ...prev]);
    onSelect(s);
    return s;
  };

  // Десктопный чат (ADR-008): тип задаётся ТОЛЬКО при создании — продолжить его обычным
  // чатом нельзя, поэтому и создаётся он отдельной дверью, а не переключателем в готовом
  // чате. Персону сюда не подставляем: у грани своя ось выдачи «проект + тип чата».
  const createDesktop = async () => {
    try {
      const s = await api.sessions.create(project.id, 'auto', undefined, undefined,
        undefined, undefined, undefined, true);
      setSessions(prev => [s, ...prev]);
      onSelect(s);
    } catch (e: unknown) {
      // Отказ сервера показываем как есть: он называет причину (флаг, тумблер проекта)
      showToast('Десктопный чат не создан',
        e instanceof Error && e.message ? e.message : 'Сервер отказал', 'info');
    }
  };


  // Загрузка и поллинг сессий
  useEffect(() => {
    initializedRef.current = false;

    const init = async () => {
      // Офлайн без кэша — список недоступен, выходим без выбора
      const list = await api.sessions.list(project.id).catch(() => null);
      if (!list) return;
      setSessions(list);
      setLoaded(true);
      if (!initializedRef.current) {
        initializedRef.current = true;
        // Автовыбор первого чата, если он есть. Пустой список чат НЕ создаём —
        // центр показывает пустое состояние с кнопкой «Новый чат» (создание только по клику).
        // Читаем через ref-зеркала: эффект живёт на весь project.id, пропсы за это время свежие.
        if (!activeRef.current && list.length > 0) {
          onSelectRef.current(list[0], undefined, true);
        }
      }
    };

    init();
    const interval = setInterval(() => {
      api.sessions.list(project.id).then(setSessions).catch(() => {});
    }, 5000);
    return () => clearInterval(interval);
  }, [project.id]);

  // Подписка на статусы в реальном времени. Членство в project-группе держит WorkspacePage.
  useEffect(() => {
    let mounted = true;

    // Переподключение — рефетчим статусы (могли пропустить status_changed)
    onReconnected(() => {
      if (!mounted) return;
      api.sessions.list(project.id).then(list => {
        if (mounted) setSessions(list);
      }).catch(() => {});
    });

    const unsub = onMessage(msg => {
      if (!mounted) return;
      // Сессия удалена на сервере (в т.ч. авто-удаление временной) — убираем из списка;
      // если была открыта — переключаемся на первую оставшуюся
      if (msg.type === 'chat_deleted') {
        setSessions(prev => {
          const updated = prev.filter(s => s.id !== msg.sessionId);
          if (activeRef.current?.id === msg.sessionId) {
            if (updated.length > 0) queueMicrotask(() => onSelectRef.current(updated[0], undefined, true));
            // Удалён последний активный чат — сбрасываем в пустое состояние
            else queueMicrotask(() => onClearedRef.current?.());
          }
          return updated;
        });
        return;
      }
      // Смена статуса задачи (task_changed) меняет признак «Готово» у её чата-исполнителя:
      // taskDone не приходит в status_changed — обновляем по полному Task из события.
      if (msg.type === 'task_changed') {
        const t = msg.task;
        const done = msg.action !== 'deleted' && t.status === 'done';
        setSessions(prev => {
          let changed = false;
          const next = prev.map(s => {
            if (s.taskId !== t.id || s.taskDone === done) return s;
            changed = true;
            return { ...s, taskDone: done };
          });
          return changed ? next : prev;
        });
        return;
      }
      // Чат переименован моделью (авто-заголовок или «Обновить название») — правим имя
      // и значок в списке сразу, а не ждём поллинга через 5с
      if (msg.type === 'chat_renamed') {
        setSessions(prev => prev.map(s =>
          s.id === msg.sessionId
            ? { ...s, name: msg.name, topic: msg.topic ?? s.topic }
            : s
        ));
        return;
      }
      if (msg.type !== 'status_changed') return;
      setSessions(prev => prev.map(s =>
        s.id === msg.sessionId
          ? {
              ...s,
              status: msg.status as Session['status'],
              ...(msg.lastMessage !== undefined && { lastMessage: msg.lastMessage }),
              ...(msg.messageCount !== undefined && msg.messageCount > 0 && { messageCount: msg.messageCount }),
            }
          : s
      ));
    });

    return () => {
      mounted = false;
      unsub();
    };
  }, [project.id]);

  // Если активную сессию отредактировали из шапки чата — подхватываем название/модель,
  // не затирая статус, который приходит по realtime. Режим — тоже: changeMode обновляет
  // activeSession напрямую, а список иначе узнал бы о нём только поллингом (5с), и быстрый
  // уход-возврат в чат откатывал бы режим в UI к прежнему (объект из списка устарел).
  // Если активной сессии ещё нет в списке
  // (создана из пустого состояния в центре, мимо SessionList) — добавляем её сразу,
  // не дожидаясь 5-секундного поллинга.
  useEffect(() => {
    if (!activeSession) return;
    // eslint-disable-next-line react-hooks/set-state-in-effect -- подхват правок активной сессии в список, не дожидаясь поллинга
    setSessions(prev => {
      if (prev.some(s => s.id === activeSession.id)) {
        return prev.map(s =>
          s.id === activeSession.id ? { ...s, name: activeSession.name, model: activeSession.model, mode: activeSession.mode } : s
        );
      }
      // Чужую (глобальную) сессию в список этого проекта не добавляем
      return activeSession.projectId === project.id ? [activeSession, ...prev] : prev;
    });
    // eslint-disable-next-line react-hooks/exhaustive-deps -- сужение до полей осознанно: эффект зовёт setSessions, и зависимость от всего объекта activeSession дала бы цикл
  }, [activeSession?.id, activeSession?.name, activeSession?.model, activeSession?.mode, project.id]);

  const handleSessionUpdated = (updated: Session) => {
    setSessions(prev => prev.map(s => s.id === updated.id ? { ...s, ...updated } : s));
    if (activeSession?.id === updated.id) onSessionUpdated?.(updated);
  };

  const handleDelete = async () => {
    if (!deleteTarget) return;
    // Кнопка удаления скрыта офлайн, но сеть могла упасть между показом и кликом —
    // защищаемся от unhandled rejection и не закрываем диалог при сбое
    try {
      await api.sessions.delete(project.id, deleteTarget.id);
    } catch {
      setDeleteTarget(null);
      return;
    }
    const updated = sessions.filter(s => s.id !== deleteTarget.id);
    setSessions(updated);
    setDeleteTarget(null);
    if (activeSession?.id === deleteTarget.id) {
      if (updated.length > 0) {
        onSelect(updated[0], undefined, true);
      } else {
        // Удалён последний чат — не создаём новый автоматически, показываем
        // пустое состояние с кнопкой (создание только по клику).
        onCleared?.();
      }
    }
  };

  // === Фильтры и оси списка чатов ===
  // Фильтры + оси вида (groupBy/sortOrder/hierarchy) — единый state, персистится
  // в localStorage отдельно для каждого проекта (scope = project.id)
  const { filters, patch } = useChatFilters(project.id);
  const { groupBy, sortOrder, hierarchy } = filters;
  // Память свёрнутых веток дерева
  const { collapsedIds, toggleCollapse } = useTreeCollapse(project.id);
  // Чаты с живыми фоновыми агентами — считаются работающими в счётчике свёрнутой ветки
  const agentsRunningIds = useAgentsPresence();

  // Персоны в списке (для селектора фильтра)
  const personaIdsInList = [...new Set(sessions.filter(s => s.personaId).map(s => s.personaId!))];
  useSanitizePersonaFilter(filters, patch, personaIdsInList, sessions.length > 0);

  // Применение фильтров (единый предикат — общий с глобальным списком чатов)
  const isVisible = matchChatFilter(filters);
  const filteredSessions = sessions.filter(isVisible);
  // Фильтр применяется ко всем узлам дерева (не только к корням) — множество видимых
  // чатов совпадает с плоским списком. Сборка леса мемоизирована — hover по карточке
  // (hoveredId) не пересобирает дерево; isVisible пересоздаётся каждый рендер, его
  // исходные данные покрывает зависимость filters
  const activeSessionId = activeSession?.id ?? null;
  const tree = useMemo(
    () => hierarchy
      ? buildChatTreeRows(sessions, { isVisible, collapsedIds, activeId: activeSessionId, sortOrder, agentsRunningIds })
      : null,
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [sessions, hierarchy, sortOrder, collapsedIds, activeSessionId, filters, agentsRunningIds],
  );
  // Скрыто фильтрами — одинаково для плоского и дерева: множество видимых чатов одно
  const hiddenCount = sessions.length - filteredSessions.length;

  // Номер в подписи безымянного чата берём из исходного порядка списка:
  // группировка тасует карточки по дням, и позиция в группе давала бы скачущие номера
  const numberById = new Map(sessions.map((s, i) => [s.id, i + 1]));

  // === Композиция списка по осям (groupBy × sortOrder × hierarchy) ===
  // Сегменты дерева по корням (корень + его видимые строки-потомки). Ключ секции
  // корня — maxActivity поддерева: корень с живым ребёнком не тонет в старых днях.
  const treeSegments = useMemo(() => {
    if (!tree) return null;
    return splitChatTreeByRoots(tree.rows).map(seg => ({
      seg,
      // Синтетическая сессия корня для секционеров (groupChats/groupByTags работают
      // по Session): дату подменяем активностью поддерева, остальное — как у чата
      rootChat: { ...seg[0].chat, updatedAt: new Date(seg[0].maxActivity).toISOString() } as Session,
    }));
  }, [tree]);
  const segByRootId = useMemo(
    () => treeSegments ? new Map(treeSegments.map(x => [x.seg[0].chat.id, x.seg])) : null,
    [treeSegments],
  );

  // Секции: плоский список — из отфильтрованных чатов; дерево — из корней
  const dayGroups = treeSegments
    ? (groupBy === 'days' ? groupChats(treeSegments.map(x => x.rootChat), sortOrder) : [])
    : (groupBy === 'days' ? groupChats(filteredSessions, sortOrder) : []);
  // Режим «Теги»: секции по реестру (+ сироты, + хвост «Без тегов»); корень дерева
  // с несколькими тегами дублируется в каждой своей секции
  const tagGroups = groupBy === 'tags'
    ? groupByTags(treeSegments ? treeSegments.map(x => x.rootChat) : filteredSessions, registry, sortOrder)
    : null;
  // Без группировки: единый список (порядок дерева — pin+maxActivity, плоского — pin+дата)
  const flatList = groupBy === 'none' && !treeSegments
    ? sortChatsFlat(filteredSessions, sortOrder)
    : null;

  // leadingInset — место под контрол ветки в дереве (в плоском списке 0)
  const renderCard = (s: Session, leadingInset = 0) => {
    const card = (
      <ChatCard
        key={s.id}
        session={s}
        leadingInset={leadingInset}
        isActive={activeSession?.id === s.id}
        isMobile={isMobile}
        fallbackName={`Чат #${numberById.get(s.id) ?? 1}`}
        online={online}
        hovered={hoveredId === s.id}
        workflowRunning={workflowRunningFor === s.id}
        onSelect={() => onSelect(s)}
        onHover={h => setHoveredId(h ? s.id : null)}
        onDelete={() => setDeleteTarget(s)}
        tags={chatTagsSorted(s, registry).map(name => ({ name, color: tagColor(registry, name) }))}
        onRemoveTag={online ? name => toggleTag(s, name) : undefined}
        onAssignTags={online ? anchor => setTagMenu(prev => prev?.sessionId === s.id ? null : { sessionId: s.id, anchor }) : undefined}
        onRename={online ? name => renameSession(s, name) : undefined}
        onAddToWall={onAddToWall ? () => onAddToWall(s) : undefined}
        onEdited={handleSessionUpdated}
      />
    );
    // Перетаскивание на док стены — ТОЛЬКО в плоском режиме: в Иерархии строки уже
    // держит dnd-kit (ChatTreeRow.useDraggable), и нативный drag глушил бы вложение
    if (!onAddToWall || hierarchy) return card;
    return (
      <div
        key={s.id}
        draggable
        onDragStart={e => {
          e.dataTransfer.setData(WALL_DRAG_TYPE, JSON.stringify(s));
          e.dataTransfer.effectAllowed = 'copy';
        }}
      >
        {card}
      </div>
    );
  };

  // Заголовок секции режима «Теги»: цветовая точка, имя, счётчик чатов, кнопки
  // порядка ▲▼ (только у реестровых тегов; сироты и «Без тегов» неупорядочены)
  const renderTagSectionHeader = (g: TagChatGroup) => {
    const rt = g.registryTag;
    const idx = rt ? registry.findIndex(t => t.name.toLowerCase() === rt.name.toLowerCase()) : -1;
    const dot = tagColor(registry, g.title);
    return (
      <div style={{ display: 'flex', alignItems: 'center', gap: 6, padding: '10px 4px 7px' }}>
        <span style={{
          width: 8, height: 8, borderRadius: '50%', flexShrink: 0,
          background: g.tag ? (dot ?? C.accent) : C.textMuted,
        }} />
        <span style={{
          fontSize: 11, fontWeight: 700, color: C.textSecondary,
          overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
        }}>
          {g.title}
        </span>
        <span style={{
          fontSize: 10, fontFamily: 'inherit', color: C.textMuted,
          background: C.bgSelected, padding: '1px 6px', borderRadius: R.sm, flexShrink: 0,
        }}>
          {g.items.length}
        </span>
        <span style={{ flex: 1 }} />
        {rt && online && (
          <span style={{ display: 'flex', gap: 2, flexShrink: 0 }}>
            <button
              onClick={() => moveTag(rt, -1)}
              disabled={idx <= 0}
              title="Переместить тег выше"
              aria-label={`Тег «${rt.name}» выше`}
              style={orderBtnStyle(idx <= 0)}
            >
              <ChevronUp size={13} strokeWidth={2.2} />
            </button>
            <button
              onClick={() => moveTag(rt, 1)}
              disabled={idx < 0 || idx >= registry.length - 1}
              title="Переместить тег ниже"
              aria-label={`Тег «${rt.name}» ниже`}
              style={orderBtnStyle(idx < 0 || idx >= registry.length - 1)}
            >
              <ChevronDown size={13} strokeWidth={2.2} />
            </button>
          </span>
        )}
      </div>
    );
  };

  return (
    <div style={{ display: 'flex', flexDirection: 'column', flex: 1, minHeight: 0 }}>
      <ChatListToolbar
        onNew={() => { void createNew(); }}
        onNewDesktop={desktopReady ? () => { void createDesktop(); } : undefined}
        hideNew={!online}
        sessions={sessions}
        filters={filters}
        patch={patch}
        allPersonas={personas}
        hiddenCount={hiddenCount}
        isMobile={isMobile}
      />

      {/* Сверху отступ ужимается только под разделитель группы («Сегодня»): свой
          верхний padding у него есть, и вместе с общим набегало 18px пустоты под
          шапкой. Без группировки список начинается сразу карточкой — ей нужен
          обычный отступ, иначе она липнет к заголовку.
          minHeight:0 нужен потому, что внешний контейнер — flex-элемент
          контентной зоны PanelShell при fill=false (одна панель в drawer'е): без
          явного обнуления min-height flex-элемент по умолчанию растёт до высоты
          контента и не сжимается, а overflow:auto у скролл-дива не срабатывает,
          потому что нет переполнения. С minHeight:0 список сжимается до
          свободного места родителя, а длинный контент уходит в скролл. */}
      <div style={{ flex: 1, minHeight: 0, overflowY: 'auto', padding: `${groupBy === 'none' ? 8 : 2}px 8px 8px` }}>
        {/* Чатов в проекте нет вовсе (список уже приехал) — не голая панель, а empty-state.
            Условие по loaded, а не по длине: пустой стартовый массив ещё не значит «чатов
            нет», и empty мигнул бы до загрузки. Кнопки создания тут нет — «Новый» живёт
            в тулбаре панели сверху, дублировать его в empty незачем. */}
        {loaded && sessions.length === 0 && (
          <EmptyState
            compact
            icon={<MessageCircle size={20} strokeWidth={2} />}
            title="Чатов пока нет"
            subtitle="Начните первый чат по этому проекту."
          />
        )}
        {(tree ? tree.rows.length === 0 : filteredSessions.length === 0) && sessions.length > 0 && (
          <EmptyState
            compact
            icon={<FilterX size={20} strokeWidth={2} />}
            title="Ничего не нашлось"
            subtitle={buildHiddenReason(sessions.length, filters.search)}
            action={
              <ChatFilterResetActions
                search={filters.search}
                hasNonSearchFilters={!isDefaultFilters({ ...filters, search: '' })}
                onResetSearch={() => patch({ search: '' })}
                onResetAll={() => patch(defaultChatFiltersKeepingView(filters))}
              />
            }
          />
        )}
        {treeSegments ? (
          <ChatGroupingDnd chats={sessions} isMobile={isMobile} onEdited={handleSessionUpdated}>
            {tagGroups ? (
              tagGroups.map(g => (
                <div key={g.tag ?? '__untagged__'} style={{ marginBottom: 6, display: 'flow-root' }}>
                  {renderTagSectionHeader(g)}
                  {g.items.map(root => nestTreeRows(segByRootId!.get(root.id) ?? []).map(node => (
                    // Корень с несколькими тегами дублируется в каждой своей секции —
                    // key несёт тег, иначе React сочтёт ветки одним узлом между секциями
                    <ChatTreeBranch key={`${g.tag ?? '__untagged__'}:${node.row.chat.id}`} node={node} isMobile={isMobile} onToggleCollapse={toggleCollapse} renderCard={renderCard} />
                  )))}
                </div>
              ))
            ) : groupBy === 'none' ? (
              treeSegments.map(({ seg }) => nestTreeRows(seg).map(node => (
                <ChatTreeBranch key={node.row.chat.id} node={node} isMobile={isMobile} onToggleCollapse={toggleCollapse} renderCard={renderCard} />
              )))
            ) : (
              dayGroups.map(g => (
                <div key={g.title} style={{ marginBottom: 6, display: 'flow-root' }}>
                  <ListDateDivider title={g.title} />
                  {g.items.map(root => nestTreeRows(segByRootId!.get(root.id) ?? []).map(node => (
                    <ChatTreeBranch key={node.row.chat.id} node={node} isMobile={isMobile} onToggleCollapse={toggleCollapse} renderCard={renderCard} />
                  )))}
                </div>
              ))
            )}
          </ChatGroupingDnd>
        ) : tagGroups ? (
          // display:flow-root у секций — иначе marginBottom последней карточки
          // схлопывался бы с отступом секции, и высота плоского списка отличалась
          // бы от «Иерархии» на 5px за каждую секцию (там последняя строка —
          // flex-контейнер ChatTreeRow, её margin наружу не выходит). Панель
          // растёт по контенту, и переключение иерархии дёргало её на эту разницу
          tagGroups.map(g => (
            <div key={g.tag ?? '__untagged__'} style={{ marginBottom: 6, display: 'flow-root' }}>
              {renderTagSectionHeader(g)}
              {g.items.map(c => renderCard(c))}
            </div>
          ))
        ) : flatList ? (
          flatList.map(c => renderCard(c))
        ) : dayGroups.map(g => (
          <div key={g.title} style={{ marginBottom: 6, display: 'flow-root' }}>
            <ListDateDivider title={g.title} />
            {g.items.map(c => renderCard(c))}
          </div>
        ))}
      </div>

      {/* Меню маркировки чата общими тегами (fixed по якорю кнопки на карточке) */}
      {tagMenu && (() => {
        const target = sessions.find(x => x.id === tagMenu.sessionId);
        if (!target) return null;
        return (
          <TagAssignMenu
            anchor={tagMenu.anchor}
            registry={registry}
            selected={target.tags ?? []}
            onToggle={name => toggleTag(target, name)}
            onCreate={name => createTag(target, name)}
            onClose={() => setTagMenu(null)}
          />
        );
      })()}


      {deleteTarget && (
        <Modal
          title="Удалить чат?"
          width={MODAL_W.confirm}
          onClose={() => setDeleteTarget(null)}
          subtitle={
            <>
              Чат «<strong style={{ color: C.textPrimary, fontWeight: 600 }}>{deleteTarget.name ?? 'Новый чат'}</strong>» будет удалён без возможности восстановления.
            </>
          }
          footer={
            <ModalActions
              confirmLabel="Удалить"
              confirmVariant="danger"
              onConfirm={handleDelete}
              onCancel={() => setDeleteTarget(null)}
            />
          }
        />
      )}
    </div>
  );
}
