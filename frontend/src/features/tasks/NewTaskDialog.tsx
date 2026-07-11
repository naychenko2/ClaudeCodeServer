// Диалог быстрого создания задачи: название, проект, срок, приоритет, исполнитель.
// Остальные поля задаются позже в редактировании.

import { useEffect, useMemo, useRef, useState } from 'react';
import type { Project, Task, TaskAssignee, TaskPriority } from '../../types';
import { C, FONT, R } from '../../lib/design';
import { Button, FieldLabel, Modal, TextField } from '../../components/ui';
import { api } from '../../lib/api';
import { NO_PROJECT_COLOR, NO_PROJECT_LABEL, PRIORITY_LABEL, PRIORITY_ORDER, createTask, projectColor } from '../../lib/tasks';
import { PriorityFlag } from './bits';
import { ExecutorPicker } from './ExecutorPicker';
import { DueDatePicker } from './DueDatePicker';

interface Props {
  // Проект, выбранный по умолчанию (контекст воркспейса или фильтра календаря)
  defaultProjectId?: string;
  // Срок по умолчанию (быстрое создание на день из календаря)
  defaultDueDate?: string;
  // Персона-исполнитель по умолчанию (открыто из вкладки «Задачи» персоны:
  // «Поручить задачу») — предзаполняет исполнителя, assignee станет claude
  defaultPersonaId?: string;
  // Подпись кнопки «создать и открыть редактор» (в календаре — «Подробнее»)
  configureLabel?: string;
  // configure=true — открыть карточку сразу в редактировании
  onCreated: (task: Task, configure: boolean) => void;
  onClose: () => void;
}

export function NewTaskDialog({ defaultProjectId, defaultDueDate, defaultPersonaId, configureLabel = 'Создать и настроить', onCreated, onClose }: Props) {
  const [title, setTitle] = useState('');
  const [projects, setProjects] = useState<Project[]>([]);
  // null = «Личное» (задача вне проекта) — дефолт, когда открыто не из проекта
  const [projectId, setProjectId] = useState<string | null>(defaultProjectId ?? null);
  const [priority, setPriority] = useState<TaskPriority>('medium');
  // Персона-исполнитель ⇒ assignee=claude (иначе по умолчанию «Я»)
  const [assignee, setAssignee] = useState<TaskAssignee>(defaultPersonaId ? 'claude' : 'me');
  const [personaId, setPersonaId] = useState(defaultPersonaId ?? '');
  const [dueDate, setDueDate] = useState<string | null>(defaultDueDate ?? null);
  const [dueTime, setDueTime] = useState<string | null>(null);
  // Какая из кнопок создаёт: 'plain' — просто создать, 'configure' — создать и настроить
  const [saving, setSaving] = useState<null | 'plain' | 'configure'>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    api.projects.list().then(setProjects).catch(() => {});
  }, []);

  // Проектов может быть много (10+) — чипы одной горизонтальной лентой на всех
  // размерах, проект контекста первым, выбранный подъезжает в видимую зону
  const chipsRef = useRef<HTMLDivElement>(null);
  const orderedProjects = useMemo(() => {
    if (!defaultProjectId) return projects;
    const def = projects.find(p => p.id === defaultProjectId);
    return def ? [def, ...projects.filter(p => p.id !== defaultProjectId)] : projects;
  }, [projects, defaultProjectId]);

  useEffect(() => {
    if (projects.length === 0) return;
    chipsRef.current?.querySelector('[data-active="true"]')
      ?.scrollIntoView({ inline: 'center', block: 'nearest' });
  }, [projects.length]);

  const canCreate = title.trim().length > 0 && !saving;

  const handleCreate = async (configure: boolean) => {
    if (!canCreate) return;
    setSaving(configure ? 'configure' : 'plain');
    setError(null);
    try {
      const task = await createTask(projectId, {
        title: title.trim(), priority, assignee,
        // Персона-исполнитель имеет смысл только у Claude
        personaId: assignee === 'claude' && personaId ? personaId : undefined,
        dueDate: dueDate ?? undefined, dueTime: dueTime ?? undefined,
      });
      onCreated(task, configure);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Не удалось создать задачу');
      setSaving(null);
    }
  };

  return (
    <Modal
      title="Новая задача"
      onClose={onClose}
      width={480}
      footer={
        <>
          <Button variant="secondary" onClick={onClose}>Отмена</Button>
          <Button variant="secondary" disabled={!canCreate} loading={saving === 'configure'} onClick={() => handleCreate(true)}>
            {configureLabel}
          </Button>
          <Button variant="primary" disabled={!canCreate} loading={saving === 'plain'} onClick={() => handleCreate(false)}>
            Создать
          </Button>
        </>
      }
    >
      {/* Название */}
      <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
        <FieldLabel>Название</FieldLabel>
        <TextField value={title} onChange={setTitle} placeholder="Что нужно сделать?" autoFocus onEnter={() => handleCreate(false)} />
      </div>

      {/* Проект */}
      <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
        <FieldLabel>Проект</FieldLabel>
        {/* Одна горизонтальная лента: 10+ проектов не распирают форму */}
        <div
          ref={chipsRef}
          className="cc-hide-scrollbar"
          style={{ display: 'flex', gap: 8, overflowX: 'auto', paddingBottom: 2 }}
        >
          {/* «Личное» — нейтральная опция первой (задача вне проекта) */}
          <button
            data-active={projectId === null}
            onClick={() => setProjectId(null)}
            style={{
              display: 'inline-flex', alignItems: 'center', gap: 7, flexShrink: 0,
              padding: '7px 13px', cursor: 'pointer',
              border: `1px solid ${projectId === null ? C.accent : C.border}`,
              borderRadius: 999,
              background: projectId === null ? C.accentLight : C.bgWhite,
              fontFamily: FONT.sans, fontSize: 13, fontWeight: projectId === null ? 600 : 500,
              color: C.textPrimary,
              transition: 'border-color 0.12s, background 0.12s',
            }}
          >
            <span style={{ width: 8, height: 8, borderRadius: '50%', background: NO_PROJECT_COLOR.main, flexShrink: 0 }} />
            {NO_PROJECT_LABEL}
          </button>
          {orderedProjects.map(p => {
            const active = p.id === projectId;
            const color = projectColor(p.id);
            return (
              <button
                key={p.id}
                data-active={active}
                onClick={() => setProjectId(p.id)}
                style={{
                  display: 'inline-flex', alignItems: 'center', gap: 7, flexShrink: 0,
                  padding: '7px 13px', cursor: 'pointer',
                  border: `1px solid ${active ? C.accent : C.border}`,
                  borderRadius: 999,
                  background: active ? C.accentLight : C.bgWhite,
                  fontFamily: FONT.sans, fontSize: 13, fontWeight: active ? 600 : 500,
                  color: C.textPrimary,
                  transition: 'border-color 0.12s, background 0.12s',
                }}
              >
                <span style={{ width: 8, height: 8, borderRadius: '50%', background: color.main, flexShrink: 0 }} />
                {p.name}
              </button>
            );
          })}
        </div>
      </div>

      {/* Срок — как в редакторе: чипы + календарик с временем */}
      <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
        <FieldLabel>Срок</FieldLabel>
        <DueDatePicker
          dueDate={dueDate}
          dueTime={dueTime}
          onChange={(d, t) => { setDueDate(d); setDueTime(t); }}
        />
      </div>

      {/* Приоритет */}
      <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
        <FieldLabel>Приоритет</FieldLabel>
        <div style={{ display: 'flex', gap: 7 }}>
          {PRIORITY_ORDER.map(p => {
            const active = p === priority;
            return (
              <button
                key={p}
                onClick={() => setPriority(p)}
                title={PRIORITY_LABEL[p]}
                style={{
                  width: 36, height: 36, padding: 0, cursor: 'pointer',
                  border: `1px solid ${active ? C.accent : C.border}`,
                  borderRadius: R.lg,
                  background: active ? C.accentLight : C.bgWhite,
                  display: 'flex', alignItems: 'center', justifyContent: 'center',
                  transition: 'border-color 0.12s, background 0.12s',
                }}
              >
                <PriorityFlag priority={p} size={15} />
              </button>
            );
          })}
        </div>
      </div>

      {/* Исполнитель — единый пикер (Я / Claude / персона) */}
      <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
        <FieldLabel>Исполнитель</FieldLabel>
        <ExecutorPicker
          assignee={assignee}
          personaId={personaId || null}
          projectId={projectId}
          onChange={v => { setAssignee(v.assignee); setPersonaId(v.personaId ?? ''); }}
        />
      </div>

      {error && (
        <div style={{ fontFamily: FONT.sans, fontSize: 12.5, color: C.danger }}>{error}</div>
      )}
    </Modal>
  );
}
