// Диалог быстрого создания задачи: название, проект, приоритет, исполнитель.
// Срок и остальные поля задаются позже в редактировании (как в макете).

import { useEffect, useState } from 'react';
import type { Project, Task, TaskAssignee, TaskPriority } from '../../types';
import { C, FONT, R } from '../../lib/design';
import { Button, FieldLabel, Modal, TextField } from '../../components/ui';
import { api } from '../../lib/api';
import { NO_PROJECT_COLOR, NO_PROJECT_LABEL, PRIORITY_LABEL, PRIORITY_ORDER, createTask, projectColor } from '../../lib/tasks';
import { ClaudeBadge, PriorityFlag } from './bits';

interface Props {
  // Проект, выбранный по умолчанию (контекст воркспейса или фильтра календаря)
  defaultProjectId?: string;
  // Подпись кнопки «создать и открыть редактор» (в календаре — «Подробнее»)
  configureLabel?: string;
  // configure=true — открыть карточку сразу в редактировании
  onCreated: (task: Task, configure: boolean) => void;
  onClose: () => void;
}

export function NewTaskDialog({ defaultProjectId, configureLabel = 'Создать и настроить', onCreated, onClose }: Props) {
  const [title, setTitle] = useState('');
  const [projects, setProjects] = useState<Project[]>([]);
  // null = «Личное» (задача вне проекта) — дефолт, когда открыто не из проекта
  const [projectId, setProjectId] = useState<string | null>(defaultProjectId ?? null);
  const [priority, setPriority] = useState<TaskPriority>('medium');
  const [assignee, setAssignee] = useState<TaskAssignee>('me');
  // Какая из кнопок создаёт: 'plain' — просто создать, 'configure' — создать и настроить
  const [saving, setSaving] = useState<null | 'plain' | 'configure'>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    api.projects.list().then(setProjects).catch(() => {});
  }, []);

  const canCreate = title.trim().length > 0 && !saving;

  const handleCreate = async (configure: boolean) => {
    if (!canCreate) return;
    setSaving(configure ? 'configure' : 'plain');
    setError(null);
    try {
      const task = await createTask(projectId, { title: title.trim(), priority, assignee });
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
        <div style={{ display: 'flex', flexWrap: 'wrap', gap: 8 }}>
          {/* «Личное» — нейтральная опция первой (задача вне проекта) */}
          <button
            onClick={() => setProjectId(null)}
            style={{
              display: 'inline-flex', alignItems: 'center', gap: 7,
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
          {projects.map(p => {
            const active = p.id === projectId;
            const color = projectColor(p.id);
            return (
              <button
                key={p.id}
                onClick={() => setProjectId(p.id)}
                style={{
                  display: 'inline-flex', alignItems: 'center', gap: 7,
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

      {/* Приоритет + исполнитель в одну строку */}
      <div style={{ display: 'flex', gap: 24, flexWrap: 'wrap' }}>
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

        <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
          <FieldLabel>Исполнитель</FieldLabel>
          <div style={{ display: 'flex', gap: 7, alignItems: 'center' }}>
            <button
              onClick={() => setAssignee('me')}
              title="Я"
              style={{
                width: 36, height: 36, padding: 0, cursor: 'pointer',
                borderRadius: '50%', boxSizing: 'border-box',
                border: assignee === 'me' ? `2px solid ${C.textHeading}` : `1px solid ${C.border}`,
                background: C.bgPanel,
                fontFamily: FONT.sans, fontSize: 14, fontWeight: 700, color: C.textSecondary,
                display: 'flex', alignItems: 'center', justifyContent: 'center',
              }}
            >
              Я
            </button>
            <button
              onClick={() => setAssignee('claude')}
              title="Claude"
              style={{
                width: 36, height: 36, padding: 0, cursor: 'pointer',
                borderRadius: R.lg, boxSizing: 'border-box',
                border: assignee === 'claude' ? `2px solid ${C.textHeading}` : '2px solid transparent',
                background: 'transparent',
                display: 'flex', alignItems: 'center', justifyContent: 'center',
              }}
            >
              <ClaudeBadge size={30} />
            </button>
          </div>
        </div>
      </div>

      {error && (
        <div style={{ fontFamily: FONT.sans, fontSize: 12.5, color: C.danger }}>{error}</div>
      )}
    </Modal>
  );
}
