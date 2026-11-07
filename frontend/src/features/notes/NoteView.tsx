import { useEffect, useRef, useState, lazy, Suspense } from 'react';
import { AlertTriangle, BookMarked, BookOpen, Check, CheckCircle2, Copy, FileText, MessageCircle, Undo2, X } from 'lucide-react';
import type { NoteDetail, NoteReply, Persona } from '../../types';
import {
  api, bumpNotes, useNotesVersion, isFavorite, withFavoriteTag, withoutFavoriteTag,
  C, FONT, ISLAND, R, TB,
  MarkdownViewer, stripFrontmatter, BackButton, ConfirmDialog, IconButton, Modal, WaitingIndicator,
  useAiJob, runAiJob, patchAiJobResult, resetAiJob, ICON_SIZE, ICON_STROKE,
  tbBtnPrimary, tbBtnGhost, useNotes, useOnline, OfflineError, useNow,
  getNoteForView, saveNoteOffline, deleteNoteOffline, offlineResolve,
  showToast, openChatById, beginAiBusy, endAiBusy,
  registerCopyDoc, copyMarkdown, copyRenderedHtml,
  ensurePersonasLoaded, personaLabel, usePersonas, NO_AUTOFILL,
  FLAGS, useFeature, Notice, requestOpenWikiPage, transferWikiNote, WikiError,
} from 'aihome_shell/kit';
import type { NoteSource } from '../../types';
// Конфликт сохранения (S2c-F3, п.28): типизированный 409 заметки и тот же диалог с тем же
// диффом, что у файлов (S2c-F2b) — своих текстов и своей формы здесь нет
import { NoteConflictError } from './api';
import { conflictCopyName } from '../../lib/conflictCopyName';
import { lineDiff } from '../../lib/lineDiff';
import { SaveConflictDialog } from '../../components/SaveConflictDialog';
import { useAllProjects } from '../projects/useAllProjects';
import { canWriteContent, type ContentDoor } from '../projects/members/contentRights';
import { NoteConnections } from './NoteConnections';
import { NoteTasksSection } from './NoteTasksSection';
import { useSphereOf } from '../../lib/useSphereOf';
import { requestNoteIngest } from './WikiChatActions';
import { DocCommentedMarkdown, PersonaAssignMenu, filterAssignablePersonas } from './DocComments';
import {
  SourceBadge,
  IconTrash, IconLink, IconSparkle, IconFolder, IconFolderMove, IconStar,
} from './shared';

// CodeMirror тяжёлый — редактор грузим лениво, только при входе в правку
const NoteEditor = lazy(() => import('./NoteEditor').then(m => ({ default: m.NoteEditor })));

// Конфликт сохранения заметки (S2c-F3, п.28): тело последнего 409 note_changed и свой текст
// на момент попытки — та же форма, что в FileViewer (S2c-F2b). again — заметку изменили ещё
// раз после «Заменить своей»; copyError — копия не создалась (диалог остаётся); mineAt —
// время своей версии для варианта без диффа (оно одно: момент попытки сохранения).
interface SaveConflictState {
  error: NoteConflictError;
  mine: string;
  // Заголовок черновика на момент попытки (доработка S2c-F3): «Заменить своей» пишет его по
  // тому же правилу, что save(), — то, что человек набрал, не теряется ни на одном действии диалога.
  draftTitle: string;
  again: boolean;
  copyError: boolean;
  busy: boolean;
  mineAt: string;
}

// Просмотр и правка одной заметки; связи (backlinks/исходящие/упоминания/граф) —
// в правом сайдбаре на десктопе, снизу на мобильном.
export function NoteView({ noteId, existingTitles, onWikilink, onAskClaude, onSelectNote, onDeleted, onTag, isMobile, connectionsBelow, onBack, extraToolbar, hero, onOpenFileRef }: {
  noteId: string;
  existingTitles: Set<string>;
  onWikilink: (target: string) => void;
  onAskClaude?: (note: NoteDetail) => void;
  onSelectNote: (id: string) => void;
  onDeleted: () => void;
  onTag?: (tag: string) => void;
  isMobile?: boolean;
  // Планшет/узкий десктоп: связи под контентом (а не правым сайдбаром), но
  // раскладка остаётся десктопной (список слева, без стрелки «назад»).
  connectionsBelow?: boolean;
  // Мобайл: стрелка «назад» в тулбаре (и клик по заголовку) — как у файлов/чатов
  onBack?: () => void;
  // Дополнительные кнопки справа в тулбаре (закрыть/fullscreen при встраивании в файлы)
  extraToolbar?: React.ReactNode;
  // Стиль Islands (раздел «Заметки», десктоп): шапка — заголовок раздела прямо
  // на холсте (как в календаре/чатах), тело заметки — карточка-остров
  hero?: boolean;
  // Открыть привязанный файл проекта (чип file: в тулбаре). Без колбэка (раздел
  // хаба — там центр не открывает файлы проектов) чип информационный, не кликается.
  onOpenFileRef?: (path: string) => void;
}) {
  const version = useNotesVersion();
  const online = useOnline();
  // «Разобрать в базу» (S7b) — за флагом llm-wiki; requestNoteIngest никогда не бросает,
  // поэтому экрану нужен только busy для кнопки и текст для тоста
  const wikiOn = useFeature(FLAGS.llmWiki);
  // «Перенести в вики» (F5b) — только при включённом llm-wiki.
  const [ingestBusy, setIngestBusy] = useState(false);
  // Контейнер панели комментариев (портал из DocCommentedMarkdown). Плавающий сайдбар
  // связей справа от текста заметки — как у документов (см. docSide в FileViewer):
  // aside с float:right внутри обёртки display:flow-root. Свёрнутые секции связей
  // стягиваются в компактные строки, а текст заметки обтекает их слева — место не
  // пропадает в пустой колонке. На мобильном/узком (connectionsBelow) связи уезжают
  // под контент, сайдбара нет → panelTarget не нужен.
  const [panelEl, setPanelEl] = useState<HTMLElement | null>(null);
  const [note, setNote] = useState<NoteDetail | null>(null);
  // Право записи по роли в проекте источника (S2c-F4e): у 'personal' проекта нет — всё
  // разрешено; пока кэш проектов не загружен — тоже как раньше. Закрытое прячем, не отключаем.
  const allProjects = useAllProjects();
  // Проект-источник заметки ('personal' — проекта нет → undefined → все двери открыты);
  // он же — проп права 'note.tasks' для NoteTasksSection (S2c-F4f)
  const sourceProject = !note || note.source === 'personal' ? undefined : allProjects.find(p => p.id === note.source);
  const can = (door: ContentDoor) => canWriteContent(sourceProject, door);
  const [loading, setLoading] = useState(true);
  const [editing, setEditing] = useState(false);
  const [draftTitle, setDraftTitle] = useState('');
  const [draftBody, setDraftBody] = useState('');
  const [saving, setSaving] = useState(false);
  const [saveConflict, setSaveConflict] = useState<SaveConflictState | null>(null);
  const editingRef = useRef(false);
  useEffect(() => { editingRef.current = editing; }, [editing]);
  // Два тега (К8/S2c-F3): noteEtag — прочитанной версии (та, что показана сейчас),
  // draftEtag — той версии, на которой стоит черновик редактора. Сохранение идёт по
  // второму: если заметку изменили под нами, черновик честно упрётся в 409, а не ляжет
  // поверх чужой правки молча.
  const noteEtag = useRef<string | null>(null);
  const draftEtag = useRef<string | null>(null);
  // Живые часы для бейджа срока истечения: Date.now() в рендере нечистый (purity),
  // а без тика отсчёт замирал до случайного перерендера. Интервал нужен только
  // когда бейдж реально виден.
  const now = useNow(30_000, !!note?.expiresAt && !editing);
  // Фидбек кнопки «Скопировать» + корень отрендеренного markdown (для копии с форматированием)
  const [copied, setCopied] = useState(false);
  const rootRef = useRef<HTMLDivElement>(null);

  // Ctrl+C без выделения: отдаём исходник заметки (см. selectionScope)
  const noteRef = useRef<NoteDetail | null>(null);
  useEffect(() => { noteRef.current = note; }, [note]);
  // Регистрируемся после появления заметки: до неё root ещё не отрендерен (ранние return)
  const hasNote = note != null;
  useEffect(() => {
    const el = rootRef.current;
    if (!el) return;
    return registerCopyDoc(el, () => noteRef.current?.content ?? null);
  }, [hasNote]);

  // Клик — скопировать исходный markdown заметки; Shift+клик — с форматированием
  const copyNote = async (e: React.MouseEvent) => {
    if (!noteRef.current) return;
    const rendered = e.shiftKey
      ? rootRef.current?.querySelector<HTMLElement>('[data-selection-scope]')
      : null;
    const ok = rendered
      ? await copyRenderedHtml(rendered)
      : await copyMarkdown(noteRef.current.content);
    if (ok) { setCopied(true); setTimeout(() => setCopied(false), 1500); }
  };

  // Заявка «Разобрать в базу» (S7b0b): исход и любой отказ (409 packages_limit, 404)
  // становятся текстом тоста — экран не падает; повторный клик пока идёт — игнорируется
  const runIngest = async () => {
    if (ingestBusy) return;
    setIngestBusy(true);
    const text = await requestNoteIngest(noteId);
    showToast('Разобрать в базу', text, 'info');
    setIngestBusy(false);
  };

  // «Перенести в вики» (F5b): подтверждение → POST переноса → заметка становится страницей
  // в доме заметки, а сама заменяется ссылкой на неё (ручка делает обе записи сразу).
  // Конкурентность (урок S5b/S5e, образец doRetract в WikiChatActions): пока идёт — второй
  // клик ничего не шлёт; подтверждение закрыто по исходу. Ответ применяется только к той
  // заметке, для которой начался: после смены заметки он отбрасывается по noteId. Отказ
  // наружу не выходит — любой становится текстом Notice, кнопка остаётся.
  const [transferConfirm, setTransferConfirm] = useState(false);
  const [transferBusy, setTransferBusy] = useState(false);
  const [transferFlash, setTransferFlash] = useState<{ ok: boolean; text: string } | null>(null);
  const openNoteIdRef = useRef(noteId);
  useEffect(() => {
    // Смена заметки: прежний запрос стал чужим (его ответ отбрасывается по noteId), а его
    // подтверждение и плашка не переходят в новую заметку
    openNoteIdRef.current = noteId;
    // eslint-disable-next-line react-hooks/set-state-in-effect -- сброс состояния переноса прежней заметки при смене заметки
    { setTransferConfirm(false); setTransferBusy(false); setTransferFlash(null); }
  }, [noteId]);
  const doTransfer = async () => {
    if (transferBusy) return;
    const forNoteId = noteId;
    setTransferBusy(true);
    // Исход переноса определяет ТОЛЬКО ответ сервера: действия после успеха — вне этого try
    // (свой защитный catch ниже). Иначе баг в приёмнике 'cc-open-note' показал бы состоявшийся
    // на сервере перенос как «Не удалось…», а повтор дал бы 409 wiki_note_transferred.
    let openPage: { section: string; path: string } | null = null;
    let failure: unknown = null;
    try {
      const res = await transferWikiNote(forNoteId);
      openPage = { section: res.section, path: res.page };
    } catch (e) {
      failure = e;
    }
    // Заметка сменилась, пока запрос шёл: плашка и страница — прежней заметки,
    // ответ отбрасывается по noteId; состояние новой заметки (её возможный перенос) не трогаем
    if (forNoteId !== openNoteIdRef.current) return;
    setTransferConfirm(false);
    if (openPage) {
      setTransferFlash({ ok: true, text: TRANSFER_TEXT.done });
      setTransferBusy(false);
      // На сервере заметка заменена ссылкой — перечитываем список и саму заметку
      // Открыть созданную страницу: запрос — в стор вики (OPEN-1), смонтированный раздел
      // «Заметки» переключится на «Вики» и откроет её; из встраивания в файлы к разделу
      // ведёт тот же канал 'cc-open-note', что у «[[wikilink]]»
      try {
        bumpNotes();
        requestOpenWikiPage(openPage);
        window.dispatchEvent(new Event('cc-open-note'));
      } catch (e) {
        // Приёмник 'cc-open-note' упал: перенос уже состоялся — плашка остаётся «Перенесено».
        // Молча не глотаем: сбой слушателя виден в логе (образец: notesOffline.putOpOrWarn)
        console.warn('[notes] приёмник cc-open-note упал:', e);
      }
    } else {
      setTransferFlash({ ok: false, text: transferFailText(failure) });
      setTransferBusy(false);
    }
  };

  useEffect(() => {
    let alive = true;
    // eslint-disable-next-line react-hooks/set-state-in-effect -- индикатор загрузки перед перечитыванием заметки
    setLoading(true);
    // Читаем через офлайн-слой (черновик/кэш офлайн, сеть онлайн)
    getNoteForView(noteId)
      .then(n => {
        if (!alive) return;
        setNote(n);
        noteEtag.current = n.etag ?? null;
        // Вне правки показанная версия и есть база следующей правки. В режиме правки базу
        // черновика НЕ двигаем: событие notes_changed обновляет просмотр, но не текст,
        // который человек правит, и не версию, против которой он пишет (п.28)
        if (!editingRef.current) { setDraftTitle(n.title); setDraftBody(n.content); draftEtag.current = n.etag ?? null; }
      })
      .catch(() => { if (alive) setNote(null); })
      .finally(() => { if (alive) setLoading(false); });
    return () => { alive = false; };
    // Перечитываем при смене заметки и при realtime-изменении (version)
  }, [noteId, version]);

  // Черновик начинается с показанной версии — её тег и становится базой сохранения
  const startEdit = () => {
    if (!note || !can('note.edit')) return;
    setDraftTitle(note.title); setDraftBody(note.content);
    draftEtag.current = noteEtag.current;
    setEditing(true);
  };

  // Открыть чат, из которого создана заметка: проектный уедет в воркспейс своего
  // проекта, чат вне проектов — в раздел «Чаты» (решает общий хелпер)
  const openSession = (sessionId: string) =>
    openChatById(sessionId, { missingTitle: 'Чат заметки', missingBody: 'Чат не найден — возможно, он удалён' });

  // Резолв вики-имени для hover-preview и embed ![[…]] (фрагмент по якорю приоритетен)
  const resolveNote = async (name: string, anchor?: string) => {
    try {
      const r = await api.notes.resolve(name, anchor);
      return { title: r.note.title, content: r.fragment ?? r.note.content };
    } catch {
      // Офлайн — резолвим по кэшированному контенту (title-match)
      return offlineResolve(name, anchor);
    }
  };

  // ✨ AI-помощь: предложение связей, тегов, конспект дня. Статус/результат — в
  // aiJobStore по ключу заметки (переживает уход со страницы «Заметки» и обратно).
  const linksKey = `notes:${note?.id ?? '_none'}:suggest-links`;
  const linksJob = useAiJob<{ title: string; why: string }[]>(linksKey);
  const tagsKey = `notes:${note?.id ?? '_none'}:suggest-tags`;
  const tagsJob = useAiJob<string[]>(tagsKey);
  // Ручное добавление тега (инлайн-инпут у чипов)
  const [addingTag, setAddingTag] = useState(false);
  const [newTag, setNewTag] = useState('');
  const isDaily = note?.source === 'personal' && note.path.startsWith('Journal/');
  const dailyDay = note && isDaily ? note.path.replace(/^Journal\//, '').replace(/\.md$/, '') : null;
  const dailyKey = `notes:daily-summary:${dailyDay ?? '_none'}`;
  const dailyJob = useAiJob<NoteDetail>(dailyKey);

  // Комментарий к документу (флаг doc-annotations): ответы треда + переходы
  const ann = note?.annotation ?? null;
  const [annReplies, setAnnReplies] = useState<NoteReply[] | null>(null);
  const [annReplyDraft, setAnnReplyDraft] = useState('');
  const [annReplySending, setAnnReplySending] = useState(false);
  useEffect(() => {
    // eslint-disable-next-line react-hooks/set-state-in-effect -- сброс устаревших ответов перед загрузкой ответов аннотации
    if (!note || !ann || ann.isReply) { setAnnReplies(null); return; }
    let alive = true;
    api.notes.replies(note.id).then(r => { if (alive) setAnnReplies(r); }).catch(() => {});
    return () => { alive = false; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [note?.id, version]);
  const sendAnnReply = async () => {
    if (!note || !annReplyDraft.trim() || !can('note.comment')) return;
    setAnnReplySending(true);
    try {
      await api.notes.reply(note.id, annReplyDraft.trim());
      setAnnReplyDraft('');
      setAnnReplies(await api.notes.replies(note.id));
      bumpNotes();
    } catch { /* realtime подтянет */ }
    finally { setAnnReplySending(false); }
  };
  // «Перейти к месту»: проектный документ → файл проекта; личный vault → заметка.
  // Для ответа в треде та же кнопка ведёт к корневому комментарию (docPath = его файл).
  // Якорь для скролла к блоку передаётся через sessionStorage — его подберёт
  // DocCommentedMarkdown в открывшемся документе.
  const findDocNote = () =>
    ann && allNotes.find(n => n.source === ann.docScope &&
      (n.path.toLowerCase() === ann.docPath.toLowerCase() ||
       ('notes/' + n.path).toLowerCase() === ann.docPath.toLowerCase()));
  const gotoAnnTarget = () => {
    if (!note || !ann) return;
    if (!ann.isReply) sessionStorage.setItem('cc_pending_annotation', note.id);
    if (ann.docScope === 'personal' || ann.isReply) {
      const target = findDocNote();
      if (target) onSelectNote(target.id);
      return;
    }
    window.dispatchEvent(new CustomEvent('cc-open-url', {
      detail: { url: `#/project/${ann.docScope}/file/${encodeURIComponent(ann.docPath)}` },
    }));
  };

  // «Обработать» = создать задачу из комментария (лично или с персоной-исполнителем).
  // Меню персон — общее с панелью комментариев: команда проекта + глобальные,
  // карточки с аватарами, портал с клэмпом по экрану.
  const personas = usePersonas();
  const sphereOf = useSphereOf();
  const assignable = ann ? filterAssignablePersonas(personas, ann.docScope, sphereOf) : [];
  const [assignAnchor, setAssignAnchor] = useState<HTMLElement | null>(null);
  useEffect(() => { if (ann && !ann.isReply) void ensurePersonasLoaded(); }, [ann]);
  const createTaskFromComment = async (persona?: Persona) => {
    if (!note || !ann || !can('note.tasks')) return;
    setAssignAnchor(null);
    try {
      await api.tasks.create(ann.docScope === 'personal' ? null : ann.docScope, {
        title: `Обработать комментарий: ${note.title}`,
        description: [
          `Комментарий к документу \`${ann.docPath}\`${ann.anchorHeading ? ` (${ann.anchorHeading})` : ''}:`,
          '',
          ann.anchorQuote ? `> ${ann.anchorQuote}` : '',
          '',
          `Заметка-комментарий: [${note.title}](#/notes/${encodeURIComponent(note.id)}). ` +
          'После обработки пометь комментарий решённым (notes_set_status).',
        ].filter(l => l !== '').join('\n'),
        ...(persona ? { assignee: 'claude' as const, personaId: persona.id } : {}),
      });
      showToast('Задачи', persona
        ? `Задача создана и поручена: ${personaLabel(persona)}`
        : 'Задача из комментария создана');
    } catch { showToast('Задачи', 'Не удалось создать задачу'); }
  };

  const suggestLinks = () => {
    if (!note) return;
    runAiJob(linksKey, () => api.notes.suggestLinks(note.id));
  };
  const suggestTags = () => {
    if (!note) return;
    runAiJob(tagsKey, () => api.notes.suggestTags(note.id));
  };
  // Принять связь: секция «## Связанное» с [[…]] в конце заметки
  const acceptLink = async (title: string) => {
    if (!note || !can('note.edit')) return;
    const hasSection = /(^|\n)## Связанное\s*\n/.test(note.content);
    const content = hasSection
      ? note.content.trimEnd() + `\n- [[${title}]]\n`
      : note.content.trimEnd() + `\n\n## Связанное\n\n- [[${title}]]\n`;
    const updated = await api.notes.update(note.id, { content });
    setNote(updated);
    patchAiJobResult<{ title: string; why: string }[]>(linksKey, prev => prev.filter(l => l.title !== title));
    bumpNotes();
  };
  // Избранное — тот же тег в тексте: тело уже загружено, дочитывать не надо
  const toggleFav = async () => {
    if (!note || !can('note.edit')) return;
    const content = isFavorite(note.tags) ? withoutFavoriteTag(note.content) : withFavoriteTag(note.content);
    setNote(await api.notes.update(note.id, { content }));
    bumpNotes();
  };
  // Принять тег (ИИ-предложение или ручной ввод): inline #тег в конец заметки
  const acceptTag = async (tagRaw: string) => {
    if (!note || !can('note.edit')) return;
    const tag = tagRaw.trim().replace(/^#+/, '').replace(/\s+/g, '-');
    if (!tag || note.tags.some(t => t.toLowerCase() === tag.toLowerCase())) return;
    const updated = await api.notes.update(note.id, { content: note.content.trimEnd() + ` #${tag}\n` });
    setNote(updated);
    patchAiJobResult<string[]>(tagsKey, prev => prev.filter(t => t !== tagRaw));
    bumpNotes();
  };
  // Конспект дня (только в daily-заметке)
  const makeDailySummary = () => {
    if (!note || !dailyDay) return;
    runAiJob(dailyKey, () => api.notes.dailySummary(dailyDay));
  };
  useEffect(() => {
    if (dailyJob.status === 'done' && dailyJob.result) {
      // eslint-disable-next-line react-hooks/set-state-in-effect -- применение результата AI-задачи: замена заметки по завершении
      setNote(dailyJob.result);
      bumpNotes();
      resetAiJob(dailyKey);
    } else if (dailyJob.status === 'error') {
      showToast('Конспект дня', dailyJob.error ?? 'Не удалось собрать конспект дня', 'info');
      resetAiJob(dailyKey);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [dailyJob.status]);

  // AI-хаб: контекстные действия из палитры делегируются сюда — над открытой заметкой,
  // переиспользуя те же обработчики, что и ✨-кнопки тулбара.
  useEffect(() => {
    const onRun = (e: Event) => {
      const action = (e as CustomEvent<{ action?: string }>).detail?.action;
      if (!action || !note) return;
      // AI-действия пишут в заметку (теги/связи/заголовок/оглавление/перевод/конспект) — дверь note.edit
      if (action !== 'note.ask' && !can('note.edit')) return;
      if (action === 'note.links') suggestLinks();
      else if (action === 'note.tags') suggestTags();
      else if (action === 'note.ask') onAskClaude?.(note);
      else if (action === 'note.daily') {
        if (isDaily) makeDailySummary();
        else showToast('Конспект дня', 'Доступно только в дневниковой заметке (раздел «Заметки» → сегодня)', 'info');
      }
      else if (action === 'note.title') {
        void (async () => {
          beginAiBusy();
          try {
            const { title } = await api.notes.suggestTitle(note.id);
            if (!title) { showToast('Заголовок', 'Не удалось придумать заголовок', 'info'); return; }
            const updated = await api.notes.update(note.id, { title });
            bumpNotes();
            // Переименование меняет id заметки (id = путь файла) — переключаемся на новый,
            // иначе навигация останется на старом id и покажет «заметка не найдена».
            if (updated.id !== note.id) onSelectNote(updated.id);
            else setNote(updated);
            showToast('Заголовок обновлён', title, 'info');
          } catch { showToast('Ошибка', 'Не удалось придумать заголовок', 'info'); }
          finally { endAiBusy(); }
        })();
      }
      else if (action === 'note.toc' || action === 'note.translate') {
        const isToc = action === 'note.toc';
        void (async () => {
          beginAiBusy();
          try {
            const updated = isToc ? await api.notes.toc(note.id) : await api.notes.translate(note.id);
            bumpNotes();
            // id мог смениться при обновлении — переключаемся на новый
            if (updated.id !== note.id) onSelectNote(updated.id);
            else setNote(updated);
            showToast(isToc ? 'Оглавление собрано' : 'Перевод добавлен', updated.title, 'info');
          } catch {
            showToast('Ошибка', isToc ? 'Не удалось собрать оглавление' : 'Не удалось перевести заметку', 'info');
          } finally { endAiBusy(); }
        })();
      }
    };
    window.addEventListener('cc-ai-run', onRun);
    return () => window.removeEventListener('cc-ai-run', onRun);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [note, isDaily]);

  const save = async () => {
    if (!note) return;
    setSaving(true);
    try {
      // If-Match — только когда известна версия, на которой стоит черновик. Без него запрос
      // ровно прежний: сервер 409 по конфликту не отдаёт вовсе, диалога нет (п.28).
      const updated = await api.notes.update(note.id, {
        // Переименование — только онлайн (офлайн title не меняем)
        title: draftTitle !== note.title ? draftTitle : undefined,
        content: draftBody,
      }, draftEtag.current ?? undefined);
      // 200 принёс новый ETag (NotesController.Update): следующая правка идёт по нему
      noteEtag.current = updated.etag ?? null;
      draftEtag.current = updated.etag ?? null;
      setEditing(false);
      bumpNotes();
      // id мог смениться при переименовании — переключаемся на новый
      if (updated.id !== note.id) onSelectNote(updated.id);
      else setNote(updated);
    } catch (e) {
      // Из правки не выходим: несохранённые правки должны остаться (как в FileViewer)
      if (e instanceof NoteConflictError) { openSaveConflict(e, draftBody, draftTitle); return; }
      if (e instanceof OfflineError) {
        // Офлайн: правка остаётся локальной и в очереди несёт свою базу — при синхронизации
        // она пишется обычным путём по If-Match, а не поверх чужой правки молча (notesOffline)
        await saveNoteOffline(note.id, { content: draftBody });
        setEditing(false);
        setNote({ ...note, content: draftBody });
        bumpNotes();
      } else throw e;
    } finally {
      setSaving(false);
    }
  };

  // ——— Конфликт сохранения (S2c-F3, п.28) ———
  // Вид и тексты держит SaveConflictDialog (S2c-F1); здесь — данные конфликта и три действия.
  // Диалог закрывается, когда выбор исполнен; при повторном 409 «Заменить своей» он остаётся.
  // По событию notes_changed диалог НЕ открывается: человек мог ещё не закончить правку.

  // Имя автора копии — то же, чем приложение называет пользователя (App держит cc_display_name
  // по ответу /auth/me). Имени нет — суффикс «без имени» (conflictCopyName).
  const copyAuthorName = () => {
    try { return localStorage.getItem('cc_display_name') || ''; } catch { return ''; }
  };

  const openSaveConflict = (e: NoteConflictError, mine: string, title: string) => {
    setSaveConflict({
      error: e, mine, draftTitle: title, again: false, copyError: false, busy: false,
      // Другой даты у своей версии нет: это тот текст, который только что не записался
      mineAt: relTime(new Date().toISOString()),
    });
  };

  // «Сохранить мою копией» (primary): заметка-копия рядом в том же источнике и папке,
  // оригинал не тронут. Заметка — всегда текст, поэтому дифф читается (без варианта draw.io)
  const conflictSaveCopy = async () => {
    const c = saveConflict;
    if (!c || c.busy || !note) return;
    setSaveConflict({ ...c, busy: true, copyError: false });
    try {
      await api.notes.create({
        title: conflictCopyName(note.title, copyAuthorName()),
        content: c.mine,
        source: note.source,
        folder: note.path.includes('/') ? note.path.slice(0, note.path.lastIndexOf('/')) : '',
      });
      setSaveConflict(null);
      bumpNotes();
    } catch {
      // Копия не создалась — диалог остаётся, свой текст в редакторе (Notice copyError)
      setSaveConflict({ ...c, busy: false, copyError: true });
    }
  };

  // «Заменить своей»: повторная запись по актуальному состоянию — If-Match = sha256 из тела 409
  const conflictReplace = async () => {
    const c = saveConflict;
    if (!c || c.busy || !note) return;
    setSaveConflict({ ...c, busy: true });
    try {
      const updated = await api.notes.update(note.id, {
        // Переименование черновика — по тому же правилу, что save(): только если отличается от
        // заголовка прочитанной версии. Без него «Заменить своей» молча теряло переименование.
        title: c.draftTitle !== note.title ? c.draftTitle : undefined,
        content: c.mine,
      }, c.error.sha256 ?? undefined);
      noteEtag.current = updated.etag ?? null;
      draftEtag.current = updated.etag ?? null;
      setSaveConflict(null);
      setEditing(false);
      bumpNotes();
      if (updated.id !== note.id) onSelectNote(updated.id);
      else setNote(updated);
    } catch (e) {
      if (e instanceof NoteConflictError) {
        // Заметку изменили ещё раз: диалог остаётся, с новым телом 409 и again (Notice над диффом)
        setSaveConflict({ ...c, error: e, again: true, busy: false, mineAt: relTime(new Date().toISOString()) });
        return;
      }
      // Прочий сбой (офлайн и т.п.) — обычный фидбек; свой текст остаётся в редакторе
      setSaveConflict(null);
      showToast('Заметка', e instanceof OfflineError ? 'Действие недоступно офлайн' : 'Не удалось сохранить заметку', 'error');
    }
  };

  // «Взять чужую версию»: редактор и база — чужный текст и чужой тег; правка продолжается
  // от актуальной копии, а не от проигравшей
  const conflictTakeTheirs = () => {
    const c = saveConflict;
    if (!c) return;
    setSaveConflict(null);
    const theirs = c.error.content;
    // Текста в 409 нет — брать нечего, но и терять нечего
    if (theirs == null) return;
    setNote(prev => prev ? { ...prev, content: theirs } : prev);
    setDraftBody(theirs);
    setEditing(true);
    noteEtag.current = c.error.sha256 ?? null;
    draftEtag.current = c.error.sha256 ?? null;
  };

  // Удаление в два шага: запрос подтверждения (диалог) → само удаление
  const [deleteConfirm, setDeleteConfirm] = useState(false);
  const del = () => { if (note && can('note.delete')) setDeleteConfirm(true); };
  const doDelete = async () => {
    if (!note || !can('note.delete')) return;
    setDeleteConfirm(false);
    try {
      await api.notes.delete(note.id);
    } catch (e) {
      if (e instanceof OfflineError) await deleteNoteOffline(note.id);
      else throw e;
    }
    bumpNotes();
    onDeleted();
  };

  // «Связать» несвязанное упоминание (кнопка в блоке связей)
  const linkMention = (targetTitle: string) => {
    if (!note || !can('note.edit')) return;
    void api.notes.linkMention(note.id, targetTitle).then(n => { if (n) setNote(n); bumpNotes(); });
  };

  // Перенос в папку и/или другой источник (модалка)
  const allNotes = useNotes();
  const [showMove, setShowMove] = useState(false);
  const [moveError, setMoveError] = useState<string | null>(null);
  const [moveSources, setMoveSources] = useState<NoteSource[]>([]);
  const currentDir = note && note.path.includes('/') ? note.path.slice(0, note.path.lastIndexOf('/')) : '';
  const openMove = () => {
    setMoveError(null);
    setShowMove(true);
    // Другие источники — дверь note.moveSource (редактору закрыта): выбор источника не рисуется
    api.notes.sources()
      .then(all => setMoveSources(can('note.moveSource') ? all : all.filter(s => s.key === note?.source)))
      .catch(() => setMoveSources([]));
  };
  // Папки источника — из путей всех его заметок (включая промежуточные уровни)
  const foldersFor = (src: string) => [...new Set(
    allNotes.filter(n => n.source === src && n.path.includes('/'))
      .flatMap(n => {
        const parts = n.path.slice(0, n.path.lastIndexOf('/')).split('/');
        return parts.map((_, i) => parts.slice(0, i + 1).join('/'));
      }),
  )].sort((a, b) => a.localeCompare(b, 'ru'));
  const moveTo = async (folder: string, targetSource?: string) => {
    if (!note || !can('note.move')) return;
    if (targetSource && targetSource !== note.source && !can('note.moveSource')) return;
    setMoveError(null);
    try {
      const updated = await api.notes.move(note.id, folder || null,
        targetSource && targetSource !== note.source ? targetSource : undefined);
      setShowMove(false);
      bumpNotes();
      if (updated.id !== note.id) onSelectNote(updated.id);
      else setNote(updated);
    } catch {
      setMoveError('Не удалось перенести: в целевом месте уже есть заметка с таким именем');
    }
  };

  if (loading && !note)
    return <div style={{ padding: 40, textAlign: 'center', color: C.textMuted, fontFamily: FONT.sans }}>Загрузка…</div>;
  if (!note)
    return <div style={{ padding: 40, textAlign: 'center', color: C.textMuted, fontFamily: FONT.sans }}>Заметка не найдена</div>;

  return (
    <div ref={rootRef} style={{ height: '100%', display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
      {/* Тулбар заметки — единый стиль тулбаров приложения (как FileViewer);
          в hero-режиме — заголовок раздела прямо на холсте (без фона и границы),
          тело заметки ниже отделяется собственной карточкой-островом */}
      <div style={{
        display: 'flex', alignItems: 'center', gap: 10, flex: 'none',
        // Hero: тулбар без заголовка — строка компактнее, чтобы заметка
        // начиналась выше (меньше пустого места над заголовком)
        minHeight: isMobile ? TB.heightMobile : hero ? 40 : TB.heightDesktop,
        padding: `4px ${isMobile ? TB.padXMobile : TB.padX}px`,
        boxSizing: 'border-box',
        background: hero ? 'transparent' : TB.bg,
        borderBottom: hero ? 'none' : TB.borderBottom,
      }}>
        {/* Мобайл: стрелка «назад» у заголовка — как у файлов/чатов */}
        {onBack && !editing && <BackButton onClick={onBack} title="К списку" style={{ height: 32 }} />}
        {editing
          ? <input
              {...NO_AUTOFILL}
              value={draftTitle}
              onChange={e => setDraftTitle(e.target.value)}
              // Переименование меняет id файла и каскадно правит [[ссылки]] — только онлайн
              readOnly={!online}
              title={!online ? 'Переименование недоступно офлайн' : undefined}
              style={{
                flex: 1, minWidth: 120, fontFamily: FONT.serif, fontSize: 16, fontWeight: 700,
                color: !online ? C.textMuted : C.textHeading,
                background: C.bgWhite, border: `1px solid ${C.border}`,
                borderRadius: R.md, padding: '5px 10px',
              }}
            />
          : hero
            // Hero: заголовок НЕ в тулбаре — он рендерится крупно над контентом
            // (тулбар — только мета и действия), иначе задваивался с H1 заметки
            ? <div style={{ flex: 1, minWidth: 0 }} />
            : <h1 title={note.title} onClick={onBack}
                style={{ flex: 1, minWidth: 0, margin: 0, fontFamily: FONT.serif, fontSize: 16, fontWeight: 700, color: C.textHeading, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis', cursor: onBack ? 'pointer' : 'default' }}>
                {note.title}
              </h1>}
        {!isMobile && <SourceBadge source={note.source} label={note.sourceLabel} />}
        {!editing && !isMobile && <span style={{ fontSize: 11, color: C.textMuted, flex: 'none' }}>изменено {relTime(note.updatedAt)}</span>}
        {!editing && note.sourceSessionId && (
          <button onClick={() => openSession(note.sourceSessionId!)}
            style={{ fontSize: 11, color: C.info, flex: 'none', background: 'none', border: 'none', cursor: 'pointer', fontFamily: FONT.sans, display: 'flex', alignItems: 'center', gap: 3, padding: 0 }}>
            <MessageCircle size={11} strokeWidth={2} /> Из чата
          </button>
        )}
        {/* Привязка к файлу проекта (frontmatter file:) — переход к файлу; битая
            привязка (файл удалён/переименован) — состояние «файл не найден» */}
        {!editing && note.file && (
          note.fileMissing
            ? <span title={`${note.file} — файл не найден`}
                style={{ fontSize: 11, color: C.warning, flex: 'none', display: 'flex', alignItems: 'center', gap: 3, whiteSpace: 'nowrap' }}>
                <FileText size={11} strokeWidth={2} /> файл не найден
              </span>
            : <button onClick={onOpenFileRef ? () => onOpenFileRef(note.file!) : undefined}
                disabled={!onOpenFileRef}
                title={note.file}
                style={{ fontSize: 11, color: C.info, flex: 'none', background: 'none', border: 'none',
                  cursor: onOpenFileRef ? 'pointer' : 'default', fontFamily: FONT.sans,
                  display: 'flex', alignItems: 'center', gap: 3, padding: 0, maxWidth: 180 }}>
                <FileText size={11} strokeWidth={2} style={{ flexShrink: 0 }} />
                <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{note.file.split('/').pop()}</span>
              </button>
        )}
        {!editing && note.expiresAt && (() => {
          const left = new Date(note.expiresAt).getTime() - now;
          if (left <= 0) return <span style={{ fontSize: 11, color: C.warning, flex: 'none', whiteSpace: 'nowrap' }}>⏳ скоро</span>;
          const min = Math.round(left / 60_000);
          const urgent = min < 60;
          const label = min < 60 ? `${Math.max(min, 1)} мин` : min < 1440 ? `${Math.round(min / 60)} ч` : `${Math.round(min / 1440)} дн`;
          return <span style={{ fontSize: 11, color: urgent ? C.warning : C.textMuted, flex: 'none', whiteSpace: 'nowrap' }}>⏳ {label}</span>;
        })()}
        <div style={{ display: 'flex', alignItems: 'center', gap: 2, flex: 'none' }}>
          {editing ? (
            /* В правке — «Отмена» + «Сохранить» в тулбаре, как у файлов */
            <>
              <button onClick={() => { setEditing(false); setDraftBody(note.content); setDraftTitle(note.title); draftEtag.current = noteEtag.current; }} style={tbBtnGhost}>Отмена</button>
              <button onClick={save} disabled={saving}
                style={{ ...tbBtnPrimary, marginLeft: 6, ...(saving ? { opacity: 0.6, cursor: 'default' } : {}) }}>
                {saving ? 'Сохранение…' : 'Сохранить'}
              </button>
            </>
          ) : (
            <>
              {/* AI-действия (связи, теги, конспект дня, «спросить Claude») — только через AI-палитру (⌘/Ctrl+K) */}
              {can('note.edit') && (
                <IconButton title={isFavorite(note.tags) ? 'Убрать из избранного' : 'В избранное'}
                  tone={isFavorite(note.tags) ? 'accent' : undefined} onClick={() => { void toggleFav(); }}>
                  <IconStar size={ICON_SIZE.sm} filled={isFavorite(note.tags)} />
                </IconButton>
              )}
              <IconButton title={copied ? 'Скопировано' : 'Скопировать Markdown (Shift — с форматированием)'} onClick={copyNote}>
                {copied ? <Check size={ICON_SIZE.sm} color={C.success} strokeWidth={2.5} /> : <Copy size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
              </IconButton>
              {wikiOn && (
                <IconButton title="Разобрать в базу: Хранитель соберёт пакет правок вики" disabled={!online || ingestBusy} onClick={() => { void runIngest(); }}>
                  <BookOpen size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
                </IconButton>
              )}
              {/* «Перенести в вики» (F5b) — та же форма действия, что у соседних: ряд тулбара
                  на 360 не переносится, подписанная кнопка в нём не помещается */}
              {wikiOn && can('note.delete') && (
                <IconButton title="Перенести в вики" disabled={!online || transferBusy} onClick={() => setTransferConfirm(true)}>
                  <BookMarked size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
                </IconButton>
              )}
              {can('note.move') && (
                <IconButton title={online ? 'Переместить…' : 'Перенос недоступен офлайн'} onClick={openMove} disabled={!online}><IconFolderMove /></IconButton>
              )}
              {can('note.delete') && <IconButton title="Удалить" tone="danger" onClick={del}><IconTrash /></IconButton>}
              {can('note.edit') && <button onClick={startEdit} style={{ ...tbBtnPrimary, marginLeft: 6 }}>Править</button>}
            </>
          )}
          {extraToolbar}
        </div>
      </div>

      {/* Тело: контент со плавающим сайдбаром связей (десктоп). В hero-режиме контент
          живёт прямо на холсте (без рамки), островом оформлен только сайдбар связей */}
      <div style={{ flex: 1, minHeight: 0, display: 'flex', overflow: 'hidden' }}>
      {editing ? (
        /* Правка — редактор на всю контентную зону (как правка файла); кнопки — в тулбаре */
        <div style={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column' }}>
          <Suspense fallback={<div style={{ padding: 24, color: C.textMuted, fontSize: 13 }}>Загрузка редактора…</div>}>
            <NoteEditor value={draftBody} onChange={setDraftBody} fill
              placeholder="Текст заметки… связывай через [[Заголовок]]" onWikilink={onWikilink} />
          </Suspense>
        </div>
      ) : (
      // Полупрозрачный «лист» под текстом — тот же тон, что у ответа Claude в чате:
      // заметка лежит на фоне страницы с дудл-паттерном
      <div style={{ flex: 1, minWidth: 0, overflowY: 'auto', padding: '10px 18px 24px',
        ...(hero ? { background: C.msgBg, borderRadius: ISLAND.radius } : {}) }}>
        {/* Исход «Перенести в вики» (F5b): успех — плашка остаётся там, где была заметка
            (сам экран переходит к «Вики» запросом в стор); отказ — причина по-русски,
            кнопка тулбара на месте. Тон и икона — как у флешей правки страницы (WikiPageView) */}
        {!editing && transferFlash && (
          <Notice
            tone={transferFlash.ok ? 'info' : 'danger'}
            icon={transferFlash.ok ? CheckCircle2 : AlertTriangle}
            title={transferFlash.text}
            style={{ marginBottom: 12 }}
          />
        )}
        {/* Hero: заголовок заметки — крупно над тегами (в стиле markdown-H1);
            ведущий H1 самого контента глушится (hideLeadingH1), чтобы не дублировался */}
        {!editing && hero && (
          <h1 style={{ fontFamily: FONT.serif, fontSize: 28, fontWeight: 500, margin: '0 0 8px', color: C.textHeading, letterSpacing: '-0.01em', lineHeight: 1.25 }}>
            {note.title}
          </h1>
        )}
        {!editing && (
          <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6, marginBottom: 12, alignItems: 'center' }}>
            {note.tags.map(t => (
              <button key={t} onClick={() => onTag?.(t)} title={`Заметки с тегом ${t}`}
                style={{ fontSize: 11.5, fontWeight: 500, color: C.accent, background: C.accentLight, border: 'none', borderRadius: R.sm, padding: '2px 8px', cursor: onTag ? 'pointer' : 'default', fontFamily: FONT.sans }}>
                #{t}
              </button>
            ))}
            {/* Ручное добавление тега */}
            {!can('note.edit') ? null : addingTag ? (
                <input
                  {...NO_AUTOFILL}
                  autoFocus
                  value={newTag}
                  onChange={e => setNewTag(e.target.value)}
                  onKeyDown={e => {
                    if (e.key === 'Enter') { void acceptTag(newTag); setNewTag(''); setAddingTag(false); }
                    if (e.key === 'Escape') { setNewTag(''); setAddingTag(false); }
                  }}
                  onBlur={() => { if (newTag.trim()) void acceptTag(newTag); setNewTag(''); setAddingTag(false); }}
                  placeholder="тег"
                  style={{ width: 90, fontSize: 11.5, fontFamily: FONT.sans, color: C.textHeading, background: C.bgWhite, border: `1px solid ${C.accent}`, borderRadius: R.sm, padding: '2px 7px', outline: 'none' }}
                />
              ) : (
                <button onClick={() => setAddingTag(true)} title="Добавить тег"
                  style={{ fontSize: 11.5, fontWeight: 500, color: C.textMuted, background: 'none', border: `1px dashed ${C.dashed}`, borderRadius: R.sm, padding: '2px 8px', cursor: 'pointer', fontFamily: FONT.sans }}>
                  + тег
                </button>
              )}
              {/* Кнопка «теги (AI)» убрана — предложение тегов только через AI-палитру */}
              {tagsJob.status === 'running' && (
                <WaitingIndicator />
              )}
              {tagsJob.status === 'error' && (
                <span style={{ fontSize: 11, color: C.dangerText }}>ИИ недоступен (AI не залогинен на сервере)</span>
              )}
              {tagsJob.status === 'done' && (tagsJob.result ?? []).length === 0 && (
                <span style={{ fontSize: 11, color: C.textMuted }}>нечего предложить</span>
              )}
              {can('note.edit') && tagsJob.status === 'done' && (tagsJob.result ?? []).map(t => (
                <button key={t} onClick={() => void acceptTag(t)} title="Добавить тег"
                  style={{ fontSize: 11.5, fontWeight: 500, color: C.textSecondary, background: C.bgSelected, border: `1px dashed ${C.dashed}`, borderRadius: R.sm, padding: '2px 8px', cursor: 'pointer', fontFamily: FONT.sans }}>
                  +#{t}
                </button>
              ))}
          </div>
        )}
        {!editing && linksJob.status !== 'idle' && (
          <div style={{ marginBottom: 12, padding: '9px 12px', background: C.accentLight, borderRadius: R.lg, border: `1px solid ${C.accentMuted}` }}>
            {linksJob.status === 'running' ? (
              <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                <WaitingIndicator />
                <button onClick={() => resetAiJob(linksKey)} style={{ marginLeft: 'auto', background: 'none', border: 'none', cursor: 'pointer', color: C.textMuted, padding: 0, display: 'flex' }}><X size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} /></button>
              </div>
            ) : (
              <>
                <div style={{ display: 'flex', alignItems: 'center', gap: 6, fontSize: 11.5, fontWeight: 600, color: linksJob.status === 'error' ? C.dangerText : C.textSecondary, marginBottom: (linksJob.result?.length ?? 0) > 0 ? 8 : 0 }}>
                  <IconSparkle />
                  {linksJob.status === 'error' ? 'ИИ недоступен (AI не залогинен на сервере)'
                    : (linksJob.result?.length ?? 0) === 0 ? 'Подходящих связей не нашлось' : 'Предложенные связи'}
                  <button onClick={() => resetAiJob(linksKey)} style={{ marginLeft: 'auto', background: 'none', border: 'none', cursor: 'pointer', color: C.textMuted, padding: 0, display: 'flex' }}><X size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} /></button>
                </div>
                {(linksJob.result ?? []).map(l => (
                  <div key={l.title} style={{ display: 'flex', alignItems: 'center', gap: 8, padding: '3px 0' }}>
                    <span style={{ flex: 1, minWidth: 0, fontSize: 12.5 }}>
                      <span style={{ fontWeight: 500, color: C.textPrimary }}>{l.title}</span>
                      <span style={{ color: C.textMuted }}> — {l.why}</span>
                    </span>
                    {can('note.edit') && <button onClick={() => void acceptLink(l.title)}
                      style={{ display: 'flex', alignItems: 'center', gap: 4, flex: 'none', fontSize: 11, fontWeight: 500, color: C.accent, background: C.bgWhite, border: `1px solid ${C.accentMuted}`, borderRadius: R.sm, padding: '3px 8px', cursor: 'pointer', fontFamily: FONT.sans }}>
                      <IconLink />Связать
                    </button>}
                  </div>
                ))}
              </>
            )}
          </div>
        )}
        {!editing && dailyJob.status === 'running' && (
          <div style={{ marginBottom: 12, padding: '9px 12px', background: C.accentLight, borderRadius: R.lg, border: `1px solid ${C.accentMuted}` }}>
            <WaitingIndicator hint="Собираю конспект дня из заметок и активности" />
          </div>
        )}
        {/* Карточка привязки комментария к документу (флаг doc-annotations) */}
        {ann && (
          <div style={{
            marginBottom: 14, padding: '11px 14px', background: C.bgWhite,
            border: `1px solid ${C.border}`, borderRadius: R.lg,
            display: 'flex', flexDirection: 'column', gap: 8,
          }}>
            <div style={{ display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap', fontSize: 12.5, color: C.textSecondary }}>
              <MessageCircle size={14} strokeWidth={2} style={{ color: C.accent, flexShrink: 0 }} />
              {ann.isReply ? 'Отвечает на комментарий' : 'Комментирует'}
              {!ann.isReply && (
                <span style={{ display: 'inline-flex', alignItems: 'center', gap: 4, fontFamily: FONT.mono, fontSize: 12, color: ann.docMissing ? C.textMuted : C.textPrimary, textDecoration: ann.docMissing ? 'line-through' : undefined }}>
                  <FileText size={12} strokeWidth={2} />{ann.docPath}
                </span>
              )}
              {ann.docMissing && !ann.isReply && (
                <span style={{ display: 'inline-flex', alignItems: 'center', gap: 4, fontSize: 11, fontWeight: 600, borderRadius: 11, padding: '1px 8px', color: C.textMuted, background: C.bgInset }}>
                  <X size={11} strokeWidth={2.5} />документ удалён
                </span>
              )}
              {ann.anchorHeading && !ann.isReply && (
                <span style={{ color: C.textMuted }}>› {ann.anchorHeading}</span>
              )}
              {!ann.isReply && (
                <span style={{ marginLeft: 'auto', display: 'inline-flex', alignItems: 'center', gap: 4, fontSize: 11, fontWeight: 600, borderRadius: 11, padding: '1px 8px',
                  color: ann.status === 'open' ? C.warningText : C.successText,
                  background: ann.status === 'open' ? C.warningBg : C.successBg }}>
                  {ann.status === 'open'
                    ? <><MessageCircle size={11} strokeWidth={2.5} />открыт</>
                    : <><Check size={11} strokeWidth={2.5} />решён</>}
                </span>
              )}
            </div>
            {ann.anchorQuote && !ann.isReply && (
              <div style={{
                fontSize: 12, color: C.textSecondary, fontStyle: 'italic',
                borderLeft: `2px solid ${C.accent}`, paddingLeft: 9,
              }}>«{ann.anchorQuote}»</div>
            )}
            <div style={{ display: 'flex', gap: 6, flexWrap: 'wrap' }}>
              {!ann.isReply && can('note.comment') && (
                <button
                  onClick={() => {
                    const next = ann.status === 'open' ? 'resolved' : 'open';
                    void api.notes.setStatus(note.id, next).then(() => bumpNotes()).catch(() => {});
                  }}
                  style={{
                    display: 'inline-flex', alignItems: 'center', gap: 5, fontSize: 11.5,
                    color: C.textSecondary, background: C.bgWhite, border: `1px solid ${C.border}`,
                    borderRadius: R.sm, padding: '3px 10px', cursor: 'pointer', fontFamily: FONT.sans,
                  }}>
                  {ann.status === 'open'
                    ? <><Check size={12} strokeWidth={2.5} /> Решён</>
                    : <><Undo2 size={12} strokeWidth={2.5} /> Снова открыть</>}
                </button>
              )}
              {(!ann.docMissing || ann.isReply) && (
                <button onClick={gotoAnnTarget} style={{
                  display: 'inline-flex', alignItems: 'center', gap: 5, fontSize: 11.5,
                  color: C.textSecondary, background: C.bgWhite, border: `1px solid ${C.border}`,
                  borderRadius: R.sm, padding: '3px 10px', cursor: 'pointer', fontFamily: FONT.sans,
                }}>
                  {ann.isReply ? 'К корневому комментарию →' : 'Перейти к месту →'}
                </button>
              )}
              {/* «Обработать» = задача из комментария: лично или персоне-исполнителю */}
              {!ann.isReply && can('note.tasks') && (
                <span style={{ display: 'inline-flex', gap: 6 }}>
                  <button onClick={() => void createTaskFromComment()} style={annBtn}>
                    Создать задачу
                  </button>
                  {assignable.length > 0 && (
                    <button onClick={e => setAssignAnchor(assignAnchor ? null : e.currentTarget as HTMLElement)} style={annBtn}>
                      Поручить персоне ▾
                    </button>
                  )}
                  {assignAnchor && (
                    <PersonaAssignMenu
                      personas={assignable}
                      anchorEl={assignAnchor}
                      onPick={p => void createTaskFromComment(p)}
                      onClose={() => setAssignAnchor(null)}
                    />
                  )}
                </span>
              )}
            </div>
            {/* Тред: ответы + поле «Ответить» (только у корневого комментария) */}
            {!ann.isReply && annReplies !== null && (
              <div style={{ borderTop: `1px solid ${C.borderLight}`, paddingTop: 8, display: 'flex', flexDirection: 'column', gap: 6 }}>
                {annReplies.map(r => (
                  <div key={r.noteId} style={{ fontSize: 12.5, color: C.textSecondary, display: 'flex', gap: 6, alignItems: 'baseline' }}>
                    <span style={{ flex: 1 }}>{r.excerpt || r.title}</span>
                    <button onClick={() => onSelectNote(r.noteId)} title="Открыть ответ" style={{
                      border: 'none', background: 'none', cursor: 'pointer', color: C.textMuted, fontSize: 10.5, padding: 0, fontFamily: FONT.sans,
                    }}>{new Date(r.createdAt).toLocaleDateString('ru')}</button>
                  </div>
                ))}
                {can('note.comment') && <div style={{ display: 'flex', gap: 6 }}>
                  <input
                    {...NO_AUTOFILL}
                    value={annReplyDraft}
                    onChange={e => setAnnReplyDraft(e.target.value)}
                    onKeyDown={e => { if (e.key === 'Enter') void sendAnnReply(); }}
                    placeholder="Ответить…"
                    style={{
                      flex: 1, minWidth: 0, border: `1px solid ${C.border}`, borderRadius: 8,
                      background: C.bgMain, color: C.textHeading, font: `12.5px ${FONT.sans}`, padding: '5px 9px',
                    }}
                  />
                  <button onClick={() => void sendAnnReply()} disabled={annReplySending} style={{
                    fontSize: 11.5, color: C.textSecondary, background: C.bgWhite,
                    border: `1px solid ${C.border}`, borderRadius: R.sm, padding: '3px 10px',
                    cursor: 'pointer', fontFamily: FONT.sans, opacity: annReplySending ? 0.6 : 1,
                  }}>Отправить</button>
                </div>}
              </div>
            )}
          </div>
        )}
        {/* Плавающий сайдбар связей (десктоп): aside с float:right внутри flow-root —
            свёрнутые секции связей освобождают место, и текст заметки обтекает их
            слева, как у документов (docSide в FileViewer). Теги/AI/карточка комментария
            живут выше на полной ширине — обтекает только тело заметки. */}
        <div style={{ display: 'flow-root' }}>
          {!isMobile && !connectionsBelow && (
            <aside style={hero ? {
              float: 'right', width: 290, marginLeft: 18, marginBottom: 14, padding: '10px 12px',
              background: C.bgMain, border: `1px solid ${ISLAND.border}`, borderRadius: ISLAND.radius, boxShadow: ISLAND.shadow,
            } : {
              float: 'right', width: 290, marginLeft: 18, marginBottom: 14,
              borderLeft: `1px solid ${C.border}`, paddingLeft: 14,
            }}>
              {/* Контейнер панели комментариев — портал из DocCommentedMarkdown; пока
                  комментариев нет, пуст и высоты не занимает */}
              <div ref={setPanelEl} />
              <NoteTasksSection noteId={note.id} version={version} project={sourceProject} />
              <NoteConnections note={note} onOpenNote={id => onSelectNote(id)}
                onWikilink={onWikilink} onLinkMention={can('note.edit') ? linkMention : undefined} />
            </aside>
          )}
          {/* data-selection-scope — «Копировать как Markdown/HTML» из master работает
              и поверх слоя комментариев */}
          <div data-selection-scope="doc" data-selection-priority="2">
            {ann ? (
              // Просмотр без frontmatter — служебные поля (annotates/anchor_*) не текст
              <MarkdownViewer content={stripFrontmatter(note.content).body} existingTitles={existingTitles} onWikilink={onWikilink}
                resolveNote={resolveNote} embedSource={note.source} hideLeadingH1={hero} />
            ) : (
              // Обычная заметка — тоже документ: выделение → комментарий. Панель
              // комментариев уходит порталом в плавающий сайдбар (panelEl) на десктопе,
              // снизу — на мобильном/узком. Заметки-комментарии не аннотируются
              // выделением — тред ведётся ответами.
              <DocCommentedMarkdown
                scope={note.source}
                docPath={note.source === 'personal' ? note.path : 'notes/' + note.path}
                content={note.content}
                isMobile={isMobile}
                panelBelow={isMobile || connectionsBelow}
                panelTarget={!isMobile && !connectionsBelow ? panelEl : null}
                deferPanel={!isMobile && !connectionsBelow}
                // Сайдбар связей плавает справа, текст обтекает его и уходит под него —
                // рельс маркеров живёт на левом поле. Связи ушли под контент — правое
                // поле свободно, рельс возвращается туда
                railSide={!isMobile && !connectionsBelow ? 'left' : 'right'}
                viewer={{ onWikilink, existingTitles, resolveNote, embedSource: note.source, hideLeadingH1: hero }}
              />
            )}
          </div>
        </div>

        {/* Мобильный/планшет: задачи из заметки + связи снизу под контентом (сайдбару нет места) */}
        {(isMobile || connectionsBelow) && (
          <div style={{ marginTop: 20, borderTop: `1px solid ${C.border}`, paddingTop: 12 }}>
            <NoteTasksSection noteId={note.id} version={version} project={sourceProject} />
            <NoteConnections note={note} onOpenNote={id => onSelectNote(id)}
              onWikilink={onWikilink} onLinkMention={can('note.edit') ? linkMention : undefined} />
          </div>
        )}
      </div>
      )}

      </div>

      {showMove && (
        <MoveDialog
          currentDir={currentDir}
          currentSource={note.source}
          sources={moveSources}
          foldersFor={foldersFor}
          error={moveError}
          onMove={moveTo}
          onClose={() => setShowMove(false)}
        />
      )}

      {/* Конфликт сохранения (S2c-F3, п.28): 409 note_changed на запись по If-Match. Вид и
          тексты — целиком SaveConflictDialog (S2c-F1), своих здесь нет. Заметка — всегда
          текст, поэтому дифф обычный; выше потолка строк lineDiff — тот же вариант без диффа */}
      {saveConflict && note && (() => {
        const e = saveConflict.error;
        const theirs = e.content;
        const noText = theirs === null;
        const diff = noText ? null : lineDiff(theirs, saveConflict.mine);
        return (
          <SaveConflictDialog
            conflict={{
              code: 'note_changed',
              sha256: e.sha256 ?? '',
              // author и source бэк заполнит с S3-3, сейчас всегда null — диалог сам
              // подставит «Другой участник» и не подпишет источник
              author: e.author ? { userId: e.author.userId, displayName: e.author.displayName ?? '' } : undefined,
              changedAt: e.changedAt ?? undefined,
              source: e.source ?? undefined,
            }}
            fileName={note.title}
            copyName={conflictCopyName(note.title, copyAuthorName())}
            diff={diff ?? undefined}
            noDiff={noText || diff === null}
            mineAt={saveConflict.mineAt}
            again={saveConflict.again}
            copyError={saveConflict.copyError}
            busy={saveConflict.busy}
            onSaveCopy={() => { void conflictSaveCopy(); }}
            onReplace={() => { void conflictReplace(); }}
            onTakeTheirs={conflictTakeTheirs}
            onClose={() => setSaveConflict(null)}
          />
        );
      })()}

      {deleteConfirm && (
        <ConfirmDialog
          title="Удалить заметку?"
          subtitle={<>Заметка «<strong style={{ color: C.textPrimary, fontWeight: 600 }}>{note.title}</strong>» будет удалена без возможности восстановления.</>}
          confirmLabel="Удалить"
          confirmVariant="danger"
          onConfirm={doDelete}
          onCancel={() => setDeleteConfirm(false)}
        />
      )}

      {/* Подтверждение переноса (F5b) — текст записки notes-wiki-unified-v1 дословно (§«е. Заметка»);
          «✎ вы» вместо «✎ Андрей» из макета: сноска человека рисуется знаком «✎ вы, {дата}»
          (ADR-038 «Авторство…»), так же делает страница (WikiPageView). Диалог открыт до
          исхода запроса (образец doRetract): спиннер на кнопке, закрытие — по результату */}
      {transferConfirm && (
        <ConfirmDialog
          title={TRANSFER_TEXT.confirmTitle}
          subtitle={TRANSFER_TEXT.confirmBody}
          confirmLabel={TRANSFER_TEXT.confirm}
          onConfirm={doTransfer}
          onCancel={() => setTransferConfirm(false)}
        />
      )}
    </div>
  );
}

// Тексты «Перенести в вики» (F5b). Подтверждение — из записки notes-wiki-unified-v1 (§«е.
// Заметка»), дословно, кроме «✎ вы» вместо имени макета (см. комментарий модалки). Отказы —
// свой текст по кодам контракта (формулировки — ADR-038); тексты фичи ведёт Софья.
const TRANSFER_TEXT = {
  confirmTitle: 'Перенести заметку в вики?',
  confirmBody: 'Заметка станет страницей вики: ваш текст — вашими утверждениями «✎ вы», а ИИ сможет дописывать страницу рядом с ними. Сама заметка заменится ссылкой на страницу — копий не будет.',
  confirm: 'Перенести',
  done: 'Перенесено',
  notFound: 'Заметки нет или она чужая.',
  alreadyMoved: 'Заметка уже перенесена в вики: в ней ссылка на страницу.',
  nameTaken: 'Страница с таким именем уже есть: переименуйте заметку и повторите перенос.',
  off: 'Перенос в вики сейчас выключен: включите «Заметки и Вики» в настройках и повторите.',
  refused: 'Перенос отказан. Повторите перенос — он доведёт его до конца.',
  failed: 'Не удалось перенести заметку. Проверьте соединение и повторите.',
} as const;

// Текст отказа переноса. У именованных кодов — свой текст; у wiki_refused, wiki_edit_invalid и
// wiki_not_initialized причина — в message бэка (ADR-038 «причина текстом»: «страница „X“ уже
// есть: переименуйте заметку…», «в заметке нет текста для утверждений: переносить нечего»,
// «вики этого владельца не заведена») — показываем его с большой буквы; без message — общий
// текст. С LI-2 ручка переноса сама заводит вики владельца (WikiController.cs:498), и 409
// wiki_not_initialized из действия человека не приходит: что остаётся — ответ второй линии
// защиты (гейт WikiApplyService.cs:194), его message — честная причина. Своей готовой фразы
// «вики не заведена» здесь нет: подразумеваемая ею просьба нажимать кнопку «Завести вики»
// лишняя — самой кнопки нет с LI-3. Сетевой сбой (не WikiError) — общий текст.
function transferFailText(e: unknown): string {
  if (!(e instanceof WikiError)) return TRANSFER_TEXT.failed;
  switch (e.code) {
    case 'not_found': return TRANSFER_TEXT.notFound;
    case 'wiki_note_transferred': return TRANSFER_TEXT.alreadyMoved;
    case 'wiki_page_changed': return TRANSFER_TEXT.nameTaken;
    case 'wiki_unified_off': return TRANSFER_TEXT.off;
    case 'wiki_refused':
    case 'wiki_edit_invalid':
    case 'wiki_not_initialized':
      return e.message ? e.message.charAt(0).toUpperCase() + e.message.slice(1) : TRANSFER_TEXT.refused;
  }
  if (e.status === 403) return 'Нет доступа.';
  return TRANSFER_TEXT.failed;
}

// Диалог переноса: выбор источника (личный vault / проекты) + папки выбранного
// источника + ввод новой папки; «Корень» — наверх выбранного источника.
// Кнопка действий карточки привязки комментария
const annBtn: React.CSSProperties = {
  display: 'inline-flex', alignItems: 'center', gap: 5, fontSize: 11.5,
  color: C.textSecondary, background: C.bgWhite, border: `1px solid ${C.border}`,
  borderRadius: R.sm, padding: '3px 10px', cursor: 'pointer', fontFamily: FONT.sans,
};

function MoveDialog({ currentDir, currentSource, sources, foldersFor, error, onMove, onClose }: {
  currentDir: string;
  currentSource: string;
  sources: NoteSource[];
  foldersFor: (source: string) => string[];
  error: string | null;
  onMove: (folder: string, targetSource: string) => void;
  onClose: () => void;
}) {
  const [src, setSrc] = useState(currentSource);
  const [custom, setCustom] = useState('');
  const folders = foldersFor(src);
  const isCurrent = (folder: string) => src === currentSource && folder === currentDir;
  const row = (label: React.ReactNode, folder: string) => (
    <button key={folder || '(root)'} disabled={isCurrent(folder)} onClick={() => onMove(folder, src)}
      style={{
        width: '100%', display: 'flex', alignItems: 'center', gap: 8, textAlign: 'left',
        padding: '8px 10px', borderRadius: R.md, border: 'none', fontFamily: FONT.sans, fontSize: 13,
        background: 'transparent', color: isCurrent(folder) ? C.textMuted : C.textPrimary,
        cursor: isCurrent(folder) ? 'default' : 'pointer', opacity: isCurrent(folder) ? 0.6 : 1,
      }}>
      <span style={{ color: C.accent, display: 'flex' }}><IconFolder /></span>
      <span style={{ flex: 1 }}>{label}</span>
      {isCurrent(folder) && <span style={{ fontSize: 10.5, color: C.textMuted }}>текущая</span>}
    </button>
  );
  return (
    <Modal width={420} title="Переместить заметку" onClose={onClose}>
      {sources.length > 1 && (
        <select value={src} onChange={e => setSrc(e.target.value)}
          style={{ width: '100%', boxSizing: 'border-box', marginBottom: 10, background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.md, padding: '7px 10px', fontSize: 13, fontFamily: FONT.sans, color: C.textHeading, outline: 'none' }}>
          {sources.map(s => (
            <option key={s.key} value={s.key}>{s.label}{s.key === currentSource ? ' (текущий)' : ''}</option>
          ))}
        </select>
      )}
      <div style={{ maxHeight: 300, overflowY: 'auto' }}>
        {row(<i>Корень</i>, '')}
        {folders.map(f => row(f, f))}
      </div>
      <div style={{ display: 'flex', gap: 8, marginTop: 12 }}>
        <input {...NO_AUTOFILL} value={custom} onChange={e => setCustom(e.target.value)}
          onKeyDown={e => { if (e.key === 'Enter' && custom.trim()) onMove(custom.trim(), src); }}
          placeholder="Новая папка (Идеи/Черновики)"
          style={{ flex: 1, background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.md, padding: '7px 10px', fontSize: 13, fontFamily: FONT.sans, color: C.textHeading, outline: 'none' }} />
        <button onClick={() => custom.trim() && onMove(custom.trim(), src)} disabled={!custom.trim()}
          style={{ background: C.accent, color: C.onAccent, border: 'none', borderRadius: R.md, padding: '7px 14px', fontSize: 13, fontWeight: 500, cursor: 'pointer', fontFamily: FONT.sans, opacity: custom.trim() ? 1 : 0.6 }}>
          Создать и перенести
        </button>
      </div>
      {error && <div style={{ marginTop: 8, fontSize: 12, color: C.dangerText }}>{error}</div>}
    </Modal>
  );
}

// Относительное время последнего изменения заметки (из updatedAt)
function relTime(iso: string): string {
  const t = Date.parse(iso);
  if (Number.isNaN(t)) return '';
  const s = Math.max(0, (Date.now() - t) / 1000);
  if (s < 60) return 'только что';
  const m = Math.floor(s / 60);
  if (m < 60) return `${m} мин назад`;
  const h = Math.floor(m / 60);
  if (h < 24) return `${h} ч назад`;
  const d = Math.floor(h / 24);
  if (d < 7) return `${d} дн назад`;
  return new Date(t).toLocaleDateString('ru-RU', { day: 'numeric', month: 'short' });
}

