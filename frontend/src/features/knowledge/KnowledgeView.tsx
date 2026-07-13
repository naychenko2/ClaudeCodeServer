import { useEffect, useRef, useState } from 'react';
import type { KnowledgeBaseSummary, KnowledgeDocument, KnowledgeSearchHit } from '../../types';
import { C, FONT, R } from '../../lib/design';
import { api } from '../../lib/api';
import { bumpKnowledge, useKnowledgeVersion } from '../../lib/knowledge';
import { Toolbar, ToolbarIconButton, tbBtnPrimary } from '../../components/Toolbar';
import { Splitter } from '../../components/ui';
import { typeIcon, IconBack, IconPlus, IconDots, IconFile, IconTrash, IconSearch, IconLock, IconChevronRight } from './shared';
import { KbActionsMenu } from './KbActionsMenu';
import { DocumentViewer } from './DocumentViewer';

// Ширина правой панели просмотра документа (десктоп). Общий паттерн с «Артефактами
// сессии»: персист в localStorage, clamp по разумным границам. Сплиттер между списком
// документов и панелью — общий `Splitter` из components/ui.
const DOC_PANEL_KEY = 'cc_knowledge_doc_width';
const DOC_PANEL_MIN = 320;
const DOC_PANEL_MAX = 560;
const DOC_PANEL_DEFAULT = 420;
function loadDocPanelWidth(): number {
  const v = localStorage.getItem(DOC_PANEL_KEY);
  if (!v) return DOC_PANEL_DEFAULT;
  const n = Number(v);
  return Number.isFinite(n) ? Math.max(DOC_PANEL_MIN, Math.min(DOC_PANEL_MAX, n)) : DOC_PANEL_DEFAULT;
}

// Детальная зона базы: тулбар (симметричный другим разделам) + описание + документы +
// семантический/полнотекстовый поиск. На десктопе/планшете в тулбаре много места —
// поиск, кол-во документов и пометка привязки живут прямо там; на мобиле они остаются
// отдельными строками под тулбаром. Состав перезапрашивается по версии (realtime).
export function KnowledgeView({ kb, isMobile, onBack, onAddDocument, onDelete }: {
  kb: KnowledgeBaseSummary;
  isMobile: boolean;
  onBack: () => void;
  onAddDocument: (kb: KnowledgeBaseSummary) => void;
  onDelete: (kb: KnowledgeBaseSummary) => void;
}) {
  const version = useKnowledgeVersion();
  const [docs, setDocs] = useState<KnowledgeDocument[] | null>(null);
  const [loadErr, setLoadErr] = useState<string | null>(null);
  const [menu, setMenu] = useState(false);
  // Какой документ открыт для просмотра (содержимое). null — просмотр закрыт.
  const [viewDoc, setViewDoc] = useState<KnowledgeDocument | null>(null);
  // Ширина правой панели просмотра (десктоп). На мобиле take-over — ширина не нужна.
  const [docPanelWidth, setDocPanelWidth] = useState(loadDocPanelWidth);
  const [draggingSplitter, setDraggingSplitter] = useState(false);
  useEffect(() => { localStorage.setItem(DOC_PANEL_KEY, String(docPanelWidth)); }, [docPanelWidth]);

  // Ресайз панели просмотра: панель справа, тянем влево (clientX падает) → ширина растёт.
  // Тот же паттерн, что у handleArtifactsSplitterMouseDown в WorkspacePage.
  const handleDocSplitterMouseDown = (e: React.PointerEvent) => {
    e.preventDefault();
    setDraggingSplitter(true);
    const startX = e.clientX;
    const startW = docPanelWidth;
    const onMove = (ev: PointerEvent) => {
      setDocPanelWidth(Math.max(DOC_PANEL_MIN, Math.min(DOC_PANEL_MAX, startW - (ev.clientX - startX))));
    };
    const onUp = () => {
      setDraggingSplitter(false);
      document.removeEventListener('pointermove', onMove);
      document.removeEventListener('pointerup', onUp);
      document.body.style.cursor = '';
      document.body.style.userSelect = '';
    };
    document.body.style.cursor = 'col-resize';
    document.body.style.userSelect = 'none';
    document.addEventListener('pointermove', onMove);
    document.addEventListener('pointerup', onUp);
  };

  // Поиск: semantic (по смыслу) | fulltext (точный). Дебаунс.
  const [query, setQuery] = useState('');
  const [mode, setMode] = useState<'semantic' | 'fulltext'>('semantic');
  const [hits, setHits] = useState<KnowledgeSearchHit[] | null>(null);
  const [searching, setSearching] = useState(false);
  const searchRef = useRef<HTMLInputElement>(null);

  useEffect(() => {
    setDocs(null); setLoadErr(null);
    api.knowledgeBases.get(kb.id)
      .then(d => setDocs(d.documents))
      .catch(e => setLoadErr(e instanceof Error ? e.message : 'Не удалось загрузить'));
  }, [kb.id, version]);

  useEffect(() => {
    setQuery(''); setHits(null);
  }, [kb.id]);

  // Смена базы — закрываем просмотр документа (содержимое принадлежит старой базе)
  useEffect(() => { setViewDoc(null); }, [kb.id]);

  useEffect(() => {
    const q = query.trim();
    if (!q) { setHits(null); setSearching(false); return; }
    setSearching(true);
    const t = setTimeout(() => {
      api.knowledgeBases.search(kb.id, q, mode)
        .then(r => setHits(r.items))
        .catch(() => setHits([]))
        .finally(() => setSearching(false));
    }, mode === 'semantic' ? 450 : 250);
    return () => clearTimeout(t);
  }, [query, mode, kb.id]);

  const removeDoc = async (docId: string) => {
    try { await api.knowledgeBases.removeDocument(kb.id, docId); }
    catch { return; }
    setDocs(cur => cur ? cur.filter(d => d.id !== docId) : cur);
    bumpKnowledge();
  };

  // Поле поиска с «смысл»-пилюлей. inToolbar — десктоп/планшет (растёт в тулбаре),
  // иначе — отдельная строка под тулбаром (мобила).
  const searchInput = (inToolbar: boolean) => (
    <div style={{
      flex: inToolbar ? 1 : undefined, maxWidth: inToolbar ? 540 : 480, minWidth: 0,
      display: 'flex', alignItems: 'center', gap: 8, height: 34,
      padding: '0 11px', borderRadius: R.lg, background: C.bgCard, border: `1px solid ${C.border}`, color: C.textMuted,
    }}>
      <IconSearch size={15} />
      <input ref={searchRef} value={query} onChange={e => setQuery(e.target.value)}
        placeholder={mode === 'semantic' ? 'Поиск по смыслу…' : 'Полнотекстовый поиск…'}
        style={{ flex: 1, minWidth: 0, border: 'none', outline: 'none', background: 'transparent', fontFamily: FONT.sans, fontSize: 13, color: C.textHeading }} />
      <button
        onClick={() => setMode(m => m === 'semantic' ? 'fulltext' : 'semantic')}
        title={mode === 'semantic' ? 'Поиск по смыслу — включён; клик — точный' : 'Точный поиск — включён; клик — по смыслу'}
        style={semanticPill(mode === 'semantic')}>смысл</button>
    </div>
  );

  return (
    <div style={{ height: '100%', display: 'flex', flexDirection: 'column', background: C.bgMain }}>
      {/* На мобиле при открытом документе базовый тулбар базы прячем — иначе он
          дублируется с шапкой DocumentViewer (два тулбара). Возврат к списку — «назад»
          в шапке документа (onClose). На десктопе просмотр — правая панель, тулбар нужен. */}
      {!(isMobile && viewDoc) && (
      <Toolbar isMobile={isMobile}>
        {isMobile && (
          <ToolbarIconButton onClick={onBack} title="Назад"><IconBack size={18} /></ToolbarIconButton>
        )}
        <span style={{ color: C.accent, display: 'flex', flexShrink: 0 }}>{typeIcon(kb.type, 18)}</span>
        <div style={{ display: 'flex', alignItems: 'center', gap: 8, minWidth: 0, flex: isMobile ? 1 : '0 1 auto' }}>
          <span style={{
            fontFamily: FONT.serif, fontWeight: 600, fontSize: 17, color: C.textHeading,
            whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
          }}>{kb.title}</span>
          {!isMobile && <TypeChip>{kb.type}</TypeChip>}
        </div>

        {/* Десктоп/планшет: поиск, кол-во документов и пометка привязки — прямо в тулбаре */}
        {!isMobile && searchInput(true)}
        {!isMobile && (
          <span style={{
            flex: 'none', display: 'inline-flex', alignItems: 'center', height: 24, padding: '0 9px',
            borderRadius: R.sm, background: C.bgSelected, color: C.textSecondary, fontSize: 11.5, fontWeight: 500, whiteSpace: 'nowrap',
          }}>{kb.documentCount} {pluralDocs(kb.documentCount)}</span>
        )}
        {!isMobile && !kb.deletable && (
          <span title={`Привязана к разделу «${kb.type}»`} style={{
            flex: 'none', display: 'inline-flex', alignItems: 'center', gap: 5,
            fontSize: 11.5, color: C.textMuted, whiteSpace: 'nowrap',
          }}><IconLock size={12} />привязана</span>
        )}

        <button onClick={() => onAddDocument(kb)} style={tbBtnPrimary}><IconPlus size={15} />Добавить</button>
        <span style={{ position: 'relative', flex: 'none' }}>
          <ToolbarIconButton onClick={() => setMenu(true)} title="Действия"><IconDots size={18} /></ToolbarIconButton>
          {menu && (
            <KbActionsMenu kb={kb} isMobile={isMobile}
              onClose={() => setMenu(false)}
              onAddDocument={() => onAddDocument(kb)}
              onDelete={() => onDelete(kb)} />
          )}
        </span>
      </Toolbar>
      )}

      {/* Тело под тулбаром: на десктопе — flex-строка [список | splitter | панель просмотра]
          (push-панель в потоке, как «Артефакты сессии»); на мобиле — один экран за раз:
          либо список, либо полноэкранный просмотр (list↔item take-over, как NoteView). */}
      <div style={{ flex: 1, minHeight: 0, display: 'flex', overflow: 'hidden' }}>
        {/* Список/поиск — на мобиле скрывается, когда открыт документ (take-over) */}
        <div style={{
          flex: 1, minWidth: 0,
          display: isMobile && viewDoc ? 'none' : 'flex',
          flexDirection: 'column',
          overflowY: 'auto',
        }}>
          {/* Описание — везде. Кол-во/привязка отдельной строкой — только на мобиле
              (на десктопе они в тулбаре). */}
          <div style={{ padding: '16px 18px 8px' }}>
            {kb.description && (
              <div style={{ color: C.textSecondary, fontSize: 13.5, maxWidth: 680, lineHeight: 1.5 }}>{kb.description}</div>
            )}
            {isMobile && (
              <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginTop: 8, fontSize: 12, color: C.textMuted }}>
                <span>{kb.documentCount} {pluralDocs(kb.documentCount)}</span>
                {!kb.deletable && (
                  <>
                    <span>·</span>
                    <span style={{ display: 'inline-flex', alignItems: 'center', gap: 4 }}>
                      <IconLock size={11} />привязана к разделу «{kb.type}»
                    </span>
                  </>
                )}
              </div>
            )}
          </div>

          {/* Поиск отдельной строкой — только на мобиле */}
          {isMobile && (
            <div style={{
              display: 'flex', alignItems: 'center', gap: 8, padding: '8px 18px 10px',
              position: 'sticky', top: 0, background: C.bgMain, zIndex: 2,
            }}>{searchInput(false)}</div>
          )}

          {query.trim() ? (
            <SearchResults hits={hits} searching={searching} query={query.trim()} mode={mode} />
          ) : (
            <DocumentsList docs={docs} err={loadErr} onOpen={setViewDoc} onRemove={removeDoc} />
          )}
        </div>

        {/* Десктоп: push-панель просмотра в потоке + сплиттер (как ArtifactsPanel).
            Мобила обрабатывается отдельным take-over блоком ниже. */}
        {!isMobile && viewDoc && (
          <>
            <Splitter orientation="v" active={draggingSplitter} onMouseDown={handleDocSplitterMouseDown} />
            <div style={{ width: docPanelWidth, flexShrink: 0, height: '100%', minWidth: 0 }}>
              <DocumentViewer kbId={kb.id} doc={viewDoc} onClose={() => setViewDoc(null)} isMobile={false} />
            </div>
          </>
        )}

        {/* Мобила: полноэкранный take-over списка (list↔item). Панель занимает всю зону
            детали под тулбаром, закрытие — кнопка «назад» в шапке панели (IconBack). */}
        {isMobile && viewDoc && (
          <div style={{ flex: 1, minWidth: 0, height: '100%' }}>
            <DocumentViewer kbId={kb.id} doc={viewDoc} onClose={() => setViewDoc(null)} isMobile={true} />
          </div>
        )}
      </div>
    </div>
  );
}

// Пилюля «смысл» — компактная, в стиле Notes: accent когда активна, приглушённая — нет.
function semanticPill(active: boolean): React.CSSProperties {
  return {
    fontSize: 10, fontWeight: 600, border: 'none', borderRadius: R.sm, padding: '3px 7px',
    cursor: 'pointer', fontFamily: FONT.sans, flex: 'none',
    background: active ? C.accent : C.bgSelected,
    color: active ? C.onAccent : C.textMuted,
  };
}

function TypeChip({ children }: { children: React.ReactNode }) {
  return (
    <span style={{
      display: 'inline-flex', alignItems: 'center', height: 20, padding: '0 9px', borderRadius: R.sm,
      background: C.bgSelected, border: `1px solid ${C.borderLight}`, fontSize: 11, color: C.textSecondary, fontWeight: 500,
      whiteSpace: 'nowrap',
    }}>{children}</span>
  );
}

function docStatus(status: string): { label: string; dot: string; pulse: boolean } {
  const s = (status || '').toLowerCase();
  if (s === 'completed' || s === 'indexing_completed') return { label: 'Готов', dot: C.success, pulse: false };
  if (s === 'error') return { label: 'Ошибка индексации', dot: C.danger, pulse: false };
  return { label: 'Индексируется…', dot: C.accent, pulse: true };
}

function DocumentsList({ docs, err, onOpen, onRemove }: {
  docs: KnowledgeDocument[] | null;
  err: string | null;
  onOpen: (doc: KnowledgeDocument) => void;
  onRemove: (docId: string) => void;
}) {
  if (err) return <div style={{ padding: '20px 18px', color: C.danger, fontSize: 13, fontFamily: FONT.sans }}>{err}</div>;
  if (docs === null) return <div style={{ padding: '20px 18px', color: C.textMuted, fontSize: 13, fontFamily: FONT.sans }}>Загрузка…</div>;
  if (docs.length === 0) return <div style={{ padding: '24px 18px', color: C.textMuted, fontSize: 13, fontFamily: FONT.sans }}>В базе нет документов — добавьте первый.</div>;
  return (
    <div style={{ padding: '4px 12px 28px' }}>
      <SectionLabel>Документы</SectionLabel>
      {docs.map(d => {
        const st = docStatus(d.indexingStatus);
        return (
          <div key={d.id} onClick={() => onOpen(d)} title="Открыть документ"
            style={{
              display: 'flex', alignItems: 'center', gap: 12, padding: '8px 10px', borderRadius: R.lg,
              cursor: 'pointer', transition: 'background 0.1s',
            }}
            onMouseEnter={e => { e.currentTarget.style.background = C.bgSelected; }}
            onMouseLeave={e => { e.currentTarget.style.background = 'transparent'; }}>
            <span style={{
              width: 28, height: 28, borderRadius: R.sm, background: C.bgPanel, flex: 'none',
              display: 'flex', alignItems: 'center', justifyContent: 'center', color: C.textSecondary,
            }}><IconFile size={14} /></span>
            <div style={{ flex: 1, minWidth: 0 }}>
              <div style={{
                fontFamily: FONT.mono, fontSize: 12, color: C.textPrimary,
                whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
              }}>{d.name}</div>
              <div style={{ display: 'flex', alignItems: 'center', gap: 6, marginTop: 2, fontSize: 11, color: C.textSecondary }}>
                <span style={{
                  width: 7, height: 7, borderRadius: '50%', background: st.dot,
                  animation: st.pulse ? 'kb-pulse 1.2s infinite' : undefined, flex: 'none',
                }} />
                {st.label}
              </div>
            </div>
            {/* Индикатор кликабельности — шеврон «раскрыть» */}
            <span style={{ flex: 'none', color: C.textMuted, display: 'flex', alignItems: 'center' }}>
              <IconChevronRight size={15} />
            </span>
            <button onClick={(e) => { e.stopPropagation(); onRemove(d.id); }} title="Удалить документ"
              style={{
                width: 26, height: 26, borderRadius: R.sm, border: 'none', background: 'transparent', cursor: 'pointer',
                color: C.textMuted, display: 'flex', alignItems: 'center', justifyContent: 'center', flex: 'none',
              }}
              onMouseEnter={e => { e.currentTarget.style.background = C.dangerBg; e.currentTarget.style.color = C.danger; }}
              onMouseLeave={e => { e.currentTarget.style.background = 'transparent'; e.currentTarget.style.color = C.textMuted; }}>
              <IconTrash size={14} />
            </button>
          </div>
        );
      })}
      <style>{`@keyframes kb-pulse{0%,100%{opacity:1}50%{opacity:.35}}`}</style>
    </div>
  );
}

function SearchResults({ hits, searching, query, mode }: {
  hits: KnowledgeSearchHit[] | null;
  searching: boolean;
  query: string;
  mode: 'semantic' | 'fulltext';
}) {
  const ql = query.toLowerCase();
  const highlight = (text: string) => {
    const idx = text.toLowerCase().indexOf(ql);
    if (idx < 0) return text;
    return <>{text.slice(0, idx)}<mark style={{ background: C.accentMuted, color: C.accent, padding: '0 2px', borderRadius: 3 }}>{text.slice(idx, idx + query.length)}</mark>{text.slice(idx + query.length)}</>;
  };
  return (
    <div style={{ padding: '4px 14px 28px' }}>
      <SectionLabel>
        {mode === 'semantic' ? 'По смыслу' : 'Полнотекстово'} · {searching ? '…' : (hits?.length ?? 0)}
      </SectionLabel>
      {searching ? (
        <div style={{ padding: '12px 8px', color: C.textMuted, fontSize: 13, fontFamily: FONT.sans }}>Ищу…</div>
      ) : hits && hits.length > 0 ? (
        hits.map((h, i) => (
          <div key={i} style={{
            margin: '0 0 10px', padding: '13px 15px', borderRadius: R.lg, background: C.bgCard,
            border: `1px solid ${C.borderLight}`,
          }}>
            <div style={{ display: 'flex', alignItems: 'center', gap: 9, fontSize: 11, color: C.textSecondary, marginBottom: 6 }}>
              {mode === 'semantic' && <span style={{ fontFamily: FONT.mono, color: C.accent, fontWeight: 600 }}>{h.score.toFixed(2)}</span>}
              <span style={{ fontFamily: FONT.mono }}>{h.documentName}</span>
            </div>
            <div style={{ fontSize: 13, color: C.textPrimary, lineHeight: 1.55 }}>{highlight(h.content)}</div>
          </div>
        ))
      ) : (
        <div style={{ padding: '12px 8px', color: C.textMuted, fontSize: 13, fontFamily: FONT.sans }}>Ничего не найдено.</div>
      )}
    </div>
  );
}

// Единая подпись секции списка (как в NotesList/Files)
function SectionLabel({ children }: { children: React.ReactNode }) {
  return (
    <div style={{
      fontSize: 10.5, fontWeight: 700, letterSpacing: '0.06em', textTransform: 'uppercase',
      color: C.textMuted, fontFamily: FONT.sans, padding: '10px 8px 6px',
    }}>{children}</div>
  );
}

function pluralDocs(n: number): string {
  const last = n % 10, last2 = n % 100;
  if (last === 1 && last2 !== 11) return 'документ';
  if (last >= 2 && last <= 4 && (last2 < 10 || last2 >= 20)) return 'документа';
  return 'документов';
}
