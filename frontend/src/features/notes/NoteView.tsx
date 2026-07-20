import { useEffect, useRef, useState } from 'react';
import { Check, FileText, MessageCircle, Undo2, X } from 'lucide-react';
import type { NoteDetail, NoteReply } from '../../types';
import { api } from '../../lib/api';
import { bumpNotes, useNotesVersion } from '../../lib/notes';
import { C, FONT, R, TB } from '../../lib/design';
import { lazy, Suspense } from 'react';
import { MarkdownViewer } from '../../components/MarkdownViewer';
// CodeMirror тяжёлый — редактор грузим лениво, только при входе в правку
const NoteEditor = lazy(() => import('./NoteEditor').then(m => ({ default: m.NoteEditor })));
import { BackButton, ConfirmDialog, IconButton, Splitter, Modal, WaitingIndicator } from '../../components/ui';
import { useAiJob, runAiJob, patchAiJobResult, resetAiJob } from '../../lib/aiJobStore';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { tbBtnPrimary, tbBtnGhost } from '../../components/Toolbar';
import { useNotes } from '../../lib/notes';
import type { NoteSource } from '../../types';
import { NoteConnections } from './NoteConnections';
import { NoteTasksSection } from './NoteTasksSection';
import { DocCommentedMarkdown } from './DocComments';
import { useOnline } from '../../hooks/useOnline';
import { OfflineError } from '../../lib/offline';
import { getNoteForView, saveNoteOffline, deleteNoteOffline, offlineResolve } from '../../lib/notesOffline';
import { showToast } from '../../lib/toast';
import {
  SourceBadge, usePanelWidth,
  IconTrash, IconLink, IconSparkle, IconFolder, IconFolderMove,
} from './shared';

// Просмотр и правка одной заметки; связи (backlinks/исходящие/упоминания/граф) —
// в правом сайдбаре на десктопе, снизу на мобильном.
export function NoteView({ noteId, existingTitles, onWikilink, onAskClaude, onSelectNote, onDeleted, onTag, isMobile, connectionsBelow, onBack, extraToolbar }: {
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
}) {
  const version = useNotesVersion();
  const online = useOnline();
  // Перетаскиваемая ширина сайдбара связей (справа: тянем влево — растёт)
  const [connWidth, connDragging, startConnDrag] = usePanelWidth('cc_notes_conn_width', 280, 230, 460, true);
  const [note, setNote] = useState<NoteDetail | null>(null);
  const [loading, setLoading] = useState(true);
  const [editing, setEditing] = useState(false);
  const [draftTitle, setDraftTitle] = useState('');
  const [draftBody, setDraftBody] = useState('');
  const [saving, setSaving] = useState(false);
  const editingRef = useRef(false);
  editingRef.current = editing;

  useEffect(() => {
    let alive = true;
    setLoading(true);
    // Читаем через офлайн-слой (черновик/кэш офлайн, сеть онлайн)
    getNoteForView(noteId)
      .then(n => { if (alive) { setNote(n); if (!editingRef.current) { setDraftTitle(n.title); setDraftBody(n.content); } } })
      .catch(() => { if (alive) setNote(null); })
      .finally(() => { if (alive) setLoading(false); });
    return () => { alive = false; };
    // Перечитываем при смене заметки и при realtime-изменении (version)
  }, [noteId, version]);

  const startEdit = () => { if (note) { setDraftTitle(note.title); setDraftBody(note.content); setEditing(true); } };

  // Открыть чат, из которого создана заметка
  const openSession = async (sessionId: string) => {
    try {
      const chat = await api.chats.get(sessionId);
      if (chat) {
        window.dispatchEvent(new CustomEvent('cc-open-chat', { detail: { chatId: chat.id } }));
      }
    } catch { /* чат не найден — возможно удалён */ }
  };

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
    if (!note || !ann || ann.isReply) { setAnnReplies(null); return; }
    let alive = true;
    api.notes.replies(note.id).then(r => { if (alive) setAnnReplies(r); }).catch(() => {});
    return () => { alive = false; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [note?.id, version]);
  const sendAnnReply = async () => {
    if (!note || !annReplyDraft.trim()) return;
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
  const findDocNote = () =>
    ann && allNotes.find(n => n.source === ann.docScope &&
      (n.path.toLowerCase() === ann.docPath.toLowerCase() ||
       ('notes/' + n.path).toLowerCase() === ann.docPath.toLowerCase()));
  const gotoAnnTarget = () => {
    if (!ann) return;
    if (ann.docScope === 'personal' || ann.isReply) {
      const target = findDocNote();
      if (target) onSelectNote(target.id);
      return;
    }
    window.dispatchEvent(new CustomEvent('cc-open-url', {
      detail: { url: `#/project/${ann.docScope}/file/${encodeURIComponent(ann.docPath)}` },
    }));
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
    if (!note) return;
    const hasSection = /(^|\n)## Связанное\s*\n/.test(note.content);
    const content = hasSection
      ? note.content.trimEnd() + `\n- [[${title}]]\n`
      : note.content.trimEnd() + `\n\n## Связанное\n\n- [[${title}]]\n`;
    const updated = await api.notes.update(note.id, { content });
    setNote(updated);
    patchAiJobResult<{ title: string; why: string }[]>(linksKey, prev => prev.filter(l => l.title !== title));
    bumpNotes();
  };
  // Принять тег (ИИ-предложение или ручной ввод): inline #тег в конец заметки
  const acceptTag = async (tagRaw: string) => {
    if (!note) return;
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
      if (action === 'note.links') suggestLinks();
      else if (action === 'note.tags') suggestTags();
      else if (action === 'note.ask') onAskClaude?.(note);
      else if (action === 'note.daily') {
        if (isDaily) makeDailySummary();
        else showToast('Конспект дня', 'Доступно только в дневниковой заметке (раздел «Заметки» → сегодня)', 'info');
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
      const updated = await api.notes.update(note.id, {
        // Переименование — только онлайн (офлайн title не меняем)
        title: draftTitle !== note.title ? draftTitle : undefined,
        content: draftBody,
      });
      setEditing(false);
      bumpNotes();
      // id мог смениться при переименовании — переключаемся на новый
      if (updated.id !== note.id) onSelectNote(updated.id);
      else setNote(updated);
    } catch (e) {
      if (e instanceof OfflineError) {
        await saveNoteOffline(note.id, { content: draftBody });
        setEditing(false);
        setNote({ ...note, content: draftBody });
        bumpNotes();
      } else throw e;
    } finally {
      setSaving(false);
    }
  };

  // Удаление в два шага: запрос подтверждения (диалог) → само удаление
  const [deleteConfirm, setDeleteConfirm] = useState(false);
  const del = () => { if (note) setDeleteConfirm(true); };
  const doDelete = async () => {
    if (!note) return;
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
    if (!note) return;
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
    api.notes.sources().then(setMoveSources).catch(() => setMoveSources([]));
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
    if (!note) return;
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
    <div style={{ height: '100%', display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
      {/* Тулбар заметки — единый стиль тулбаров приложения (как FileViewer) */}
      <div style={{
        display: 'flex', alignItems: 'center', gap: 10, flex: 'none',
        minHeight: isMobile ? TB.heightMobile : TB.heightDesktop,
        padding: `4px ${isMobile ? TB.padXMobile : TB.padX}px`,
        boxSizing: 'border-box', background: TB.bg, borderBottom: TB.borderBottom,
      }}>
        {/* Мобайл: стрелка «назад» у заголовка — как у файлов/чатов */}
        {onBack && !editing && <BackButton onClick={onBack} title="К списку" style={{ height: 32 }} />}
        {editing
          ? <input
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
        {!editing && note.expiresAt && (() => {
          const left = new Date(note.expiresAt).getTime() - Date.now();
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
              <button onClick={() => { setEditing(false); setDraftBody(note.content); setDraftTitle(note.title); }} style={tbBtnGhost}>Отмена</button>
              <button onClick={save} disabled={saving}
                style={{ ...tbBtnPrimary, marginLeft: 6, ...(saving ? { opacity: 0.6, cursor: 'default' } : {}) }}>
                {saving ? 'Сохранение…' : 'Сохранить'}
              </button>
            </>
          ) : (
            <>
              {/* AI-действия (связи, теги, конспект дня, «спросить Claude») — только через AI-палитру (⌘/Ctrl+K) */}
              <IconButton title={online ? 'Переместить…' : 'Перенос недоступен офлайн'} onClick={openMove} disabled={!online}><IconFolderMove /></IconButton>
              <IconButton title="Удалить" tone="danger" onClick={del}><IconTrash /></IconButton>
              <button onClick={startEdit} style={{ ...tbBtnPrimary, marginLeft: 6 }}>Править</button>
            </>
          )}
          {extraToolbar}
        </div>
      </div>

      {/* Тело: контент + сайдбар связей (десктоп) */}
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
      <div style={{ flex: 1, minWidth: 0, overflowY: 'auto', padding: '4px 18px 24px' }}>
        {!editing && (
          <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6, marginBottom: 12, alignItems: 'center' }}>
            {note.tags.map(t => (
              <button key={t} onClick={() => onTag?.(t)} title={`Заметки с тегом ${t}`}
                style={{ fontSize: 11.5, fontWeight: 500, color: C.accent, background: C.accentLight, border: 'none', borderRadius: R.sm, padding: '2px 8px', cursor: onTag ? 'pointer' : 'default', fontFamily: FONT.sans }}>
                #{t}
              </button>
            ))}
            {/* Ручное добавление тега */}
            {addingTag ? (
                <input
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
                <span style={{ fontSize: 11, color: C.dangerText }}>ИИ недоступен (claude не залогинен на сервере)</span>
              )}
              {tagsJob.status === 'done' && (tagsJob.result ?? []).length === 0 && (
                <span style={{ fontSize: 11, color: C.textMuted }}>нечего предложить</span>
              )}
              {tagsJob.status === 'done' && (tagsJob.result ?? []).map(t => (
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
                  {linksJob.status === 'error' ? 'ИИ недоступен (claude не залогинен на сервере)'
                    : (linksJob.result?.length ?? 0) === 0 ? 'Подходящих связей не нашлось' : 'Предложенные связи'}
                  <button onClick={() => resetAiJob(linksKey)} style={{ marginLeft: 'auto', background: 'none', border: 'none', cursor: 'pointer', color: C.textMuted, padding: 0, display: 'flex' }}><X size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} /></button>
                </div>
                {(linksJob.result ?? []).map(l => (
                  <div key={l.title} style={{ display: 'flex', alignItems: 'center', gap: 8, padding: '3px 0' }}>
                    <span style={{ flex: 1, minWidth: 0, fontSize: 12.5 }}>
                      <span style={{ fontWeight: 500, color: C.textPrimary }}>{l.title}</span>
                      <span style={{ color: C.textMuted }}> — {l.why}</span>
                    </span>
                    <button onClick={() => void acceptLink(l.title)}
                      style={{ display: 'flex', alignItems: 'center', gap: 4, flex: 'none', fontSize: 11, fontWeight: 500, color: C.accent, background: C.bgWhite, border: `1px solid ${C.accentMuted}`, borderRadius: R.sm, padding: '3px 8px', cursor: 'pointer', fontFamily: FONT.sans }}>
                      <IconLink />Связать
                    </button>
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
              {!ann.isReply && (
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
                <div style={{ display: 'flex', gap: 6 }}>
                  <input
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
                </div>
              </div>
            )}
          </div>
        )}
        {ann ? (
          <MarkdownViewer content={note.content} existingTitles={existingTitles} onWikilink={onWikilink}
            resolveNote={resolveNote} embedSource={note.source} />
        ) : (
          // Обычная заметка — тоже документ: выделение → комментарий, панель снизу
          // (правая колонка занята связями). Заметки-комментарии не аннотируются
          // выделением — тред ведётся ответами.
          <DocCommentedMarkdown
            scope={note.source}
            docPath={note.source === 'personal' ? note.path : 'notes/' + note.path}
            content={note.content}
            isMobile={isMobile}
            panelBelow
            viewer={{ onWikilink, existingTitles, resolveNote, embedSource: note.source }}
          />
        )}

        {/* Мобильный/планшет: задачи из заметки + связи снизу под контентом (сайдбару нет места) */}
        {(isMobile || connectionsBelow) && (
          <div style={{ marginTop: 20, borderTop: `1px solid ${C.border}`, paddingTop: 12 }}>
            <NoteTasksSection noteId={note.id} version={version} />
            <NoteConnections note={note} onOpenNote={id => onSelectNote(id)}
              onWikilink={onWikilink} onLinkMention={linkMention} />
          </div>
        )}
      </div>
      )}

      {/* Десктоп: сайдбар связей справа — стиль как при просмотре заметки в файлах
          (прозрачный фон, тонкая линия-сплиттер), ширина перетаскивается.
          На планшете/узком экране (connectionsBelow) связи уходят под контент. */}
      {!editing && !isMobile && !connectionsBelow && (
        <>
          <Splitter active={connDragging} onMouseDown={startConnDrag} />
          <aside style={{
            width: connWidth, flex: 'none', overflowY: 'auto',
            padding: '12px 14px 24px', boxSizing: 'border-box',
          }}>
            <NoteTasksSection noteId={note.id} version={version} />
            <NoteConnections note={note} onOpenNote={id => onSelectNote(id)}
              onWikilink={onWikilink} onLinkMention={linkMention} />
          </aside>
        </>
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
    </div>
  );
}

// Диалог переноса: выбор источника (личный vault / проекты) + папки выбранного
// источника + ввод новой папки; «Корень» — наверх выбранного источника.
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
        <input value={custom} onChange={e => setCustom(e.target.value)}
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

