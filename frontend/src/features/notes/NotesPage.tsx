import { useEffect, useMemo, useState } from 'react';
import type { AuthState, NoteDetail, NoteSource } from '../../types';
import type { HubTab } from '../../components/HubTabs';
import { HubHeader } from '../../components/HubHeader';
import { PillSwitch } from '../../components/Toolbar';
import { Modal } from '../../components/ui';
import { C, FONT, R } from '../../lib/design';
import { api } from '../../lib/api';
import { useNotes, ensureNotesLoaded, existingTitleSet, bumpNotes } from '../../lib/notes';
import { parseHash } from '../../lib/nav';
import { NotesList } from './NotesList';
import { NoteView } from './NoteView';
import { NotesGraph } from './NotesGraph';
import { IconSearch, IconPlus, IconBack, SourceDot } from './shared';

function useIsMobile(): boolean {
  const [m, setM] = useState(() => typeof window !== 'undefined' && window.matchMedia('(max-width: 767px)').matches);
  useEffect(() => {
    const mq = window.matchMedia('(max-width: 767px)');
    const h = (e: MediaQueryListEvent) => setM(e.matches);
    mq.addEventListener('change', h);
    return () => mq.removeEventListener('change', h);
  }, []);
  return m;
}

type Mode = 'notes' | 'graph';

export function NotesPage({ auth, onLogout, onHubTab }: {
  auth: AuthState;
  onLogout: () => void;
  onHubTab: (t: HubTab) => void;
}) {
  const isMobile = useIsMobile();
  const notes = useNotes();
  const [mode, setMode] = useState<Mode>('notes');
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [query, setQuery] = useState('');
  const [showNew, setShowNew] = useState(false);
  const [mobileView, setMobileView] = useState<'list' | 'note'>('list');
  // Фильтр источников для графа (null = все)
  const [hiddenSources, setHiddenSources] = useState<Set<string>>(new Set());

  useEffect(() => { void ensureNotesLoaded(); }, []);

  // Диплинк #/notes/{id}
  useEffect(() => {
    const t = parseHash();
    if (t?.screen === 'notes' && t.noteId) { setSelectedId(t.noteId); setMobileView('note'); }
  }, []);

  const existingTitles = useMemo(() => existingTitleSet(notes), [notes]);

  const filtered = useMemo(() => {
    const q = query.trim().toLowerCase();
    if (!q) return notes;
    return notes.filter(n =>
      n.title.toLowerCase().includes(q) || n.tags.some(t => t.toLowerCase().includes(q)));
  }, [notes, query]);

  const sources = useMemo(() => {
    const m = new Map<string, string>();
    notes.forEach(n => m.set(n.source, n.sourceLabel));
    return [...m.entries()].map(([key, label]) => ({ key, label }));
  }, [notes]);

  const sourceFilter = useMemo(
    () => hiddenSources.size === 0 ? null : new Set(sources.map(s => s.key).filter(k => !hiddenSources.has(k))),
    [hiddenSources, sources],
  );

  const selectNote = (id: string) => { setSelectedId(id); setMobileView('note'); };

  // Клик по [[wikilink]]: найти заметку по заголовку, иначе предложить создать
  const onWikilink = (target: string) => {
    const name = target.split('/').pop()!.split('#')[0].trim().toLowerCase();
    const found = notes.find(n => n.title.trim().toLowerCase() === name);
    if (found) { selectNote(found.id); return; }
    if (window.confirm(`Заметки «${target}» ещё нет. Создать?`)) {
      const source = selectedId ? notes.find(n => n.id === selectedId)?.source ?? 'personal' : 'personal';
      void api.notes.create({ title: target.split('/').pop()!.split('#')[0].trim(), source })
        .then(n => { bumpNotes(); selectNote(n.id); });
    }
  };

  const askClaude = (note: NoteDetail) => {
    sessionStorage.setItem('cc_pending_chat_prompt', `Про мою заметку «${note.title}»:\n\n${note.content}\n\n`);
    onHubTab('chats');
  };

  const toolbar = (
    <div style={{ display: 'flex', alignItems: 'center', gap: 10, padding: isMobile ? '8px 12px' : '10px 16px', borderBottom: `1px solid ${C.border}` }}>
      <PillSwitch<Mode>
        value={mode} onChange={setMode}
        options={[{ value: 'notes', label: 'Заметки' }, { value: 'graph', label: 'Граф' }]}
      />
      {mode === 'notes' && (
        <div style={{ flex: 1, display: 'flex', alignItems: 'center', gap: 7, background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.md, height: 32, padding: '0 10px', color: C.textMuted }}>
          <IconSearch />
          <input
            value={query} onChange={e => setQuery(e.target.value)}
            placeholder="Поиск по заметкам"
            style={{ flex: 1, border: 'none', outline: 'none', background: 'transparent', fontFamily: FONT.sans, fontSize: 13, color: C.textHeading }}
          />
        </div>
      )}
      {mode === 'graph' && <div style={{ flex: 1 }} />}
      <button onClick={() => setShowNew(true)} style={newBtn}>
        <IconPlus />{!isMobile && 'Новая'}
      </button>
    </div>
  );

  // --- Содержимое режима «Заметки» ---
  const notesContent = isMobile ? (
    mobileView === 'list' || !selectedId
      ? <div style={{ height: '100%', overflowY: 'auto' }}><NotesList notes={filtered} selectedId={selectedId} onSelect={selectNote} /></div>
      : <div style={{ height: '100%', display: 'flex', flexDirection: 'column' }}>
          <button onClick={() => setMobileView('list')} style={backBar}><IconBack /> К списку</button>
          <div style={{ flex: 1, minHeight: 0 }}>
            <NoteView key={selectedId} noteId={selectedId} existingTitles={existingTitles} onWikilink={onWikilink} onAskClaude={askClaude} onSelectNote={selectNote} onDeleted={() => { setSelectedId(null); setMobileView('list'); }} />
          </div>
        </div>
  ) : (
    <div style={{ height: '100%', display: 'flex' }}>
      <div style={{ width: 250, borderRight: `1px solid ${C.border}`, overflowY: 'auto', flex: 'none', background: C.bgPanel }}>
        <NotesList notes={filtered} selectedId={selectedId} onSelect={selectNote} />
      </div>
      <div style={{ flex: 1, minWidth: 0 }}>
        {selectedId
          ? <NoteView key={selectedId} noteId={selectedId} existingTitles={existingTitles} onWikilink={onWikilink} onAskClaude={askClaude} onSelectNote={selectNote} onDeleted={() => setSelectedId(null)} />
          : <div style={{ height: '100%', display: 'flex', alignItems: 'center', justifyContent: 'center', color: C.textMuted, fontFamily: FONT.sans, fontSize: 14 }}>Выбери заметку слева</div>}
      </div>
    </div>
  );

  // --- Содержимое режима «Граф» ---
  const graphContent = (
    <div style={{ height: '100%', display: 'flex' }}>
      {!isMobile && (
        <div style={{ width: 180, borderRight: `1px solid ${C.border}`, padding: '14px 12px', flex: 'none', background: C.bgPanel }}>
          <div style={{ fontSize: 10.5, letterSpacing: '.05em', textTransform: 'uppercase', color: C.textMuted, fontWeight: 600, marginBottom: 10 }}>Источники</div>
          {sources.map(s => {
            const on = !hiddenSources.has(s.key);
            return (
              <button key={s.key} onClick={() => setHiddenSources(prev => { const next = new Set(prev); on ? next.add(s.key) : next.delete(s.key); return next; })}
                style={{ width: '100%', display: 'flex', alignItems: 'center', gap: 8, padding: '5px 4px', background: 'none', border: 'none', cursor: 'pointer', fontFamily: FONT.sans, fontSize: 12.5, color: on ? C.textPrimary : C.textMuted, opacity: on ? 1 : 0.55 }}>
                <SourceDot source={s.key} />
                <span style={{ flex: 1, textAlign: 'left', whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>{s.label}</span>
              </button>
            );
          })}
          <div style={{ fontSize: 10.5, color: C.textMuted, lineHeight: 1.5, marginTop: 16 }}>
            Узел — заметка. Размер = число связей. Кольцо = выбранная. Наведи — подсветятся соседи.
          </div>
        </div>
      )}
      <div style={{ flex: 1, minWidth: 0 }}>
        <NotesGraph sourceFilter={sourceFilter} selectedId={selectedId} onSelectNode={selectNote} />
      </div>
    </div>
  );

  return (
    <div style={{ height: '100dvh', background: C.bgMain, fontFamily: FONT.sans, display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
      <HubHeader value="notes" onTab={onHubTab} auth={auth} onLogout={onLogout} />
      {toolbar}
      <div style={{ flex: 1, minHeight: 0 }}>
        {mode === 'notes' ? notesContent : graphContent}
      </div>
      {showNew && <NewNoteDialog onClose={() => setShowNew(false)} onCreated={id => { setShowNew(false); bumpNotes(); selectNote(id); }} />}
    </div>
  );
}

const newBtn: React.CSSProperties = {
  display: 'flex', alignItems: 'center', gap: 5, background: C.accent, color: C.onAccent,
  border: 'none', borderRadius: R.md, padding: '7px 12px', fontSize: 13, fontWeight: 500,
  cursor: 'pointer', fontFamily: FONT.sans, flex: 'none',
};
const backBar: React.CSSProperties = {
  display: 'flex', alignItems: 'center', gap: 6, padding: '8px 12px', background: C.bgPanel,
  border: 'none', borderBottom: `1px solid ${C.border}`, cursor: 'pointer', fontFamily: FONT.sans,
  fontSize: 13, color: C.textSecondary,
};

// --- Диалог создания заметки ---

function NewNoteDialog({ onClose, onCreated }: { onClose: () => void; onCreated: (id: string) => void }) {
  const [title, setTitle] = useState('');
  const [source, setSource] = useState('personal');
  const [sources, setSources] = useState<NoteSource[]>([{ key: 'personal', label: 'Личный' }]);
  const [busy, setBusy] = useState(false);

  useEffect(() => { api.notes.sources().then(setSources).catch(() => {}); }, []);

  const create = async () => {
    if (!title.trim()) return;
    setBusy(true);
    try {
      const note = await api.notes.create({ title: title.trim(), source });
      onCreated(note.id);
    } finally { setBusy(false); }
  };

  return (
    <Modal width={440} title="Новая заметка" onClose={onClose}
      footer={
        <div style={{ display: 'flex', gap: 8, justifyContent: 'flex-end' }}>
          <button onClick={onClose} style={{ background: 'transparent', border: `1px solid ${C.border}`, borderRadius: R.lg, padding: '8px 16px', fontSize: 13, cursor: 'pointer', fontFamily: FONT.sans, color: C.textSecondary }}>Отмена</button>
          <button onClick={create} disabled={busy || !title.trim()} style={{ background: C.accent, color: C.onAccent, border: 'none', borderRadius: R.lg, padding: '8px 16px', fontSize: 13, fontWeight: 500, cursor: 'pointer', fontFamily: FONT.sans, opacity: busy || !title.trim() ? 0.6 : 1 }}>Создать</button>
        </div>
      }
    >
      <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
        <div>
          <label style={fieldLabel}>Заголовок</label>
          <input autoFocus value={title} onChange={e => setTitle(e.target.value)}
            onKeyDown={e => { if (e.key === 'Enter') create(); }}
            placeholder="Название заметки"
            style={fieldInput} />
        </div>
        <div>
          <label style={fieldLabel}>Куда</label>
          <select value={source} onChange={e => setSource(e.target.value)} style={{ ...fieldInput, cursor: 'pointer' }}>
            {sources.map(s => <option key={s.key} value={s.key}>{s.label}</option>)}
          </select>
        </div>
      </div>
    </Modal>
  );
}

const fieldLabel: React.CSSProperties = {
  display: 'block', fontSize: 11, fontWeight: 600, textTransform: 'uppercase', letterSpacing: '.04em',
  color: C.textMuted, marginBottom: 6,
};
const fieldInput: React.CSSProperties = {
  width: '100%', boxSizing: 'border-box', background: C.bgWhite, border: `1px solid ${C.border}`,
  borderRadius: R.xl, padding: '9px 12px', fontSize: 14, fontFamily: FONT.sans, color: C.textHeading, outline: 'none',
};
