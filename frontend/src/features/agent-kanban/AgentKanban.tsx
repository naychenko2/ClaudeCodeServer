import { useEffect, useState, useMemo } from 'react';
import { ensurePersonasLoaded, usePersonas } from '../../lib/personas';
import { ensureAgentsLoaded, useAgentBoard } from '../../lib/agentBoard';
import { AgentCard } from './AgentCard';
import { SlidersHorizontal } from 'lucide-react';
import { C, FONT, R } from '../../lib/design';
import { useIsMobile } from '../../lib/breakpoints';
import { ToolbarOverflowMenu } from '../../components/ToolbarOverflowMenu';
import { api } from '../../lib/api';
import type { Project } from '../../types';

// Конфигурация колонок
const COLUMNS = [
  { key: 'queue' as const, label: 'Очередь', desc: 'Ожидают запуска', color: C.textMuted },
  { key: 'working' as const, label: 'Работает', desc: 'Выполняют задачу', color: C.accent },
  { key: 'waiting' as const, label: 'Ждёт ответа', desc: 'Требуют внимания', color: C.warning },
  { key: 'done' as const, label: 'Готово', desc: 'Завершённые', color: C.success },
];

// Временные окна
// 3days = вчера~завтра (дефолт), today = сегодня, week = ±3 дня, all = всё
type TimeWindow = '3days' | 'today' | 'week' | 'all';
const TIME_WINDOWS: { key: TimeWindow; label: string }[] = [
  { key: '3days', label: '3 дня' },
  { key: 'today', label: 'Сегодня' },
  { key: 'week', label: 'Неделя' },
  { key: 'all', label: 'Всё время' },
];

// Границы дня в UTC (начало/конец дня относительно текущего времени)
function dayStart(offset: number): number {
  const d = new Date();
  d.setDate(d.getDate() + offset);
  d.setHours(0, 0, 0, 0);
  return d.getTime();
}
function dayEnd(offset: number): number {
  const d = new Date();
  d.setDate(d.getDate() + offset);
  d.setHours(23, 59, 59, 999);
  return d.getTime();
}

function inTimeWindow(startedAt: string | undefined, window: TimeWindow): boolean {
  if (!startedAt) return window === 'all' ? true : false;
  if (window === 'all') return true;
  const t = new Date(startedAt).getTime();
  switch (window) {
    case 'today':    return t >= dayStart(0) && t <= dayEnd(0);
    case '3days':    return t >= dayStart(-1) && t <= dayEnd(1);   // вчера 00:00 ~ завтра 23:59
    case 'week':     return t >= dayStart(-6) && t <= dayEnd(0);   // последние 7 дней
    default:         return true;
  }
}

export function AgentKanban() {
  const items = useAgentBoard();
  const isMobile = useIsMobile();
  const [timeWindow, setTimeWindow] = useState<TimeWindow>('3days');
  const [personaFilter, setPersonaFilter] = useState<string>('all');
  const [projectFilter, setProjectFilter] = useState<string>('all');
  const [projects, setProjects] = useState<Project[]>([]);

  useEffect(() => {
    void ensureAgentsLoaded();
    void ensurePersonasLoaded();
    api.projects.list().then(setProjects).catch(() => {});
  }, []);

  // Список уникальных персон среди всех карточек
  const personas = usePersonas();
  const personaOptions = useMemo(() => {
    const seen = new Set<string>();
    for (const item of items) {
      if (item.personaId) seen.add(item.personaId);
    }
    return Array.from(seen);
  }, [items]);

  // Список уникальных проектов среди всех карточек
  const projectOptions = useMemo(() => {
    const seen = new Set<string>();
    for (const item of items) {
      if (item.projectId) seen.add(item.projectId);
    }
    return Array.from(seen);
  }, [items]);

  // Проекты с именем — маппинг
  const projectMap = useMemo(() => {
    const m = new Map<string, string>();
    for (const p of projects) m.set(p.id, p.name);
    return m;
  }, [projects]);

  // Временной фильтр — ко всем колонкам (не только к done)
  const filtered = useMemo(() => {
    return items.filter(item => {
      if (personaFilter !== 'all' && item.personaId !== personaFilter) return false;
      if (projectFilter !== 'all' && item.projectId !== projectFilter) return false;
      // Активные (working/waiting) — всегда видны независимо от окна
      if (item.column === 'working' || item.column === 'waiting') return true;
      // queue и done — по временному окну
      return inTimeWindow(item.startedAt ?? item.sessionId, timeWindow);
    });
  }, [items, timeWindow, personaFilter, projectFilter]);

  const hasItems = filtered.length > 0;
  // Мобильная разгрузка (наши концепции): фильтры Период/Агент/Проект → «⋯ Фильтры»
  const activeFilterCount = (timeWindow !== '3days' ? 1 : 0) + (personaFilter !== 'all' ? 1 : 0) + (projectFilter !== 'all' ? 1 : 0);
  const filterSecLabel: React.CSSProperties = {
    fontFamily: FONT.sans, fontSize: 10.5, fontWeight: 700, color: C.textMuted,
    textTransform: 'uppercase', letterSpacing: '0.07em', marginBottom: 8,
  };

  // Чип-фильтр в стиле календаря (CalendarPage.filterChip)
  const filterChip = (key: string, label: string, active: boolean, onClick: () => void) => (
    <button
      key={key}
      onClick={onClick}
      style={{
        display: 'inline-flex', alignItems: 'center', gap: 7, flexShrink: 0,
        padding: '6px 13px', cursor: 'pointer',
        border: `1px solid ${active ? C.accent : C.border}`,
        borderRadius: 999,
        background: active ? C.accentLight : C.bgWhite,
        fontFamily: FONT.sans, fontSize: 12.5, fontWeight: active ? 700 : 500,
        color: C.textPrimary, whiteSpace: 'nowrap',
        transition: 'border-color 0.12s, background 0.12s',
      }}
    >
      {label}
    </button>
  );

  return (
    <div>
      {/* Фильтры: десктоп — ряд чипов; мобилка — «⋯ Фильтры» (наши концепции разгрузки) */}
      <div style={{ marginBottom: 16 }}>
        {isMobile ? (
          <ToolbarOverflowMenu
            isMobile title="Фильтры"
            triggerIcon={<SlidersHorizontal size={15} strokeWidth={2.2} />}
            triggerLabel="Фильтры"
            indicator={activeFilterCount || undefined}
          >
            <div style={{ display: 'flex', flexDirection: 'column', gap: 16, padding: '4px 6px 10px' }}>
              <div>
                <div style={filterSecLabel}>Период</div>
                <div style={{ display: 'flex', flexWrap: 'wrap', gap: 8 }}>
                  {TIME_WINDOWS.map(w => filterChip(w.key, w.label, timeWindow === w.key, () => setTimeWindow(w.key)))}
                </div>
              </div>
              {personaOptions.length > 0 && (
                <div>
                  <div style={filterSecLabel}>Агент</div>
                  <div style={{ display: 'flex', flexWrap: 'wrap', gap: 8 }}>
                    {filterChip('all', 'Все', personaFilter === 'all', () => setPersonaFilter('all'))}
                    {personaOptions.map(pid => filterChip(pid, personas.find(p => p.id === pid)?.name ?? pid.slice(0, 7), personaFilter === pid, () => setPersonaFilter(pid)))}
                  </div>
                </div>
              )}
              {projectOptions.length > 0 && (
                <div>
                  <div style={filterSecLabel}>Проект</div>
                  <div style={{ display: 'flex', flexWrap: 'wrap', gap: 8 }}>
                    {filterChip('all', 'Все', projectFilter === 'all', () => setProjectFilter('all'))}
                    {projectOptions.map(pid => filterChip(pid, projectMap.get(pid) ?? pid.slice(0, 7), projectFilter === pid, () => setProjectFilter(pid)))}
                  </div>
                </div>
              )}
            </div>
          </ToolbarOverflowMenu>
        ) : (
          <div className="cc-hide-scrollbar" style={{
            display: 'flex', alignItems: 'center', gap: 8,
            overflowX: 'auto', paddingBottom: 2, flexWrap: 'wrap',
          }}>
            <span style={{
              fontFamily: FONT.sans, fontSize: 10.5, fontWeight: 700, color: C.textMuted,
              textTransform: 'uppercase', letterSpacing: '0.07em', flexShrink: 0,
            }}>
              Период
            </span>
            {TIME_WINDOWS.map(w =>
              filterChip(w.key, w.label, timeWindow === w.key, () => setTimeWindow(w.key))
            )}

            {personaOptions.length > 0 && (
              <>
                <span style={{
                  fontFamily: FONT.sans, fontSize: 10.5, fontWeight: 700, color: C.textMuted,
                  textTransform: 'uppercase', letterSpacing: '0.07em', flexShrink: 0, marginLeft: 8,
                }}>
                  Агент
                </span>
                {filterChip('all', 'Все', personaFilter === 'all', () => setPersonaFilter('all'))}
                {personaOptions.map(pid =>
                  filterChip(pid, personas.find(p => p.id === pid)?.name ?? pid.slice(0, 7),
                    personaFilter === pid, () => setPersonaFilter(pid))
                )}
              </>
            )}

            {projectOptions.length > 0 && (
              <>
                <span style={{
                  fontFamily: FONT.sans, fontSize: 10.5, fontWeight: 700, color: C.textMuted,
                  textTransform: 'uppercase', letterSpacing: '0.07em', flexShrink: 0, marginLeft: 8,
                }}>
                  Проект
                </span>
                {filterChip('all', 'Все', projectFilter === 'all', () => setProjectFilter('all'))}
                {projectOptions.map(pid =>
                  filterChip(pid, projectMap.get(pid) ?? pid.slice(0, 7),
                    projectFilter === pid, () => setProjectFilter(pid))
                )}
              </>
            )}
          </div>
        )}
      </div>

      {/* Доска */}
      {!hasItems ? (
        <div style={{ padding: 32, textAlign: 'center' }}>
          <div style={{ fontFamily: FONT.sans, fontSize: 15, color: C.textMuted, marginBottom: 8 }}>
            Нет активных агентов
          </div>
          <div style={{ fontFamily: FONT.sans, fontSize: 12.5, color: C.textMuted, lineHeight: 1.5 }}>
            {timeWindow !== 'all'
              ? 'За выбранный период ничего нет'
              : 'Задачи с исполнителем Claude или персоной появятся здесь'}
          </div>
        </div>
      ) : (
        <div style={{
          display: 'flex', flexDirection: isMobile ? 'column' : 'row',
          gap: 14, overflowX: isMobile ? 'hidden' : 'auto', overflowY: 'auto',
          paddingBottom: 24,
        }}>
          {COLUMNS.map(col => {
            const colItems = filtered.filter(i => i.column === col.key);
            return (
              <div key={col.key} style={{
                flex: isMobile ? 'none' : '1',
                minWidth: isMobile ? 'auto' : 260,
                display: 'flex', flexDirection: 'column', gap: 8,
              }}>
                <div style={{
                  display: 'flex', alignItems: 'center', gap: 8, padding: '0 4px 8px',
                  borderBottom: `2px solid ${col.color}33`,
                }}>
                  <span style={{ width: 10, height: 10, borderRadius: '50%', background: col.color, flexShrink: 0 }} />
                  <span style={{
                    fontFamily: FONT.sans, fontSize: 14, fontWeight: 700, color: C.textHeading,
                  }}>
                    {col.label}
                  </span>
                  <span style={{
                    fontFamily: FONT.sans, fontSize: 12, fontWeight: 700, color: C.textMuted,
                    background: C.bgSelected, borderRadius: R.sm, padding: '1px 7px', minWidth: 18, textAlign: 'center',
                  }}>
                    {colItems.length}
                  </span>
                </div>

                <div style={{
                  display: 'flex', flexDirection: 'column', gap: 8,
                  minHeight: 80,
                }}>
                  {colItems.map(item => (
                    <AgentCard key={item.taskId} item={item} />
                  ))}
                  {colItems.length === 0 && (
                    <div style={{
                      display: 'flex', alignItems: 'center', justifyContent: 'center',
                      minHeight: 60, fontFamily: FONT.sans, fontSize: 12, color: C.textMuted,
                    }}>
                      {col.key === 'queue' ? 'Все задачи в работе' :
                       col.key === 'working' ? 'Никто не работает' :
                       col.key === 'waiting' ? 'Нет ожидающих' :
                       'Нет завершённых'}
                    </div>
                  )}
                </div>
              </div>
            );
          })}
        </div>
      )}
    </div>
  );
}
