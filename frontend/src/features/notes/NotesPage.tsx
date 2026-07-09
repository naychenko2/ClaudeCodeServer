import { useEffect, useMemo, useState } from 'react';
import type { AuthState, NoteDetail, NoteSemanticHit, NoteSummary } from '../../types';
import type { HubTab } from '../../components/HubTabs';
import { HubHeader } from '../../components/HubHeader';
import { PillSwitch } from '../../components/Toolbar';
import { NewNoteDialog } from './NewNoteDialog';
import { C, FONT, R } from '../../lib/design';
import { api } from '../../lib/api';
import { useNotes, ensureNotesLoaded, existingTitleSet, bumpNotes } from '../../lib/notes';
import { parseHash, navPush, navReplace, getNav, type NavSnapshot } from '../../lib/nav';
import { NotesList } from './NotesList';
import { NoteView } from './NoteView';
import { NotesGraph, type GraphStats } from './NotesGraph';
import { GraphSettingsBody } from './graph/GraphSettingsBody';
import { useGraphSettings } from './graph/graphSettings';
import { EmptyState } from '../../components/EmptyState';
import { Splitter } from '../../components/ui';
import { IconSearch, IconPlus, IconNotes, IconCalendarDay, SourceDot, usePanelWidth } from './shared';

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
  // Диалог создания: null — закрыт; поля — préfill (создание из «+» на папке)
  const [newDialog, setNewDialog] = useState<{ source?: string; folder?: string } | null>(null);
  const [mobileView, setMobileView] = useState<'list' | 'note'>('list');
  // Настройки графа подняты сюда: сайдбар в режиме «Граф» показывает и правит их
  const [graphSettings, setGraphSettings] = useGraphSettings('cc_graph_global');
  const [graphStats, setGraphStats] = useState<GraphStats | null>(null);

  // Перетаскиваемая ширина сайдбара (персист, как в Workspace)
  const [listWidth, listDragging, startListDrag] = usePanelWidth('cc_notes_list_width', 260, 210, 420);

  useEffect(() => { void ensureNotesLoaded(); }, []);

  // Диплинк #/notes/{id}
  useEffect(() => {
    const t = parseHash();
    if (t?.screen === 'notes' && t.noteId) { setSelectedId(t.noteId); setMobileView('note'); }
  }, []);

  // Back/forward браузера внутри вкладки «Заметки» — синхронизируем открытую заметку из истории
  useEffect(() => {
    const onPop = (e: PopStateEvent) => {
      const s = e.state as NavSnapshot | null;
      if (s?.screen === 'notes') {
        setSelectedId(s.note ?? null);
        setMobileView(s.note ? 'note' : 'list');
      }
    };
    window.addEventListener('popstate', onPop);
    return () => window.removeEventListener('popstate', onPop);
  }, []);

  // Открытие заметки по заголовку (из [[wikilink]] в файлах/чате). Заголовок в
  // sessionStorage; ждём загрузки списка (deps: notes) и открываем по совпадению.
  useEffect(() => {
    const consume = () => {
      // По id (из графа сайдбара FileViewer) — приоритетнее
      const id = sessionStorage.getItem('cc_pending_note_id');
      if (id) { sessionStorage.removeItem('cc_pending_note_id'); setSelectedId(id); setMobileView('note'); navPush({ screen: 'notes', note: id }); return; }
      const title = sessionStorage.getItem('cc_pending_note_title');
      if (!title) return;
      const n = notes.find(x => x.title.trim().toLowerCase() === title.trim().toLowerCase());
      if (n) { sessionStorage.removeItem('cc_pending_note_title'); setSelectedId(n.id); setMobileView('note'); navPush({ screen: 'notes', note: n.id }); }
    };
    consume();
    window.addEventListener('cc-open-note', consume);
    return () => window.removeEventListener('cc-open-note', consume);
  }, [notes]);

  const existingTitles = useMemo(() => existingTitleSet(notes), [notes]);

  // Поиск: точный (по заголовку/тексту/тегам, серверный) или «по смыслу» (Dify RAG).
  const [semanticAvailable, setSemanticAvailable] = useState(false);
  const [searchMode, setSearchMode] = useState<'exact' | 'semantic'>('exact');
  useEffect(() => { api.notes.caps().then(c => setSemanticAvailable(c.semantic)).catch(() => {}); }, []);

  const [results, setResults] = useState<NoteSummary[] | null>(null);
  const [semanticHits, setSemanticHits] = useState<NoteSemanticHit[] | null>(null);
  useEffect(() => {
    const q = query.trim();
    if (!q) { setResults(null); setSemanticHits(null); return; }
    const t = setTimeout(() => {
      if (searchMode === 'semantic' && semanticAvailable)
        api.notes.semantic(q).then(r => setSemanticHits(r.results)).catch(() => setSemanticHits([]));
      else
        api.notes.list(undefined, q).then(setResults).catch(() => {});
    }, searchMode === 'semantic' ? 450 : 250);
    return () => clearTimeout(t);
  }, [query, searchMode, semanticAvailable]);
  const listed = results ?? notes;
  const showSemantic = searchMode === 'semantic' && query.trim().length > 0;

  // Источники и теги для фильтров графа — из списка заметок
  const graphSources = useMemo(() => {
    const m = new Map<string, string>();
    notes.forEach(n => m.set(n.source, n.sourceLabel));
    return [...m.entries()].map(([key, label]) => ({ key, label }));
  }, [notes]);
  const graphTags = useMemo(() => {
    const s = new Set<string>();
    notes.forEach(n => n.tags.forEach(t => s.add(t)));
    return [...s].sort((a, b) => a.localeCompare(b, 'ru'));
  }, [notes]);

  const selectNote = (id: string) => { setSelectedId(id); setMobileView('note'); navPush({ screen: 'notes', note: id }); };

  // Возврат к списку (удаление/кнопка «назад» на мобилке): снимаем выбор и
  // откатываем запись истории с note к списку (детерминированно, как в «Чатах»).
  const clearNote = () => { setSelectedId(null); setMobileView('list'); if (getNav()?.note) navReplace({ screen: 'notes' }); };

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

  // Дневниковая заметка на сегодня (get-or-create по локальной дате устройства)
  const openDaily = () => {
    const d = new Date();
    const iso = `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
    void api.notes.daily(iso).then(n => { bumpNotes(); setMode('notes'); selectNote(n.id); });
  };

  const askClaude = (note: NoteDetail) => {
    sessionStorage.setItem('cc_pending_chat_prompt', `Про мою заметку «${note.title}»:\n\n${note.content}\n\n`);
    // Событие для уже смонтированного композера + переключение на «Чаты»
    window.dispatchEvent(new Event('cc-compose-prefill'));
    onHubTab('chats');
  };

  // Панель управления в сайдбаре (как у Workspace: всё управление разделом — слева)
  const sidebarControls = (
    <div style={{ padding: '10px 10px 9px', borderBottom: `1px solid ${C.border}`, display: 'flex', flexDirection: 'column', gap: 8, flex: 'none' }}>
      <PillSwitch<Mode>
        fill
        value={mode} onChange={setMode}
        options={[{ value: 'notes', label: 'Заметки' }, { value: 'graph', label: 'Граф' }]}
      />
      {mode === 'notes' && (
        <div style={{ display: 'flex', alignItems: 'center', gap: 6, background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.md, height: 30, padding: '0 8px', color: C.textMuted }}>
          <IconSearch />
          <input
            value={query} onChange={e => setQuery(e.target.value)}
            placeholder="Поиск…"
            title="Операторы: tag:идея source:Личный"
            style={{ flex: 1, minWidth: 0, border: 'none', outline: 'none', background: 'transparent', fontFamily: FONT.sans, fontSize: 12.5, color: C.textHeading }}
          />
          {semanticAvailable && (
            <button
              onClick={() => setSearchMode(m => m === 'exact' ? 'semantic' : 'exact')}
              title={searchMode === 'semantic' ? 'Поиск по смыслу (семантический) — включён' : 'Точный поиск по тексту — включён; клик = по смыслу'}
              style={{
                fontSize: 10, fontWeight: 600, border: 'none', borderRadius: 5, padding: '3px 6px',
                cursor: 'pointer', fontFamily: FONT.sans, flex: 'none',
                background: searchMode === 'semantic' ? C.accent : C.bgSelected,
                color: searchMode === 'semantic' ? C.onAccent : C.textMuted,
              }}>смысл</button>
          )}
        </div>
      )}
      <div style={{ display: 'flex', gap: 6 }}>
        <button onClick={() => setNewDialog({})} style={{ ...newBtn, flex: 1, justifyContent: 'center' }}>
          <IconPlus />Новая
        </button>
        <button onClick={openDaily} title="Дневниковая заметка на сегодня"
          style={{ ...newBtn, background: 'transparent', color: C.textSecondary, border: `1px solid ${C.border}` }}>
          <IconCalendarDay />
        </button>
      </div>
    </div>
  );

  // Сайдбар в режиме «Граф»: статистика + полный набор настроек (фильтры, группы,
  // отображение, силы). Настройки — из поднятого сюда состояния graphSettings.
  const graphSidebar = (
    <div style={{ padding: '10px 12px 16px' }}>
      <div style={{ display: 'flex', alignItems: 'baseline', gap: 6, marginBottom: 10, fontSize: 12, color: C.textSecondary }}>
        {graphStats ? (
          <>
            <span style={{ fontWeight: 600, color: C.textPrimary }}>{graphStats.shown}</span>
            <span>{graphStats.shown === graphStats.total ? 'заметок' : `из ${graphStats.total}`}</span>
            <span style={{ color: C.textMuted }}>· {graphStats.edges} связей</span>
          </>
        ) : <span style={{ color: C.textMuted }}>Граф связей</span>}
      </div>
      <GraphSettingsBody
        settings={graphSettings}
        onChange={setGraphSettings}
        sources={graphSources}
        tags={graphTags}
        localMode={false}
      />
      <div style={{ fontSize: 10.5, color: C.textMuted, lineHeight: 1.6, marginTop: 14, paddingTop: 12, borderTop: `1px solid ${C.border}` }}>
        Колесо/щипок — зум, тяни узел — сдвинуть, двойной клик по фону — показать весь граф. Наведи на узел — подсветятся соседи.
      </div>
    </div>
  );

  // --- Содержимое режима «Заметки» ---
  const listPane = showSemantic
    ? <SemanticResults hits={semanticHits} selectedId={selectedId} onSelect={selectNote} />
    : <NotesList notes={listed} selectedId={selectedId} onSelect={selectNote} isMobile={isMobile}
        onMoved={(oldId, newId) => { if (selectedId === oldId) { setSelectedId(newId); navReplace({ screen: 'notes', note: newId }); } }}
        onCreateInFolder={(source, folder) => setNewDialog({ source, folder })}
        onDeleted={ids => { if (selectedId && ids.includes(selectedId)) clearNote(); }}
        onIdsRemapped={map => {
          const hit = selectedId && map.find(m => m.oldId === selectedId);
          if (hit) { setSelectedId(hit.newId); navReplace({ screen: 'notes', note: hit.newId }); }
        }} />;

  // Сайдбар целиком: управление сверху, ниже — список (режим «Заметки») или фильтры («Граф»)
  const sidebar = (
    <>
      {sidebarControls}
      <div style={{ flex: 1, minHeight: 0, overflowY: 'auto' }}>
        {mode === 'notes' ? listPane : graphSidebar}
      </div>
    </>
  );

  // Центральная зона: заметка/пустое состояние или граф
  const centerPane = mode === 'graph'
    ? <NotesGraph selectedId={selectedId} onSelectNode={id => { setMode('notes'); selectNote(id); }}
        maxNodes={isMobile ? 40 : undefined}
        settings={graphSettings} onSettingsChange={setGraphSettings}
        hidePanel={!isMobile} onStats={setGraphStats} />
    : selectedId
      ? <NoteView key={selectedId} noteId={selectedId} existingTitles={existingTitles} onWikilink={onWikilink}
          onAskClaude={askClaude} onSelectNote={selectNote} onTag={setQuery} isMobile={isMobile}
          onBack={isMobile ? clearNote : undefined}
          onDeleted={clearNote} />
      : <div style={{ height: '100%', display: 'flex', alignItems: 'center', justifyContent: 'center' }}>
          <EmptyState icon={<IconNotes />} title="Заметки"
            subtitle={notes.length ? 'Выбери заметку слева или создай новую' : 'Создай первую заметку или попроси ассистента законспектировать разговор'}
            action={<button onClick={() => setNewDialog({})} style={newBtn}><IconPlus />Новая заметка</button>} />
        </div>;

  const body = isMobile ? (
    // Мобайл: один экран за раз — сайдбар (список/фильтры+граф) ↔ заметка
    mode === 'graph'
      ? <div style={{ height: '100%', display: 'flex', flexDirection: 'column', background: C.bgPanel }}>
          {sidebarControls}
          <div style={{ flex: 1, minHeight: 0, background: C.bgMain }}>{centerPane}</div>
        </div>
      : (mobileView === 'list' || !selectedId)
        ? <div style={{ height: '100%', display: 'flex', flexDirection: 'column', background: C.bgPanel }}>{sidebar}</div>
        // Возврат к списку — стрелкой/заголовком в тулбаре заметки (onBack), как у файлов
        : <div style={{ height: '100%', display: 'flex', flexDirection: 'column' }}>{centerPane}</div>
  ) : (
    // Десктоп: сайдбар (управление + список/фильтры) | центр — как в Workspace
    <div style={{ height: '100%', display: 'flex' }}>
      <div style={{ width: listWidth, flex: 'none', background: C.bgPanel, display: 'flex', flexDirection: 'column' }}>
        {sidebar}
      </div>
      <Splitter active={listDragging} onMouseDown={startListDrag} />
      <div style={{ flex: 1, minWidth: 0 }}>{centerPane}</div>
    </div>
  );

  return (
    <div style={{ height: '100dvh', background: C.bgMain, fontFamily: FONT.sans, display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
      <HubHeader value="notes" onTab={onHubTab} auth={auth} onLogout={onLogout} />
      <div style={{ flex: 1, minHeight: 0 }}>
        {body}
      </div>
      {newDialog && <NewNoteDialog defaults={newDialog} onClose={() => setNewDialog(null)} onCreated={id => { setNewDialog(null); bumpNotes(); selectNote(id); }} />}
    </div>
  );
}

const newBtn: React.CSSProperties = {
  display: 'flex', alignItems: 'center', gap: 5, background: C.accent, color: C.onAccent,
  border: 'none', borderRadius: R.md, padding: '7px 12px', fontSize: 13, fontWeight: 500,
  cursor: 'pointer', fontFamily: FONT.sans, flex: 'none',
};

// Результаты семантического поиска: заметка + score + сниппет чанка
function SemanticResults({ hits, selectedId, onSelect }: {
  hits: NoteSemanticHit[] | null;
  selectedId: string | null;
  onSelect: (id: string) => void;
}) {
  if (hits === null)
    return <div style={{ padding: '20px 12px', color: C.textMuted, fontSize: 13, fontFamily: FONT.sans }}>Ищу по смыслу…</div>;
  if (hits.length === 0)
    return <div style={{ padding: '20px 12px', color: C.textMuted, fontSize: 13, fontFamily: FONT.sans }}>Ничего близкого не нашлось</div>;
  return (
    <div style={{ padding: '8px 8px 20px' }}>
      {hits.map(h => (
        <button key={h.id} onClick={() => onSelect(h.id)}
          style={{
            width: '100%', textAlign: 'left', border: 'none', cursor: 'pointer',
            padding: '7px 8px', borderRadius: R.md, fontFamily: FONT.sans, marginBottom: 2,
            background: h.id === selectedId ? C.accentMuted : 'transparent',
          }}>
          <span style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
            <SourceDot source={h.source} size={7} />
            <span style={{ flex: 1, fontSize: 12.5, fontWeight: 500, color: C.textPrimary, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>{h.title}</span>
            <span style={{ fontSize: 10, fontFamily: FONT.mono, color: C.accent }}>{Math.round(h.score * 100)}%</span>
          </span>
          <span style={{ display: 'block', fontSize: 11, color: C.textMuted, marginTop: 2, lineHeight: 1.4,
            overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{h.snippet}</span>
        </button>
      ))}
    </div>
  );
}

