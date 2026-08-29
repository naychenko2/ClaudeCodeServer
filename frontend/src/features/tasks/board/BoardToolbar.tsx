// Тулбар доски: группировка (дорожки), поиск, фильтры приоритета/исполнителя,
// кнопка настройки колонок. Читает общий стор boardControls. Два layout:
// 'inline' — горизонтально над сеткой (хаб/мобайл), 'sidebar' — вертикально (десктоп-проект).
//
// Фильтр «Только дефекты» (волна 2) хранит defectsOnly в localStorage под ключом
// cc_board_defects_only. Параллельный исполнитель в TaskBoard.tsx читает тот же
// ключ и применяет фильтр в `filtered` (одна строка `if (defectsOnly && t.kind !==
// 'defect') return false;`). Синхронизация между вкладками — через событие 'storage'.
// Локальный сторон паттерна useSyncExternalStore: тот же, что у boardControls.

import { useSyncExternalStore } from 'react';
import { Bug, SlidersHorizontal } from 'lucide-react';
import { C, FONT, R } from '../../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../../../components/ui/icons';
import { BOARD_GROUP_LABEL, PRIORITY_COLOR, PRIORITY_LABEL, PRIORITY_ORDER, type BoardGroupBy } from '../../../lib/tasks';
import {
  useBoardControls, setGroupBy, setSearch, togglePriorityFilter, setAssigneeFilter,
} from '../../../lib/boardControls';
import { useIsMobile } from '../../../lib/breakpoints';
import { ToolbarOverflowMenu } from '../../../components/ToolbarOverflowMenu';

const LABEL_STYLE = {
  fontFamily: FONT.sans, fontSize: 10.5, fontWeight: 700, color: C.textMuted,
  textTransform: 'uppercase' as const, letterSpacing: '0.07em',
};

// === Стор фильтра «Только дефекты» ===
// Хранилище — localStorage с ключом cc_board_defects_only (для переживания F5 и для
// чтения соседним по волне кодом). Подписки на изменения — через window.StorageEvent
// (изменения из другой вкладки) + внутренние emit() (из этой). SSR-безопасно:
// useSyncExternalStore с одним snapshotter'ом не падает на сервере — нам и не надо,
// компонент рендерится только в браузере.

const DEFECTS_KEY = 'cc_board_defects_only';
function readStorage(): boolean {
  try { return localStorage.getItem(DEFECTS_KEY) === '1'; } catch { return false; }
}
let _defectsOnly = readStorage();
const _listeners = new Set<() => void>();
function emit() { _listeners.forEach(fn => fn()); }
// Изменения из другой вкладки: событие 'storage' уже не стреляет в той же вкладке —
// здесь дёргаем только внешние изменения
if (typeof window !== 'undefined') {
  window.addEventListener('storage', (e: StorageEvent) => {
    if (e.key !== DEFECTS_KEY) return;
    const next = e.newValue === '1';
    if (next === _defectsOnly) return;
    _defectsOnly = next;
    emit();
  });
}
export function setDefectsOnly(b: boolean) {
  if (b === _defectsOnly) return;
  _defectsOnly = b;
  try { localStorage.setItem(DEFECTS_KEY, b ? '1' : '0'); } catch { /* лимиты квоты localStorage — тихо */ }
  emit();
}
function useDefectsOnly(): boolean {
  return useSyncExternalStore(
    fn => { _listeners.add(fn); return () => { _listeners.delete(fn); }; },
    () => _defectsOnly,
    () => _defectsOnly,
  );
}

export function BoardToolbar({ layout, groupOptions, onEditColumns }: {
  layout: 'inline' | 'sidebar';
  groupOptions: BoardGroupBy[];
  onEditColumns?: () => void;   // только проектная доска — открыть редактор колонок
}) {
  const { groupBy, search, priorities, assignee } = useBoardControls();
  const defectsOnly = useDefectsOnly();
  const sidebar = layout === 'sidebar';
  const isMobile = useIsMobile();

  const groupSelect = (
    <label style={{ display: sidebar ? 'block' : 'inline-flex', alignItems: 'center', gap: 6 }}>
      <span style={{ ...LABEL_STYLE, display: 'block', marginBottom: sidebar ? 6 : 0 }}>Дорожки</span>
      <select
        value={groupBy}
        onChange={e => setGroupBy(e.target.value as BoardGroupBy)}
        style={{
          width: sidebar ? '100%' : undefined, boxSizing: 'border-box',
          padding: '7px 10px', border: `1px solid ${C.border}`, borderRadius: R.lg,
          background: C.bgWhite, color: C.textPrimary, fontFamily: FONT.sans, fontSize: 12.5, fontWeight: 600, cursor: 'pointer',
        }}
      >
        {groupOptions.map(g => <option key={g} value={g}>{BOARD_GROUP_LABEL[g]}</option>)}
      </select>
    </label>
  );

  const searchInput = (
    <input
      value={search}
      onChange={e => setSearch(e.target.value)}
      placeholder="Поиск…"
      style={{
        boxSizing: 'border-box',
        flex: sidebar ? undefined : '1 1 100%', width: sidebar ? '100%' : undefined,
        order: sidebar ? undefined : 3,
        padding: '7px 11px', border: `1px solid ${C.border}`, borderRadius: R.lg,
        background: C.bgWhite, color: C.textPrimary, fontFamily: FONT.sans, fontSize: 13,
      }}
    />
  );

  const priorityChips = (
    <div style={{ display: 'flex', gap: 5, flexWrap: 'wrap' }}>
      {PRIORITY_ORDER.map(p => {
        const active = priorities.includes(p);
        return (
          <button
            key={p}
            onClick={() => togglePriorityFilter(p)}
            style={{
              display: 'inline-flex', alignItems: 'center', gap: 5, padding: '5px 10px', cursor: 'pointer',
              border: `1px solid ${active ? PRIORITY_COLOR[p] : C.border}`, borderRadius: 999,
              background: active ? C.bgSelected : C.bgWhite,
              fontFamily: FONT.sans, fontSize: 12, fontWeight: active ? 700 : 500, color: C.textPrimary,
            }}
          >
            <span style={{ width: 7, height: 7, borderRadius: '50%', background: PRIORITY_COLOR[p] }} />
            {PRIORITY_LABEL[p]}
          </button>
        );
      })}
    </div>
  );

  const assigneeToggle = (
    <div style={{ display: sidebar ? 'flex' : 'inline-flex', border: `1px solid ${C.border}`, borderRadius: R.lg, overflow: 'hidden' }}>
      {(['all', 'me', 'claude'] as const).map(a => (
        <button
          key={a}
          onClick={() => setAssigneeFilter(a)}
          style={{
            flex: sidebar ? 1 : undefined,
            padding: '6px 11px', cursor: 'pointer', border: 'none',
            background: assignee === a ? C.accentLight : C.bgWhite,
            color: assignee === a ? C.accent : C.textSecondary,
            fontFamily: FONT.sans, fontSize: 12, fontWeight: assignee === a ? 700 : 500,
          }}
        >
          {a === 'all' ? 'Все' : a === 'me' ? 'Я' : 'AI'}
        </button>
      ))}
    </div>
  );

  // Чип «Только дефекты»: тот же стиль, что у фильтров приоритета, но без точки-маркера —
  // иконка Bug вместо неё. Семантика «дефект» сразу видна рядом с пометкой активного
  // состояния; по сути это второй фильтр секции «Приоритет» — кладём рядом в sidebar
  const defectsChip = (
    <button
      onClick={() => setDefectsOnly(!defectsOnly)}
      title="Показывать только задачи-дефекты (Kind=defect)"
      aria-pressed={defectsOnly}
      style={{
        display: 'inline-flex', alignItems: 'center', gap: 5, padding: '5px 10px', cursor: 'pointer',
        border: `1px solid ${defectsOnly ? C.accent : C.border}`, borderRadius: 999,
        background: defectsOnly ? C.bgSelected : C.bgWhite,
        fontFamily: FONT.sans, fontSize: 12, fontWeight: defectsOnly ? 700 : 500, color: C.textPrimary,
      }}
    >
      <Bug size={ICON_SIZE.xs} strokeWidth={ICON_STROKE}
        color={defectsOnly ? C.accent : C.textMuted} style={{ flexShrink: 0 }} />
      Только дефекты
    </button>
  );

  const columnsBtn = onEditColumns && (
    <button
      onClick={onEditColumns}
      style={{
        display: 'inline-flex', alignItems: 'center', gap: 7, width: sidebar ? '100%' : undefined, justifyContent: sidebar ? 'center' : undefined,
        padding: '7px 12px', cursor: 'pointer',
        border: `1px solid ${C.border}`, borderRadius: R.lg, background: C.bgWhite,
        fontFamily: FONT.sans, fontSize: 12.5, fontWeight: 600, color: C.textPrimary,
      }}
    >
      <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
        <rect x="3" y="4" width="4.5" height="16" rx="1.5" /><rect x="9.75" y="4" width="4.5" height="16" rx="1.5" /><rect x="16.5" y="4" width="4.5" height="16" rx="1.5" />
      </svg>
      Настроить колонки
    </button>
  );

  if (sidebar) {
    return (
      <div style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
        {groupSelect}
        <div>
          <span style={{ ...LABEL_STYLE, display: 'block', marginBottom: 6 }}>Поиск</span>
          {searchInput}
        </div>
        <div>
          <span style={{ ...LABEL_STYLE, display: 'block', marginBottom: 6 }}>Приоритет</span>
          {priorityChips}
        </div>
        <div>
          <span style={{ ...LABEL_STYLE, display: 'block', marginBottom: 6 }}>Исполнитель</span>
          {assigneeToggle}
        </div>
        <div>
          <span style={{ ...LABEL_STYLE, display: 'block', marginBottom: 6 }}>Тип</span>
          {defectsChip}
        </div>
        {columnsBtn}
      </div>
    );
  }

  // Мобильный inline: поиск (primary) + «Фильтры» (группировка/приоритет/исполнитель/дефекты/колонки — в боттом-шит).
  if (isMobile) {
    const activeCount = (priorities.length > 0 ? 1 : 0) + (assignee !== 'all' ? 1 : 0) + (defectsOnly ? 1 : 0);
    return (
      <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 14 }}>
        <div style={{ flex: 1, minWidth: 0, display: 'flex' }}>{searchInput}</div>
        <ToolbarOverflowMenu
          isMobile
          title="Фильтры"
          triggerIcon={<SlidersHorizontal size={15} strokeWidth={2.2} />}
          triggerLabel="Фильтры"
          indicator={activeCount}
        >
          <div style={{ display: 'flex', flexDirection: 'column', gap: 16, padding: '4px 6px 10px' }}>
            <div>
              <span style={{ ...LABEL_STYLE, display: 'block', marginBottom: 6 }}>Дорожки</span>
              <select
                value={groupBy}
                onChange={e => setGroupBy(e.target.value as BoardGroupBy)}
                style={{
                  width: '100%', boxSizing: 'border-box', padding: '9px 10px',
                  border: `1px solid ${C.border}`, borderRadius: R.lg, background: C.bgWhite,
                  color: C.textPrimary, fontFamily: FONT.sans, fontSize: 13, fontWeight: 600, cursor: 'pointer',
                }}
              >
                {groupOptions.map(g => <option key={g} value={g}>{BOARD_GROUP_LABEL[g]}</option>)}
              </select>
            </div>
            <div>
              <span style={{ ...LABEL_STYLE, display: 'block', marginBottom: 6 }}>Приоритет</span>
              {priorityChips}
            </div>
            <div>
              <span style={{ ...LABEL_STYLE, display: 'block', marginBottom: 6 }}>Исполнитель</span>
              <div style={{ display: 'flex', border: `1px solid ${C.border}`, borderRadius: R.lg, overflow: 'hidden' }}>
                {(['all', 'me', 'claude'] as const).map(a => (
                  <button
                    key={a}
                    onClick={() => setAssigneeFilter(a)}
                    style={{
                      flex: 1, padding: '9px 11px', cursor: 'pointer', border: 'none',
                      background: assignee === a ? C.accentLight : C.bgWhite,
                      color: assignee === a ? C.accent : C.textSecondary,
                      fontFamily: FONT.sans, fontSize: 13, fontWeight: assignee === a ? 700 : 500,
                    }}
                  >
                    {a === 'all' ? 'Все' : a === 'me' ? 'Я' : 'AI'}
                  </button>
                ))}
              </div>
            </div>
            <div>
              <span style={{ ...LABEL_STYLE, display: 'block', marginBottom: 6 }}>Тип</span>
              {defectsChip}
            </div>
            {onEditColumns && (
              <button
                onClick={onEditColumns}
                style={{
                  display: 'inline-flex', alignItems: 'center', justifyContent: 'center', gap: 8, width: '100%',
                  padding: '10px 12px', cursor: 'pointer', border: `1px solid ${C.border}`, borderRadius: R.lg,
                  background: C.bgWhite, fontFamily: FONT.sans, fontSize: 13, fontWeight: 600, color: C.textPrimary,
                }}
              >
                <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                  <rect x="3" y="4" width="4.5" height="16" rx="1.5" /><rect x="9.75" y="4" width="4.5" height="16" rx="1.5" /><rect x="16.5" y="4" width="4.5" height="16" rx="1.5" />
                </svg>
                Настроить колонки
              </button>
            )}
          </div>
        </ToolbarOverflowMenu>
      </div>
    );
  }

  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: 10, flexWrap: 'wrap', marginBottom: 14 }}>
      {groupSelect}
      {searchInput}
      {priorityChips}
      {assigneeToggle}
      {defectsChip}
      {columnsBtn}
    </div>
  );
}
