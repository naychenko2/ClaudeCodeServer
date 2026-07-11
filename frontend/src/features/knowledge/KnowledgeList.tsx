import { useState } from 'react';
import type { KnowledgeBaseSummary } from '../../types';
import { C, FONT, R } from '../../lib/design';
import { Menu, MenuItem } from '../../components/ui';
import { typeIcon, IconDots, IconLock, IconPlus, IconBook } from './shared';

// Список баз знаний с группами «Мои» / «Публичные». Карточка — кликабельная;
// у удаляемых баз есть ⋯-меню и правый клик (Открыть / Добавить документ / Удалить),
// у привязанных (заметок/проектов/персон) — только 🔒 (удалить через раздел-владелец).
export function KnowledgeList({ items, selectedId, onSelect, onAddDocument, onDelete }: {
  items: KnowledgeBaseSummary[];
  selectedId: string | null;
  onSelect: (id: string) => void;
  onAddDocument: (kb: KnowledgeBaseSummary) => void;
  onDelete: (kb: KnowledgeBaseSummary) => void;
}) {
  const personal = items.filter(i => i.visibility === 'personal');
  const pub = items.filter(i => i.visibility === 'public');

  if (items.length === 0) {
    return (
      <div style={{ padding: '28px 14px', textAlign: 'center', color: C.textMuted, fontSize: 13, fontFamily: FONT.sans }}>
        Баз пока нет
      </div>
    );
  }

  return (
    <div style={{ padding: '6px 8px 18px' }}>
      {personal.length > 0 && (
        <>
          <GroupLabel>Мои</GroupLabel>
          {personal.map(kb => (
            <KnowledgeCard key={kb.id} kb={kb} active={kb.id === selectedId}
              onSelect={onSelect} onAddDocument={onAddDocument} onDelete={onDelete} />
          ))}
        </>
      )}
      {pub.length > 0 && (
        <>
          <GroupLabel>Публичные</GroupLabel>
          {pub.map(kb => (
            <KnowledgeCard key={kb.id} kb={kb} active={kb.id === selectedId}
              onSelect={onSelect} onAddDocument={onAddDocument} onDelete={onDelete} />
          ))}
        </>
      )}
    </div>
  );
}

function GroupLabel({ children }: { children: React.ReactNode }) {
  return (
    <div style={{
      fontSize: 11, fontWeight: 600, letterSpacing: '0.06em', textTransform: 'uppercase',
      color: C.textMuted, padding: '14px 10px 6px',
    }}>{children}</div>
  );
}

function VisibilityBadge({ visibility }: { visibility: 'personal' | 'public' }) {
  const isPub = visibility === 'public';
  return (
    <span style={{
      display: 'inline-flex', alignItems: 'center', height: 16, padding: '0 7px', borderRadius: 8,
      fontSize: 10, fontWeight: 600,
      background: isPub ? C.successBg : C.accentMuted,
      color: isPub ? C.successText : C.accent,
    }}>{isPub ? 'Публ.' : 'Личн.'}</span>
  );
}

function KnowledgeCard({ kb, active, onSelect, onAddDocument, onDelete }: {
  kb: KnowledgeBaseSummary;
  active: boolean;
  onSelect: (id: string) => void;
  onAddDocument: (kb: KnowledgeBaseSummary) => void;
  onDelete: (kb: KnowledgeBaseSummary) => void;
}) {
  const [menu, setMenu] = useState(false);

  const triggerMenu = (e: React.MouseEvent) => {
    e.preventDefault();
    e.stopPropagation();
    setMenu(true);
  };

  return (
    <div
      onClick={() => onSelect(kb.id)}
      onContextMenu={kb.deletable ? triggerMenu : undefined}
      title={kb.deletable ? undefined : 'Привязана к разделу — удаляется через него'}
      style={{
        position: 'relative',
        display: 'flex', alignItems: 'center', gap: 11, padding: '9px 10px', borderRadius: R.lg,
        cursor: 'pointer', transition: 'background 0.1s',
        border: `1px solid ${active ? C.border : 'transparent'}`,
        background: active ? C.bgCard : 'transparent',
        boxShadow: active ? '0 1px 2px rgba(60,45,30,.05)' : 'none',
      }}
      onMouseEnter={e => { if (!active) e.currentTarget.style.background = C.bgSelected; }}
      onMouseLeave={e => { if (!active) e.currentTarget.style.background = 'transparent'; }}
    >
      <span style={{
        width: 32, height: 32, borderRadius: 9, flex: 'none', display: 'flex', alignItems: 'center', justifyContent: 'center',
        color: active ? C.accent : C.textSecondary, background: active ? C.accentMuted : C.bgCard,
      }}>{typeIcon(kb.type, 17)}</span>

      <div style={{ flex: 1, minWidth: 0 }}>
        <div style={{
          fontWeight: 600, fontSize: 13, color: C.textHeading,
          whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
        }}>{kb.title}</div>
        <div style={{
          display: 'flex', alignItems: 'center', gap: 6, marginTop: 2, fontSize: 11, color: C.textSecondary,
          whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
        }}>
          <span>{kb.type}</span>
          <span style={{ color: C.textMuted }}>·</span>
          <span>{kb.documentCount} док.</span>
          <span style={{ color: C.textMuted }}>·</span>
          <VisibilityBadge visibility={kb.visibility} />
        </div>
      </div>

      {kb.deletable ? (
        <span style={{ position: 'relative', flex: 'none' }}>
          <button
            onClick={triggerMenu}
            title="Действия"
            style={{
              width: 26, height: 26, borderRadius: 7, border: 'none', background: 'transparent',
              cursor: 'pointer', color: C.textMuted, display: 'flex', alignItems: 'center', justifyContent: 'center',
            }}
            onMouseEnter={e => { e.currentTarget.style.background = C.bgSelected; e.currentTarget.style.color = C.textPrimary; }}
            onMouseLeave={e => { e.currentTarget.style.background = 'transparent'; e.currentTarget.style.color = C.textMuted; }}
          ><IconDots size={16} /></button>
          {menu && (
            <Menu onClose={() => setMenu(false)} top={30} align="right" minWidth={210}>
              <MenuItem icon={<><path d="M9 18l6-6-6-6" /></>} label="Открыть" onClick={() => { setMenu(false); onSelect(kb.id); }} />
              <MenuItem icon={<><path d="M12 5v14M5 12h14" /></>} label="Добавить документ" onClick={() => { setMenu(false); onAddDocument(kb); }} />
              <div style={{ height: 1, background: C.border, margin: '4px 2px' }} />
              <MenuItem icon={<><path d="M3 6h18M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2" /></>}
                label="Удалить базу" danger onClick={() => { setMenu(false); onDelete(kb); }} />
            </Menu>
          )}
        </span>
      ) : (
        <span style={{ flex: 'none', color: C.textMuted, width: 26, display: 'flex', justifyContent: 'center' }} title="Привязана к разделу">
          <IconLock size={13} />
        </span>
      )}
    </div>
  );
}

// Empty-state раздела (нет баз / Dify не настроен)
export function KnowledgeEmptyState({ configured, onNew }: { configured: boolean; onNew: () => void }) {
  return (
    <div style={{
      height: '100%', display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center',
      textAlign: 'center', gap: 8, padding: 40,
    }}>
      <div style={{
        width: 64, height: 64, borderRadius: 18, background: C.bgPanel, color: C.accent,
        display: 'flex', alignItems: 'center', justifyContent: 'center', marginBottom: 8,
      }}><IconBook size={30} /></div>
      <div style={{ fontFamily: FONT.serif, fontWeight: 500, fontSize: 22, color: C.textPrimary }}>
        {configured ? 'Знания' : 'Базы знаний недоступны'}
      </div>
      <div style={{ color: C.textSecondary, maxWidth: 380, fontSize: 13.5 }}>
        {configured
          ? 'Создайте базу знаний — свою или публичную — и наполняйте её документами для семантического поиска.'
          : <>Dify не настроен. Задайте <code style={{ fontFamily: FONT.mono, fontSize: 12, background: C.bgPanel, padding: '1px 5px', borderRadius: 4 }}>Dify:ApiKey</code> в appsettings.Local.json.</>}
      </div>
      {configured && (
        <button onClick={onNew} style={{
          marginTop: 14, display: 'inline-flex', alignItems: 'center', gap: 7, height: 36, padding: '0 16px',
          borderRadius: R.lg, background: C.accent, color: C.onAccent, border: 'none', cursor: 'pointer',
          fontFamily: FONT.sans, fontSize: 13.5, fontWeight: 600,
        }}><IconPlus size={16} />Новая база</button>
      )}
    </div>
  );
}
