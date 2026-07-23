import { useEffect, useState } from 'react';
import { Menu as MenuIcon } from 'lucide-react';
import type { AuthState, KnowledgeBaseSummary } from '../../types';
import type { HubTabValue } from '../../components/HubTabs';
import { HubHeader } from '../../components/HubHeader';
import { C, FONT, R } from '../../lib/design';
import { useKnowledge, useKnowledgeConfigured, ensureKnowledgeLoaded, bumpKnowledge } from '../../lib/knowledge';
import { api } from '../../lib/api';
import { parseHash, navPush, navReplace, getNav, type NavSnapshot } from '../../lib/nav';
import { SidebarSplitter, IconButton, ConfirmDialog } from '../../components/ui';
import { ICON_SIZE } from '../../components/ui/icons';
import { useSidebarDrag } from '../../lib/sidebarWidth';
import { useIsMobile } from '../../lib/breakpoints';
import { KnowledgeList, KnowledgeEmptyState } from './KnowledgeList';
import { KnowledgeView } from './KnowledgeView';
import { NewKnowledgeBaseDialog } from './NewKnowledgeBaseDialog';
import { AddDocumentDialog } from './AddDocumentDialog';
import { IconSearch, IconPlus } from './shared';

export function KnowledgePage({ auth, onLogout, onHubTab }: {
  auth: AuthState;
  onLogout: () => void;
  onHubTab: (t: HubTabValue) => void;
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

  // Режим сайдбара: pinned (в потоке) | collapsed (свёрнут). Сворачивание — кнопкой
  // на сплиттере, разворот — гамбургером обратно в поток.
  const [sidebarMode, setSidebarMode] = useState<'pinned' | 'collapsed'>(() =>
    localStorage.getItem('cc_knowledge_sidebar_mode') === 'collapsed' ? 'collapsed' : 'pinned');
  useEffect(() => { localStorage.setItem('cc_knowledge_sidebar_mode', sidebarMode); }, [sidebarMode]);

  useEffect(() => { void ensureKnowledgeLoaded(); }, []);

  // Диплинк #/knowledge/{id}
  useEffect(() => {
    const t = parseHash();
    if (t?.screen === 'knowledge' && t.knowledgeId) { setSelectedId(t.knowledgeId); setMobileView('item'); }
  }, []);

  // Открытие базы по id из ленты активности (событие knowledge_changed в командном
  // центре): pending в sessionStorage + событие cc-open-knowledge (канал как у заметок).
  // При монтировании после switchHubTab id подхватывается сразу через consume().
  useEffect(() => {
    const consume = () => {
      const id = sessionStorage.getItem('cc_pending_knowledge');
      if (id) {
        sessionStorage.removeItem('cc_pending_knowledge');
        setSelectedId(id); setMobileView('item');
        navPush({ screen: 'knowledge', knowledge: id });
      }
    };
    consume();
    window.addEventListener('cc-open-knowledge', consume);
    return () => window.removeEventListener('cc-open-knowledge', consume);
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

  // --- Сайдбар: управление (фильтр + «Новая») + список ---
  const sidebarControls = (
    <div style={{ padding: '10px 10px 9px', borderBottom: `1px solid ${C.border}`, display: 'flex', flexDirection: 'column', gap: 8, flex: 'none' }}>
      <div style={{
        display: 'flex', alignItems: 'center', gap: 6, background: C.bgWhite, border: `1px solid ${C.border}`,
        borderRadius: R.md, height: 30, padding: '0 8px', color: C.textMuted,
      }}>
        <IconSearch size={15} />
        <input value={filter} onChange={e => setFilter(e.target.value)} placeholder="Поиск…"
          style={{ flex: 1, minWidth: 0, border: 'none', outline: 'none', background: 'transparent', fontFamily: FONT.sans, fontSize: 12.5, color: C.textHeading }} />
      </div>
      <button onClick={() => setNewDialog(true)} style={{ ...newBtn, flex: 1, justifyContent: 'center' }}>
        <IconPlus size={16} />Новая
      </button>
    </div>
  );

  const sidebar = (
    <>
      {sidebarControls}
      <div style={{ flex: 1, minHeight: 0, overflowY: 'auto' }}>
        <KnowledgeList
          items={filtered}
          selectedId={selectedId}
          isMobile={isMobile}
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
          <SidebarSplitter active={listDragging} onMouseDown={startListDrag} onCollapse={() => setSidebarMode('collapsed')} />
        </>
      )}

      <div style={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column' }}>
        {sidebarMode === 'collapsed' && (
          <div style={{
            flex: 'none', display: 'flex', alignItems: 'center', padding: '0 8px', height: 48,
            borderBottom: `1px solid ${C.divider}`,
          }}>
            <IconButton onClick={() => setSidebarMode('pinned')} title="Открыть панель" size="md" variant="soft">
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
  display: 'flex', alignItems: 'center', gap: 5, background: C.accent, color: C.onAccent,
  border: 'none', borderRadius: R.md, padding: '7px 12px', fontSize: 13, fontWeight: 500,
  cursor: 'pointer', fontFamily: FONT.sans, flex: 'none',
};
