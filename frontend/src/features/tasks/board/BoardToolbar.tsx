// Тулбар доски: группировка (дорожки), поиск, фильтры приоритета/исполнителя,
// кнопка настройки колонок. Читает общий стор boardControls. Два layout:
// 'inline' — горизонтально над сеткой (хаб/мобайл), 'sidebar' — вертикально (десктоп-проект).

import { C, FONT, R } from '../../../lib/design';
import { BOARD_GROUP_LABEL, PRIORITY_COLOR, PRIORITY_LABEL, PRIORITY_ORDER, type BoardGroupBy } from '../../../lib/tasks';
import {
  useBoardControls, setGroupBy, setSearch, togglePriorityFilter, setAssigneeFilter,
} from '../../../lib/boardControls';

const LABEL_STYLE = {
  fontFamily: FONT.sans, fontSize: 10.5, fontWeight: 700, color: C.textMuted,
  textTransform: 'uppercase' as const, letterSpacing: '0.07em',
};

export function BoardToolbar({ layout, groupOptions, onEditColumns }: {
  layout: 'inline' | 'sidebar';
  groupOptions: BoardGroupBy[];
  onEditColumns?: () => void;   // только проектная доска — открыть редактор колонок
}) {
  const { groupBy, search, priorities, assignee } = useBoardControls();
  const sidebar = layout === 'sidebar';

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
          {a === 'all' ? 'Все' : a === 'me' ? 'Я' : 'Claude'}
        </button>
      ))}
    </div>
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
        {columnsBtn}
      </div>
    );
  }

  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: 10, flexWrap: 'wrap', marginBottom: 14 }}>
      {groupSelect}
      {searchInput}
      {priorityChips}
      {assigneeToggle}
      {columnsBtn}
    </div>
  );
}
