import { useEffect, useRef, useState } from 'react';
import type { NoteDetail } from '../../types';
import { api } from '../../lib/api';
import { bumpNotes, useNotesVersion } from '../../lib/notes';
import { C, FONT, R } from '../../lib/design';
import { lazy, Suspense } from 'react';
import { MarkdownViewer } from '../../components/MarkdownViewer';
// CodeMirror тяжёлый — редактор грузим лениво, только при входе в правку
const NoteEditor = lazy(() => import('./NoteEditor').then(m => ({ default: m.NoteEditor })));
import { IconButton, Splitter } from '../../components/ui';
import { NoteConnections } from './NoteConnections';
import {
  SourceBadge, usePanelWidth,
  IconEye, IconPencil, IconChat, IconTrash, IconGraph, IconLink, IconSparkle,
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

  // ✨ AI-помощь: предложение связей, тегов, конспект дня
  const [aiLinks, setAiLinks] = useState<{ title: string; why: string }[] | 'loading' | null>(null);
  const [aiTags, setAiTags] = useState<string[] | 'loading' | null>(null);
  const [aiBusy, setAiBusy] = useState(false);
  const isDaily = note?.source === 'personal' && note.path.startsWith('Journal/');

  const suggestLinks = () => {
    if (!note) return;
    setAiLinks('loading');
    api.notes.suggestLinks(note.id)
      .then(l => setAiLinks(l))
      .catch(() => setAiLinks([]));
  };
  const suggestTags = () => {
    if (!note) return;
    setAiTags('loading');
    api.notes.suggestTags(note.id)
      .then(t => setAiTags(t))
      .catch(() => setAiTags([]));
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
  // Принять тег: inline #тег в конец заметки
  const acceptTag = async (tag: string) => {
    if (!note) return;
    const updated = await api.notes.update(note.id, { content: note.content.trimEnd() + ` #${tag}\n` });
    setNote(updated);
    setAiTags(prev => Array.isArray(prev) ? prev.filter(t => t !== tag) : prev);
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

  if (loading && !note)
    return <div style={{ padding: 40, textAlign: 'center', color: C.textMuted, fontFamily: FONT.sans }}>Загрузка…</div>;
  if (!note)
    return <div style={{ padding: 40, textAlign: 'center', color: C.textMuted, fontFamily: FONT.sans }}>Заметка не найдена</div>;

  return (
    <div style={{ height: '100%', display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
      {/* Шапка */}
      <div style={{ display: 'flex', alignItems: 'center', gap: 10, padding: '14px 18px 10px', flexWrap: 'wrap' }}>
        {editing
          ? <input
              value={draftTitle}
              onChange={e => setDraftTitle(e.target.value)}
              style={{
                flex: 1, minWidth: 160, fontFamily: FONT.serif, fontSize: 20, fontWeight: 700,
                color: C.textHeading, background: C.bgWhite, border: `1px solid ${C.border}`,
                borderRadius: R.md, padding: '6px 10px',
              }}
            />
          : <h1 style={{ flex: 1, minWidth: 0, margin: 0, fontFamily: FONT.serif, fontSize: 22, fontWeight: 700, color: C.textHeading }}>{note.title}</h1>}
        <SourceBadge source={note.source} label={note.sourceLabel} />
        {!editing && <span style={{ fontSize: 11, color: C.textMuted }}>изменено {relTime(note.updatedAt)}</span>}
        <div style={{ display: 'flex', gap: 2 }}>
          <IconButton title={editing ? 'Просмотр' : 'Читать'} active={!editing} onClick={() => setEditing(false)}><IconEye /></IconButton>
          <IconButton title="Править" active={editing} onClick={startEdit}><IconPencil /></IconButton>
          {onAskClaude && <IconButton title="Спросить Claude про это" onClick={() => onAskClaude(note)}><IconChat /></IconButton>}
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
            <button onClick={suggestTags} title="Предложить теги (AI)"
              style={{ display: 'flex', alignItems: 'center', gap: 3, fontSize: 11, color: C.textMuted, background: 'none', border: `1px dashed ${C.dashed}`, borderRadius: R.sm, padding: '2px 7px', cursor: 'pointer', fontFamily: FONT.sans }}>
              <IconSparkle />{aiTags === 'loading' ? '…' : 'теги'}
            </button>
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
            <div style={{ display: 'flex', alignItems: 'center', gap: 6, fontSize: 11.5, fontWeight: 600, color: C.textSecondary, marginBottom: aiLinks === 'loading' || aiLinks.length === 0 ? 0 : 8 }}>
              <IconSparkle />
              {aiLinks === 'loading' ? 'Ищу связи…' : aiLinks.length === 0 ? 'Подходящих связей не нашлось' : 'Предложенные связи'}
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

      {/* Десктоп: сайдбар связей справа (ширина перетаскивается) */}
      {!editing && !isMobile && (
        <>
          <Splitter active={connDragging} onMouseDown={startConnDrag} />
          <aside style={{
            width: connWidth, flex: 'none', overflowY: 'auto',
            background: C.bgPanel, padding: '12px 12px 24px', boxSizing: 'border-box',
          }}>
            <NoteConnections note={note} onOpenNote={id => onSelectNote(id)}
              onWikilink={onWikilink} onLinkMention={linkMention} />
          </aside>
        </>
      )}
      </div>
    </div>
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
