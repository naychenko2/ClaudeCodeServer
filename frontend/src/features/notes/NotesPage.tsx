import { useEffect, useMemo, useRef, useState } from 'react';
import { ChevronLeft, Menu as MenuIcon, Pin } from 'lucide-react';
import type { AuthState, NoteDetail, NoteSemanticHit, NoteSummary } from '../../types';
import type { HubTab } from '../../components/HubTabs';
import { HubHeader } from '../../components/HubHeader';
import { PillSwitch } from '../../components/Toolbar';
import { NewNoteDialog } from './NewNoteDialog';
import { C, FONT, R } from '../../lib/design';
import { api } from '../../lib/api';
import { useNotes, ensureNotesLoaded, existingTitleSet, bumpNotes } from '../../lib/notes';
import { useOnline } from '../../hooks/useOnline';
import { OfflineError } from '../../lib/offline';
import { createNoteOffline } from '../../lib/notesOffline';
import { parseHash, navPush, navReplace, getNav, type NavSnapshot } from '../../lib/nav';
import { NotesList } from './NotesList';
import { NoteView } from './NoteView';
import { NotesGraph, type GraphStats } from './NotesGraph';
import { GraphSettingsBody } from './graph/GraphSettingsBody';
import { useGraphSettings } from './graph/graphSettings';
import { EmptyState } from '../../components/EmptyState';
import { Splitter, IconButton, ConfirmDialog } from '../../components/ui';
import { ICON_SIZE } from '../../components/ui/icons';
import { IconSearch, IconPlus, IconNotes, IconCalendarDay, SourceDot } from './shared';
import { useSidebarDrag } from '../../lib/sidebarWidth';
import { useIsMobile, useWindowWidth } from '../../lib/breakpoints';
import { FLAGS, useFeature } from '../../lib/featureFlags';

type Mode = 'notes' | 'graph';

// Правый сайдбар связей внутри заметки уместен только на широком экране: список
// слева + контент + связи справа. Ниже этого порога (планшет/раскладной Galaxy
// Fold развёрнут ~716px) три колонки не помещаются — связи показываем снизу под
// контентом, а список слева остаётся (десктопная раскладка, порог 699).
const NOTE_CONN_SIDEBAR_MIN = 1000;

export function NotesPage({ auth, onLogout, onHubTab }: {
  auth: AuthState;
  onLogout: () => void;
  onHubTab: (t: HubTab) => void;
}) {
  const isMobile = useIsMobile();
  const windowWidth = useWindowWidth();
  // «Планшет»: раскладка десктопная (список слева), но правому сайдбару связей уже
  // мало места — показываем связи под контентом заметки.
  const connectionsBelow = !isMobile && windowWidth < NOTE_CONN_SIDEBAR_MIN;
  const notes = useNotes();
  const online = useOnline();
  const docAnnotationsOn = useFeature(FLAGS.docAnnotations);
  const [mode, setMode] = useState<Mode>('notes');
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [query, setQuery] = useState('');
  // Диалог создания: null — закрыт; поля — préfill (создание из «+» на папке)
  const [newDialog, setNewDialog] = useState<{ source?: string; folder?: string } | null>(null);
  const [mobileView, setMobileView] = useState<'list' | 'note'>('list');
  // Настройки графа подняты сюда: сайдбар в режиме «Граф» показывает и правит их
  const [graphSettings, setGraphSettings] = useGraphSettings('cc_graph_global');
  const [graphStats, setGraphStats] = useState<GraphStats | null>(null);

  // Ширина сайдбара — общая со всеми разделами (чаты/проекты/воркспейс)
  const { width: listWidth, dragging: listDragging, startDrag: startListDrag } = useSidebarDrag();

  // Режим сайдбара: pinned (в потоке) | collapsed (свёрнут) | open (drawer поверх),
  // как в «Чатах»/«Проектах»/воркспейсе. Персистим только pinned/collapsed.
  const [sidebarMode, setSidebarMode] = useState<'pinned' | 'collapsed' | 'open'>(() =>
    localStorage.getItem('cc_notes_sidebar_mode') === 'collapsed' ? 'collapsed' : 'pinned');
  useEffect(() => { if (sidebarMode !== 'open') localStorage.setItem('cc_notes_sidebar_mode', sidebarMode); }, [sidebarMode]);

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

  // Синхронизация офлайн-заметки сменила её id (localKey/старый serverId → серверный):
  // если открыта эта заметка — переключаемся на новый id.
  useEffect(() => {
    const onRemap = (e: Event) => {
      const { from, to } = (e as CustomEvent<{ from: string[]; to: string }>).detail;
      setSelectedId(cur => (cur && from.includes(cur) ? to : cur));
    };
    window.addEventListener('cc-note-remapped', onRemap);
    return () => window.removeEventListener('cc-note-remapped', onRemap);
  }, []);

  const existingTitles = useMemo(() => existingTitleSet(notes), [notes]);

  // Поиск: точный (по заголовку/тексту/тегам, серверный) или «по смыслу» (Dify RAG).
  const [semanticAvailable, setSemanticAvailable] = useState(false);
  const [searchMode, setSearchMode] = useState<'exact' | 'semantic'>('exact');
  useEffect(() => { api.notes.caps().then(c => setSemanticAvailable(c.semantic)).catch(() => {}); }, []);

  const [results, setResults] = useState<NoteSummary[] | null>(null);
  const [semanticHits, setSemanticHits] = useState<NoteSemanticHit[] | null>(null);
  const searchInputRef = useRef<HTMLInputElement>(null);

  // AI-хаб: «Поиск по смыслу» из палитры — переключаем режим и ставим фокус в поиск
  useEffect(() => {
    const onRun = (e: Event) => {
      if ((e as CustomEvent<{ action?: string }>).detail?.action !== 'note.semantic') return;
      setMode('notes');
      if (semanticAvailable) setSearchMode('semantic');
      setTimeout(() => searchInputRef.current?.focus(), 50);
    };
    window.addEventListener('cc-ai-run', onRun);
    return () => window.removeEventListener('cc-ai-run', onRun);
  }, [semanticAvailable]);
  useEffect(() => {
    const q = query.trim();
    if (!q) { setResults(null); setSemanticHits(null); return; }
    // Офлайн — фильтруем кэшированный список локально (серверный поиск/семантика недоступны)
    if (!online) {
      const ql = q.toLowerCase();
      setResults(notes.filter(n => n.title.toLowerCase().includes(ql) || n.tags.some(t => t.toLowerCase().includes(ql))));
      setSemanticHits(null);
      return;
    }
    const t = setTimeout(() => {
      if (searchMode === 'semantic' && semanticAvailable)
        api.notes.semantic(q).then(r => setSemanticHits(r.results)).catch(() => setSemanticHits([]));
      else
        api.notes.list(undefined, q).then(setResults).catch(() => {});
    }, searchMode === 'semantic' ? 450 : 250);
    return () => clearTimeout(t);
  }, [query, searchMode, semanticAvailable, online, notes]);
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

  const selectNote = (id: string) => {
    setSelectedId(id); setMobileView('note'); navPush({ screen: 'notes', note: id });
    // В режиме drawer после выбора — закрываем оверлей (как в «Чатах»)
    setSidebarMode(m => m === 'open' ? 'collapsed' : m);
  };

  // Возврат к списку (удаление/кнопка «назад» на мобилке): снимаем выбор и
  // откатываем запись истории с note к списку (детерминированно, как в «Чатах»).
  const clearNote = () => { setSelectedId(null); setMobileView('list'); if (getNav()?.note) navReplace({ screen: 'notes' }); };

  // Клик по [[wikilink]]: найти заметку по заголовку, иначе предложить создать
  // (в два шага: диалог подтверждения → создание)
  const [wikilinkTarget, setWikilinkTarget] = useState<string | null>(null);
  const onWikilink = (target: string) => {
    const name = target.split('/').pop()!.split('#')[0].trim().toLowerCase();
    const found = notes.find(n => n.title.trim().toLowerCase() === name);
    if (found) { selectNote(found.id); return; }
    setWikilinkTarget(target);
  };
  const createFromWikilink = (target: string) => {
    setWikilinkTarget(null);
    const title = target.split('/').pop()!.split('#')[0].trim();
    const source = selectedId ? notes.find(n => n.id === selectedId)?.source ?? 'personal' : 'personal';
    void api.notes.create({ title, source })
      .then(n => { bumpNotes(); selectNote(n.id); })
      .catch(async e => {
        if (e instanceof OfflineError) {
          const localKey = await createNoteOffline({ title, source });
          bumpNotes(); selectNote(localKey);
        }
      });
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
            ref={searchInputRef}
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
      {/* Фильтры комментариев к документам (флаг doc-annotations): чипы = операторы status: */}
      {mode === 'notes' && docAnnotationsOn && notes.some(n => n.annotation) && (
        <div style={{ display: 'flex', gap: 5, flexWrap: 'wrap' }}>
          {([
            ['status:open', 'Открытые', notes.filter(n => n.annotation?.status === 'open').length],
            ['status:resolved', 'Решённые', notes.filter(n => n.annotation?.status === 'resolved').length],
            ['status:orphaned', 'Сироты', null],
          ] as const).map(([q, label, count]) => {
            const on = query.trim() === q;
            return (
              <button key={q} onClick={() => setQuery(on ? '' : q)} style={{
                display: 'inline-flex', alignItems: 'center', gap: 4,
                border: `1px solid ${on ? C.accent : C.border}`, borderRadius: 12,
                padding: '2px 9px', fontSize: 11, cursor: 'pointer', fontFamily: FONT.sans,
                background: on ? C.accentMuted : 'transparent',
                color: on ? C.textHeading : C.textMuted, fontWeight: on ? 600 : 400,
              }}>{label}{count != null && count > 0 ? ` · ${count}` : ''}</button>
            );
          })}
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

  // Строка управления панелью (только десктоп): свернуть (◀) + «Закрепить» (📌) в режиме drawer
  const sidebarHeader = (
    <div style={{ display: 'flex', alignItems: 'center', gap: 6, padding: '8px 10px 0', minHeight: 28, flex: 'none' }}>
      <IconButton onClick={() => setSidebarMode('collapsed')} title="Свернуть панель" size="sm" style={{ marginLeft: -2 }}>
        <ChevronLeft size={ICON_SIZE.sm} strokeWidth={2} />
      </IconButton>
      <span style={{ flex: 1, minWidth: 0, fontSize: 14, fontWeight: 600, color: C.textHeading, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>Заметки</span>
      {sidebarMode === 'open' && (
        <IconButton onClick={() => setSidebarMode('pinned')} title="Закрепить панель" size="sm">
          <Pin size={ICON_SIZE.sm} strokeWidth={2} />
        </IconButton>
      )}
    </div>
  );

  // Сайдбар целиком: управление сверху, ниже — список (режим «Заметки») или фильтры («Граф»)
  const sidebar = (
    <>
      {!isMobile && sidebarHeader}
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
        hidePanel={false} onStats={setGraphStats} />
    : selectedId
      ? <NoteView key={selectedId} noteId={selectedId} existingTitles={existingTitles} onWikilink={onWikilink}
          onAskClaude={askClaude} onSelectNote={selectNote} onTag={setQuery} isMobile={isMobile}
          connectionsBelow={connectionsBelow}
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
    // Десктоп: сайдбар (pinned/collapsed/open) | центр — как в «Чатах»/«Проектах»
    <div style={{ height: '100%', display: 'flex', position: 'relative' }}>
      {/* Pinned: в потоке + перетаскиваемый сплиттер */}
      {sidebarMode === 'pinned' && (
        <>
          <div style={{ width: listWidth, flex: 'none', background: C.bgPanel, display: 'flex', flexDirection: 'column', minHeight: 0 }}>
            {sidebar}
          </div>
          <Splitter active={listDragging} onMouseDown={startListDrag} />
        </>
      )}

      {/* Collapsed/Open: drawer поверх контента */}
      {sidebarMode !== 'pinned' && (
        <div style={{
          position: 'absolute', top: 0, left: 0, bottom: 0, zIndex: 10, width: Math.min(listWidth, 320),
          background: C.bgPanel, borderRight: `1px solid ${C.border}`, display: 'flex', flexDirection: 'column',
          transform: sidebarMode === 'open' ? 'translateX(0)' : 'translateX(-110%)',
          transition: 'transform 0.3s cubic-bezier(0.4, 0, 0.2, 1)',
          boxShadow: sidebarMode === 'open' ? '4px 0 20px rgba(20,16,10,0.15)' : 'none',
        }}>
          {sidebar}
        </div>
      )}

      {/* Backdrop — только когда drawer открыт */}
      {sidebarMode === 'open' && (
        <div onClick={() => setSidebarMode('collapsed')} style={{ position: 'absolute', inset: 0, zIndex: 9, background: C.overlay }} />
      )}

      {/* Центр: в свёрнутом режиме — тонкая шапка с гамбургером «открыть панель» */}
      <div style={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column' }}>
        {sidebarMode === 'collapsed' && (
          <div style={{ flex: 'none', display: 'flex', alignItems: 'center', padding: '0 8px', height: 48, borderBottom: `1px solid ${C.divider}` }}>
            <IconButton onClick={() => setSidebarMode('open')} title="Открыть панель" size="md" variant="soft">
              <MenuIcon size={ICON_SIZE.sm} strokeWidth={2} />
            </IconButton>
          </div>
        )}
        <div style={{ flex: 1, minWidth: 0, minHeight: 0 }}>{centerPane}</div>
      </div>
    </div>
  );

  return (
    <div style={{ height: '100dvh', background: C.bgMain, fontFamily: FONT.sans, display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
      <HubHeader value="notes" onTab={onHubTab} auth={auth} onLogout={onLogout} />
      <div style={{ flex: 1, minHeight: 0 }}>
        {body}
      </div>
      {newDialog && <NewNoteDialog defaults={newDialog} onClose={() => setNewDialog(null)} onCreated={id => { setNewDialog(null); bumpNotes(); selectNote(id); }} />}
      {wikilinkTarget && (
        <ConfirmDialog
          title="Создать заметку?"
          subtitle={<>Заметки «<strong style={{ color: C.textPrimary, fontWeight: 600 }}>{wikilinkTarget}</strong>» ещё нет.</>}
          confirmLabel="Создать"
          onConfirm={() => createFromWikilink(wikilinkTarget)}
          onCancel={() => setWikilinkTarget(null)}
        />
      )}
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

