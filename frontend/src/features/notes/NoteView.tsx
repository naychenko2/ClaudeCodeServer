import { useEffect, useRef, useState } from 'react';
import type { NoteDetail } from '../../types';
import { api } from '../../lib/api';
import { bumpNotes, useNotesVersion } from '../../lib/notes';
import { C, FONT, R, TB } from '../../lib/design';
import { lazy, Suspense } from 'react';
import { MarkdownViewer } from '../../components/MarkdownViewer';
// CodeMirror тяжёлый — редактор грузим лениво, только при входе в правку
const NoteEditor = lazy(() => import('./NoteEditor').then(m => ({ default: m.NoteEditor })));
import { IconButton, Splitter, Modal } from '../../components/ui';
import { useNotes } from '../../lib/notes';
import { NoteConnections } from './NoteConnections';
import {
  SourceBadge, usePanelWidth,
  IconEye, IconPencil, IconChat, IconTrash, IconGraph, IconLink, IconSparkle, IconFolder, IconFolderMove,
} from './shared';

// Просмотр и правка одной заметки; связи (backlinks/исходящие/упоминания/граф) —
// в правом сайдбаре на десктопе, снизу на мобильном.
export function NoteView({ noteId, existingTitles, onWikilink, onAskClaude, onSelectNote, onDeleted, onTag, isMobile }: {
  noteId: string;
  existingTitles: Set<string>;
  onWikilink: (target: string) => void;
  onAskClaude?: (note: NoteDetail) => void;
  onSelectNote: (id: string) => void;
  onDeleted: () => void;
  onTag?: (tag: string) => void;
  isMobile?: boolean;
}) {
  const version = useNotesVersion();
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
    api.notes.get(noteId)
      .then(n => { if (alive) { setNote(n); if (!editingRef.current) { setDraftTitle(n.title); setDraftBody(n.content); } } })
      .catch(() => { if (alive) setNote(null); })
      .finally(() => { if (alive) setLoading(false); });
    return () => { alive = false; };
    // Перечитываем при смене заметки и при realtime-изменении (version)
  }, [noteId, version]);

  const startEdit = () => { if (note) { setDraftTitle(note.title); setDraftBody(note.content); setEditing(true); } };

  // Резолв вики-имени для hover-preview и embed ![[…]] (фрагмент по якорю приоритетен)
  const resolveNote = async (name: string, anchor?: string) => {
    try {
      const r = await api.notes.resolve(name, anchor);
      return { title: r.note.title, content: r.fragment ?? r.note.content };
    } catch { return null; }
  };

  // ✨ AI-помощь: предложение связей, тегов, конспект дня.
  // 'error' — ИИ недоступен (нет логина claude/таймаут): показываем явно, не молчим.
  const [aiLinks, setAiLinks] = useState<{ title: string; why: string }[] | 'loading' | 'error' | null>(null);
  const [aiTags, setAiTags] = useState<string[] | 'loading' | 'error' | null>(null);
  const [aiBusy, setAiBusy] = useState(false);
  // Ручное добавление тега (инлайн-инпут у чипов)
  const [addingTag, setAddingTag] = useState(false);
  const [newTag, setNewTag] = useState('');
  const isDaily = note?.source === 'personal' && note.path.startsWith('Journal/');

  const suggestLinks = () => {
    if (!note) return;
    setAiLinks('loading');
    api.notes.suggestLinks(note.id)
      .then(l => setAiLinks(l))
      .catch(() => setAiLinks('error'));
  };
  const suggestTags = () => {
    if (!note) return;
    setAiTags('loading');
    api.notes.suggestTags(note.id)
      .then(t => setAiTags(t))
      .catch(() => setAiTags('error'));
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
    setAiLinks(prev => Array.isArray(prev) ? prev.filter(l => l.title !== title) : prev);
    bumpNotes();
  };
  // Принять тег (ИИ-предложение или ручной ввод): inline #тег в конец заметки
  const acceptTag = async (tagRaw: string) => {
    if (!note) return;
    const tag = tagRaw.trim().replace(/^#+/, '').replace(/\s+/g, '-');
    if (!tag || note.tags.some(t => t.toLowerCase() === tag.toLowerCase())) return;
    const updated = await api.notes.update(note.id, { content: note.content.trimEnd() + ` #${tag}\n` });
    setNote(updated);
    setAiTags(prev => Array.isArray(prev) ? prev.filter(t => t !== tagRaw) : prev);
    bumpNotes();
  };
  // Конспект дня (только в daily-заметке)
  const makeDailySummary = () => {
    if (!note) return;
    setAiBusy(true);
    const day = note.path.replace(/^Journal\//, '').replace(/\.md$/, '');
    api.notes.dailySummary(day)
      .then(n => { setNote(n); bumpNotes(); })
      .finally(() => setAiBusy(false));
  };

  const save = async () => {
    if (!note) return;
    setSaving(true);
    try {
      const updated = await api.notes.update(note.id, {
        title: draftTitle !== note.title ? draftTitle : undefined,
        content: draftBody,
      });
      setEditing(false);
      bumpNotes();
      // id мог смениться при переименовании — переключаемся на новый
      if (updated.id !== note.id) onSelectNote(updated.id);
      else setNote(updated);
    } finally {
      setSaving(false);
    }
  };

  const del = async () => {
    if (!note) return;
    if (!window.confirm(`Удалить заметку «${note.title}»?`)) return;
    await api.notes.delete(note.id);
    bumpNotes();
    onDeleted();
  };

  // «Связать» несвязанное упоминание (кнопка в блоке связей)
  const linkMention = (targetTitle: string) => {
    if (!note) return;
    void api.notes.linkMention(note.id, targetTitle).then(n => { if (n) setNote(n); bumpNotes(); });
  };

  // Перенос в папку (модалка; на мобиле — bottom-sheet)
  const allNotes = useNotes();
  const [showMove, setShowMove] = useState(false);
  const [moveError, setMoveError] = useState<string | null>(null);
  const currentDir = note && note.path.includes('/') ? note.path.slice(0, note.path.lastIndexOf('/')) : '';
  const moveTo = async (folder: string) => {
    if (!note) return;
    setMoveError(null);
    try {
      const updated = await api.notes.move(note.id, folder || null);
      setShowMove(false);
      bumpNotes();
      if (updated.id !== note.id) onSelectNote(updated.id);
      else setNote(updated);
    } catch {
      setMoveError('Не удалось перенести: в целевой папке уже есть заметка с таким именем');
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
        {editing
          ? <input
              value={draftTitle}
              onChange={e => setDraftTitle(e.target.value)}
              style={{
                flex: 1, minWidth: 120, fontFamily: FONT.serif, fontSize: 16, fontWeight: 700,
                color: C.textHeading, background: C.bgWhite, border: `1px solid ${C.border}`,
                borderRadius: R.md, padding: '5px 10px',
              }}
            />
          : <h1 title={note.title} style={{ flex: 1, minWidth: 0, margin: 0, fontFamily: FONT.serif, fontSize: 16, fontWeight: 700, color: C.textHeading, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>{note.title}</h1>}
        {!isMobile && <SourceBadge source={note.source} label={note.sourceLabel} />}
        {!editing && !isMobile && <span style={{ fontSize: 11, color: C.textMuted, flex: 'none' }}>изменено {relTime(note.updatedAt)}</span>}
        <div style={{ display: 'flex', gap: 2, flex: 'none' }}>
          <IconButton title={editing ? 'Просмотр' : 'Читать'} active={!editing} onClick={() => setEditing(false)}><IconEye /></IconButton>
          <IconButton title="Править" active={editing} onClick={startEdit}><IconPencil /></IconButton>
          {onAskClaude && <IconButton title="Спросить Claude про это" onClick={() => onAskClaude(note)}><IconChat /></IconButton>}
          {!editing && <IconButton title="Переместить в папку…" onClick={() => { setMoveError(null); setShowMove(true); }}><IconFolderMove /></IconButton>}
          {!editing && <IconButton title="Предложить связи (AI)" tone="accent" onClick={suggestLinks}><IconSparkle /></IconButton>}
          {isDaily && !editing && (
            <IconButton title="Конспект дня (AI)" tone="accent" onClick={makeDailySummary} disabled={aiBusy}><IconGraph /></IconButton>
          )}
          <IconButton title="Удалить" tone="danger" onClick={del}><IconTrash /></IconButton>
        </div>
      </div>

      {/* Тело: контент + сайдбар связей (десктоп) */}
      <div style={{ flex: 1, minHeight: 0, display: 'flex', overflow: 'hidden' }}>
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
            <button onClick={suggestTags} title="Предложить теги (AI)"
              style={{ display: 'flex', alignItems: 'center', gap: 3, fontSize: 11, color: C.textMuted, background: 'none', border: `1px dashed ${C.dashed}`, borderRadius: R.sm, padding: '2px 7px', cursor: 'pointer', fontFamily: FONT.sans }}>
              <IconSparkle />{aiTags === 'loading' ? '…' : 'теги'}
            </button>
            {aiTags === 'error' && (
              <span style={{ fontSize: 11, color: C.dangerText }}>ИИ недоступен (claude не залогинен на сервере)</span>
            )}
            {Array.isArray(aiTags) && aiTags.length === 0 && (
              <span style={{ fontSize: 11, color: C.textMuted }}>нечего предложить</span>
            )}
            {Array.isArray(aiTags) && aiTags.map(t => (
              <button key={t} onClick={() => void acceptTag(t)} title="Добавить тег"
                style={{ fontSize: 11.5, fontWeight: 500, color: C.textSecondary, background: C.bgSelected, border: `1px dashed ${C.dashed}`, borderRadius: R.sm, padding: '2px 8px', cursor: 'pointer', fontFamily: FONT.sans }}>
                +#{t}
              </button>
            ))}
          </div>
        )}
        {!editing && aiLinks != null && (
          <div style={{ marginBottom: 12, padding: '9px 12px', background: C.accentLight, borderRadius: R.lg, border: `1px solid ${C.accentMuted}` }}>
            <div style={{ display: 'flex', alignItems: 'center', gap: 6, fontSize: 11.5, fontWeight: 600, color: aiLinks === 'error' ? C.dangerText : C.textSecondary, marginBottom: Array.isArray(aiLinks) && aiLinks.length > 0 ? 8 : 0 }}>
              <IconSparkle />
              {aiLinks === 'loading' ? 'Ищу связи…'
                : aiLinks === 'error' ? 'ИИ недоступен (claude не залогинен на сервере)'
                : aiLinks.length === 0 ? 'Подходящих связей не нашлось' : 'Предложенные связи'}
              <button onClick={() => setAiLinks(null)} style={{ marginLeft: 'auto', background: 'none', border: 'none', cursor: 'pointer', color: C.textMuted, fontSize: 13 }}>✕</button>
            </div>
            {Array.isArray(aiLinks) && aiLinks.map(l => (
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
          </div>
        )}
        {editing ? (
          <>
            <Suspense fallback={<div style={{ padding: 24, color: C.textMuted, fontSize: 13 }}>Загрузка редактора…</div>}>
              <NoteEditor value={draftBody} onChange={setDraftBody} minHeight={280}
                placeholder="Текст заметки… связывай через [[Заголовок]]" onWikilink={onWikilink} />
            </Suspense>
            <div style={{ display: 'flex', gap: 8, marginTop: 12 }}>
              <button onClick={save} disabled={saving} style={primaryBtn}>{saving ? 'Сохранение…' : 'Сохранить'}</button>
              <button onClick={() => { setEditing(false); setDraftBody(note.content); setDraftTitle(note.title); }} style={ghostBtn}>Отмена</button>
            </div>
          </>
        ) : (
          <MarkdownViewer content={note.content} existingTitles={existingTitles} onWikilink={onWikilink}
            resolveNote={resolveNote} embedSource={note.source} />
        )}

        {/* Мобильный: блок связей снизу под контентом (сайдбару нет места) */}
        {!editing && isMobile && (
          <div style={{ marginTop: 20, borderTop: `1px solid ${C.border}`, paddingTop: 12 }}>
            <NoteConnections note={note} onOpenNote={id => onSelectNote(id)}
              onWikilink={onWikilink} onLinkMention={linkMention} />
          </div>
        )}
      </div>

      {/* Десктоп: сайдбар связей справа — стиль как при просмотре заметки в файлах
          (прозрачный фон, тонкая линия-сплиттер), ширина перетаскивается */}
      {!editing && !isMobile && (
        <>
          <Splitter active={connDragging} onMouseDown={startConnDrag} />
          <aside style={{
            width: connWidth, flex: 'none', overflowY: 'auto',
            padding: '12px 14px 24px', boxSizing: 'border-box',
          }}>
            <NoteConnections note={note} onOpenNote={id => onSelectNote(id)}
              onWikilink={onWikilink} onLinkMention={linkMention} />
          </aside>
        </>
      )}
      </div>

      {showMove && (
        <MoveDialog
          currentDir={currentDir}
          folders={[...new Set(
            allNotes.filter(n => n.source === note.source && n.path.includes('/'))
              .flatMap(n => {
                const parts = n.path.slice(0, n.path.lastIndexOf('/')).split('/');
                return parts.map((_, i) => parts.slice(0, i + 1).join('/'));
              }),
          )].sort((a, b) => a.localeCompare(b, 'ru'))}
          error={moveError}
          onMove={moveTo}
          onClose={() => setShowMove(false)}
        />
      )}
    </div>
  );
}

// Диалог переноса в папку: существующие папки источника + ввод новой; «Корень» — наверх
function MoveDialog({ currentDir, folders, error, onMove, onClose }: {
  currentDir: string;
  folders: string[];
  error: string | null;
  onMove: (folder: string) => void;
  onClose: () => void;
}) {
  const [custom, setCustom] = useState('');
  const row = (label: React.ReactNode, folder: string, disabled: boolean) => (
    <button key={folder || '(root)'} disabled={disabled} onClick={() => onMove(folder)}
      style={{
        width: '100%', display: 'flex', alignItems: 'center', gap: 8, textAlign: 'left',
        padding: '8px 10px', borderRadius: R.md, border: 'none', fontFamily: FONT.sans, fontSize: 13,
        background: 'transparent', color: disabled ? C.textMuted : C.textPrimary,
        cursor: disabled ? 'default' : 'pointer', opacity: disabled ? 0.6 : 1,
      }}>
      <span style={{ color: C.accent, display: 'flex' }}><IconFolder /></span>
      <span style={{ flex: 1 }}>{label}</span>
      {disabled && <span style={{ fontSize: 10.5, color: C.textMuted }}>текущая</span>}
    </button>
  );
  return (
    <Modal width={420} title="Переместить в папку" onClose={onClose}>
      <div style={{ maxHeight: 300, overflowY: 'auto' }}>
        {row(<i>Корень</i>, '', currentDir === '')}
        {folders.map(f => row(f, f, f === currentDir))}
      </div>
      <div style={{ display: 'flex', gap: 8, marginTop: 12 }}>
        <input value={custom} onChange={e => setCustom(e.target.value)}
          onKeyDown={e => { if (e.key === 'Enter' && custom.trim()) onMove(custom.trim()); }}
          placeholder="Новая папка (Идеи/Черновики)"
          style={{ flex: 1, background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.md, padding: '7px 10px', fontSize: 13, fontFamily: FONT.sans, color: C.textHeading, outline: 'none' }} />
        <button onClick={() => custom.trim() && onMove(custom.trim())} disabled={!custom.trim()}
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

const primaryBtn: React.CSSProperties = {
  background: C.accent, color: C.onAccent, border: 'none', borderRadius: R.lg,
  padding: '8px 16px', fontSize: 13, fontWeight: 500, cursor: 'pointer', fontFamily: FONT.sans,
};
const ghostBtn: React.CSSProperties = {
  background: 'transparent', color: C.textSecondary, border: `1px solid ${C.border}`, borderRadius: R.lg,
  padding: '8px 16px', fontSize: 13, cursor: 'pointer', fontFamily: FONT.sans,
};
