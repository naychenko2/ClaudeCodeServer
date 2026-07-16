import { useMemo, useState } from 'react';
import { Search } from 'lucide-react';
import { ICON_STROKE } from '../../components/ui/icons';
import { Button, IconField, TextArea } from '../../components/ui';
import { PillSwitch } from '../../components/Toolbar';
import { C, FONT, R, SHADOW } from '../../lib/design';
import { useInlineEdit } from '../../lib/useInlineEdit';
import { MemoryEntryCard } from './MemoryEntryCard';
import type { MemoryEntryView, MemoryTypeMeta } from './memoryTypes';

// Общая панель памяти — контент (без внешней шапки/скролл-обёртки: те остаются у
// конкретной страницы, т.к. персона встраивается ещё и в стрип чата, а команда живёт
// внутри общей вкладочной прокрутки TeamCommandCenter). Отвечает за: заметное ручное
// добавление НАВЕРХУ, поиск, фильтры (источник/тип), список — сгруппированный ПО
// ИСТОЧНИКУ (✋ Ручное → ✨ Предложено (только там, где есть pending) → ✨ Авто),
// внутри секции — важность убыв., затем дата убыв.
type SourceFilter = 'all' | 'manual' | 'auto';
const SOURCE_OPTIONS: { value: SourceFilter; label: string }[] = [
  { value: 'all', label: 'Все' },
  { value: 'manual', label: '✋ Ручное' },
  { value: 'auto', label: '✨ Авто' },
];

interface Section<TType extends string> {
  key: string;
  label: string;
  hint?: string;
  items: MemoryEntryView<TType>[];
}

export interface MemoryPanelProps<TType extends string> {
  entries: MemoryEntryView<TType>[] | null;   // null — загрузка
  error?: boolean;
  onRetry?: () => void;
  typeMeta: Record<TType, MemoryTypeMeta>;
  typeOrder: TType[];

  // Быстрое ручное добавление
  addHint: string;
  addPlaceholder: string;
  onAdd: (type: TType, text: string) => Promise<unknown>;

  // Действия карточки
  onEdit: (id: string, text: string) => Promise<unknown>;
  onRemove: (id: string) => Promise<unknown>;
  onConfirm?: (id: string) => Promise<unknown>;   // персона — есть; команда — нет (без pending-гейта)
  onToNote?: (id: string) => void;                // персона — есть; команда — нет

  emptyIcon: string;
  emptyTitle: string;
  emptyHint: string;
}

export function MemoryPanel<TType extends string>({
  entries, error, onRetry, typeMeta, typeOrder,
  addHint, addPlaceholder, onAdd,
  onEdit, onRemove, onConfirm, onToNote,
  emptyIcon, emptyTitle, emptyHint,
}: MemoryPanelProps<TType>) {
  const [search, setSearch] = useState('');
  const [typeFilter, setTypeFilter] = useState<TType | 'all'>('all');
  const [sourceFilter, setSourceFilter] = useState<SourceFilter>('all');
  const [addType, setAddType] = useState<TType>(typeOrder[0]);
  const [addText, setAddText] = useState('');
  const [adding, setAdding] = useState(false);
  const [removingId, setRemovingId] = useState<string | null>(null);
  const edit = useInlineEdit(onEdit);

  const loading = entries === null;
  const all = useMemo(() => entries ?? [], [entries]);

  const typeCounts = useMemo(() => {
    const c = new Map<TType, number>();
    for (const e of all) c.set(e.type, (c.get(e.type) ?? 0) + 1);
    return c;
  }, [all]);

  const filtered = useMemo(() => {
    const q = search.trim().toLowerCase();
    return all.filter(e => {
      if (typeFilter !== 'all' && e.type !== typeFilter) return false;
      if (sourceFilter !== 'all' && e.origin !== sourceFilter) return false;
      if (q && !e.text.toLowerCase().includes(q)) return false;
      return true;
    });
  }, [all, typeFilter, sourceFilter, search]);

  const sections = useMemo<Section<TType>[]>(() => {
    const manual: MemoryEntryView<TType>[] = [];
    const pending: MemoryEntryView<TType>[] = [];
    const auto: MemoryEntryView<TType>[] = [];
    for (const e of filtered) {
      if (e.origin === 'manual') manual.push(e);
      else if (e.pending) pending.push(e);
      else auto.push(e);
    }
    const bySalienceDate = (a: MemoryEntryView<TType>, b: MemoryEntryView<TType>) =>
      (b.salience - a.salience) || b.createdAt.localeCompare(a.createdAt);
    manual.sort(bySalienceDate); pending.sort(bySalienceDate); auto.sort(bySalienceDate);
    const list: Section<TType>[] = [
      { key: 'manual', label: '✋ Ручное', items: manual },
      { key: 'pending', label: '✨ Предложено', hint: 'ждут решения', items: pending },
      { key: 'auto', label: '✨ Авто', items: auto },
    ];
    return list.filter(s => s.items.length > 0);
  }, [filtered]);

  const submit = async () => {
    const text = addText.trim();
    if (!text || adding) return;
    setAdding(true);
    try { await onAdd(addType, text); setAddText(''); }
    finally { setAdding(false); }
  };

  const remove = async (id: string) => {
    setRemovingId(id);
    try { await onRemove(id); }
    finally { setRemovingId(null); }
  };

  const isEmpty = !loading && !error && all.length === 0;
  const noMatches = !loading && !error && all.length > 0 && filtered.length === 0;

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 14, maxWidth: 720, margin: '0 auto', width: '100%' }}>
      {/* Быстрое ручное добавление — заметная карточка НАВЕРХУ (ручное первично) */}
      <div style={cardStyle}>
        <div style={{ fontFamily: FONT.serif, fontSize: 15, color: C.textHeading }}>✋ Добавить вручную</div>
        <div style={{ fontSize: 12, color: C.textMuted, fontFamily: FONT.sans, lineHeight: 1.5, margin: '4px 0 10px' }}>{addHint}</div>
        <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
          <select value={addType} onChange={e => setAddType(e.target.value as TType)} style={selectStyle} aria-label="Тип записи">
            {typeOrder.map(t => <option key={t} value={t}>{typeMeta[t].title}</option>)}
          </select>
          <TextArea
            value={addText}
            onChange={setAddText}
            autoGrow
            minHeight={44}
            placeholder={addPlaceholder}
            onKeyDown={e => { if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); void submit(); } }}
          />
          <div style={{ display: 'flex', justifyContent: 'flex-end' }}>
            <Button variant="primary" size="sm" loading={adding} disabled={adding || !addText.trim()} onClick={() => void submit()}>
              {adding ? 'Сохраняю…' : 'Запомнить'}
            </Button>
          </div>
        </div>
      </div>

      {!isEmpty && (
        <>
          <IconField icon={<Search size={15} strokeWidth={ICON_STROKE} />} value={search} onChange={setSearch}
            placeholder="Поиск по записям…" height={38} fontSize={13} />

          <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
            <PillSwitch value={sourceFilter} onChange={setSourceFilter} options={SOURCE_OPTIONS} />
            <div style={{ display: 'flex', gap: 5, flexWrap: 'wrap' }}>
              <button onClick={() => setTypeFilter('all')} style={typeFilter === 'all' ? filterChipActive : filterChip}>
                Все {all.length}
              </button>
              {typeOrder.map(t => (
                <button key={t} onClick={() => setTypeFilter(t)} style={typeFilter === t ? filterChipActive : filterChip}>
                  {typeMeta[t].title} {typeCounts.get(t) ?? 0}
                </button>
              ))}
            </div>
          </div>
        </>
      )}

      {loading && <div style={{ padding: 40, textAlign: 'center', color: C.textMuted, fontSize: 13 }}>Загрузка…</div>}
      {error && !loading && (
        <div style={{ padding: 40, textAlign: 'center', color: C.dangerText, fontSize: 13 }}>
          Не удалось загрузить память.{' '}
          {onRetry && <button onClick={onRetry} style={linkBtn}>Повторить</button>}
        </div>
      )}
      {isEmpty && (
        <div style={{ padding: '40px 20px', textAlign: 'center', display: 'flex', flexDirection: 'column', alignItems: 'center', gap: 8 }}>
          <div style={{ fontSize: 30, opacity: 0.5 }}>{emptyIcon}</div>
          <div style={{ fontFamily: FONT.serif, fontSize: 17, color: C.textHeading }}>{emptyTitle}</div>
          <div style={{ fontSize: 13, color: C.textSecondary, maxWidth: 320, lineHeight: 1.5 }}>{emptyHint}</div>
        </div>
      )}
      {noMatches && (
        <div style={{ ...cardStyle, textAlign: 'center', color: C.textMuted, fontFamily: FONT.sans, fontSize: 13 }}>
          Ничего не найдено по фильтру.{' '}
          <button onClick={() => { setSearch(''); setTypeFilter('all'); setSourceFilter('all'); }} style={linkBtn}>Сбросить фильтры</button>
        </div>
      )}

      {!loading && !error && sections.map(s => (
        <div key={s.key}>
          <div style={{ display: 'flex', alignItems: 'baseline', gap: 8, marginBottom: 8, padding: '0 2px' }}>
            <span style={{ fontFamily: FONT.sans, fontSize: 13, fontWeight: 700, color: C.textHeading, letterSpacing: '0.01em' }}>{s.label}</span>
            <span style={{ fontSize: 11.5, color: C.textMuted }}>{s.items.length}</span>
            {s.hint && <span style={{ fontSize: 11.5, color: C.textMuted, fontStyle: 'italic', marginLeft: 'auto' }}>{s.hint}</span>}
          </div>
          <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
            {s.items.map(e => (
              <MemoryEntryCard
                key={e.id} entry={e} typeMeta={typeMeta[e.type]}
                onRemove={() => void remove(e.id)} removing={removingId === e.id}
                onToNote={onToNote ? () => onToNote(e.id) : undefined}
                onConfirm={e.pending && onConfirm ? () => onConfirm(e.id) : undefined}
                editing={edit.editingId === e.id} editText={edit.text} onEditTextChange={edit.setText}
                onStartEdit={() => edit.start(e.id, e.text)} onSaveEdit={() => void edit.save()}
                onCancelEdit={edit.cancel} savingEdit={edit.saving}
              />
            ))}
          </div>
        </div>
      ))}
    </div>
  );
}

const cardStyle = { background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.xl, boxShadow: SHADOW.card, padding: 16 } as const;
const selectStyle = {
  background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.xl,
  padding: '0 10px', fontSize: 13, fontFamily: FONT.sans, color: C.textHeading, cursor: 'pointer', height: 38,
} as const;
const linkBtn = { background: 'transparent', border: 'none', color: C.accent, cursor: 'pointer', fontSize: 13, fontFamily: FONT.sans, textDecoration: 'underline', padding: 0 } as const;
const filterChip = { border: 'none', background: C.bgInset, color: C.textSecondary, borderRadius: R.sm, padding: '3px 9px', fontSize: 11.5, fontWeight: 600, cursor: 'pointer', fontFamily: FONT.sans } as const;
const filterChipActive = { ...filterChip, background: C.accentLight, color: C.accent };
