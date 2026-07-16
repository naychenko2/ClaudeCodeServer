import { useEffect } from 'react';
import { ensurePersonasLoaded } from '../../lib/personas';
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

export function AgentKanban({ onOpenChat }: { onOpenChat: (sessionId: string) => void }) {
  const items = useAgentBoard();
  const isMobile = useIsMobile();

  useEffect(() => {
    void ensureAgentsLoaded();
    void ensurePersonasLoaded();
  }, []);

  if (items.length === 0) {
    return (
      <div style={{ padding: 32, textAlign: 'center' }}>
        <div style={{ fontFamily: FONT.sans, fontSize: 15, color: C.textMuted, marginBottom: 8 }}>
          Нет активных агентов
        </div>
        <div style={{ fontFamily: FONT.sans, fontSize: 12.5, color: C.textMuted, lineHeight: 1.5 }}>
          Задачи с исполнителем Claude или персоной появятся здесь
        </div>
      </div>
    );
  }

  return (
    <div style={{
      display: 'flex', flexDirection: isMobile ? 'column' : 'row',
      gap: 14, overflowX: isMobile ? 'hidden' : 'auto', overflowY: 'auto',
      paddingBottom: 24, height: '100%', boxSizing: 'border-box',
    }}>
      {COLUMNS.map(col => {
        const colItems = items.filter(i => i.column === col.key);
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
                <AgentCard key={item.taskId} item={item} onOpenChat={onOpenChat} />
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
  );
}
