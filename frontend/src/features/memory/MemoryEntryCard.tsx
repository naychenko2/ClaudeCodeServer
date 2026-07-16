import { Check, FileText, Pencil, X } from 'lucide-react';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { IconButton, TextArea } from '../../components/ui';
import { C, FONT, R } from '../../lib/design';
import type { MemoryEntryView, MemoryTypeMeta } from './memoryTypes';
import { fmtDate, shortAgo } from './memoryTypes';

// Карточка одной записи памяти — общая для персоны и команды проекта. Анатомия:
// мета-строка (тип + источник + важность) → текст → теги → дата → колонка действий.
// «Отклонить» вместо «Забыть» для авто-записей — один и тот же обработчик onRemove,
// разница только в подписи/тултипе (не плодим второй визуальный язык).
export function MemoryEntryCard({
  entry, typeMeta, onRemove, removing, onToNote, onConfirm,
  editing, editText, onEditTextChange, onStartEdit, onSaveEdit, onCancelEdit, savingEdit,
}: {
  entry: MemoryEntryView;
  typeMeta: MemoryTypeMeta;
  onRemove: () => void;
  removing: boolean;
  onToNote?: () => void;
  onConfirm?: () => void;
  editing: boolean;
  editText: string;
  onEditTextChange: (v: string) => void;
  onStartEdit: () => void;
  onSaveEdit: () => void;
  onCancelEdit: () => void;
  savingEdit: boolean;
}) {
  const isAuto = entry.origin === 'auto';
  const removeLabel = isAuto ? 'Отклонить' : 'Забыть';

  return (
    <div style={{
      position: 'relative', background: C.bgWhite, border: `1px solid ${C.border}`,
      borderLeft: `3px solid ${typeMeta.color}`, borderRadius: R.xl, padding: '10px 12px',
      opacity: removing ? 0.5 : 1,
    }}>
      {editing ? (
        <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
          <TextArea
            value={editText}
            onChange={onEditTextChange}
            autoGrow
            autoFocus
            minHeight={44}
            style={{ fontSize: 13.5 }}
            onKeyDown={e => {
              if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); onSaveEdit(); }
              else if (e.key === 'Escape') { e.preventDefault(); onCancelEdit(); }
            }}
          />
          <div style={{ display: 'flex', gap: 8, justifyContent: 'flex-end' }}>
            <button onClick={onCancelEdit} disabled={savingEdit} style={editGhostBtn}>Отмена</button>
            <button onClick={onSaveEdit} disabled={savingEdit || !editText.trim()} style={{ ...editSaveBtn, opacity: savingEdit || !editText.trim() ? 0.55 : 1 }}>
              {savingEdit ? 'Сохраняю…' : 'Сохранить'}
            </button>
          </div>
        </div>
      ) : (
        <div style={{ display: 'flex', gap: 8, alignItems: 'flex-start' }}>
          <div style={{ flex: 1, minWidth: 0 }}>
            {/* Мета-строка: тип + источник (+ «предложено») + важность */}
            <div style={{ display: 'flex', alignItems: 'center', gap: 6, flexWrap: 'wrap', marginBottom: 6 }}>
              <TypeBadge meta={typeMeta} />
              <OriginBadge origin={entry.origin} detail={entry.originDetail} />
              {entry.pending && (
                <span style={pendingBadge}>предложено</span>
              )}
              <span style={{ marginLeft: 'auto' }}><SalienceMeter value={entry.salience} /></span>
            </div>
            <div style={{ fontSize: 13.5, color: C.textPrimary, lineHeight: 1.5, whiteSpace: 'pre-wrap', wordBreak: 'break-word' }}>
              {entry.text}
            </div>
            {(entry.tags && entry.tags.length > 0) && (
              <div style={{ display: 'flex', flexWrap: 'wrap', gap: 5, marginTop: 7 }}>
                {entry.tags.map(tag => (
                  <span key={tag} style={{
                    fontSize: 11, color: C.textSecondary, background: C.bgInset,
                    border: `1px solid ${C.border}`, borderRadius: R.sm, padding: '1px 7px',
                  }}>#{tag}</span>
                ))}
              </div>
            )}
            <div style={{ fontSize: 11, color: C.textMuted, marginTop: 6 }} title={fmtDate(entry.createdAt)}>{shortAgo(entry.createdAt)}</div>
          </div>
          <div style={{ display: 'flex', flexDirection: 'column', gap: 2, flexShrink: 0 }}>
            {onConfirm && (
              <IconButton size="xs" title="Подтвердить — запомнить" color={C.success} onClick={onConfirm}>
                <Check size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
              </IconButton>
            )}
            <IconButton size="xs" title="Редактировать" onClick={onStartEdit}>
              <Pencil size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
            </IconButton>
            {onToNote && (
              <IconButton size="xs" title="Превратить в заметку" onClick={onToNote}>
                <FileText size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
              </IconButton>
            )}
            <IconButton size="xs" tone="danger" title={removeLabel} disabled={removing} onClick={onRemove}>
              <X size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
            </IconButton>
          </div>
        </div>
      )}
    </div>
  );
}

function TypeBadge({ meta }: { meta: MemoryTypeMeta }) {
  return (
    <span style={{
      fontSize: 10.5, fontWeight: 700, color: meta.color, background: meta.softBg,
      borderRadius: R.sm, padding: '1px 7px', fontFamily: FONT.sans, whiteSpace: 'nowrap',
    }} title={meta.hint}>
      {meta.title}
    </span>
  );
}

// Источник — намеренно эмодзи (не lucide-иконка): ✋/✨ уже устоявшаяся в приложении пара
// сигналов «рука человека» / «прикосновение ИИ» (см. ✨-кнопки в Заметках/Персонах),
// мгновенно читается даже в мелком бейдже.
function OriginBadge({ origin, detail }: { origin: MemoryOrigin; detail?: string }) {
  if (origin === 'manual') {
    return <span style={{ ...originBadgeBase, color: C.textSecondary, background: C.bgInset }} title="Добавлено вручную">✋ Ручное</span>;
  }
  return (
    <span style={{ ...originBadgeBase, color: C.info, background: C.infoBg }}
      title={detail ? `Добавлено автоматически — ${detail}` : 'Добавлено автоматически'}>
      ✨ Авто
    </span>
  );
}

// Лёгкий индикатор важности — 5 точек, компактно, подробности по tooltip
function SalienceMeter({ value }: { value: number }) {
  const clamped = Math.min(1, Math.max(0, value));
  const filled = Math.round(clamped * 5);
  return (
    <span style={{ display: 'inline-flex', gap: 2, alignItems: 'center', flexShrink: 0 }} title={`Важность: ${Math.round(clamped * 100)}%`}>
      {Array.from({ length: 5 }).map((_, i) => (
        <span key={i} style={{
          width: 4, height: 4, borderRadius: R.full, flexShrink: 0,
          background: i < filled ? C.textSecondary : C.border,
        }} />
      ))}
    </span>
  );
}

type MemoryOrigin = MemoryEntryView['origin'];

const originBadgeBase = {
  fontSize: 10.5, fontWeight: 600, borderRadius: R.sm, padding: '1px 7px',
  fontFamily: FONT.sans, whiteSpace: 'nowrap',
} as const;
const pendingBadge = { display: 'inline-block', fontSize: 10.5, fontWeight: 600, color: C.accent, background: C.accentSoft, borderRadius: R.sm, padding: '1px 7px', fontFamily: FONT.sans, whiteSpace: 'nowrap' } as const;
const editGhostBtn = {
  background: 'transparent', border: `1px solid ${C.border}`, borderRadius: R.md,
  padding: '5px 12px', fontSize: 12.5, fontFamily: FONT.sans, color: C.textSecondary, cursor: 'pointer',
} as const;
const editSaveBtn = {
  background: C.accent, color: C.onAccent, border: 'none', borderRadius: R.md,
  padding: '5px 12px', fontSize: 12.5, fontWeight: 600, fontFamily: FONT.sans, cursor: 'pointer',
} as const;
