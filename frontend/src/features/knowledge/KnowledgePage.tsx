import { useEffect, useState } from 'react';
import type { AuthState, KnowledgeBaseSummary } from '../../types';
import type { HubTab } from '../../components/HubTabs';
import { HubHeader } from '../../components/HubHeader';
import { C, FONT, R } from '../../lib/design';
import { useKnowledge, useKnowledgeConfigured, ensureKnowledgeLoaded, bumpKnowledge } from '../../lib/knowledge';
import { api } from '../../lib/api';
import { parseHash, navPush, navReplace, getNav, type NavSnapshot } from '../../lib/nav';
import { Splitter, IconButton, ConfirmDialog } from '../../components/ui';
import { useSidebarDrag } from '../../lib/sidebarWidth';
import { useIsMobile } from '../../lib/breakpoints';
import { KnowledgeList, KnowledgeEmptyState } from './KnowledgeList';
import { KnowledgeView } from './KnowledgeView';
import { NewKnowledgeBaseDialog } from './NewKnowledgeBaseDialog';
import { AddDocumentDialog } from './AddDocumentDialog';
import { IconSearch, IconPlus, IconChevronsLeft, IconPin } from './shared';

export function KnowledgePage({ auth, onLogout, onHubTab }: {
  auth: AuthState;
  onLogout: () => void;
  onHubTab: (t: HubTab) => void;
}) {
  const isMobile = useIsMobile();
  const items = useKnowledge();
  const configured = useKnowledgeConfigured();
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [filter, setFilter] = useState('');
  const [mobileView, setMobileView] = useState<'list' | 'item'>('list');
  const [newDialog, setNewDialog] = useState(false);
  const [addDocFor, setAddDocFor] = useState<KnowledgeBaseSummary | null>(null);
  const [deleteKb, setDeleteKb] = useState<KnowledgeBaseSummary | null>(null);

  const { width: listWidth, dragging: listDragging, startDrag: startListDrag } = useSidebarDrag();

  // Режим сайдбара: pinned | collapsed | open (как в «Заметках»/«Чатах»/воркспейсе).
  const [sidebarMode, setSidebarMode] = useState<'pinned' | 'collapsed' | 'open'>(() =>
    localStorage.getItem('cc_knowledge_sidebar_mode') === 'collapsed' ? 'collapsed' : 'pinned');
  useEffect(() => { if (sidebarMode !== 'open') localStorage.setItem('cc_knowledge_sidebar_mode', sidebarMode); }, [sidebarMode]);

  useEffect(() => { void ensureKnowledgeLoaded(); }, []);

  // Диплинк #/knowledge/{id}
  useEffect(() => {
    const t = parseHash();
    if (t?.screen === 'knowledge' && t.knowledgeId) { setSelectedId(t.knowledgeId); setMobileView('item'); }
  }, []);

  // Back/forward браузера внутри вкладки «Знания»
  useEffect(() => {
    const onPop = (e: PopStateEvent) => {
      const s = e.state as NavSnapshot | null;
      if (s?.screen === 'knowledge') {
        setSelectedId(s.knowledge ?? null);
        setMobileView(s.knowledge ? 'item' : 'list');
      }
    };
    window.addEventListener('popstate', onPop);
    return () => window.removeEventListener('popstate', onPop);
  }, []);

  // Удалённую из списка базу (с другого устройства) — снимаем выбор
  useEffect(() => {
    if (selectedId && !items.some(i => i.id === selectedId)) {
      setSelectedId(null); setMobileView('list');
    }
  }, [items, selectedId]);

  const filtered = filter.trim()
    ? items.filter(i => i.title.toLowerCase().includes(filter.trim().toLowerCase()))
    : items;
  const selected = selectedId ? items.find(i => i.id === selectedId) ?? null : null;

  const selectKb = (id: string) => {
    setSelectedId(id); setMobileView('item'); navPush({ screen: 'knowledge', knowledge: id });
    setSidebarMode(m => m === 'open' ? 'collapsed' : m);
  };
  const clearKb = () => {
    setSelectedId(null); setMobileView('list');
    if (getNav()?.knowledge) navReplace({ screen: 'knowledge' });
  };

  const onAddDocument = (kb: KnowledgeBaseSummary) => setAddDocFor(kb);
  const doDelete = async () => {
    if (!deleteKb) return;
    const id = deleteKb.id;
    setDeleteKb(null);
    try { await api.knowledgeBases.remove(id); }
    catch { return; }
    bumpKnowledge();
    if (selectedId === id) clearKb();
  };

  // --- Сайдбар: шапка (свернуть/закрепить) + управление (фильтр + «Новая») + список ---
  const sidebarHeader = (
    <div style={{ display: 'flex', alignItems: 'center', gap: 6, padding: '8px 10px 0', minHeight: 28, flex: 'none' }}>
      <IconButton onClick={() => setSidebarMode('collapsed')} title="Свернуть панель" size="sm" style={{ marginLeft: -2 }}>
        <IconChevronsLeft size={16} />
      </IconButton>
      <span style={{
        flex: 1, minWidth: 0, fontSize: 14, fontWeight: 600, color: C.textHeading,
        overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
      }}>Знания</span>
      {sidebarMode === 'open' && (
        <IconButton onClick={() => setSidebarMode('pinned')} title="Закрепить панель" size="sm">
          <IconPin size={15} />
        </IconButton>
      )}
    </div>
  );

  const sidebarControls = (
    <div style={{ padding: '10px 10px 9px', borderBottom: `1px solid ${C.border}`, display: 'flex', flexDirection: 'column', gap: 8, flex: 'none' }}>
      <div style={{
        display: 'flex', alignItems: 'center', gap: 6, background: C.bgCard, border: `1px solid ${C.border}`,
        borderRadius: R.md, height: 32, padding: '0 9px', color: C.textMuted,
      }}>
        <IconSearch size={15} />
        <input value={filter} onChange={e => setFilter(e.target.value)} placeholder="Поиск по базам…"
          style={{ flex: 1, minWidth: 0, border: 'none', outline: 'none', background: 'transparent', fontFamily: FONT.sans, fontSize: 12.5, color: C.textHeading }} />
      </div>
      <button onClick={() => setNewDialog(true)} style={{ ...newBtn, flex: 1, justifyContent: 'center' }}>
        <IconPlus size={16} />Новая
      </button>
    </div>
  );

  const sidebar = (
    <>
      {!isMobile && sidebarHeader}
      {sidebarControls}
      <div style={{ flex: 1, minHeight: 0, overflowY: 'auto' }}>
        <KnowledgeList
          items={filtered}
          selectedId={selectedId}
          onSelect={selectKb}
          onAddDocument={onAddDocument}
          onDelete={setDeleteKb}
        />
      </div>
    </>
  );

  const centerPane = selected
    ? <KnowledgeView key={selected.id} kb={selected} isMobile={isMobile}
        onBack={clearKb} onAddDocument={onAddDocument} onDelete={setDeleteKb} />
    : <KnowledgeEmptyState configured={configured} onNew={() => setNewDialog(true)} />;

  // --- Dify не настроен — весь раздел недоступен ---
  if (!configured) {
    return (
      <div style={{ height: '100dvh', background: C.bgMain, fontFamily: FONT.sans, display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
        <HubHeader value="knowledge" onTab={onHubTab} auth={auth} onLogout={onLogout} />
        <div style={{ flex: 1, minHeight: 0 }}><KnowledgeEmptyState configured={false} onNew={() => setNewDialog(true)} /></div>
      </div>
    );
  }

  const body = isMobile ? (
    (mobileView === 'list' || !selected)
      ? <div style={{ height: '100%', display: 'flex', flexDirection: 'column', background: C.bgPanel }}>{sidebar}</div>
      : <div style={{ height: '100%', display: 'flex', flexDirection: 'column' }}>{centerPane}</div>
  ) : (
    <div style={{ height: '100%', display: 'flex', position: 'relative' }}>
      {sidebarMode === 'pinned' && (
        <>
          <div style={{ width: listWidth, flex: 'none', background: C.bgPanel, display: 'flex', flexDirection: 'column', minHeight: 0 }}>
            {sidebar}
          </div>
          <Splitter active={listDragging} onMouseDown={startListDrag} />
        </>
      )}

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

      {sidebarMode === 'open' && (
        <div onClick={() => setSidebarMode('collapsed')} style={{ position: 'absolute', inset: 0, zIndex: 9, background: C.overlay }} />
      )}

      <div style={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column' }}>
        {sidebarMode === 'collapsed' && (
          <div style={{
            flex: 'none', display: 'flex', alignItems: 'center', padding: '0 8px', height: 48,
            borderBottom: `1px solid ${C.divider}`,
          }}>
            <IconButton onClick={() => setSidebarMode('open')} title="Открыть панель" size="md" variant="soft">
              <svg width="15" height="15" viewBox="0 0 16 16" fill="none">
                <path d="M2 4h12M2 8h12M2 12h12" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" />
              </svg>
            </IconButton>
          </div>
        )}
        <div style={{ flex: 1, minWidth: 0, minHeight: 0 }}>{centerPane}</div>
      </div>
    </div>
  );

  return (
    <div style={{ height: '100dvh', background: C.bgMain, fontFamily: FONT.sans, display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
      <HubHeader value="knowledge" onTab={onHubTab} auth={auth} onLogout={onLogout} />
      <div style={{ flex: 1, minHeight: 0 }}>
        {body}
      </div>

      {newDialog && (
        <NewKnowledgeBaseDialog
          onClose={() => setNewDialog(false)}
          onCreated={id => { setNewDialog(false); selectKb(id); }}
        />
      )}
      {addDocFor && (
        <AddDocumentDialog
          kb={addDocFor}
          onClose={() => setAddDocFor(null)}
          onAdded={() => setAddDocFor(null)}
        />
      )}
      {deleteKb && (
        <ConfirmDialog
          title={`Удалить базу «${deleteKb.title}»?`}
          subtitle={<>Будут безвозвратно удалены все документы базы. Действие необратимо.</>}
          confirmLabel="Удалить"
          confirmVariant="danger"
          onConfirm={doDelete}
          onCancel={() => setDeleteKb(null)}
        />
      )}
    </div>
  );
}

const newBtn: React.CSSProperties = {
  display: 'flex', alignItems: 'center', gap: 6, background: C.accent, color: C.onAccent,
  border: 'none', borderRadius: R.md, padding: '7px 12px', fontSize: 13, fontWeight: 600,
  cursor: 'pointer', fontFamily: FONT.sans, flex: 'none',
};
