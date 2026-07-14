import { useState } from 'react';
import { Pencil, Search, X } from 'lucide-react';
import type { CSSProperties } from 'react';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import type { TeamMemoryEntry } from '../../types';
import { C, FONT, R, SHADOW } from '../../lib/design';
import { TextArea, IconField, Button, IconButton } from '../../components/ui';
import { useInlineEdit } from '../../lib/useInlineEdit';

// Вкладка «Память команды» (Командный центр проекта): общие факты, которые recall'ят
// все персоны команды наравне с личной памятью. Дизайн — по образцу личной памяти
// персоны (PersonaMemoryPanel): карточка с цветной левой рамкой, относительная давность,
// инлайн-редактирование текста по клику ✎ вместо только добавления/удаления.
export function TeamMemoryPanel({ mem, onAdd, onUpdate, onRemove, stripe }: {
  mem: TeamMemoryEntry[] | null;
  onAdd: (text: string) => Promise<unknown>;
  onUpdate: (id: string, text: string) => Promise<unknown>;
  onRemove: (id: string) => Promise<unknown>;
  stripe: string;
}) {
  const [search, setSearch] = useState('');
  const [newText, setNewText] = useState('');
  const [adding, setAdding] = useState(false);
  const [removingId, setRemovingId] = useState<string | null>(null);
  const edit = useInlineEdit(onUpdate);

  const q = search.trim().toLowerCase();
  const sorted = [...(mem ?? [])].sort((a, b) => b.createdAt.localeCompare(a.createdAt));
  const filtered = q ? sorted.filter(m => m.text.toLowerCase().includes(q)) : sorted;

  const submit = async () => {
    const text = newText.trim();
    if (!text || adding) return;
    setAdding(true);
    try { await onAdd(text); setNewText(''); }
    finally { setAdding(false); }
  };

  const remove = async (id: string) => {
    setRemovingId(id);
    try { await onRemove(id); }
    finally { setRemovingId(null); }
  };

  return (
    <>
      <div style={cardStyle}>
        <div style={{ fontFamily: FONT.serif, fontSize: 17, color: C.textHeading }}>Память команды</div>
        <div style={{ fontSize: 12.5, color: C.textMuted, fontFamily: FONT.sans, lineHeight: 1.5, margin: '6px 0 14px' }}>
          Общие факты и договорённости проекта — их recall'ят все персоны команды в каждом ходе.
        </div>
        <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
          <TextArea
            value={newText}
            onChange={setNewText}
            autoGrow
            minHeight={44}
            placeholder="Напр.: ревью через PR, не напрямую в main; релизы по пятницам"
            onKeyDown={e => { if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); void submit(); } }}
          />
          <div style={{ display: 'flex', justifyContent: 'flex-end' }}>
            <Button variant="primary" size="sm" loading={adding} disabled={adding || !newText.trim()} onClick={() => void submit()}>
              {adding ? 'Сохраняю…' : 'Добавить'}
            </Button>
          </div>
        </div>
      </div>

      <IconField icon={<Search size={15} strokeWidth={ICON_STROKE} />} value={search} onChange={setSearch}
        placeholder="Поиск по записям…" height={38} fontSize={13} />

      <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
        {mem === null ? <Muted>Загрузка…</Muted>
          : filtered.length === 0 ? (
            <div style={{ ...cardStyle, textAlign: 'center', color: C.textMuted, fontFamily: FONT.sans, fontSize: 13 }}>
              {mem.length === 0 ? 'Пока пусто. Запишите общий факт выше — его запомнят все персоны команды.' : 'Ничего не найдено. '}
              {mem.length > 0 && <button onClick={() => setSearch('')} style={linkBtn}>Сбросить фильтр</button>}
            </div>
          ) : filtered.map(m => (
            <MemoryRow
              key={m.id} entry={m} stripe={stripe} removing={removingId === m.id} onRemove={() => void remove(m.id)}
              editing={edit.editingId === m.id} editText={edit.text} onEditTextChange={edit.setText}
              onStartEdit={() => edit.start(m.id, m.text)} onSaveEdit={() => void edit.save()}
              onCancelEdit={edit.cancel} savingEdit={edit.saving}
            />
          ))}
      </div>
    </>
  );
}

// Карточка одной записи общей памяти команды — тот же форм-фактор, что у MemoryCard
// личной памяти персоны, но без типов/тегов (память команды — плоская, курируемая вручную).
function MemoryRow({ entry, stripe, removing, onRemove, editing, editText, onEditTextChange, onStartEdit, onSaveEdit, onCancelEdit, savingEdit }: {
  entry: TeamMemoryEntry;
  stripe: string;
  removing: boolean;
  onRemove: () => void;
  editing: boolean;
  editText: string;
  onEditTextChange: (v: string) => void;
  onStartEdit: () => void;
  onSaveEdit: () => void;
  onCancelEdit: () => void;
  savingEdit: boolean;
}) {
  return (
    <div style={{
      position: 'relative', background: C.bgWhite, border: `1px solid ${C.border}`,
      borderLeft: `3px solid ${stripe}`, borderRadius: R.xl, padding: '10px 12px',
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
            <div style={{ fontSize: 13.5, color: C.textPrimary, lineHeight: 1.5, whiteSpace: 'pre-wrap', wordBreak: 'break-word' }}>
              {entry.text}
            </div>
            <div style={{ fontSize: 11, color: C.textMuted, marginTop: 6 }} title={fmtDate(entry.createdAt)}>{shortAgo(entry.createdAt)}</div>
          </div>
          <div style={{ display: 'flex', flexDirection: 'column', gap: 2, flexShrink: 0 }}>
            <IconButton size="xs" title="Редактировать" onClick={onStartEdit}>
              <Pencil size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
            </IconButton>
            <IconButton size="xs" tone="danger" title="Удалить" disabled={removing} onClick={onRemove}>
              <X size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
            </IconButton>
          </div>
        </div>
      )}
    </div>
  );
}

function Muted({ children }: { children: React.ReactNode }) {
  return <div style={{ fontSize: 13, color: C.textMuted, fontFamily: FONT.sans }}>{children}</div>;
}

// Короткая относительная дата: «только что», «5 мин», «3 ч», «2 дн», иначе — число/месяц
function shortAgo(iso: string): string {
  const t = new Date(iso).getTime();
  if (Number.isNaN(t)) return '';
  const diffMs = Date.now() - t;
  const min = Math.floor(diffMs / 60000);
  if (min < 1) return 'только что';
  if (min < 60) return `${min} мин`;
  const h = Math.floor(min / 60);
  if (h < 24) return `${h} ч`;
  const d = Math.floor(h / 24);
  if (d < 7) return `${d} дн`;
  return new Date(t).toLocaleDateString('ru-RU', { day: 'numeric', month: 'short' }).replace('.', '');
}
function fmtDate(iso: string): string {
  try { return new Date(iso).toLocaleDateString('ru-RU', { day: 'numeric', month: 'long', hour: '2-digit', minute: '2-digit' }); } catch { return ''; }
}

const cardStyle: CSSProperties = { background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.xl, boxShadow: SHADOW.card, padding: 16 };
const linkBtn: CSSProperties = { background: 'transparent', border: 'none', color: C.accent, fontSize: 13, fontWeight: 600, cursor: 'pointer', fontFamily: FONT.sans, padding: '4px 8px' };
const editGhostBtn: CSSProperties = {
  background: 'transparent', border: `1px solid ${C.border}`, borderRadius: R.md,
  padding: '5px 12px', fontSize: 12.5, fontFamily: FONT.sans, color: C.textSecondary, cursor: 'pointer',
};
const editSaveBtn: CSSProperties = {
  background: C.accent, color: C.onAccent, border: 'none', borderRadius: R.md,
  padding: '5px 12px', fontSize: 12.5, fontWeight: 600, fontFamily: FONT.sans, cursor: 'pointer',
};
