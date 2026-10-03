import { useState, type ReactNode } from 'react';
import { ChevronDown, ChevronUp, Lock } from 'lucide-react';
import { C, FS, R, SP } from '../../lib/design';
import { Badge, type BadgeTone } from '../ui/Badge';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';

// «Исполнитель» панели генерации: свёрнутая строка «Чем: Авто · локально · модель ·
// цена ▾» и под ней список радио-строк с группами «Бесплатно на своей видеокарте» /
// «Облако», цена справа. Только рисует: строки из своего каталога каждый раздел
// (звук, картинки) строит своей чистой функцией.

export type ExecutorGroup = 'auto' | 'local' | 'cloud';

// Подписи групп; у «Авто» заголовка нет — строка стоит первой
export const EXECUTOR_GROUP_LABEL: Record<Exclude<ExecutorGroup, 'auto'>, string> = {
  local: 'Бесплатно на своей видеокарте',
  cloud: 'Облако',
};
const GROUP_ORDER: readonly ExecutorGroup[] = ['auto', 'local', 'cloud'];

// Тон бейджа в контракте сервера (ADR-023): good / warn вместо success / warning кита
export type ExecutorBadgeTone = BadgeTone | 'good' | 'warn';
export interface ExecutorBadge { label: string; tone?: ExecutorBadgeTone }

const TONE_ALIAS: Partial<Record<ExecutorBadgeTone, BadgeTone>> = { good: 'success', warn: 'warning' };
export const badgeTone = (t: ExecutorBadgeTone | undefined): BadgeTone => (t && (TONE_ALIAS[t] ?? (t as BadgeTone))) || 'neutral';

export type ExecutorUnit = 'free' | 'usd' | 'credits' | 'rub';

export interface ExecutorRow {
  id: string;
  group: ExecutorGroup;
  name: string;
  sub?: string;
  // «бесплатно · ~40 с», «$0.04 / шт.»: готовая подпись только для показа, не для разбора
  price: string;
  // Цена полями (контракт ExecutorRowDto): бесплатность и сумма за единицу работы
  free?: boolean;
  amount?: number | null;
  unit?: ExecutorUnit | null;
  etaSeconds?: number | null;
  badges?: readonly ExecutorBadge[];
  disabled?: boolean;
  // Замок перед именем: путь закрыт самим местом («Локально» в личном чате), а не сбоем поставщика
  locked?: boolean;
  // Почему серая: «не умеет «Изменить» — только новая картинка». Встаёт вместо sub
  reason?: string;
}

// Короткая цена для чипа и меню: из полей, а не из подписи price
export function rowPriceShort(r: Pick<ExecutorRow, 'price' | 'free' | 'amount' | 'unit'>): string {
  if (r.free) return 'бесплатно';
  if (r.amount != null) {
    if (r.unit === 'usd') return `$${r.amount}`;
    if (r.unit === 'credits') return `${r.amount} кр.`;
    if (r.unit === 'rub') return `${r.amount} ₽`;
  }
  return r.price;
}

// Строки по группам в порядке Авто → своя видеокарта → облако, пустые группы
// пропускаются; внутри группы порядок раздела сохраняется
export function groupExecutorRows(rows: readonly ExecutorRow[]): { group: ExecutorGroup; rows: ExecutorRow[] }[] {
  return GROUP_ORDER
    .map(group => ({ group, rows: rows.filter(r => r.group === group) }))
    .filter(g => g.rows.length > 0);
}

export function ExecutorSummaryRow({ label = 'Чем', name, parts = [], price, open, onToggle, isMobile }: {
  label?: string;
  // Жирное имя выбора: «Авто», «fal · FLUX Kontext»
  name: string;
  // Уточнения через точку: «локально», «Qwen-Image Edit»
  parts?: readonly string[];
  price?: ExecutorBadge;
  open: boolean;
  onToggle: () => void;
  isMobile?: boolean;
}) {
  const Chevron = open ? ChevronUp : ChevronDown;
  return (
    <button
      type="button"
      aria-expanded={open}
      onClick={onToggle}
      style={{
        display: 'flex', alignItems: 'center', gap: SP.sm, width: '100%', boxSizing: 'border-box',
        minHeight: isMobile ? 40 : 36, padding: `${SP.xs + 2}px ${SP.md - 2}px`,
        border: `1px solid ${open ? C.accentMuted : C.borderLight}`, borderRadius: R.md,
        background: C.bgCard, cursor: 'pointer', textAlign: 'left',
        fontFamily: 'inherit', fontSize: FS.sm, color: C.textPrimary,
      }}
    >
      <span style={{ color: C.textMuted, flexShrink: 0 }}>{label}</span>
      <span style={{ flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
        <b style={{ color: C.textHeading, fontWeight: 600 }}>{name}</b>
        {parts.map(p => ` · ${p}`).join('')}
      </span>
      {price && <Badge size="xs" tone={badgeTone(price.tone)}>{price.label}</Badge>}
      <Chevron size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ flexShrink: 0, color: C.textMuted }} aria-hidden />
    </button>
  );
}

export function ExecutorList({ rows, value, onChange, ariaLabel = 'Исполнитель', isMobile }: {
  rows: readonly ExecutorRow[];
  value: string;
  onChange: (id: string) => void;
  ariaLabel?: string;
  isMobile?: boolean;
}) {
  return (
    <div role="radiogroup" aria-label={ariaLabel} style={{
      border: `1px solid ${C.borderLight}`, borderRadius: R.md, background: C.bgCard, overflow: 'hidden',
    }}>
      {groupExecutorRows(rows).map(g => (
        <div key={g.group} role="group" aria-label={g.group === 'auto' ? undefined : EXECUTOR_GROUP_LABEL[g.group]}>
          {g.group !== 'auto' && (
            <div style={{
              padding: `${SP.xs + 2}px ${SP.md - 2}px ${SP.xxs}px`, fontSize: FS.xs, fontWeight: 600,
              color: C.textMuted, background: C.bgInset,
            }}>
              {EXECUTOR_GROUP_LABEL[g.group]}
            </div>
          )}
          {g.rows.map(r => <ExecutorRowView key={r.id} row={r} on={r.id === value} onPick={onChange} isMobile={isMobile} />)}
        </div>
      ))}
    </div>
  );
}

function ExecutorRowView({ row, on, onPick, isMobile }: { row: ExecutorRow; on: boolean; onPick: (id: string) => void; isMobile?: boolean }) {
  const [hover, setHover] = useState(false);
  const off = !!row.disabled;
  const sub: ReactNode = off ? row.reason ?? row.sub : row.sub;
  return (
    <button
      type="button"
      role="radio"
      aria-checked={on}
      disabled={off}
      title={off ? row.reason : undefined}
      onClick={() => onPick(row.id)}
      onMouseEnter={() => setHover(true)}
      onMouseLeave={() => setHover(false)}
      style={{
        display: 'flex', alignItems: 'center', gap: SP.sm, width: '100%', boxSizing: 'border-box',
        minHeight: isMobile ? 44 : 36, padding: `${SP.xs + 3}px ${SP.md - 2}px`,
        border: 'none', borderTop: `1px solid ${C.borderLight}`,
        background: on ? C.accentLight : hover && !off ? C.bgSelected : 'transparent',
        cursor: off ? 'not-allowed' : 'pointer', opacity: off ? 0.55 : 1, textAlign: 'left',
        fontFamily: 'inherit', fontSize: FS.sm, color: C.textPrimary,
      }}
    >
      {/* Радио-точка: кольцо, у выбранной — залитая середина */}
      <span aria-hidden style={{
        width: 14, height: 14, borderRadius: R.full, flexShrink: 0, boxSizing: 'border-box',
        border: `1.5px solid ${on ? C.accent : C.textMuted}`,
        background: on ? C.accent : 'transparent', boxShadow: on ? `inset 0 0 0 2.5px ${C.bgCard}` : 'none',
      }} />
      <span style={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', gap: 1 }}>
        <span style={{ display: 'flex', alignItems: 'center', gap: SP.xs, minWidth: 0 }}>
          {row.locked && <Lock data-executor-lock="" size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ flexShrink: 0, color: C.textMuted }} aria-label="Закрыто" />}
          <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', fontWeight: on ? 600 : 400 }}>{row.name}</span>
          {row.badges?.map(b => <Badge key={b.label} size="xs" tone={badgeTone(b.tone)}>{b.label}</Badge>)}
        </span>
        {/* Подпись и причина переносятся, а не режутся: в хвосте смысл («при отказе — облако
            с вашего согласия»), а на таче title нет */}
        {sub && <span style={{ fontSize: FS.xs, color: C.textMuted, overflowWrap: 'anywhere' }}>{sub}</span>}
      </span>
      <span style={{ flexShrink: 0, fontSize: FS.xs, color: C.textSecondary, whiteSpace: 'nowrap' }}>{off ? '—' : row.price}</span>
    </button>
  );
}
