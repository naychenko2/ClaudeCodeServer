import { useEffect, useRef, useState } from 'react';
import type { KnowledgeBaseSummary, KnowledgeDocument, KnowledgeSearchHit } from '../../types';
import { C, FONT, R } from '../../lib/design';
import { api } from '../../lib/api';
import { bumpKnowledge, useKnowledgeVersion } from '../../lib/knowledge';
import { Toolbar, ToolbarIconButton } from '../../components/Toolbar';
import { Menu, MenuItem } from '../../components/ui';
import { typeIcon, IconBack, IconPlus, IconDots, IconFile, IconTrash, IconSearch, IconSparkles } from './shared';

// Детальная зона базы: тулбар (симметричный другим разделам) + описание + документы +
// семантический/полнотекстовый поиск. Состав перезапрашивается по версии (realtime).
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

  return (
    <div style={{ height: '100%', display: 'flex', flexDirection: 'column', background: C.bgMain }}>
      <Toolbar isMobile={isMobile}>
        {isMobile && (
          <ToolbarIconButton onClick={onBack} title="Назад"><IconBack size={18} /></ToolbarIconButton>
        )}
        <span style={{ color: C.accent, display: 'flex', flexShrink: 0 }}>{typeIcon(kb.type, 18)}</span>
        <div style={{ display: 'flex', alignItems: 'center', gap: 8, minWidth: 0, flex: 1 }}>
          <span style={{
            fontFamily: FONT.serif, fontWeight: 600, fontSize: 17, color: C.textHeading,
            whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
          }}>{kb.title}</span>
          <TypeChip>{kb.type}</TypeChip>
          <VisibilityBadge visibility={kb.visibility} />
        </div>
        <button onClick={() => onAddDocument(kb)} style={addBtn}><IconPlus size={15} />Добавить</button>
        <span style={{ position: 'relative' }}>
          <ToolbarIconButton onClick={() => setMenu(true)} title="Действия"><IconDots size={18} /></ToolbarIconButton>
          {menu && (
            <Menu onClose={() => setMenu(false)} top={40} align="right" minWidth={210}>
              <MenuItem icon={<><path d="M12 5v14M5 12h14" /></>} label="Добавить документ"
                onClick={() => { setMenu(false); onAddDocument(kb); }} />
              {kb.deletable && (
                <MenuItem icon={<><path d="M3 6h18M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2" /></>}
                  label="Удалить базу" danger onClick={() => { setMenu(false); onDelete(kb); }} />
              )}
            </Menu>
          )}
        </span>
      </Toolbar>

      <div style={{ flex: 1, minHeight: 0, overflowY: 'auto' }}>
        {/* Описание + статистика */}
        <div style={{ padding: '16px 18px 8px' }}>
          {kb.description && (
            <div style={{ color: C.textSecondary, fontSize: 13.5, maxWidth: 680 }}>{kb.description}</div>
          )}
          <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginTop: 8, fontSize: 12, color: C.textMuted }}>
            <span>{kb.documentCount} документов</span>
            {!kb.deletable && (
              <>
                <span>·</span>
                <span style={{ display: 'inline-flex', alignItems: 'center', gap: 4 }}>привязана к разделу «{kb.type}»</span>
              </>
            )}
          </div>
        </div>

        {/* Поиск + переключатель семантика/полнотекст */}
        <div style={{
          display: 'flex', alignItems: 'center', gap: 8, padding: '8px 18px 10px',
          position: 'sticky', top: 0, background: C.bgMain, zIndex: 2,
        }}>
          <div style={{
            flex: 1, maxWidth: 480, display: 'flex', alignItems: 'center', gap: 8, height: 34,
            padding: '0 11px', borderRadius: R.lg, background: C.bgCard, border: `1px solid ${C.border}`, color: C.textMuted,
          }}>
            <IconSearch size={15} />
            <input ref={searchRef} value={query} onChange={e => setQuery(e.target.value)}
              placeholder={mode === 'semantic' ? 'Семантический поиск по базе…' : 'Полнотекстовый поиск…'}
              style={{ flex: 1, minWidth: 0, border: 'none', outline: 'none', background: 'transparent', fontFamily: FONT.sans, fontSize: 13, color: C.textHeading }} />
          </div>
          <button onClick={() => setMode(m => m === 'semantic' ? 'fulltext' : 'semantic')}
            title={mode === 'semantic' ? 'Поиск по смыслу — включён' : 'Полнотекстовый поиск — включён'}
            style={{
              flex: 'none', height: 34, padding: '0 12px', borderRadius: R.lg, cursor: 'pointer', fontFamily: FONT.sans,
              fontSize: 12.5, fontWeight: 600, display: 'flex', alignItems: 'center', gap: 6,
              border: `1px solid ${mode === 'semantic' ? C.accent : C.border}`,
              background: mode === 'semantic' ? C.accent : C.bgCard,
              color: mode === 'semantic' ? C.onAccent : C.textSecondary,
            }}><IconSparkles size={14} />{mode === 'semantic' ? 'По смыслу' : 'Точно'}</button>
        </div>

        {query.trim() ? (
          <SearchResults hits={hits} searching={searching} query={query.trim()} mode={mode} />
        ) : (
          <DocumentsList docs={docs} err={loadErr} onRemove={removeDoc} />
        )}
      </div>
    </div>
  );
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

function VisibilityBadge({ visibility }: { visibility: 'personal' | 'public' }) {
  const isPub = visibility === 'public';
  return (
    <span style={{
      display: 'inline-flex', alignItems: 'center', height: 18, padding: '0 8px', borderRadius: 10,
      fontSize: 10.5, fontWeight: 600, whiteSpace: 'nowrap',
      background: isPub ? C.successBg : C.accentMuted, color: isPub ? C.successText : C.accent,
    }}>{isPub ? 'Публичная' : 'Личная'}</span>
  );
}

const addBtn: React.CSSProperties = {
  flex: 'none', display: 'inline-flex', alignItems: 'center', gap: 7, height: 32, padding: '0 13px',
  borderRadius: R.md, background: C.accent, color: C.onAccent, border: 'none', cursor: 'pointer',
  fontFamily: FONT.sans, fontSize: 13, fontWeight: 600,
};

function docStatus(status: string): { label: string; dot: string; pulse: boolean } {
  const s = (status || '').toLowerCase();
  if (s === 'completed' || s === 'indexing_completed') return { label: 'Готов', dot: C.success, pulse: false };
  if (s === 'error') return { label: 'Ошибка индексации', dot: C.danger, pulse: false };
  return { label: 'Индексируется…', dot: C.accent, pulse: true };
}

function DocumentsList({ docs, err, onRemove }: {
  docs: KnowledgeDocument[] | null;
  err: string | null;
  onRemove: (docId: string) => void;
}) {
  if (err) return <div style={{ padding: '20px 18px', color: C.danger, fontSize: 13 }}>{err}</div>;
  if (docs === null) return <div style={{ padding: '20px 18px', color: C.textMuted, fontSize: 13 }}>Загрузка…</div>;
  if (docs.length === 0) return <div style={{ padding: '24px 18px', color: C.textMuted, fontSize: 13 }}>В базе нет документов — добавьте первый</div>;
  return (
    <div style={{ padding: '4px 12px 28px' }}>
      <div style={{ fontSize: 11, fontWeight: 600, letterSpacing: '0.06em', textTransform: 'uppercase', color: C.textMuted, padding: '10px 8px 6px' }}>
        Документы · {docs.length}
      </div>
      {docs.map(d => {
        const st = docStatus(d.indexingStatus);
        return (
          <div key={d.id} style={{
            display: 'flex', alignItems: 'center', gap: 12, padding: '10px 10px', borderRadius: R.lg,
          }}
            onMouseEnter={e => { e.currentTarget.style.background = C.bgCard; }}
            onMouseLeave={e => { e.currentTarget.style.background = 'transparent'; }}>
            <span style={{
              width: 28, height: 28, borderRadius: 7, background: C.bgPanel, flex: 'none',
              display: 'flex', alignItems: 'center', justifyContent: 'center', color: C.textSecondary,
            }}><IconFile size={14} /></span>
            <div style={{ flex: 1, minWidth: 0 }}>
              <div style={{
                fontFamily: FONT.mono, fontSize: 12, color: C.textPrimary,
                whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
              }}>{d.name}</div>
              <div style={{ display: 'flex', alignItems: 'center', gap: 6, marginTop: 2, fontSize: 11, color: C.textSecondary }}>
                <span style={{ width: 7, height: 7, borderRadius: '50%', background: st.dot, animation: st.pulse ? 'kb-pulse 1.2s infinite' : undefined }} />
                {st.label}
              </div>
            </div>
            <button onClick={() => onRemove(d.id)} title="Удалить документ"
              style={{
                width: 26, height: 26, borderRadius: 7, border: 'none', background: 'transparent', cursor: 'pointer',
                color: C.textMuted, display: 'flex', alignItems: 'center', justifyContent: 'center',
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
      <div style={{ fontSize: 11, fontWeight: 600, letterSpacing: '0.06em', textTransform: 'uppercase', color: C.textMuted, padding: '10px 8px 6px' }}>
        {mode === 'semantic' ? 'По смыслу' : 'Полнотекстово'} · {searching ? '…' : (hits?.length ?? 0)}
      </div>
      {searching ? (
        <div style={{ padding: '12px 8px', color: C.textMuted, fontSize: 13 }}>Ищу…</div>
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
        <div style={{ padding: '12px 8px', color: C.textMuted, fontSize: 13 }}>Ничего не найдено</div>
      )}
    </div>
  );
}
