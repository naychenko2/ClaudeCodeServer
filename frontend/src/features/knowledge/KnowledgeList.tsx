import { useState } from 'react';
import type { KnowledgeBaseSummary } from '../../types';
import { C, FONT, R } from '../../lib/design';
import { typeIcon, IconDots, IconPlus, IconBook } from './shared';
import { KbActionsMenu } from './KbActionsMenu';

// Список баз знаний с группами «Мои» / «Публичные». Карточка — кликабельная;
// у удаляемых баз есть ⋯-меню и правый клик (Открыть / Добавить документ / Удалить),
// у привязанных (заметок/проектов/персон) — только 🔒 (удалить через раздел-владелец).
export function KnowledgeList({ items, selectedId, isMobile, onSelect, onAddDocument, onDelete }: {
  items: KnowledgeBaseSummary[];
  selectedId: string | null;
  isMobile: boolean;
  onSelect: (id: string) => void;
  onAddDocument: (kb: KnowledgeBaseSummary) => void;
  onDelete: (kb: KnowledgeBaseSummary) => void;
}) {
  const personal = items.filter(i => i.visibility === 'personal');
  const pub = items.filter(i => i.visibility === 'public');

  if (items.length === 0) {
    return (
      <div style={{ padding: '20px 12px', color: C.textMuted, fontSize: 13, fontFamily: FONT.sans, lineHeight: 1.5 }}>
        Баз пока нет. Создайте свою или публичную — и наполняйте документами для поиска.
      </div>
    );
  }

  return (
    <div style={{ padding: '8px 8px 20px' }}>
      {personal.length > 0 && (
        <>
          <GroupLabel>Мои</GroupLabel>
          {personal.map(kb => (
            <KnowledgeCard key={kb.id} kb={kb} active={kb.id === selectedId} isMobile={isMobile}
              onSelect={onSelect} onAddDocument={onAddDocument} onDelete={onDelete} />
          ))}
        </>
      )}
      {pub.length > 0 && (
        <>
          <GroupLabel>Публичные</GroupLabel>
          {pub.map(kb => (
            <KnowledgeCard key={kb.id} kb={kb} active={kb.id === selectedId} isMobile={isMobile}
              onSelect={onSelect} onAddDocument={onAddDocument} onDelete={onDelete} />
          ))}
        </>
      )}
    </div>
  );
}

// Заголовок группы — единый формат с NotesList/PersonaList
function GroupLabel({ children }: { children: React.ReactNode }) {
  return (
    <div style={{
      fontSize: 10.5, fontWeight: 700, letterSpacing: '0.06em', textTransform: 'uppercase',
      color: C.textMuted, fontFamily: FONT.sans, padding: '8px 10px 4px',
    }}>{children}</div>
  );
}

// Бейдж видимости — единая геометрия с бейджем в шапке базы
export function VisibilityBadge({ visibility, variant = 'list' }: { visibility: 'personal' | 'public'; variant?: 'list' | 'header' }) {
  const isPub = visibility === 'public';
  const h = variant === 'header' ? 19 : 17;
  return (
    <span style={{
      display: 'inline-flex', alignItems: 'center', height: h, padding: '0 8px', borderRadius: R.sm,
      fontSize: variant === 'header' ? 11 : 10, fontWeight: 600, whiteSpace: 'nowrap', flex: 'none',
      background: isPub ? C.successBg : C.accentMuted,
      color: isPub ? C.successText : C.accent,
    }}>{isPub ? 'Публичная' : 'Личная'}</span>
  );
}

function KnowledgeCard({ kb, active, isMobile, onSelect, onAddDocument, onDelete }: {
  kb: KnowledgeBaseSummary;
  active: boolean;
  isMobile: boolean;
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
      onContextMenu={triggerMenu}
      title={kb.deletable ? undefined : 'Привязана к разделу — удаляется через него'}
      style={{
        position: 'relative',
        display: 'flex', alignItems: 'center', gap: 10, padding: '8px 10px', borderRadius: R.md,
        cursor: 'pointer', transition: 'background 0.1s', marginBottom: 2,
        // Активная — accentMuted (как в NotesList/PersonaList), без тени
        background: active ? C.accentMuted : 'transparent',
      }}
      onMouseEnter={e => { if (!active) e.currentTarget.style.background = C.bgSelected; }}
      onMouseLeave={e => { if (!active) e.currentTarget.style.background = 'transparent'; }}
    >
      {/* Плитка-иконка типа — квадрат R.md, как у аватара персоны */}
      <span style={{
        width: 32, height: 32, borderRadius: R.md, flex: 'none', display: 'flex', alignItems: 'center', justifyContent: 'center',
        color: active ? C.accent : C.textSecondary,
        background: active ? C.bgWhite : C.bgCard,
      }}>{typeIcon(kb.type, 17)}</span>

      <div style={{ flex: 1, minWidth: 0 }}>
        <div style={{
          fontWeight: 600, fontSize: 13, color: C.textHeading,
          whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
        }}>{kb.title}</div>
        {/* Метаданные единым форматом: тип · кол-во док. · бейдж видимости */}
        <div style={{
          display: 'flex', alignItems: 'center', gap: 6, marginTop: 2, fontSize: 11.5, color: C.textSecondary,
          whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
        }}>
          <span>{kb.type}</span>
          <Dot />
          <span>{kb.documentCount} {pluralDocs(kb.documentCount)}</span>
          <Dot />
          <VisibilityBadge visibility={kb.visibility} />
        </div>
      </div>

      <span style={{ position: 'relative', flex: 'none' }}>
        <button
          onClick={triggerMenu}
          title="Действия"
          style={{
            width: 26, height: 26, borderRadius: R.sm, border: 'none', background: 'transparent',
            cursor: 'pointer', color: C.textMuted, display: 'flex', alignItems: 'center', justifyContent: 'center',
          }}
          onMouseEnter={e => { e.currentTarget.style.background = C.bgSelected; e.currentTarget.style.color = C.textPrimary; }}
          onMouseLeave={e => { e.currentTarget.style.background = 'transparent'; e.currentTarget.style.color = C.textMuted; }}>
          <IconDots size={16} />
        </button>
        {menu && (
          <KbActionsMenu kb={kb} isMobile={isMobile}
            onClose={() => setMenu(false)}
            onAddDocument={() => onAddDocument(kb)}
            onDelete={() => onDelete(kb)} />
        )}
      </span>
    </div>
  );
}

function Dot() {
  return <span style={{ color: C.textMuted }}>·</span>;
}

function pluralDocs(n: number): string {
  const last = n % 10, last2 = n % 100;
  if (last2 >= 11 && last2 <= 14) return 'док.';
  if (last === 1) return 'док.';
  if (last >= 2 && last <= 4) return 'док.';
  return 'док.';
}

// Empty-state раздела (нет баз / Dify не настроен). Переиспользуем общий EmptyState,
// как в NotesPage/PersonasPage — иконка/заголовок/подзаголовок/действие.
export function KnowledgeEmptyState({ configured, onNew }: { configured: boolean; onNew: () => void }) {
  return (
    <div style={{ height: '100%', display: 'flex', alignItems: 'center', justifyContent: 'center' }}>
      <div style={{
        display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center',
        textAlign: 'center', gap: 8, padding: 40, maxWidth: 420,
      }}>
        <div style={{
          width: 56, height: 56, borderRadius: 16, background: C.bgPanel, color: C.accent,
          display: 'flex', alignItems: 'center', justifyContent: 'center', marginBottom: 8,
        }}><IconBook size={28} /></div>
        <div style={{ fontFamily: FONT.serif, fontWeight: 500, fontSize: 21, color: C.textPrimary, letterSpacing: '-0.01em' }}>
          {configured ? 'Знания' : 'Базы знаний недоступны'}
        </div>
        <div style={{ color: C.textSecondary, maxWidth: 360, fontSize: 13.5, lineHeight: 1.5 }}>
          {configured
            ? 'Создайте базу знаний — свою или публичную — и наполняйте её документами для семантического поиска.'
            : <>Dify не настроен. Задайте <code style={{ fontFamily: FONT.mono, fontSize: 12, background: C.bgPanel, padding: '1px 5px', borderRadius: R.sm }}>Dify:ApiKey</code> в appsettings.Local.json.</>}
        </div>
        {configured && (
          <button onClick={onNew} style={{
            marginTop: 12, display: 'inline-flex', alignItems: 'center', gap: 7, height: 36, padding: '0 16px',
            borderRadius: R.md, background: C.accent, color: C.onAccent, border: 'none', cursor: 'pointer',
            fontFamily: FONT.sans, fontSize: 13.5, fontWeight: 600,
          }}><IconPlus size={16} />Новая база</button>
        )}
      </div>
    </div>
  );
}
