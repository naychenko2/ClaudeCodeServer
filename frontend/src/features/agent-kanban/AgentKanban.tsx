import { useEffect, useState, useMemo } from 'react';
import { ensurePersonasLoaded, usePersonas } from '../../lib/personas';
import { ensureAgentsLoaded, useAgentBoard } from '../../lib/agentBoard';
import { AgentCard } from './AgentCard';
import { C, FONT, R } from '../../lib/design';
import { useIsMobile } from '../../lib/breakpoints';

// Конфигурация колонок
const COLUMNS = [
  { key: 'queue' as const, label: 'Очередь', desc: 'Ожидают запуска', color: C.textMuted },
  { key: 'working' as const, label: 'Работает', desc: 'Выполняют задачу', color: C.accent },
  { key: 'waiting' as const, label: 'Ждёт ответа', desc: 'Требуют внимания', color: C.warning },
  { key: 'done' as const, label: 'Готово', desc: 'Завершённые', color: C.success },
];

// Временные окна для колонки «Готово»
type TimeWindow = 'today' | '3days' | 'week' | 'all';
const TIME_WINDOWS: { key: TimeWindow; label: string }[] = [
  { key: '3days', label: '3 дня' },
  { key: 'today', label: 'Сегодня' },
  { key: 'week', label: 'Неделя' },
  { key: 'all', label: 'Всё' },
];

function inTimeWindow(startedAt: string | undefined, window: TimeWindow): boolean {
  if (!startedAt) return false;
  if (window === 'all') return true;
  const t = new Date(startedAt).getTime();
  const now = Date.now();
  const msDay = 86400000;
  switch (window) {
    case 'today': return t > now - msDay;    // последние 24ч
    case '3days': return t > now - msDay && t < now + msDay;  // вчера~завтра
    case 'week': return t > now - 3 * msDay && t < now + 3 * msDay;  // ±3 дня
    default: return true;
  }
}

export function AgentKanban() {
  const items = useAgentBoard();
  const isMobile = useIsMobile();
  const [timeWindow, setTimeWindow] = useState<TimeWindow>('3days');
  const [personaFilter, setPersonaFilter] = useState<string>('all');

  useEffect(() => {
    void ensureAgentsLoaded();
    void ensurePersonasLoaded();
  }, []);

  // Список уникальных персон среди всех карточек
  const personas = usePersonas();
  const personaOptions = useMemo(() => {
    const seen = new Map<string, string>(); // id → label
    for (const item of items) {
      if (item.personaId && !seen.has(item.personaId)) {
        // label будет найден через PersonaAvatar
        seen.set(item.personaId, item.personaId);
      }
    }
    if (!seen.size) return [];
    // Claude без perconaId не отдельная персона
    return Array.from(seen.keys());
  }, [items]);

  // Фильтрация: активные колонки (queue/working/waiting) — всегда,
  // done — только в пределах временного окна
  const filtered = useMemo(() => {
    return items.filter(item => {
      // Фильтр по персоне
      if (personaFilter !== 'all' && item.personaId !== personaFilter) return false;
      // Временной фильтр — только для done
      if (item.column === 'done') return inTimeWindow(item.startedAt, timeWindow);
      return true;
    });
  }, [items, timeWindow, personaFilter]);

  const hasActive = filtered.some(i => i.column === 'working' || i.column === 'waiting');
  const hasItems = filtered.length > 0;

  // Чип-фильтр времени
  const timeFilterChip = (key: TimeWindow, label: string) => (
    <button
      key={key}
      onClick={() => setTimeWindow(key)}
      style={{
        display: 'inline-flex', alignItems: 'center', gap: 6, flexShrink: 0,
        padding: '5px 12px', cursor: 'pointer',
        border: `1px solid ${timeWindow === key ? C.accent : C.border}`,
        borderRadius: 999,
        background: timeWindow === key ? C.accentLight : 'transparent',
        fontFamily: FONT.sans, fontSize: 12, fontWeight: timeWindow === key ? 700 : 500,
        color: C.textPrimary, whiteSpace: 'nowrap',
      }}
    >
      {label}
    </button>
  );

  return (
    <div>
      {/* Фильтры */}
      {hasActive && (
        <div style={{ display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap', marginBottom: 16 }}>
          {/* Временное окно для «Готово» */}
          <span style={{
            fontFamily: FONT.sans, fontSize: 10.5, fontWeight: 700, color: C.textMuted,
            textTransform: 'uppercase', letterSpacing: '0.07em',
          }}>
            Готовые
          </span>
          {TIME_WINDOWS.map(w => timeFilterChip(w.key, w.label))}

          {personaOptions.length > 0 && (
            <>
              <span style={{
                fontFamily: FONT.sans, fontSize: 10.5, fontWeight: 700, color: C.textMuted,
                textTransform: 'uppercase', letterSpacing: '0.07em', marginLeft: 6,
              }}>
                Агент
              </span>
              <button
                onClick={() => setPersonaFilter('all')}
                style={{
                  display: 'inline-flex', alignItems: 'center', gap: 6, flexShrink: 0,
                  padding: '5px 12px', cursor: 'pointer',
                  border: `1px solid ${personaFilter === 'all' ? C.accent : C.border}`,
                  borderRadius: 999,
                  background: personaFilter === 'all' ? C.accentLight : 'transparent',
                  fontFamily: FONT.sans, fontSize: 12, fontWeight: personaFilter === 'all' ? 700 : 500,
                  color: C.textPrimary, whiteSpace: 'nowrap',
                }}
              >
                Все
              </button>
              {personaOptions.map(pid => (
                <button
                  key={pid}
                  onClick={() => setPersonaFilter(pid)}
                  style={{
                    display: 'inline-flex', alignItems: 'center', gap: 6, flexShrink: 0,
                    padding: '5px 12px', cursor: 'pointer',
                    border: `1px solid ${personaFilter === pid ? C.accent : C.border}`,
                    borderRadius: 999,
                    background: personaFilter === pid ? C.accentLight : 'transparent',
                    fontFamily: FONT.sans, fontSize: 12, fontWeight: personaFilter === pid ? 700 : 500,
                    color: C.textPrimary, whiteSpace: 'nowrap',
                  }}
                >
                  {personas.find(p => p.id === pid)?.name ?? pid.slice(0, 7)}
                </button>
              ))}
            </>
          )}
        </div>
      )}

      {/* Доска */}
      {!hasItems ? (
        <div style={{ padding: 32, textAlign: 'center' }}>
          <div style={{ fontFamily: FONT.sans, fontSize: 15, color: C.textMuted, marginBottom: 8 }}>
            Нет активных агентов
          </div>
          <div style={{ fontFamily: FONT.sans, fontSize: 12.5, color: C.textMuted, lineHeight: 1.5 }}>
            {timeWindow !== 'all'
              ? 'За последние 3 дня ничего не завершено'
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
                {/* Заголовок колонки */}
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

                {/* Карточки */}
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
