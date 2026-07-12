// Вкладка «Задачи» персоны: отфильтрованный вид ОБЫЧНЫХ задач проекта/календаря,
// где персона — исполнитель. Никакого своего хранилища и своей вёрстки карточек:
// переиспуем реальный TaskCard и группировку по статусу из раздела «Задачи».
// Клик по карточке открывает задачу в её родном разделе (Календарь/Проект),
// кнопка «Поручить задачу» создаёт настоящую задачу с предзаполненным исполнителем.

import { useEffect, useMemo, useState } from 'react';
import { Plus, SquareCheckBig } from 'lucide-react';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import type { Persona, Task, TaskStatus } from '../../types';
import { C, FONT, R } from '../../lib/design';
import {
  STATUS_DOT, STATUS_LABEL, STATUS_ORDER,
  ensureTasksLoaded, openTaskInSection, useTasks,
} from '../../lib/tasks';
import { TaskCard } from '../tasks/TaskCard';
import { NewTaskDialog } from '../tasks/NewTaskDialog';

export function PersonaTasksPanel({ persona, isMobile }: { persona: Persona; isMobile?: boolean }) {
  const allTasks = useTasks();
  const [loading, setLoading] = useState(true);
  const [showCreate, setShowCreate] = useState(false);

  useEffect(() => {
    let alive = true;
    ensureTasksLoaded().finally(() => { if (alive) setLoading(false); });
    return () => { alive = false; };
  }, []);

  // Задачи, порученные этой персоне-исполнителю
  const tasks = useMemo(
    () => allTasks.filter(t => t.personaId === persona.id),
    [allTasks, persona.id],
  );

  // Группировка по статусу — тем же порядком и подписями, что в разделе «Задачи»
  const groups = useMemo(() => STATUS_ORDER
    .map((s: TaskStatus) => ({ status: s, tasks: tasks.filter(t => t.status === s) }))
    .filter(g => g.tasks.length > 0), [tasks]);

  const inProgress = tasks.filter(t => t.status === 'inProgress').length;

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%', overflow: 'hidden' }}>
      {/* Шапка: сводка + «Поручить задачу» */}
      <div style={{
        display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 12,
        padding: isMobile ? '14px 16px 10px' : '18px 22px 12px', flexShrink: 0,
      }}>
        <div style={{ fontFamily: FONT.sans, fontSize: 13, color: C.textSecondary }}>
          {tasks.length > 0
            ? <>Поручено — <b style={{ color: C.textHeading }}>{tasks.length}</b>{inProgress > 0 && <>, <b style={{ color: C.accent }}>{inProgress}</b> в работе</>}</>
            : 'Пока ничего не поручено'}
        </div>
        <button onClick={() => setShowCreate(true)} style={assignBtn}>
          <Plus size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />
          Поручить задачу
        </button>
      </div>

      {/* Список — реальные карточки задач */}
      <div style={{ flex: 1, overflowY: 'auto', padding: isMobile ? '4px 16px 20px' : '4px 22px 24px' }}>
        {loading ? (
          <div style={{ padding: 24, textAlign: 'center', color: C.textMuted, fontFamily: FONT.sans, fontSize: 13 }}>Загрузка…</div>
        ) : tasks.length === 0 ? (
          <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center', gap: 14, padding: '50px 24px', textAlign: 'center' }}>
            <SquareCheckBig size={46} strokeWidth={1.5} color={C.dashed} style={{ flexShrink: 0 }} />
            <div style={{ maxWidth: 320, fontFamily: FONT.sans, fontSize: 13.5, color: C.textMuted, lineHeight: 1.5 }}>
              Этой персоне пока ничего не поручено. Создайте задачу и выберите её исполнителем — задача появится здесь.
            </div>
          </div>
        ) : (
          groups.map(group => (
            <div key={group.status} style={{ marginBottom: 12 }}>
              <div style={{ display: 'flex', alignItems: 'center', gap: 6, padding: '6px 4px 8px' }}>
                <span style={{ width: 7, height: 7, borderRadius: '50%', background: STATUS_DOT[group.status], flexShrink: 0 }} />
                <span style={{ fontFamily: FONT.sans, fontSize: 11, fontWeight: 700, color: C.textSecondary, textTransform: 'uppercase', letterSpacing: '0.06em' }}>
                  {STATUS_LABEL[group.status]}
                </span>
                <span style={{ fontFamily: FONT.sans, fontSize: 11, color: C.textMuted }}>{group.tasks.length}</span>
              </div>
              <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
                {group.tasks.map((task: Task) => (
                  <TaskCard key={task.id} task={task} onClick={() => openTaskInSection(task)} />
                ))}
              </div>
            </div>
          ))
        )}
      </div>

      {/* Создание задачи с предзаполненным исполнителем (проектная персона → её проект) */}
      {showCreate && (
        <NewTaskDialog
          defaultPersonaId={persona.id}
          defaultProjectId={persona.scope === 'project' ? persona.projectId : undefined}
          onCreated={(task, configure) => {
            setShowCreate(false);
            // «Создать и настроить» — открыть задачу в её разделе; иначе остаёмся,
            // задача появится в списке через realtime task_changed
            if (configure) openTaskInSection(task);
          }}
          onClose={() => setShowCreate(false)}
        />
      )}
    </div>
  );
}

const assignBtn: React.CSSProperties = {
  display: 'inline-flex', alignItems: 'center', gap: 7, flexShrink: 0,
  border: `1px solid ${C.accent}`, background: C.accentLight, color: C.accent,
  borderRadius: R.lg, padding: '8px 14px', cursor: 'pointer',
  fontFamily: FONT.sans, fontSize: 13, fontWeight: 600,
};
