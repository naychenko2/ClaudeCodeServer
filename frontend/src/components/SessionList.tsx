import { useCallback, useEffect, useMemo, useState, useRef } from 'react';
import { FilterX, ChevronUp, ChevronDown, MessageCircle, Archive, CheckCircle2, Sparkles } from 'lucide-react';
import type { Project, ProjectSubject, ProjectTag, Session } from '../types';
import type { FeatureIndexFeature, FeaturesIndex } from '../types/featureIndex';
import { api } from '../lib/api';
import { idbGet } from '../lib/idb';
import { forgetChatContextOnDeleted } from '../lib/chatContext/forget';
import { archiveApi, saveArchiveSessionAsNote } from '../api/chats';
import { chatNeighborForArchive } from '../lib/chatUpdate';
import { onMessage, onReconnected } from '../lib/signalr';
import { useOnline } from '../hooks/useOnline';
import { useHoverWarm } from '../hooks/useSession';
import { C, FONT, FS, GROUP_COLORS, MODAL_W, R, SHADOW, SP } from '../lib/design';
import { Modal, ModalActions, Button, IconButton, Chip } from './ui';
import { ICON_SIZE, ICON_STROKE } from './ui/icons';
import { ChatGroupHeader } from './ChatGroupHeader';
import { FeatureGroupHeader } from './FeatureGroupHeader';
import { useFeature, FLAGS } from '../lib/featureFlags';
import { useFeaturesIndex } from '../lib/featuresIndex';
import { LIST_FLASH_CLASS, LIST_FLASH_MS } from './ListDateDivider';
import {
  SubjectDeleteDialog, SubjectPickMenu, SubjectRenameDialog, SubjectRunBar, SubjectSectionHeader,
  subjectHint, subjectOf,
} from './ChatSubjects';
import {
  addEdits, moveEdits, needsRegistryReload, onSubjectRunFinished, overlayPendingMoves, prunePendingMoves, removeEdits, renameEdits,
  startSubjectRun, takeFreshPoll, useSubjectRun, type PendingSubjectMove,
} from '../lib/chatSubjects';
import { usePersonas, usePersonasVersion } from '../lib/personas';
import { createChatWithContextPersona } from '../lib/defaultPersona';
import { showToast } from '../lib/toast';
import { useSubsystem } from '../lib/subsystems';
import { ChatFilterResetActions } from './FilterBar';
import { ChatListToolbar } from './ChatListToolbar';
import { EmptyState } from './ui';
import { useChatFilters, useSanitizePersonaFilter, matchChatFilter, loadChatFilters, isDefaultFilters, defaultChatFiltersKeepingView, buildHiddenReason, isArchivedChat, ARCHIVE_EMPTY_TITLE, ARCHIVE_EMPTY_SUBTITLE, type ChatGroupBy } from '../lib/chatFilters';
import { buildChatTreeRows, splitChatTreeByRoots, rootChatForSection, rootResolver, useTreeCollapse, withAncestors, type ChatTreeRowData } from '../lib/chatTree';
import { useBgWorkPresence } from '../lib/agentsPresence';
import { startVisiblePoll } from '../lib/visiblePoll';
import { ensureGit, useGitState } from '../lib/git';
import { useLastMechanicVersion } from '../lib/lastMechanic';
import { ChatCard, ROW_GAP } from './ChatCard';
import { ChatTreeBranch, nestTreeRows } from './ChatTreeRow';
import { ChatGroupingDnd } from './ChatGroupingDnd';
import { ListDateDivider } from './ListDateDivider';
import { groupChats, groupByTags, groupBySubjects, groupByFeatures, sortChatsFlat, chatTagsSorted, featureGroupByOptions, featureGroupChats, featureGroupTreeRows, featureRoleFor, otherGroupChatIds, NO_SUBJECT_KEY, FEATURE_CHIP_LABEL, type FeatureChatGroup, type SubjectChatGroup, type TagChatGroup } from '../lib/chatGroups';
import { plural } from '../lib/plural';
import { tagColor } from '../lib/tagRegistry';
import { dateOnly } from './chat/feature/featureTime';
import { TagAssignMenu } from './TagChip';
import { useReadStateVersion } from '../lib/chatReadState';
import { autoNavigateToContinued } from '../lib/chatContinue';

// Единый focus-visible ring (клавиатура) для строк-ссылок «+N чат в группе …» (F3.3):
// проектный паттерн (один инжект <style> на модуль), не inline onFocus (минор).
const FOCUS_CLASS = 'cc-sessionlist-link';
if (typeof document !== 'undefined' && !document.getElementById('cc-sessionlist-link-style')) {
  const el = document.createElement('style');
  el.id = 'cc-sessionlist-link-style';
  el.textContent = `.${FOCUS_CLASS}:focus-visible{outline:none;box-shadow:${SHADOW.focus};}`;
  document.head.appendChild(el);
}

// F3.3: строка-ссылка на «чужую» группу (мультифичевый чат): чат, связанный с этой
// фичей в индексе, но нарисованный в группе другой фичи. Считается из уже загруженного
// индекса (без запросов); useMemo — не пересчитывать на каждом рендере списка (минор).
// Мёртвая кнопка (минор): если чата нет среди загруженных сессий — ссылку не рисуем.
function OtherGroupLinks({ featureId, featuresIndex, groupSessions, sessions, isMobile, onSelect }: {
  featureId: string;
  featuresIndex: FeaturesIndex;
  groupSessions: Session[];
  sessions: Session[];
  isMobile: boolean;
  onSelect: (s: Session) => void;
}) {
  const liveIds = useMemo(() => new Set(sessions.map(s => s.id)), [sessions]);
  const links = useMemo(() => {
    // п.49: «+N чат» — по ЖИВЫМ сессиям, не по индексу (индекс может держать
    // удалённые/чужие чаты). Ссылка видна, только если хотя бы один её чат жив.
    return otherGroupChatIds(featureId, featuresIndex, groupSessions)
      .map(g => {
        const live = g.chatIds.filter(id => liveIds.has(id));
        return { featureId: g.featureId, featureTitle: g.featureTitle, count: live.length, firstChatId: live[0] };
      })
      .filter(l => l.count > 0);
  }, [featureId, featuresIndex, groupSessions, liveIds]);
  if (links.length === 0) return null;
  return (
    <div>
      {links.map(link => (
        <button
          key={link.featureId}
          type="button"
          data-testid={`other-group-link-${link.featureId}`}
          // п.39: подпись яснее, что клик открывает чат: «+N чат — в группе «A»».
          aria-label={`+${link.count} ${plural(link.count, 'чат', 'чата', 'чатов')} — в группе «${link.featureTitle}»`}
          className={FOCUS_CLASS}
          onClick={() => {
            const target = sessions.find(s => s.id === link.firstChatId);
            if (target) onSelect(target);
          }}
          style={{
            display: 'block', width: '100%', textAlign: 'left',
            border: 'none', cursor: 'pointer',
            background: 'transparent', color: C.textSecondary, fontFamily: FONT.sans,
            fontSize: FS.xs, padding: `${SP.xs}px ${SP.xs}px`, marginTop: 2,
            ...(isMobile ? { minHeight: 40 } : null),
          }}
        >
          +{link.count} {plural(link.count, 'чат', 'чата', 'чатов')} — в группе «{link.featureTitle}»
        </button>
      ))}
    </div>
  );
}

interface Props {
  project: Project;
  activeSession: Session | null;
  onSelect: (session: Session, firstMessage?: string, autoSelect?: boolean) => void;
  onSessionUpdated?: (session: Session) => void;
  onSessionsChanged?: (count: number) => void;
  // Полный список сессий, прокинутый владельцу (WorkspacePage) — нужен для плашки
  // «Ветка от …», чтобы определить, жив ли оригинал. Не задан — старый лёгкий
  // контракт с числом продолжает работать
  onSessionsListChanged?: (sessions: Session[]) => void;
  // Список опустел (удалён последний чат) — центр показывает пустое состояние,
  // а не автосоздаёт новый чат. Владелец сбрасывает activeSession в null.
  onCleared?: () => void;
  // Открытый чат удалён на сервере (другая вкладка, авто-удаление временного) — владелец
  // сам уводит центр на соседа (null — соседа нет) и переписывает историю. Задан —
  // onSelect/onCleared на этом пути не зовутся
  onActiveDeleted?: (next: Session | null) => void;
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

// Режимы группировки списка проекта: «Темы» есть только здесь, у глобального списка их
// нет. «Фичи» — пятый пункт, появляется только при флаге features-overview (см.
// featureGroupByOptions(true, featuresOn) в теле компонента); глобальный список его не
// получает (реестра фич у него нет).
// Зазор под секцией группы (дни, теги, темы). Нижний padding списка его вычитает
// (не больше SP.sm − ROW_GAP, иначе padding уйдёт в минус)
const GROUP_GAP = SP.xs + SP.xxs;
// Дебаунс перечитывания реестра тем по неизвестному subjectId из опроса
const SUBJECT_RELOAD_DEBOUNCE_MS = 1000;

// Порция архива: столько карточек рисуется при входе в «Архивные» и добавляется кнопкой
const ARCHIVE_PAGE = 40;

// Опрос и рефетч после переподключения отдают весь список заново, обычно без изменений.
// Новый массив перерисовал бы панель целиком — оставляем прежний, если по содержимому совпало.
// Сравниваем не JSON.stringify (тяжёлый: сериализует весь объект, включая teamImplement/workLoop),
// а отпечаток полей, которые реально рисует карточка (ChatCard): имя, статус, превью, теги,
// срок, закрепление, персона, группа, тема, досье, мьют, unread, архив, происхождение.
// Порядок — позиционно: сменился порядок в списке, это уже новое состояние, а не «то же».
const sessionFingerprint = (s: Session): string => [
  s.id, s.updatedAt, s.status, s.archivedAt ?? '', s.name ?? '',
  s.personaId ?? '', (s.participants ?? []).join(','), s.topic ?? '',
  s.teamImplement ? `${s.teamImplement.stage}:${s.teamImplement.waveNumber}` : '',
  s.expiresAfterMinutes ?? '', s.isPinned ? '1' : '', s.lastMessage ?? '',
  s.lastReadAt ?? '', s.projectId ?? '', s.excludeFromDossiers ? '1' : '',
  s.notificationsMuted ? '1' : '', (s.tags ?? []).join(','), s.taskId ?? '', s.origin,
].join('|');

function keepIfSame(prev: Session[], next: Session[]): Session[] {
  if (prev.length !== next.length) return next;
  for (let i = 0; i < next.length; i++) {
    if (sessionFingerprint(prev[i]) !== sessionFingerprint(next[i])) return next;
  }
  return prev;
}

export function SessionList({ project, activeSession, onSelect, onSessionUpdated, onSessionsChanged, onSessionsListChanged, onCleared, onActiveDeleted, isMobile = false, workflowRunningFor, onTagsReorder, onAddToWall }: Props) {
  // «Сохранить в заметки» доступно только при включённой подсистеме заметок — иначе
  // кнопка/пункт в ChatCard уйдёт в архив не сохранённой, а карточка не получит
  // ссылку SummaryNoteId (часть пункта меню «Сохранить в заметки» в ChatCard).
  const notesOn = useSubsystem('notes');
  const online = useOnline();
  // История чата под курсором едет до клика — открытие обходится без спиннера
  const warmHover = useHoverWarm();
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
  // Для какого проекта приехал последний свежий список: при смене проекта sessions ещё
  // держат чаты старого, и память свёрнутых веток нового проекта чистилась бы по ним
  const [loadedProjectId, setLoadedProjectId] = useState<string | null>(null);
  const [deleteTarget, setDeleteTarget] = useState<Session | null>(null);
  // Карточка под курсором — на ней показываем действия (на тач-устройствах hover нет, там действия видны всегда)
  const [hoveredId, setHoveredId] = useState<string | null>(null);
  // Раскрытая свайпом карточка (мобильная раскладка) — как в глобальном ChatList:
  // одна максимум, закрывается скроллом/открытием другой
  const [openSwipeId, setOpenSwipeId] = useState<string | null>(null);
  const initializedRef = useRef(false);
  // Свежие activeSession/onSelect для обработчика chat_deleted (realtime-подписка живёт дольше рендера)
  const activeRef = useRef(activeSession);
  useEffect(() => { activeRef.current = activeSession; }, [activeSession]);
  const onSelectRef = useRef(onSelect);
  useEffect(() => { onSelectRef.current = onSelect; });
  const onClearedRef = useRef(onCleared);
  useEffect(() => { onClearedRef.current = onCleared; });
  const onActiveDeletedRef = useRef(onActiveDeleted);
  useEffect(() => { onActiveDeletedRef.current = onActiveDeleted; });

  // Значок «правки чата не зафиксированы в git». Стор подключаем сами: список живёт не
  // только в мастерской (WorkspacePage уже зовёт ensureGit), но и на Стене, где git
  // мог не понадобиться никому. Вызов идемпотентен — повторный ничего не грузит
  useEffect(() => { ensureGit(project.id); }, [project.id]);
  const { dirtySessionIds } = useGitState(project.id);
  // Признак наследуется вверх по ветке: родитель отвечает за работу потомков, иначе
  // свёрнутая (или просто длинная) ветка прятала бы незафиксированные правки. Считаем
  // по ПОЛНОМУ списку чатов, а не по отфильтрованному: предок, скрытый фильтром, всё
  // равно остаётся звеном цепочки к видимому прародителю. Работает во всех режимах
  // списка — иерархия лежит в данных и без включённого режима «Иерархия»
  const dirtyBranchIds = useMemo(
    () => (dirtySessionIds ? withAncestors(sessions, dirtySessionIds) : undefined),
    [dirtySessionIds, sessions],
  );

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

  // === Темы чатов проекта ===
  // Реестр — оптимистичный state поверх project.subjectRegistry: ответ PUT заменяет его
  // целиком. DTO проекта у владельца обновляется редко, поэтому
  // реестр перечитывается сам: при монтировании, на конце прохода разбора и когда опрос
  // принёс тему, которой в реестре нет (модель завела её фоном)
  const [subjects, setSubjects] = useState<ProjectSubject[]>(() => project.subjectRegistry ?? []);
  // eslint-disable-next-line react-hooks/set-state-in-effect -- синхронизация оптимистичного реестра тем с project.subjectRegistry
  useEffect(() => { setSubjects(project.subjectRegistry ?? []); }, [project.id, project.subjectRegistry]);
  // Поколение записей реестра: применяется только ответ последнего запроса (PUT или
  // перечитывания) — ответы, пришедшие не по порядку, не откатывают экран назад
  const subjectsGenRef = useRef(0);
  // PUT реестра в полёте: его ответ и есть свежий реестр, перечитывание ему не нужно
  const subjectsPutsRef = useRef(0);
  const reloadSubjects = () => {
    if (subjectsPutsRef.current > 0) return;
    const gen = ++subjectsGenRef.current;
    api.projects.subjects.list(project.id)
      .then(r => { if (gen === subjectsGenRef.current) setSubjects(r); })
      .catch(() => {});
  };
  // Эффекты ниже живут на весь project.id — зовут свежую версию через ref
  const reloadSubjectsRef = useRef(reloadSubjects);
  useEffect(() => { reloadSubjectsRef.current = reloadSubjects; });
  // Неизвестные subjectId, по которым уже перечитывали (висячий id не гоняет петлю), и
  // таймер дебаунса: несколько поводов подряд — одно перечитывание
  const triedSubjectIdsRef = useRef(new Set<string>());
  const subjectReloadTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  useEffect(() => {
    const genRef = subjectsGenRef;
    const timerRef = subjectReloadTimerRef;
    triedSubjectIdsRef.current = new Set();
    reloadSubjectsRef.current();
    const off = onSubjectRunFinished(pid => { if (pid === project.id) reloadSubjectsRef.current(); });
    return () => {
      off();
      // Ответ ушедшего проекта в реестр нового не ляжет
      genRef.current++;
      if (timerRef.current) { clearTimeout(timerRef.current); timerRef.current = null; }
    };
  }, [project.id]);
  useEffect(() => {
    if (subjectReloadTimerRef.current) return;
    if (!needsRegistryReload(sessions, subjects, triedSubjectIdsRef.current)) return;
    subjectReloadTimerRef.current = setTimeout(() => {
      subjectReloadTimerRef.current = null;
      reloadSubjectsRef.current();
    }, SUBJECT_RELOAD_DEBOUNCE_MS);
  }, [sessions, subjects]);
  // Переносы чатов, чей PUT ещё в полёте (или ответил, но опросы, ушедшие до ответа, ещё
  // не вернулись): опрос накладывает их поверх своего ответа. pollNoRef — номер последнего
  // ушедшего опроса
  const pendingMovesRef = useRef(new Map<string, PendingSubjectMove>());
  const pollNoRef = useRef(0);
  // Номер последнего ПРИМЕНЁННОГО опроса: ответ опроса, ушедшего раньше, но вернувшегося
  // позже, отбрасывается (takeFreshPoll)
  const appliedPollNoRef = useRef(0);
  const applyPoll = useCallback((pollNo: number, list: Session[]) => {
    if (!takeFreshPoll(appliedPollNoRef, pollNo)) return;
    prunePendingMoves(pendingMovesRef.current, pollNo);
    setSessions(prev => keepIfSame(prev, overlayPendingMoves(list, pendingMovesRef.current)));
  }, []);
  // Открытый выбор темы: чат + якорь (fixed-позиция, как у меню тегов)
  const [subjectMenu, setSubjectMenu] = useState<{ sessionId: string; anchor: DOMRect } | null>(null);
  const subjectTarget = subjectMenu ? sessions.find(x => x.id === subjectMenu.sessionId) : undefined;
  const [renameSubject, setRenameSubject] = useState<ProjectSubject | null>(null);
  const [deleteSubject, setDeleteSubject] = useState<ProjectSubject | null>(null);
  // Секция, принявшая чат, коротко мигает — иначе на длинном списке чат «пропадает» из-под пальца
  const [flashSubject, setFlashSubject] = useState<string | null>(null);
  const flashSection = (key: string) => {
    setFlashSubject(key);
    window.setTimeout(() => setFlashSubject(k => (k === key ? null : k)), LIST_FLASH_MS);
  };

  // Запись реестра: оптимистично, ответ PUT — правда, сбой — откат и тост. Любая ручная
  // правка темы модели переводит её в human — ✦ снимаем сразу
  // Отказ (400 «Тема не найдена», дубль) значит, что локальный реестр разошёлся с
  // сервером — после отката перечитываем его
  const persistSubjects = (optimistic: ProjectSubject[], edits: Parameters<typeof api.projects.subjects.replace>[1]) => {
    const prev = subjects;
    const gen = ++subjectsGenRef.current;
    subjectsPutsRef.current++;
    setSubjects(optimistic);
    return api.projects.subjects.replace(project.id, edits)
      .then(fresh => {
        subjectsPutsRef.current--;
        if (gen === subjectsGenRef.current) setSubjects(fresh);
        return fresh;
      })
      .catch((e: unknown) => {
        subjectsPutsRef.current--;
        if (gen === subjectsGenRef.current) setSubjects(prev);
        showToast('Темы', e instanceof Error ? e.message : 'Не удалось сохранить темы', 'info');
        reloadSubjects();
        throw e;
      });
  };
  const doRenameSubject = (t: ProjectSubject, name: string) =>
    persistSubjects(subjects.map(s => (s.id === t.id ? { ...s, name, createdBy: 'human' as const } : s)), renameEdits(subjects, t.id, name))
      .then(() => undefined);
  const doMoveSubject = (t: ProjectSubject, dir: -1 | 1) => {
    const edits = moveEdits(subjects, t.id, dir);
    if (!edits) return;
    const orderOf = new Map(edits.map(e => [e.id, e.order ?? 0]));
    persistSubjects(subjects.map(s => {
      const order = orderOf.get(s.id) ?? s.order;
      return order === s.order ? s : { ...s, order, createdBy: 'human' as const };
    }), edits).catch(() => {});
  };
  const doDeleteSubject = (t: ProjectSubject) =>
    persistSubjects(subjects.filter(s => s.id !== t.id), removeEdits(subjects, t.id)).then(() => undefined).catch(() => {});

  // Перенос чата: оптимистично в список (и владельцу), ответ — сессия с сервера. Пока PUT
  // в полёте, опрос не возвращает старую тему (pendingMovesRef); отказ (тему снесла
  // уборка прохода) — откат, тост и перечитывание реестра, чтобы тема-призрак ушла из выбора
  const moveChatSubject = (s: Session, body: { subjectId: string | null } | { release: true }) => {
    const patch: PendingSubjectMove['patch'] = 'release' in body
      ? { subjectId: null, subjectSource: null }
      : { subjectId: body.subjectId, subjectSource: 'human' };
    const optimistic: Session = { ...s, ...patch };
    const move: PendingSubjectMove = { patch };
    pendingMovesRef.current.set(s.id, move);
    // Завершение трогает только СВОЙ перенос: более поздний перенос того же чата остаётся
    const own = () => pendingMovesRef.current.get(s.id) === move;
    setSessions(prev => prev.map(x => (x.id === s.id ? optimistic : x)));
    flashSection(('subjectId' in body && body.subjectId) || NO_SUBJECT_KEY);
    api.projects.subjects.moveSession(project.id, s.id, body)
      // Успех: перенос держится, пока не вернётся опрос, ушедший ПОСЛЕ ответа PUT, —
      // ответ опроса, ушедшего раньше, принёс бы старую тему
      .then(updated => { if (own()) move.settledAt = pollNoRef.current; handleSessionUpdated(updated); })
      .catch((e: unknown) => {
        // Отказ: откат к исходной теме — старые опросы несут её же, держать нечего
        if (own()) pendingMovesRef.current.delete(s.id);
        setSessions(prev => prev.map(x => (x.id === s.id ? s : x)));
        showToast('Тема чата', e instanceof Error ? e.message : 'Не удалось перенести чат', 'info');
        reloadSubjects();
      });
  };
  // Новая тема из выбора: заводится и сразу принимает чат
  const createSubjectFor = (s: Session, name: string) => {
    persistSubjects(subjects, addEdits(subjects, name))
      .then(fresh => {
        const created = fresh.find(t => t.name.trim().toLowerCase() === name.trim().toLowerCase());
        if (created) moveChatSubject(s, { subjectId: created.id });
      })
      .catch(() => {});
  };

  useEffect(() => { if (loaded) onSessionsChanged?.(sessions.length); }, [loaded, sessions.length, onSessionsChanged]);
  // Полный список сессий прокидывается владельцу для плашки «Ветка от …»: при любом
  // изменении списка (загрузка, удаление, realtime-обновление) ChatPanel выше должен
  // знать, жив ли оригинал ветки. До первой загрузки дёргать бессмысленно — владелец
  // сам отличит «пусто, потому что ещё не приехало» по отсутствию источника
  // (загружать отдельно api.sessions.list ради этого он не должен: явный запрет постановки)
  // eslint-disable-next-line react-hooks/set-state-in-effect -- прокидывание списка наверх, не локальный setState
  useEffect(() => { if (loaded) onSessionsListChanged?.(sessions); }, [loaded, sessions, onSessionsListChanged]);

  const createNew = async (): Promise<Session> => {
    // Чат создаётся от лица дефолт-персоны проекта
    const s = await createChatWithContextPersona(project, { mode: 'auto' });
    // Чужую (глобальную) сессию в список этого проекта не добавляем — поллинг сам синхронит
    if (s.projectId === project.id) setSessions(prev => [s, ...prev]);
    onSelect(s);
    return s;
  };

  // Загрузка и поллинг сессий
  useEffect(() => {
    initializedRef.current = false;
    // Ответ ушедшего проекта не должен лечь в список нового
    let cancelled = false;
    let freshArrived = false;

    // Прошлый ответ из офлайн-кэша рисуем сразу, не дожидаясь сервера: иначе при входе в
    // проект список стоял с одним открытым чатом, пока свежий ответ ехал по сети. Свежий
    // список заменяет кэш целиком; empty-состояния и автовыбор ждут именно его (loaded)
    idbGet<Session[]>(`/projects/${project.id}/sessions`).then(cached => {
      if (cancelled || freshArrived || !Array.isArray(cached?.data)) return;
      const fromCache = cached.data;
      // Чат, подложенный до кэша (открытый, в т.ч. только что созданный), не теряем
      setSessions(prev => [...prev.filter(p => !fromCache.some(c => c.id === p.id)), ...fromCache]);
    }).catch(() => { /* кэш недоступен — ждём сервер */ });

    const init = async () => {
      // Офлайн без кэша — список недоступен, выходим без выбора
      const list = await api.sessions.list(project.id).catch(() => null);
      if (!list || cancelled) return;
      freshArrived = true;
      setSessions(list);
      setLoaded(true);
      setLoadedProjectId(project.id);
      if (!initializedRef.current) {
        initializedRef.current = true;
        // Автовыбор первого чата, если он есть. Пустой список чат НЕ создаём —
        // центр показывает пустое состояние с кнопкой «Новый чат» (создание только по клику).
        // Читаем через ref-зеркала: эффект живёт на весь project.id, пропсы за это время свежие.
        // Автовыбор — только чаты, видимые под фильтрами списка: архивные (архивация
        // не двигает UpdatedAt, и заархивированный последним стоит первым) и скрытые
        // фильтрами (напр., выполненные задачи) в списке нет — открытым в центре
        // такой чат висел бы призраком.
        const isVisible = matchChatFilter(loadChatFilters(project.id));
        const live = list.filter(s => !s.archivedAt && isVisible(s));
        if (!activeRef.current && live.length > 0) {
          onSelectRef.current(live[0], undefined, true);
        }
      }
    };

    init();
    // Пауза на скрытой вкладке + догон при возврате: час фоновых агентов не должен
    // жечь CPU/сеть опросом списка (см. lib/visiblePoll). Применяем через applyPoll:
    // дерево/свёрнутые ветки и pending-переносы origin учитываются, keepIfSame — внутри
    const stop = startVisiblePoll(() => {
      const pollNo = ++pollNoRef.current;
      api.sessions.list(project.id)
        .then(list => { if (!cancelled) applyPoll(pollNo, list); })
        .catch(() => {});
    }, 5000);
    return () => { cancelled = true; stop(); };
  }, [project.id, applyPoll]);

  // Подписка на статусы в реальном времени. Членство в project-группе держит WorkspacePage.
  useEffect(() => {
    let mounted = true;

    // Переподключение — рефетчим статусы (могли пропустить status_changed).
    // Отписку храним и зовём в cleanup: иначе колбэк живёт после unmount (утечка)
    const offReconnected = onReconnected(() => {
      if (!mounted) return;
      const pollNo = ++pollNoRef.current;
      api.sessions.list(project.id).then(list => {
        if (mounted) applyPoll(pollNo, list);
      }).catch(() => {});
    });

    const unsub = onMessage(msg => {
      if (!mounted) return;
      // Сессия удалена на сервере (в т.ч. авто-удаление временной) — убираем из списка;
      // если была открыта — переключаемся на первую оставшуюся ВИДИМУЮ: те же фильтры,
      // что у соседа при архивации — архивный или скрытый фильтром чат открылся бы призраком
      if (msg.type === 'chat_deleted') {
        forgetChatContextOnDeleted(msg);
        // Обновитель под StrictMode вызывается дважды с тем же prev — реакцию на
        // удаление активного чата ставим в очередь один раз на событие
        let reacted = false;
        setSessions(prev => {
          const updated = prev.filter(s => s.id !== msg.sessionId);
          if (!reacted && activeRef.current?.id === msg.sessionId) {
            reacted = true;
            const handler = onActiveDeletedRef.current;
            // Удалённого в updated уже нет — chatNeighborForArchive отдаёт первый живой видимый
            const neighbor = chatNeighborForArchive(updated, msg.sessionId, matchChatFilter(loadChatFilters(project.id)));
            if (handler) queueMicrotask(() => handler(neighbor));
            else if (neighbor) queueMicrotask(() => onSelectRef.current(neighbor, undefined, true));
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
      // «Продолжить в новом чате»: исходный чат продолжён — обновляем continuedInSessionId
      // в списке (пометка «Продолжен в новом чате») и, если открыт именно он, переходим
      // в новый (тост). Открыт другой чат/экран — только стор, без перехода.
      if (msg.type === 'chat_continued') {
        autoNavigateToContinued(msg);
        setSessions(prev => prev.map(s =>
          s.id === msg.sessionId
            ? { ...s, continuedInSessionId: msg.newSessionId }
            : s
        ));
        return;
      }
      // Чат убран/возвращён из архива на другом клиенте/устройстве: рефетчим
      // конкретный чат и кладём обновление в список — иначе archived-флаг
      // залежался бы до следующего тика поллинга (5с) и карточка висела бы
      // в списке, уже уйдя в архив. Локальная мутация из ChatCard идёт через
      // handleArchive и СИЛЬНЕЕ этого refetch (обновляет state синхронно, до
      // прихода события). Форма — как в ChatsPage и wallStore
      if (msg.type === 'chat_archived' && msg.sessionId) {
        api.chats.get(msg.sessionId)
          .then(updated => setSessions(prev => prev.map(s => s.id === updated.id ? { ...s, ...updated } : s)))
          .catch(() => { /* чат ушёл из набора или офлайн — ignore */ });
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
      offReconnected();
    };
  }, [project.id, applyPoll]);

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

  // === Архив чата (режим «Архивные» этого же списка) ===
  // Чат ПРЯЧЕТСЯ, а не удаляется: история и claudeSessionId целы. Признак архива
  // производный (ArchivedAt/UpdatedAt) — на клиенте его не вычисляем, кладём в
  // список ответ сервера как есть. 409 «в чате идёт ход» показываем ровно тем
  // текстом, что прислал бэкенд: он называет причину точнее нашей обёртки.
  const handleArchive = async (s: Session, archived: boolean) => {
    try {
      handleSessionUpdated(await archiveApi.setArchived(s.id, archived));
    } catch (e) {
      showToast('Архив', e instanceof Error ? e.message : 'Не удалось изменить архив', 'info');
    }
  };

  // «Сохранить в заметки» — существующий POST /api/sessions/{id}/summary. В ответе
  // приходит заметка, а SummaryNoteId сервер проставляет самой сессии: перечитываем
  // чат, иначе карточка покажет первые строки заметки только после поллинга (5с).
  const handleSaveAsNote = async (s: Session) => {
    try {
      await saveArchiveSessionAsNote(s.id);
      showToast('Сохранение в заметки', 'Заметка создана', 'info');
      const fresh = await api.chats.get(s.id).catch(() => null);
      if (fresh) handleSessionUpdated(fresh);
    } catch (e) {
      showToast('Сохранение в заметки', e instanceof Error ? e.message : 'Не удалось сохранить в заметки', 'info');
    }
  };

  // === Фильтры и оси списка чатов ===
  // Фильтры + оси вида (groupBy/sortOrder) — единый state, персистится в localStorage
  // отдельно для каждого проекта (scope = project.id). Список всегда деревом: ось
  // hierarchy в сторе осталась ради совместимости записей и здесь не читается
  const { filters, patch } = useChatFilters(project.id);
  const { sortOrder } = filters;
  // Режим «Фичи»: фасет по индексу фич (флаг features-overview, dark launch).
  // Пункт «Фичи» в группировке появляется только при флаге; индекс фич проекта
  // источник правды (бэк считает bucket/порядок/членство/ключевой чат), фронт
  // только показывает. Флаг выключен — источник не зовётся (EMPTY, фасет не рисуется)
  const featuresOn = useFeature(FLAGS.featuresOverview);
  const groupByOptions = featureGroupByOptions(true, featuresOn);
  // groupBy из хранилища может быть недоступен этому списку: 'features' при
  // выключенном флаге (пункта «Фичи» в тулбаре нет, а режим фичи показал бы
  // «Фич пока нет»). Клампим к опциям — как 'subjects' в глобальном списке
  // (ChatList.tsx) — иначе ни одна ветка рендера не сработала бы. Хранилище не
  // перезаписываем: первый явный выбор пользователя всё починит сам.
  const groupBy: ChatGroupBy = groupByOptions.includes(filters.groupBy) ? filters.groupBy : 'days';
  const subjectsMode = groupBy === 'subjects';
  const subjectRun = useSubjectRun(project.id, subjectsMode);
  const featuresMode = groupBy === 'features';
  const featuresIndex = useFeaturesIndex(project.id, featuresOn);
  // Раскрытые группы фич (по featureId). Пусто = все свёрнуты: группа свёрнута по
  // умолчанию, наружу выведены ключевой и живые/вставшие чаты, остальное — «ещё N»
  const [expandedFeatureGroups, setExpandedFeatureGroups] = useState<Set<string>>(new Set());

  // Возврат чата из архива: выходим из архивного вида и открываем этот чат — достают
  // из архива, чтобы продолжить работу, а не чтобы остаться в архивном списке
  const leaveArchiveAndOpen = (s: Session) => {
    if (filters.archivedOnly) patch({ archivedOnly: false });
    setOpenSwipeId(null);
    onSelect(s);
  };
  // Память свёрнутых веток дерева; удалённые чаты из неё вычищаются по полному списку.
  // До загрузки списка не передаём ничего — пустой стартовый массив не «чатов нет»; после
  // смены проекта список помечен старым проектом, и хук его не примет до свежего ответа
  const { collapsedIds, expandedIds, toggleCollapse } = useTreeCollapse(
    project.id,
    loadedProjectId ? { scope: loadedProjectId, ids: sessions.map(s => s.id) } : undefined,
  );
  // Чаты с живой фоновой работой (агенты или команда в фоне) — считаются работающими
  // в счётчике свёрнутой ветки, как и в переливе самой карточки
  const bgWorkIds = useBgWorkPresence();

  // Персоны в списке (для селектора фильтра)
  const personaIdsInList = [...new Set(sessions.filter(s => s.personaId).map(s => s.personaId!))];
  useSanitizePersonaFilter(filters, patch, personaIdsInList, sessions.length > 0);

  // Применение фильтров (единый предикат — общий с глобальным списком чатов)
  const matchesFilters = matchChatFilter(filters);
  const filteredSessions = sessions.filter(matchesFilters);
  // Архив рисуется порциями: в большом проекте там полторы сотни чатов, и монтирование
  // всех карточек разом подвешивало переключение «Архивные». Порция — первые по
  // текущей сортировке; счётчики «скрыто фильтрами» считаются по полному набору
  const [archiveLimit, setArchiveLimit] = useState(ARCHIVE_PAGE);
  // eslint-disable-next-line react-hooks/set-state-in-effect -- сброс порции при входе в архив и смене проекта
  useEffect(() => { setArchiveLimit(ARCHIVE_PAGE); }, [filters.archivedOnly, project.id]);
  const archiveRest = filters.archivedOnly ? Math.max(0, filteredSessions.length - archiveLimit) : 0;
  const archiveCut = archiveRest > 0;
  // Хвост под последней карточкой (её ROW_GAP и GROUP_GAP секции), который нижний
  // padding списка вычитает. Под кнопкой «Показать ещё» хвоста нет — там полный SP.sm
  const listTail = archiveCut ? 0 : ROW_GAP + (groupBy === 'none' ? 0 : GROUP_GAP);
  const archiveShownIds = useMemo(
    () => archiveCut
      ? new Set(sortChatsFlat(filteredSessions, sortOrder).slice(0, archiveLimit).map(s => s.id))
      : null,
    // eslint-disable-next-line react-hooks/exhaustive-deps -- filteredSessions пересобирается каждый рендер, его исходные данные покрывают sessions и filters
    [archiveCut, archiveLimit, sessions, filters, sortOrder],
  );
  const isVisible = archiveShownIds
    ? (s: Session) => matchesFilters(s) && archiveShownIds.has(s.id)
    : matchesFilters;
  // Чаты ТЕКУЩЕЙ оси: обычный список — неархивные, режим «Архивные» — архивные.
  // Всё, что считается «скрыто фильтрами» и «чатов нет», меряется по этому
  // подмножеству: в режиме архива бейдж иначе врал бы на весь список проекта,
  // а в обычном — записывал в «скрытые фильтрами» убранные в архив чаты, которые
  // сбросом фильтров не возвращаются.
  const scoped = sessions.filter(s => isArchivedChat(s) === filters.archivedOnly);
  // Фильтр применяется ко всем узлам дерева (не только к корням) — множество видимых
  // чатов совпадает с плоским списком. Сборка леса мемоизирована — hover по карточке
  // (hoveredId) не пересобирает дерево; isVisible пересоздаётся каждый рендер, его
  // исходные данные покрывает зависимость filters
  const activeSessionId = activeSession?.id ?? null;
  // readVersion — пересчёт счётчика непрочитанных свёрнутых веток при отметке прочтения
  const readVersion = useReadStateVersion();
  const tree = useMemo(
    () => buildChatTreeRows(sessions, { isVisible, collapsedIds, expandedIds, activeId: activeSessionId, sortOrder, bgWorkIds }),
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [sessions, sortOrder, collapsedIds, expandedIds, activeSessionId, filters, bgWorkIds, readVersion, archiveShownIds],
  );
  // Скрыто фильтрами — одинаково для плоского и дерева: множество видимых чатов одно
  const hiddenCount = scoped.length - filteredSessions.length;

  // Номер в подписи безымянного чата берём из исходного порядка списка:
  // группировка тасует карточки по дням, и позиция в группе давала бы скачущие номера
  const numberById = new Map(sessions.map((s, i) => [s.id, i + 1]));

  // === Композиция списка по осям (groupBy × sortOrder) ===
  // Сегменты дерева по корням (корень + его видимые строки-потомки). Ключ секции
  // корня — maxActivity поддерева: корень с живым ребёнком не тонет в старых днях.
  // Чем представлен корень для секционеров — одна точка на оба списка (rootChatForSection)
  const treeSegments = useMemo(
    () => splitChatTreeByRoots(tree.rows).map(seg => ({ seg, rootChat: rootChatForSection(seg, sortOrder) })),
    [tree, sortOrder],
  );
  const segByRootId = useMemo(
    () => new Map(treeSegments.map(x => [x.seg[0].chat.id, x.seg])),
    [treeSegments],
  );
  const treeRoots = treeSegments.map(x => x.rootChat);

  // Секции строятся из корней дерева
  const dayGroups = groupBy === 'days' ? groupChats(treeRoots, sortOrder) : [];
  // Режим «Теги»: секции по реестру (+ сироты, + хвост «Без тегов»); корень дерева
  // с несколькими тегами дублируется в каждой своей секции
  const tagGroups = groupBy === 'tags' ? groupByTags(treeRoots, registry, sortOrder) : null;
  // Режим «Темы»: чат ровно в одной секции, секцию задаёт корень ветки — на вход идут корни.
  // rootOf (по полному списку) нужен карточке: пункт выбора темы есть только у корня
  const rootOf = useMemo(() => rootResolver(sessions), [sessions]);
  const subjectGroups: SubjectChatGroup[] | null = subjectsMode
    ? groupBySubjects(treeRoots, subjects, sortOrder)
    : null;
  // Режим «Фичи»: группы по индексу фич (порядок = порядок features[] бэка, «Без фичи»
  // в конце). На вход — ВСЕ сессии (не только корни): правило 3 контракта С0 режет
  // межфичевые рёбра дерева, и для этого функция должна видеть детей, а не только корни
  // (C1: исполнители обеих фич оркестратора — в своих группах, а не деревом в первой)
  const featureGroups: FeatureChatGroup[] | null = featuresMode
    ? groupByFeatures(sessions, featuresIndex, sortOrder)
    : null;
  // Подсказка про залежи: чаты «без темы» старше нижней границы фона модель сама не тронет
  const sweepSince = project.subjectsSweepSince ? new Date(project.subjectsSweepSince).getTime() : null;
  const startRun = () => { void startSubjectRun(project.id); };
  const renderSubjectSection = (g: SubjectChatGroup, body: React.ReactNode) => {
    const key = g.subject?.id ?? NO_SUBJECT_KEY;
    const registryEmpty = subjects.length === 0;
    const hasOld = !g.subject && !registryEmpty && sweepSince !== null
      && g.items.some(c => new Date(c.updatedAt).getTime() < sweepSince);
    return (
      <div key={key} className={flashSubject === key ? LIST_FLASH_CLASS : undefined} style={{ marginBottom: GROUP_GAP, display: 'flow-root' }}>
        <SubjectSectionHeader
          subject={g.subject}
          // Счётчик — видимые строки секции: в дереве это корни, в плоском списке —
          // все чаты секции, включая дочерние, которых секция корня тоже показывает
          count={g.items.length}
          registry={subjects}
          isMobile={isMobile}
          online={online}
          run={subjectRun}
          onRename={setRenameSubject}
          onMove={doMoveSubject}
          onDelete={setDeleteSubject}
          onRun={startRun}
        />
        {hasOld && (
          <div style={{ padding: '0 4px 6px', fontSize: FS.xs, color: C.textMuted, fontFamily: FONT.sans }}>
            Чаты до включения тем AI сам не трогает — нажмите «Разложить»
          </div>
        )}
        {body}
      </div>
    );
  };
  // Режим «Фичи»: дерево группы по ЭФФЕКТИВНЫМ родителям (результат groupByFeatures:
  // межфичевые рёбра обрезаны, через границу фичи ребёнок всплывает корнем — контракт
  // С0, §3). Не по исходным segByRootId: иначе исполнители обеих фич оркестратора
  // оставались бы деревом в группе первой фичи, а группа второй не рисовалась (C1).
  // exposedIds — какие чаты вывести (null = все): свёрнуто — ключевой + живые/вставшие,
  // скрытые «прокалываются» (buildChatTreeRows поднимает их видимых детей). Ключевой
  // чат — первым: его поддерево (если он корень) переставляется в начало.
  // Дерево группы: по эффективным родителям (featureGroupTreeRows) + фильтры чата
  // (isVisible) + (свёрнуто) экспозиция. Ключевой чат — первым (функция сама переставляет)
  const buildGroupTree = (g: FeatureChatGroup, exposedIds: Set<string> | null): ChatTreeRowData[] =>
    featureGroupTreeRows(
      g.sessions,
      g.feature?.keySessionId ?? null,
      exposedIds,
      { isVisible, collapsedIds, expandedIds, activeId: activeSessionId, sortOrder, bgWorkIds },
    );
  // «Сейчас» в мс — для «сколько» в статусе фичи (attention.since → длительность).
  // На уровне рендера: статус пересчитывается на каждой перерисовке списка.
  const nowMs = Date.now();
  // «Кто» в статусе: имя персоны-исполнителя (attention.personaId, иначе now0.personaId).
  // null — персона не найдена (стор ещё не догрузил) или не задана: статус без «кто».
  const personaNameOf = (f: FeatureIndexFeature): string | null => {
    const pid = f.attention?.personaId ?? f.now0?.personaId ?? null;
    if (!pid) return null;
    return personas.find(p => p.id === pid)?.name ?? null;
  };
  // Резолвер id персоны → Persona: для стопки аватаров «кто сейчас» в заголовке группы.
  // Не найден — undefined (аватар не рисуется; PersonaAvatar требует полный объект Persona).
  const personaById = (id: string) => personas.find(p => p.id === id);
  // Режим «Фичи»: заголовок группы (FeatureGroupHeader) + чаты. Группа свёрнута по
  // умолчанию: наружу выведены ключевой и живые/вставшие чаты, остальное — «ещё N»;
  // раскрыта — все чаты (ключевой первым, с меткой «ключевой»). «Без фичи» — хвост
  // без заголовка группы (простой разделитель + ветки)
  const renderFeatureGroup = (g: FeatureChatGroup) => {
    if (!g.feature) {
      // «Без фичи»: дерево по эффективным родителям (межгрупповые рёбра уже обрезаны)
      const rows = buildGroupTree(g, null);
      return (
        <div key="no-feature" style={{ marginBottom: GROUP_GAP, display: 'flow-root' }}>
          <ListDateDivider title="Без фичи" plain />
          {nestTreeRows(rows).map(node => (
            <ChatTreeBranch key={node.row.chat.id} node={node} isMobile={isMobile} onToggleCollapse={toggleCollapse} renderCard={renderCard} />
          ))}
        </div>
      );
    }
    // Закрытая фича — компактная строка «на втором плане» (макет .closed, минор m4):
    // по умолчанию строка «название · смержена {дата} · {длительность}» (статус считает
    // FeatureGroupHeader в режиме compact); раскрытие (M4) — по featureId через
    // expandedFeatureGroups, как у открытых групп: дерево buildGroupTree + «ключевой».
    if (g.feature.bucket === 'closed') {
      const fid = g.feature.featureId;
      const keyId = g.feature.keySessionId;
      const expanded = expandedFeatureGroups.has(fid);
      const toggle = () => setExpandedFeatureGroups(prev => {
        const next = new Set(prev);
        if (next.has(fid)) next.delete(fid);
        else next.add(fid);
        return next;
      });
      const rows = expanded ? buildGroupTree(g, null) : [];
      return (
        <div key={fid} style={{ marginBottom: GROUP_GAP, display: 'flow-root' }}>
          <FeatureGroupHeader
            feature={g.feature}
            expanded={expanded}
            onToggle={toggle}
            // Тело открывает страницу фичи (Ф2.3): вход из СПИСКА (fromChat: false) —
            // указатель возврата не пишется (крошка → ключевой чат/список, M3).
            onOpen={() => { if (g.feature) window.dispatchEvent(new CustomEvent('cc-open-feature', { detail: { featureId: g.feature.featureId, fromChatId: activeSessionId, fromChat: false } })); }}
            isMobile={isMobile}
            compact
            personaName={personaNameOf(g.feature)}
            now={nowMs}
            personaById={personaById}
          />
          {expanded && (
            <div style={{ marginLeft: 13, paddingLeft: 9, borderLeft: `1px solid ${C.divider}` }}>
              {nestTreeRows(rows).map(node => (
                <ChatTreeBranch
                  key={node.row.chat.id}
                  node={node}
                  isMobile={isMobile}
                  onToggleCollapse={toggleCollapse}
                  renderCard={(s, inset) => renderCard(s, inset, featureRoleFor(s.id, keyId, featuresIndex), true)}
                />
              ))}
            </div>
          )}
        </div>
      );
    }
    const layout = featureGroupChats(g.feature, g.sessions);
    const expanded = expandedFeatureGroups.has(g.feature.featureId);
    // Свёрнуто — наружу ключевой + живые/вставшие; раскрыто — все (ключевой первым)
    const exposedIds = expanded ? null : new Set(layout.exposed.map(c => c.id));
    const rows = buildGroupTree(g, exposedIds);
    const keyId = g.feature.keySessionId;
    const toggle = () => setExpandedFeatureGroups(prev => {
      const next = new Set(prev);
      if (next.has(g.feature!.featureId)) next.delete(g.feature!.featureId);
      else next.add(g.feature!.featureId);
      return next;
    });
    return (
      <div key={g.feature.featureId} style={{ marginBottom: GROUP_GAP, display: 'flow-root' }}>
        <FeatureGroupHeader
          feature={g.feature}
          expanded={expanded}
          onToggle={toggle}
          // Тело открывает страницу фичи (Ф2.3): вход из СПИСКА (fromChat: false) —
          // указатель возврата не пишется (крошка → ключевой чат/список, M3).
          onOpen={() => { if (g.feature) window.dispatchEvent(new CustomEvent('cc-open-feature', { detail: { featureId: g.feature.featureId, fromChatId: activeSessionId, fromChat: false } })); }}
          isMobile={isMobile}
          // «Кто · сколько» в статусе: персона-исполнитель (attention.personaId) + «сейчас»
          personaName={personaNameOf(g.feature)}
          now={nowMs}
          personaById={personaById}
        />
        <div style={{ marginLeft: 13, paddingLeft: 9, borderLeft: `1px solid ${C.divider}` }}>
          {nestTreeRows(rows).map(node => (
            <ChatTreeBranch
              key={node.row.chat.id}
              node={node}
              isMobile={isMobile}
              onToggleCollapse={toggleCollapse}
              // Роль и «ещё N» — по id САМОЙ строки (M3): ChatTreeBranch рекурсивно
              // передаёт renderCard потомкам, и замыкание роли корня лило её на всё
              // поддерево. inFeatureGroup=true — метку фичи в карточке не рисуем:
              // её уже несёт заголовок группы (n1)
              renderCard={(s, inset) => renderCard(s, inset, featureRoleFor(s.id, keyId, featuresIndex), true)}
            />
          ))}
          {!expanded && layout.hiddenCount > 0 && (
            <button
              type="button"
              onClick={toggle}
              style={{
                display: 'block', width: '100%', textAlign: 'left', border: 'none', cursor: 'pointer',
                background: 'transparent', color: C.textSecondary, fontFamily: FONT.sans,
                fontSize: FS.xs, padding: `${SP.xs}px ${SP.xs}px`, marginTop: 2,
              }}
            >
              ещё {layout.hiddenCount}
            </button>
          )}
          {/* F3.3: строка-ссылка на «чужую» группу (мультифичевый чат). Компонент,
              чтобы otherGroupLinks был в useMemo (не пересчитывался на каждом рендере). */}
          {expanded && (
            <OtherGroupLinks
              featureId={g.feature.featureId}
              featuresIndex={featuresIndex}
              groupSessions={g.sessions}
              sessions={sessions}
              isMobile={isMobile}
              onSelect={onSelect}
            />
          )}
        </div>
      </div>
    );
  };
  // Режим «Фичи» (F8): чипы со счётчиками, открытые группы, отфильтрованные по
  // выбранному чипу, секция «Закрытые · N · на втором плане» (свёрнута по умолчанию,
  // сама раскрывается на чипе «Закрытые»), «Без фичи» и три пустых состояния
  // (фич нет / все закрыты / под фильтр ничего не попало). Принадлежность фичи к чипу
  // фронт НЕ считает — бэк отдаёт chips[] (контракт «Контракт индекса фич — утверждён
  // (Александр, 18.10)»), фронт только фильтрует по chips[] и считает счётчики.
  const renderFeaturesMode = () => {
    if (!featureGroups) return null;
    const allFeatures = featuresIndex.features;
    const openFeatures = allFeatures.filter(f => f.bucket !== 'closed');
    const closedFeatures = allFeatures.filter(f => f.bucket === 'closed');
    // Фич нет вовсе (индекс пуст): спокойный EmptyState — но БЕЗ раннего return (M1):
    // ниже остаются обычные чаты без фичи (группа «Без фичи»), иначе список пустел
    const noFeatures = allFeatures.length === 0;

    // Мультивыбор чипов (OR): пусто = все открытые; иначе — открытые, чьи chips[]
    // содержат хотя бы один выбранный чип. Принадлежность к чипу фронт НЕ считает —
    // бэк отдаёт chips[] (контракт «Контракт индекса фич — утверждён (Александр, 18.10)»)
    const selectedChips = filters.featureChips;
    const shownOpenFeatures = selectedChips.length === 0
      ? openFeatures
      : openFeatures.filter(f => f.chips.some(c => selectedChips.includes(c)));
    const shownOpenIds = new Set(shownOpenFeatures.map(f => f.featureId));
    const openGroups = featureGroups.filter(g =>
      g.feature && g.feature.bucket !== 'closed' && shownOpenIds.has(g.feature.featureId));
    const noFeatureGroup = featureGroups.find(g => g.feature === null) ?? null;
    // Закрытые — в конце, по порядку бэка (OrderRuns), когда включён тумблер «Показывать
    // закрытые». Порядок — порядок featureGroups (не пересортировываем).
    const closedGroups = featureGroups.filter(g => g.feature && g.feature.bucket === 'closed');

    // Строка снимаемых чипов под шапкой: активные чипы + «и закрытые» + «Сбросить».
    // Видна, когда есть любой активный фильтр фич (чип или тумблер закрытых).
    // ui/Chip soft selected с onRemove (крестик) — ловушки <button> внутри снимает сам
    // компонент (ChipX: border/font/color inherit); «Сбросить» — ui/Button ghost без рамки.
    const hasFeatureFilter = selectedChips.length > 0 || filters.showClosed;
    const removableChips = hasFeatureFilter && (
      <div style={{
        display: 'flex', flexWrap: 'wrap', alignItems: 'center', gap: 6,
        padding: '0 12px 8px 14px',
      }}>
        <span style={{ fontSize: FS.sm, color: C.textMuted, fontFamily: FONT.sans }}>Показаны:</span>
        {selectedChips.map(c => (
          <Chip key={c} variant="soft" selected touch={isMobile}
            onRemove={() => patch({ featureChips: selectedChips.filter(x => x !== c) })}
            removeLabel={`Убрать фильтр «${FEATURE_CHIP_LABEL[c]}»`}
          >
            {FEATURE_CHIP_LABEL[c]}
          </Chip>
        ))}
        {filters.showClosed && (
          <Chip variant="soft" selected touch={isMobile}
            onRemove={() => patch({ showClosed: false })}
            removeLabel="Убрать «и закрытые»"
          >
            и закрытые
          </Chip>
        )}
        <Button variant="ghost" size="xs"
          style={{ color: C.accent, border: 'none', ...(isMobile ? { height: 40, minHeight: 40 } : null) }}
          onClick={() => patch({ featureChips: [], showClosed: false })}>
          Сбросить
        </Button>
      </div>
    );

    // Пустое: фич нет — спокойный EmptyState (рядом, а не вместо чатов). inline (M1′):
    // по содержимому, а не height:100% — иначе плашка тянулась на всю высоту панели,
    // а под ней (в «Без фичи») ещё стояли обычные чаты
    const noFeaturesState = noFeatures && (
      <EmptyState
        compact
        inline
        icon={<Sparkles size={20} strokeWidth={2} />}
        title="Фич пока нет"
        // m10: по макету .empty.sm — заголовок 14 (не 16) и КРУГЛАЯ иконка (R.full)
        titleSize={FS.md}
        iconRadius={R.full}
        subtitle="Фича появится здесь, когда оркестратор заведёт её эпиком в трекере. Тогда её чаты соберутся в одну группу."
      />
    );

    // «Смержена ДД.ММ» для спокойного состояния: последняя по МОМЕНТУ мержа закрытая
    // фича. Сортируем по времени (Date), а не строкой: выбор фичи не зависит от пояса,
    // а дату показываем ЛОКАЛЬНО (dateOnly, как везде в режиме фичи) — в МСК 23:30Z
    // это уже следующий день.
    const lastMerged = closedFeatures
      .map(f => f.summary?.mergedAt)
      .filter((d): d is string => !!d)
      .sort((a, b) => new Date(a).getTime() - new Date(b).getTime())
      .pop();
    const lastMergedDate = lastMerged ? dateOnly(lastMerged) : null;

    // Спокойное состояние: все закрыты (фичи ЕСТЬ, открытых нет). noFeatures отдельно:
    // там свой EmptyState «Фич пока нет», а не «Открытых фич нет»
    const calm = !noFeatures && openFeatures.length === 0 && (
      <div style={{
        display: 'flex', alignItems: 'center', gap: SP.sm,
        // Только вертикальные отступы: горизонтальный задаёт контейнер списка
        margin: `0 0 ${SP.sm}px`, padding: `${SP.sm}px`,
        borderRadius: R.lg, background: C.successBg, color: C.successText,
        fontSize: FS.sm, fontFamily: FONT.sans,
      }}>
        <CheckCircle2 size={14} strokeWidth={2} />
        <span>
          Открытых фич нет.
          {lastMergedDate && <> Последняя смержена {lastMergedDate}.</>}
        </span>
      </div>
    );

    // «Под фильтр ничего не попало»: открытые фичи есть, но ни одна не под выбранные чипы
    const noMatch = selectedChips.length > 0 && openFeatures.length > 0 && openGroups.length === 0 && (
      <EmptyState
        compact
        icon={<FilterX size={20} strokeWidth={2} />}
        title="Под фильтр ничего не попало"
        subtitle="Сейчас ни одна фича не подходит под выбранные состояния."
        action={
          <Button variant="ghost" size="sm" onClick={() => patch({ featureChips: [], showClosed: false })}>
            Показать открытые
          </Button>
        }
      />
    );

    return (
      <>
        {removableChips}
        {noFeaturesState}
        {calm}
        {openGroups.map(g => renderFeatureGroup(g))}
        {noMatch}
        {filters.showClosed && closedGroups.length > 0 && (
          <>
            {/* Заголовок секции «Закрытые · N · на втором плане» (таблица §2, макет А):
                строка БЕЗ шеврона — сворачивать больше нечего, видимость решает тумблер.
                N = нарисованные закрытые группы (единый источник, как в волне 1);
                подсказка справа 11/400, без капса. На 360 — minHeight 40. */}
            <div
              data-testid="closed-section-header"
              data-closed-count={closedGroups.length}
              style={{
                display: 'flex', alignItems: 'baseline', gap: 6,
                padding: '12px 6px 5px',
                ...(isMobile ? { minHeight: 40 } : null),
              }}
            >
              <span style={{
                fontSize: FS.xxs, fontWeight: 700, textTransform: 'uppercase',
                letterSpacing: '0.06em', color: C.textMuted, fontFamily: FONT.sans,
              }}>
                Закрытые · <span style={{ fontFamily: FONT.mono, letterSpacing: 0 }}>{closedGroups.length}</span>
              </span>
              <span style={{
                marginLeft: 'auto', fontSize: FS.xs, fontWeight: 400,
                textTransform: 'none', color: C.textMuted, fontFamily: FONT.sans,
              }}>
                на втором плане
              </span>
            </div>
            {closedGroups.map(g => renderFeatureGroup(g))}
          </>
        )}
        {noFeatureGroup && renderFeatureGroup(noFeatureGroup)}
      </>
    );
  };
  // leadingInset — место под контрол ветки дерева. На док стены чат отправляется
  // пунктом меню «На стену»: строки дерева держит dnd-kit (ChatTreeRow.useDraggable),
  // и нативный drag на стену глушил бы вложение — его здесь нет намеренно
  const renderCard = (s: Session, leadingInset = 0, featureRole?: string, inFeatureGroup?: boolean) => (
      <ChatCard
        key={s.id}
        session={s}
        leadingInset={leadingInset}
        featureRole={featureRole}
        inFeatureGroup={inFeatureGroup}
        isActive={activeSession?.id === s.id}
        isMobile={isMobile}
        fallbackName={`Чат #${numberById.get(s.id) ?? 1}`}
        online={online}
        hovered={hoveredId === s.id}
        workflowRunning={workflowRunningFor === s.id}
        uncommitted={dirtySessionIds?.has(s.id) ? 'own' : (dirtyBranchIds?.has(s.id) ? 'descendants' : undefined)}
        onSelect={() => onSelect(s)}
        onHover={h => { setHoveredId(h ? s.id : null); warmHover(h && online ? s : null); }}
        onDelete={() => setDeleteTarget(s)}
        tags={chatTagsSorted(s, registry).map(name => ({ name, color: tagColor(registry, name) }))}
        onAssignTags={online ? anchor => setTagMenu(prev => prev?.sessionId === s.id ? null : { sessionId: s.id, anchor }) : undefined}
        // Тема — у корня ветки: у вложенного чата пункта нет; офлайн — тоже нет
        onPickSubject={online && rootOf(s).id === s.id
          ? anchor => setSubjectMenu(prev => prev?.sessionId === s.id ? null : { sessionId: s.id, anchor })
          : undefined}
        subjectHint={subjectHint(s, subjects)}
        subjectTitle={subjectOf(s, subjects) ? `Тема: ${subjectOf(s, subjects)!.name}` : 'Выбрать тему'}
        onRename={online ? name => renameSession(s, name) : undefined}
        onAddToWall={onAddToWall ? () => onAddToWall(s) : undefined}
        onEdited={handleSessionUpdated}
        // Архив и его действия — только онлайн: офлайн-кэш отдал бы «успех» на
        // запрос, который до сервера не доехал. «Сохранить в заметки» видно лишь
        // у архивного чата (см. ChatCard), обычной карточке оно ничего не меняет
        onArchive={online ? archived => {
          void handleArchive(s, archived);
          // Возврат из архива открывает чат и выводит список из архивного вида
          if (!archived) leaveArchiveAndOpen(s);
        } : undefined}
        onSaveAsNote={online && notesOn ? () => handleSaveAsNote(s) : undefined}
        swipeOpen={openSwipeId === s.id}
        onSwipeToggle={open => setOpenSwipeId(open ? s.id : null)}
      />
  );

  // Заголовок секции режима «Теги»: цветовая точка, имя, счётчик чатов, кнопки
  // порядка ▲▼ (только у реестровых тегов; сироты и «Без тегов» неупорядочены)
  const renderTagSectionHeader = (g: TagChatGroup) => {
    const rt = g.registryTag;
    const idx = rt ? registry.findIndex(t => t.name.toLowerCase() === rt.name.toLowerCase()) : -1;
    const dot = tagColor(registry, g.title);
    return (
      <ChatGroupHeader
        title={g.title}
        count={g.items.length}
        marker={
          <span style={{
            width: 8, height: 8, borderRadius: '50%', flexShrink: 0,
            background: g.tag ? (dot ?? C.accent) : C.textMuted,
          }} />
        }
        actions={rt && online ? (
          <span style={{ display: 'flex', gap: 2, flexShrink: 0 }}>
            <IconButton size="xs" onClick={() => moveTag(rt, -1)} disabled={idx <= 0} title={`Тег «${rt.name}» выше`}>
              <ChevronUp size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
            </IconButton>
            <IconButton size="xs" onClick={() => moveTag(rt, 1)} disabled={idx < 0 || idx >= registry.length - 1} title={`Тег «${rt.name}» ниже`}>
              <ChevronDown size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
            </IconButton>
          </span>
        ) : undefined}
      />
    );
  };

  return (
    // Внешняя обёртка — flex-участник, а не height:100%. На планшете в PanelShell
    // одиночная панель идёт с fill=false (flex: '0 1 auto'), и родитель растёт
    // по контенту: height:100% ребёнка = высоте контента, у скролл-контейнера
    // flex:1 нет свободного места, и overflowY:auto не срабатывает.
    <div style={{ display: 'flex', flexDirection: 'column', flex: 1, minHeight: 0 }}>
      <ChatListToolbar
        onNew={() => { void createNew(); }}
        hideNew={!online}
        sessions={sessions}
        filters={groupBy === filters.groupBy ? filters : { ...filters, groupBy }}
        patch={patch}
        allPersonas={personas}
        hiddenCount={hiddenCount}
        isMobile={isMobile}
        groupByOptions={groupByOptions}
        features={featuresIndex.features}
      />
      {subjectsMode && <SubjectRunBar run={subjectRun} />}

      {/* Сверху отступ ужимается только под разделитель группы («Сегодня»): свой
          верхний padding у него есть, и вместе с общим набегало 18px пустоты под
          шапкой. Без группировки список начинается сразу карточкой — ей нужен
          обычный отступ, иначе она липнет к заголовку. */}
      <div
        // Скролл закрывает раскрытую свайпом карточку (механика ChatList)
        onScroll={() => { if (openSwipeId !== null) setOpenSwipeId(null); }}
        // Фон явный — по той же причине, что в ChatList: прозрачные строки и липкая
        // подпись группы держатся на том, что под ними именно bgWhite
        // Снизу — то же поле, что сбоку (SP.sm), считая от края последней карточки:
        // её marginBottom и хвостовой отступ последней группы уже лежат под ней, и
        // поверх полного padding'а они давали низ вдвое шире боковых полей
        style={{
          flex: 1, overflowY: 'auto', background: C.bgWhite,
          padding: `${groupBy === 'none' ? SP.sm : SP.xxs}px ${SP.sm}px ${SP.sm - listTail}px`,
        }}>
        {/* Все чаты проекта в архиве — то же состояние, что у глобального списка:
            иное пустое состояние молчит (чаты есть), и панель без этого блока
            оставалась бы белой дырой. Кнопки создания нет — «Новый» в тулбаре панели */}
        {loaded && !filters.archivedOnly && sessions.length > 0 && scoped.length === 0 && (
          <div style={{ minHeight: '100%', display: 'flex', flexDirection: 'column', justifyContent: 'center', boxSizing: 'border-box' }}>
            <EmptyState
              compact
              icon={<Archive size={20} strokeWidth={2} />}
              title="Все чаты в архиве"
              subtitle="Список пуст: живых чатов нет, архив не считается."
              action={
                <Button variant="ghost" size="sm" style={{ whiteSpace: 'nowrap' }} onClick={() => patch({ archivedOnly: true })}>
                  Открыть архив
                </Button>
              }
            />
          </div>
        )}
        {/* Режим архива, а убранных чатов нет — рассказываем, откуда они берутся и
            что архив ничего не теряет. Условие по scoped, а не по sessions: живые чаты
            в scoped не попадают, и при непустом проекте с пустым архивом проверка
            sessions.length === 0 не срабатывала бы — панель оставалась пустой */}
        {loaded && filters.archivedOnly && scoped.length === 0 && (
          <EmptyState
            compact
            icon={<Archive size={20} strokeWidth={2} />}
            title={ARCHIVE_EMPTY_TITLE}
            subtitle={ARCHIVE_EMPTY_SUBTITLE}
          />
        )}
        {/* Чатов в проекте нет вовсе (список уже приехал) — не голая панель, а empty-state.
            Условие по loaded, а не по длине: пустой стартовый массив ещё не значит «чатов
            нет», и empty мигнул бы до загрузки. Кнопки создания тут нет — «Новый» живёт
            в тулбаре панели сверху, дублировать его в empty незачем. */}
        {loaded && !filters.archivedOnly && sessions.length === 0 && (
          <EmptyState
            compact
            icon={<MessageCircle size={20} strokeWidth={2} />}
            title="Чатов пока нет"
            subtitle="Начните первый чат по этому проекту."
          />
        )}
        {tree.rows.length === 0 && scoped.length > 0 && (
          <EmptyState
            compact
            icon={<FilterX size={20} strokeWidth={2} />}
            title="Ничего не нашлось"
            subtitle={buildHiddenReason(scoped.length, filters.search)}
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
          <ChatGroupingDnd chats={sessions} isMobile={isMobile} onEdited={handleSessionUpdated}>
            {featureGroups ? (
              renderFeaturesMode()
            ) : subjectGroups ? (
              subjectGroups.map(g => renderSubjectSection(g,
                g.items.map(root => nestTreeRows(segByRootId.get(root.id) ?? []).map(node => (
                  <ChatTreeBranch key={node.row.chat.id} node={node} isMobile={isMobile} onToggleCollapse={toggleCollapse} renderCard={renderCard} />
                ))),
              ))
            ) : tagGroups ? (
              tagGroups.map(g => (
                <div key={g.tag ?? '__untagged__'} style={{ marginBottom: GROUP_GAP, display: 'flow-root' }}>
                  {renderTagSectionHeader(g)}
                  {g.items.map(root => nestTreeRows(segByRootId.get(root.id) ?? []).map(node => (
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
                <div key={g.title} style={{ marginBottom: GROUP_GAP, display: 'flow-root' }}>
                  <ListDateDivider title={g.title} plain />
                  {g.items.map(root => nestTreeRows(segByRootId.get(root.id) ?? []).map(node => (
                    <ChatTreeBranch key={node.row.chat.id} node={node} isMobile={isMobile} onToggleCollapse={toggleCollapse} renderCard={renderCard} />
                  )))}
                </div>
              ))
            )}
          </ChatGroupingDnd>
        {archiveRest > 0 && (
          <div style={{ display: 'flex', justifyContent: 'center', padding: `${SP.xs + SP.xxs}px 0 0` }}>
            <Button variant="ghost" size="sm" onClick={() => setArchiveLimit(n => n + ARCHIVE_PAGE)}>
              Показать ещё {Math.min(archiveRest, ARCHIVE_PAGE)} из {archiveRest}
            </Button>
          </div>
        )}
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

      {/* Выбор темы чата: десктоп — меню по якорю, мобила — шторка */}
      {subjectMenu && subjectTarget && (
        <SubjectPickMenu
          anchor={subjectMenu.anchor}
          isMobile={isMobile}
          chat={subjectTarget}
          registry={subjects}
          onClose={() => setSubjectMenu(null)}
          onPick={id => { setSubjectMenu(null); moveChatSubject(subjectTarget, { subjectId: id }); }}
          onCreate={name => { setSubjectMenu(null); createSubjectFor(subjectTarget, name); }}
          onRelease={() => { setSubjectMenu(null); moveChatSubject(subjectTarget, { release: true }); }}
        />
      )}
      {renameSubject && (
        <SubjectRenameDialog
          subject={renameSubject}
          registry={subjects}
          onSave={name => doRenameSubject(renameSubject, name)}
          onClose={() => setRenameSubject(null)}
        />
      )}
      {deleteSubject && (
        <SubjectDeleteDialog
          subject={deleteSubject}
          chats={sessions.filter(c => c.subjectId === deleteSubject.id && rootOf(c).id === c.id).length}
          onConfirm={() => doDeleteSubject(deleteSubject)}
          onClose={() => setDeleteSubject(null)}
        />
      )}

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
