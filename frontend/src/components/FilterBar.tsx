import { useState, useEffect, useRef, useMemo, type ReactNode } from 'react';
import { Filter, List, ListTree, Search, X, Pin, Clock, Users } from 'lucide-react';
import type { Persona, Session } from '../types';
import { C, R, FONT, FS, SHADOW, TB, Z, SP } from '../lib/design';
import { Modal } from './ui';
import { personaLabel } from '../lib/personas';
import { PersonaAvatar } from '../features/personas/PersonaAvatar';
import {
  ALL_ORIGINS, ALL_STATUS_CHIPS,
  chatStatusOf, isDefaultFilters, defaultChatFilters,
  type ChatFilters, type ChatStatusChip, type ChatOnlyFilter,
} from '../lib/chatFilters';
import type { ChatViewMode } from '../lib/chatTree';

// === Фильтр списка чатов (макет варианта А — «поповер 2.0») ===
// Компактный триггер со сводкой + бейджем скрытых; по клику — поповер (десктоп)
// или мобильная шторка (через ui/Modal). Секции: поиск → статус → тип → персона → показать только.
// Архив (чаты выполненных задач) прячется чипом «Готово» в секции «Статус» —
// отдельного тумблера нет (одно из решений пользователя по макету).

const STATUS_LABEL: Record<ChatStatusChip, string> = {
  active: 'В работе', waiting: 'Ждёт меня', done: 'Готово', error: 'С ошибкой',
};
// Строчные имена для сводки на триггере
const STATUS_SUMMARY: Record<ChatStatusChip, string> = {
  active: 'в работе', waiting: 'ждут меня', done: 'готово', error: 'с ошибкой',
};
const STATUS_DOT: Record<ChatStatusChip, string> = {
  active: C.accent, waiting: C.warning, done: C.textMuted, error: C.danger,
};
const ONLY_LABEL: Record<ChatOnlyFilter, string> = {
  pinned: 'Закреплённые', temp: 'Временные', group: 'Групповые',
};
const ONLY_SUMMARY: Record<ChatOnlyFilter, string> = {
  pinned: 'закреплённые', temp: 'временные', group: 'групповые',
};
const ONLY_ICON: Record<ChatOnlyFilter, typeof Pin> = {
  pinned: Pin, temp: Clock, group: Users,
};
const ORIGIN_OPTIONS: { value: Session['origin']; label: string }[] = [
  { value: 'manual', label: 'Обычные' },
  { value: 'task', label: 'Задачи' },
  { value: 'automation', label: 'Автоматизация' },
];

interface FilterBarProps {
  // Полный список чатов области — для счётчиков на чипах
  sessions: Session[];
  filters: ChatFilters;
  patch: (p: Partial<ChatFilters>) => void;
  allPersonas: Persona[];
  // Сколько чатов скрыто текущими фильтрами (бейдж на триггере и футер)
  hiddenCount: number;
  isMobile?: boolean;
  // Режим вида списка «Плоский/Иерархия» — тумблер справа (не задан — без тумблера)
  view?: ChatViewMode;
  onChangeView?: (v: ChatViewMode) => void;
}

// === Тумблер вида «Плоский / Иерархия» ===
// Нейтральный TB.pill*-сегмент (не accent: accent занят фильтрами, а режим — не фильтр).
// На мобиле — только иконки, подпись уходит в title/aria-label.
const VIEW_OPTIONS: { value: ChatViewMode; label: string; Icon: typeof List }[] = [
  { value: 'flat', label: 'Плоский', Icon: List },
  { value: 'tree', label: 'Иерархия', Icon: ListTree },
];

function ViewToggle({ view, onChange, isMobile }: {
  view: ChatViewMode;
  onChange: (v: ChatViewMode) => void;
  isMobile?: boolean;
}) {
  return (
    <div style={{
      display: 'flex', flexShrink: 0, padding: 2,
      background: TB.pillTrack, borderRadius: TB.pillRadius,
    }}>
      {VIEW_OPTIONS.map(o => {
        const active = view === o.value;
        return (
          <button
            key={o.value}
            onClick={() => onChange(o.value)}
            title={o.label}
            aria-label={o.label}
            style={{
              display: 'inline-flex', alignItems: 'center', gap: 5,
              padding: '4px 10px',
              fontSize: FS.sm, fontWeight: 600, fontFamily: FONT.sans,
              border: 'none', cursor: 'pointer',
              borderRadius: TB.pillRadius - 2,
              background: active ? TB.pillThumbBg : 'transparent',
              boxShadow: active ? TB.pillThumbShadow : 'none',
              color: active ? C.textHeading : C.textMuted,
              transition: 'background 0.12s, color 0.12s',
            }}
          >
            <o.Icon size={14} strokeWidth={2.2} style={{ flexShrink: 0 }} />
            {!isMobile && <span>{o.label}</span>}
          </button>
        );
      })}
    </div>
  );
}

// === Чип мультивыбора ===
function Chip({ active, children, onClick, large }: {
  active: boolean;
  children: ReactNode;
  onClick: () => void;
  large?: boolean;
}) {
  return (
    <button
      onClick={onClick}
      style={{
        padding: large ? '7px 13px' : '4px 10px',
        borderRadius: R.pill,
        border: `1px solid ${active ? C.accent : C.borderLight}`,
        background: active ? C.accent : 'transparent',
        color: active ? C.onAccent : C.textSecondary,
        fontSize: large ? FS.base : FS.sm,
        fontWeight: 600, fontFamily: FONT.sans, cursor: 'pointer',
        display: 'inline-flex', alignItems: 'center', gap: SP.xs,
        transition: 'background 0.12s, border-color 0.12s',
      }}
    >
      {children}
    </button>
  );
}

// Маркер-точка статуса: на активном чипе — onAccent, иначе — цвет статуса
function StatusDot({ chip, active }: { chip: ChatStatusChip; active: boolean }) {
  return (
    <span style={{
      width: 6, height: 6, borderRadius: R.max, flexShrink: 0,
      background: active ? C.onAccent : STATUS_DOT[chip],
    }} />
  );
}

function Count({ n }: { n: number }) {
  return <span style={{ fontFamily: FONT.mono, fontSize: 10, opacity: 0.8 }}>{n}</span>;
}

function SectionTitle({ children, action }: { children: ReactNode; action?: ReactNode }) {
  return (
    <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', marginBottom: 6 }}>
      <span style={{
        fontSize: FS.xs, fontWeight: 700, color: C.textMuted,
        textTransform: 'uppercase', letterSpacing: '0.06em', fontFamily: FONT.sans,
      }}>
        {children}
      </span>
      {action}
    </div>
  );
}

const linkBtnStyle: React.CSSProperties = {
  border: 'none', background: 'none', padding: 0, cursor: 'pointer',
  fontFamily: FONT.sans, fontSize: FS.xs, color: C.accent, fontWeight: 600,
};

const sectionStyle: React.CSSProperties = { marginBottom: SP.md };

// === Содержимое фильтра (общее для поповера и мобильной шторки) ===
function FilterContent({
  sessions, filters, patch, allPersonas, large,
}: {
  sessions: Session[];
  filters: ChatFilters;
  patch: (p: Partial<ChatFilters>) => void;
  allPersonas: Persona[];
  large?: boolean;
}) {
  // Счётчики по чипам — из полного списка области
  const counts = useMemo(() => {
    const status: Record<ChatStatusChip, number> = { active: 0, waiting: 0, done: 0, error: 0 };
    const origin: Record<Session['origin'], number> = { manual: 0, task: 0, automation: 0 };
    const only: Record<ChatOnlyFilter, number> = { pinned: 0, temp: 0, group: 0 };
    for (const s of sessions) {
      status[chatStatusOf(s)]++;
      origin[s.origin]++;
      if (s.isPinned) only.pinned++;
      if (s.expiresAfterMinutes != null) only.temp++;
      if ((s.participants?.length ?? 0) > 1) only.group++;
    }
    return { status, origin, only };
  }, [sessions]);

  const personaIdsInList = useMemo(
    () => [...new Set(sessions.filter(s => s.personaId).map(s => s.personaId!))],
    [sessions],
  );
  const personasInChats = personaIdsInList
    .map(id => allPersonas.find(p => p.id === id))
    .filter((p): p is Persona => p !== undefined);

  const toggle = <T extends string>(arr: T[], v: T): T[] =>
    arr.includes(v) ? arr.filter(x => x !== v) : [...arr, v];

  const q = filters.search;
  const hiddenOrigins = ORIGIN_OPTIONS.filter(o => !filters.origins.includes(o.value));
  const showPersona = personaIdsInList.length > 0;

  return (
    <>
      {/* Поиск по названию */}
      <div style={{
        display: 'flex', alignItems: 'center', gap: 7,
        background: C.bgWhite, border: `1px solid ${C.border}`,
        borderRadius: R.xl, padding: `0 ${SP.sm}`, marginBottom: SP.md,
      }}>
        <Search size={15} strokeWidth={2} style={{ color: C.textMuted, flexShrink: 0 }} />
        <input
          value={q}
          onChange={e => patch({ search: e.target.value })}
          placeholder="Поиск по названию…"
          style={{
            flex: 1, height: large ? 40 : 34, minWidth: 0,
            border: 'none', outline: 'none', background: 'transparent',
            fontFamily: FONT.sans, fontSize: FS.md, color: C.textHeading,
          }}
        />
        {q && (
          <button
            onClick={() => patch({ search: '' })}
            aria-label="Очистить поиск"
            style={{
              border: 'none', background: 'transparent', cursor: 'pointer',
              color: C.textMuted, display: 'flex', padding: 2, flexShrink: 0,
            }}
          >
            <X size={15} strokeWidth={2} />
          </button>
        )}
      </div>

      {/* Статус */}
      <div style={sectionStyle}>
        <SectionTitle>Статус</SectionTitle>
        <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.xs }}>
          {ALL_STATUS_CHIPS.map(chip => {
            const active = filters.statuses.includes(chip);
            return (
              <Chip key={chip} active={active} large={large}
                onClick={() => {
                  const next = toggle(filters.statuses, chip);
                  // пустой набор статусов = «всё скрыто» — не даём, возвращаем дефолт
                  patch({ statuses: next.length ? next : ['active', 'waiting', 'error'] });
                }}
              >
                <StatusDot chip={chip} active={active} />
                {STATUS_LABEL[chip]}
                <Count n={counts.status[chip]} />
              </Chip>
            );
          })}
        </div>
      </div>

      {/* Тип */}
      <div style={sectionStyle}>
        <SectionTitle action={
          hiddenOrigins.length > 0 && (
            <button onClick={() => patch({ origins: [...ALL_ORIGINS] })} style={linkBtnStyle}>
              Показать все
            </button>
          )
        }>
          Тип
        </SectionTitle>
        <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.xs }}>
          {ORIGIN_OPTIONS.map(o => {
            const active = filters.origins.includes(o.value);
            return (
              <Chip key={o.value} active={active} large={large}
                onClick={() => {
                  const next = toggle(filters.origins, o.value);
                  patch({ origins: next.length ? next : [...ALL_ORIGINS] });
                }}
              >
                {o.label}
                <Count n={counts.origin[o.value]} />
              </Chip>
            );
          })}
        </div>
      </div>

      {/* Персона */}
      {showPersona && (
        <div style={sectionStyle}>
          <SectionTitle>Персона</SectionTitle>
          <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.xs }}>
            <Chip active={!filters.personaId} large={large}
              onClick={() => patch({ personaId: null })}
            >
              Все
            </Chip>
            {personasInChats.map(p => (
              <Chip key={p.id} active={filters.personaId === p.id} large={large}
                onClick={() => patch({ personaId: filters.personaId === p.id ? null : p.id })}
              >
                <PersonaAvatar persona={p} size={14} />
                <span>{personaLabel(p)}</span>
              </Chip>
            ))}
          </div>
        </div>
      )}

      {/* Показать только */}
      <div style={{ marginBottom: 0 }}>
        <SectionTitle>Показать только</SectionTitle>
        <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.xs }}>
          {(Object.keys(ONLY_LABEL) as ChatOnlyFilter[]).map(o => {
            const active = filters.only.includes(o);
            const Icon = ONLY_ICON[o];
            return (
              <Chip key={o} active={active} large={large}
                onClick={() => patch({ only: toggle(filters.only, o) })}
              >
                <Icon size={large ? 13 : 11} strokeWidth={2} style={{ flexShrink: 0 }} />
                {ONLY_LABEL[o]}
                <Count n={counts.only[o]} />
              </Chip>
            );
          })}
        </div>
      </div>
    </>
  );
}

// Сводка активных фильтров человеческим языком (для триггера)
function buildSummary(filters: ChatFilters, personaName: string | null): string {
  const parts: string[] = [];
  const q = filters.search.trim();
  if (q) parts.push(`«${q}»`);
  const liveSel = (['active', 'waiting', 'error'] as ChatStatusChip[]).filter(s => filters.statuses.includes(s));
  if (liveSel.length < 3 && liveSel.length > 0) parts.push(liveSel.map(s => STATUS_SUMMARY[s]).join(', '));
  if (!filters.statuses.includes('done')) parts.push('без готовых');
  if (filters.only.length) parts.push(filters.only.map(o => ONLY_SUMMARY[o]).join(', '));
  if (filters.personaId && personaName) parts.push(personaName);
  return parts.join(' · ');
}

// Кнопки сброса для empty-state списка чатов (макет А, сцена 3): точечный сброс поиска
// и полный сброс фильтров. Единое место, чтобы оба списка (проектный и глобальный)
// рисовали их одинаково.
export function ChatFilterResetActions({ search, hasNonSearchFilters, onResetSearch, onResetAll }: {
  search: string;
  hasNonSearchFilters: boolean;
  onResetSearch: () => void;
  onResetAll: () => void;
}) {
  const q = search.trim();
  if (!q && !hasNonSearchFilters) return null;
  return (
    <div style={{ display: 'flex', gap: SP.sm, justifyContent: 'center' }}>
      {q && (
        <button onClick={onResetSearch} style={{
          padding: '6px 14px', borderRadius: R.md, border: `1px solid ${C.border}`,
          background: 'transparent', color: C.textSecondary,
          fontSize: FS.sm, fontWeight: 600, fontFamily: FONT.sans, cursor: 'pointer',
        }}>
          Сбросить поиск
        </button>
      )}
      {hasNonSearchFilters && (
        <button onClick={onResetAll} style={{
          padding: '6px 16px', borderRadius: R.md, border: 'none',
          background: C.accent, color: C.onAccent,
          fontSize: FS.sm, fontWeight: 600, fontFamily: FONT.sans, cursor: 'pointer',
        }}>
          Сбросить фильтры
        </button>
      )}
    </div>
  );
}

export function FilterBar({
  sessions, filters, patch, allPersonas, hiddenCount, isMobile,
  view, onChangeView,
}: FilterBarProps) {
  const [open, setOpen] = useState(false);
  const rootRef = useRef<HTMLDivElement>(null);

  const selectedPersona = filters.personaId
    ? allPersonas.find(p => p.id === filters.personaId) ?? null
    : null;
  const summary = buildSummary(filters, selectedPersona ? personaLabel(selectedPersona) : null);
  const hasFilters = !isDefaultFilters(filters);
  const resetAll = () => patch(defaultChatFilters());

  // Закрытие десктоп-поповера по клику вне и по Esc (оба режима). Мобильная шторка
  // закрывается сама через ui/Modal (overlay/Esc).
  useEffect(() => {
    if (!open) return;
    const onDown = (e: MouseEvent) => {
      if (isMobile) return;
      if (rootRef.current && !rootRef.current.contains(e.target as Node)) setOpen(false);
    };
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') setOpen(false); };
    document.addEventListener('mousedown', onDown);
    document.addEventListener('keydown', onKey);
    return () => {
      document.removeEventListener('mousedown', onDown);
      document.removeEventListener('keydown', onKey);
    };
  }, [open, isMobile]);

  const trigger = (
    <div
      onClick={() => setOpen(o => !o)}
      onKeyDown={e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); setOpen(o => !o); } }}
      role="button"
      tabIndex={0}
      style={{
        display: 'flex', alignItems: 'center', gap: SP.xs, minWidth: 0,
        cursor: 'pointer', userSelect: 'none', padding: '2px 0',
        color: hasFilters ? C.accent : C.textMuted,
        fontSize: FS.sm, fontWeight: 600, fontFamily: FONT.sans,
        transition: 'color 0.15s', opacity: hasFilters ? 1 : 0.5,
      }}
      title={hasFilters ? summary : 'Фильтры'}
    >
      <Filter size={12} strokeWidth={2.2} style={{ flexShrink: 0 }} />
      <span style={{ marginLeft: 1, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
        {hasFilters ? summary : 'Фильтр'}
      </span>
      {hiddenCount > 0 && (
        <span style={{
          fontSize: 10, fontWeight: 700, fontFamily: FONT.mono,
          color: C.onAccent, background: C.accent,
          padding: '0 5px', borderRadius: R.pill, lineHeight: '16px',
          minWidth: 16, textAlign: 'center', flexShrink: 0,
        }}>
          {hiddenCount}
        </span>
      )}
    </div>
  );

  // Футер: счётчик скрытых + сброс + готово (общий вид для поповера и шторки)
  const footerRow = (
    <div style={{
      display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: SP.sm,
    }}>
      <span style={{ fontSize: FS.xs, color: C.textMuted, fontFamily: FONT.sans, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
        {hiddenCount > 0 ? `Скрыто ${hiddenCount}` : 'Все чаты показаны'}
        {hasFilters && (
          <button onClick={resetAll} style={{ ...linkBtnStyle, marginLeft: 6 }}>Сбросить всё</button>
        )}
      </span>
      <button onClick={() => setOpen(false)} style={{
        padding: '5px 14px', borderRadius: R.md, border: 'none',
        background: C.accent, color: C.onAccent,
        fontSize: FS.sm, fontWeight: 600, fontFamily: FONT.sans, cursor: 'pointer', flexShrink: 0,
      }}>
        Готово
      </button>
    </div>
  );

  return (
    <div ref={rootRef} style={{ position: 'relative', flexShrink: 0 }}>
      <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: SP.sm }}>
        {trigger}
        {view !== undefined && onChangeView && (
          <ViewToggle view={view} onChange={onChangeView} isMobile={isMobile} />
        )}
      </div>

      {/* Десктоп: компактный поповер под триггером */}
      {open && !isMobile && (
        <div style={{
          position: 'absolute', top: 'calc(100% + 4px)', left: 0,
          width: 320, maxHeight: 478, overflowY: 'auto',
          background: C.bgWhite, border: `1px solid ${C.border}`,
          borderRadius: R.xl, boxShadow: SHADOW.dropdown,
          padding: SP.md, zIndex: Z.dropdown,
        }}>
          <FilterContent sessions={sessions} filters={filters} patch={patch} allPersonas={allPersonas} />
          <div style={{
            marginTop: SP.xs, paddingTop: SP.sm, borderTop: `1px solid ${C.borderLight}`,
          }}>
            {footerRow}
          </div>
        </div>
      )}

      {/* Мобайл: шторка через ui/Modal (единый bottom-sheet с overlay/Esc/safe-area) */}
      {open && isMobile && (
        <Modal title="Фильтры" onClose={() => setOpen(false)} footer={footerRow}>
          <FilterContent sessions={sessions} filters={filters} patch={patch} allPersonas={allPersonas} large />
        </Modal>
      )}
    </div>
  );
}
