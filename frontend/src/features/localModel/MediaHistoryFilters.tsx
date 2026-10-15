// Меню «Фильтры» истории (макет §3): период — сегмент, исход/модель/пользователь —
// одиночный выбор в секции со счётчиком, футер «Сбросить всё» + «Готово».
//
// Счётчики: исход — из counters бэкенда (по всему периоду); модель и пользователь —
// из загруженных записей (бэкенд счётчики по ним не отдаёт): число — сколько таких
// записей в текущей выборке.
//
// Меню — Menu (anchor) под кнопкой-триггером; на мобиле — fullWidth-карточка
// (шторка). Выбор применяется сразу (onPatch), «Готово» только закрывает.

import { useState } from 'react';
import { Check } from 'lucide-react';
import { C, FONT, FS, ICON_SIZE, ICON_STROKE, R } from 'aihome_shell/kit';
import { Button, Menu, SegmentedControl } from '../../components/ui';
import type { MediaHistoryRecord } from './api';
import type { HistoryFilters } from './useMediaHistory';
import type { HistoryPeriod } from './mediaFormat';

// Секция меню: заголовок + содержимое
function Section({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 2 }}>
      <div style={{
        fontSize: FS.xs, fontWeight: 700, textTransform: 'uppercase', letterSpacing: '0.06em',
        color: C.textMuted, padding: '8px 8px 4px',
      }}>
        {title}
      </div>
      {children}
    </div>
  );
}

// Строка-опция: галочка (акцент) + подпись + счётчик (mono, приглушён)
function OptionRow({ label, count, selected, mono, onClick }: {
  label: string;
  count?: number;
  selected: boolean;
  mono?: boolean;
  onClick: () => void;
}) {
  const [hover, setHover] = useState(false);
  return (
    <button
      onClick={onClick}
      onMouseEnter={() => setHover(true)}
      onMouseLeave={() => setHover(false)}
      style={{
        display: 'flex', alignItems: 'center', gap: 8, width: '100%', textAlign: 'left',
        padding: '7px 8px', borderRadius: R.md, border: 'none', cursor: 'pointer',
        background: hover ? C.bgInset : 'transparent',
        fontSize: FS.md, fontFamily: FONT.sans, color: C.textPrimary,
      }}
    >
      <span style={{ width: ICON_SIZE.xs, flexShrink: 0, color: C.accent, display: 'inline-flex', alignItems: 'center' }}>
        {selected && <Check size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
      </span>
      <span style={{
        flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
        ...(mono ? { fontFamily: FONT.mono, fontSize: FS.sm } : null),
      }}>
        {label}
      </span>
      {count != null && (
        <span style={{ flexShrink: 0, fontSize: FS.xs, color: C.textMuted, fontFamily: FONT.mono }}>
          {count}
        </span>
      )}
    </button>
  );
}

// Значения + частота из записей: distinct, по убыванию частоты, затем по алфавиту
function distinctCounted(values: (string | null | undefined)[]): { value: string; count: number }[] {
  const m = new Map<string, number>();
  for (const v of values) {
    if (v == null || v === '') continue;
    m.set(v, (m.get(v) ?? 0) + 1);
  }
  return [...m.entries()]
    .map(([value, count]) => ({ value, count }))
    .sort((a, b) => b.count - a.count || a.value.localeCompare(b.value));
}

export function HistoryFilterMenu({ filters, counters, records, meUsername, onPatch, onReset, onClose }: {
  filters: HistoryFilters;
  counters: { kinds: Record<string, number>; outcomes: Record<string, number> };
  records: MediaHistoryRecord[];
  meUsername: string | null;
  onPatch: (patch: Partial<HistoryFilters>) => void;
  onReset: () => void;
  onClose: () => void;
}) {
  const outcomeOptions: { value: string | null; label: string; count?: number }[] = [
    { value: null, label: 'Любой' },
    { value: 'completed', label: 'Готово', count: counters.outcomes.completed },
    { value: 'cancelled', label: 'Отменено', count: counters.outcomes.cancelled },
    { value: 'failed', label: 'Ошибка', count: counters.outcomes.failed },
    { value: 'interrupted', label: 'Прервано', count: counters.outcomes.interrupted },
  ];
  const models = distinctCounted(records.map(r => r.model));
  // Пользователи (макет §3): «Все», «вы (логин)» (текущий, если в выборке),
  // остальные логины по алфавиту
  const ownerCounts = new Map<string, number>();
  for (const r of records) {
    if (r.owner == null || r.owner === '') continue;
    ownerCounts.set(r.owner, (ownerCounts.get(r.owner) ?? 0) + 1);
  }
  const meOwner = meUsername != null && ownerCounts.has(meUsername) ? meUsername : null;
  const otherOwners = [...ownerCounts.keys()].filter(o => o !== meOwner).sort((a, b) => a.localeCompare(b));

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 2 }}>
      <Section title="Период">
        <div style={{ padding: '0 8px 4px' }}>
          <SegmentedControl<HistoryPeriod>
            value={filters.period}
            onChange={p => onPatch({ period: p })}
            options={[
              { value: 'today', label: 'Сегодня' },
              { value: '24h', label: '24 ч' },
              { value: '7d', label: '7 дней' },
            ]}
          />
        </div>
      </Section>

      <Section title="Исход">
        {outcomeOptions.map(o => (
          <OptionRow
            key={o.value ?? 'any'}
            label={o.label}
            count={o.count}
            selected={filters.outcome === o.value}
            onClick={() => onPatch({ outcome: o.value })}
          />
        ))}
      </Section>

      <Section title="Модель">
        <OptionRow label="Любая" selected={filters.model == null} onClick={() => onPatch({ model: null })} />
        {models.map(m => (
          <OptionRow
            key={m.value}
            label={m.value}
            count={m.count}
            mono
            selected={filters.model === m.value}
            onClick={() => onPatch({ model: m.value })}
          />
        ))}
      </Section>

      <Section title="Пользователь">
        <OptionRow label="Все" selected={filters.login == null} onClick={() => onPatch({ login: null })} />
        {meOwner && (
          <OptionRow
            label={`вы (${meOwner})`}
            count={ownerCounts.get(meOwner)}
            selected={filters.login === meOwner}
            onClick={() => onPatch({ login: meOwner })}
          />
        )}
        {otherOwners.map(o => (
          <OptionRow
            key={o}
            label={o}
            count={ownerCounts.get(o)}
            selected={filters.login === o}
            onClick={() => onPatch({ login: o })}
          />
        ))}
      </Section>

      <div style={{
        display: 'flex', justifyContent: 'space-between', alignItems: 'center',
        borderTop: `1px solid ${C.borderLight}`, marginTop: 8, padding: '8px 4px 0',
      }}>
        <button
          onClick={onReset}
          style={{
            background: 'none', border: 'none', cursor: 'pointer', padding: 0,
            fontSize: FS.sm, color: C.textSecondary, fontFamily: FONT.sans,
            textDecoration: 'underline', textUnderlineOffset: 2,
          }}
        >
          Сбросить всё
        </button>
        <Button variant="primary" size="sm" onClick={onClose}>Готово</Button>
      </div>
    </div>
  );
}

// Обёртка меню: Menu (anchor) с карточкой HistoryFilterMenu.
// isMobile — fullWidth-карточка (шторка) на телефоне.
export function HistoryFilterMenuPopup({ anchor, isMobile, filters, counters, records, meUsername, onPatch, onReset, onClose }: {
  anchor: DOMRect;
  isMobile: boolean;
  filters: HistoryFilters;
  counters: { kinds: Record<string, number>; outcomes: Record<string, number> };
  records: MediaHistoryRecord[];
  meUsername: string | null;
  onPatch: (patch: Partial<HistoryFilters>) => void;
  onReset: () => void;
  onClose: () => void;
}) {
  return (
    <Menu
      anchor={anchor}
      onClose={onClose}
      anchorAlign="end"
      minWidth={280}
      maxWidth={320}
      maxHeight={420}
      innerScroll
      fullWidth={isMobile}
    >
      <HistoryFilterMenu
        filters={filters}
        counters={counters}
        records={records}
        meUsername={meUsername}
        onPatch={onPatch}
        onReset={onReset}
        onClose={onClose}
      />
    </Menu>
  );
}
