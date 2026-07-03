// Диалог быстрого создания задачи: название, проект, приоритет, исполнитель.
// Срок и остальные поля задаются позже в редактировании (как в макете).

import { useEffect, useState } from 'react';
import type { Project, Task, TaskAssignee, TaskPriority } from '../../types';
import { C, FONT, R } from '../../lib/design';
import { Button, FieldLabel, Modal, TextField } from '../../components/ui';
import { api } from '../../lib/api';
import { PRIORITY_LABEL, PRIORITY_ORDER, createTask, projectColor } from '../../lib/tasks';
import { ClaudeBadge, PriorityFlag } from './bits';

interface Props {
  // Проект, выбранный по умолчанию (контекст воркспейса или фильтра календаря)
  defaultProjectId?: string;
  onCreated: (task: Task) => void;
  onClose: () => void;
}

export function NewTaskDialog({ defaultProjectId, onCreated, onClose }: Props) {
  const [title, setTitle] = useState('');
  const [projects, setProjects] = useState<Project[]>([]);
  const [projectId, setProjectId] = useState<string | null>(defaultProjectId ?? null);
  const [priority, setPriority] = useState<TaskPriority>('medium');
  const [assignee, setAssignee] = useState<TaskAssignee>('me');
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    api.projects.list()
      .then(list => {
        setProjects(list);
        // Без контекста — предвыбираем первый проект
        setProjectId(prev => prev ?? list[0]?.id ?? null);
      })
      .catch(() => {});
  }, []);

  const canCreate = title.trim().length > 0 && !!projectId && !saving;

  const handleCreate = async () => {
    if (!canCreate || !projectId) return;
    setSaving(true);
    setError(null);
    try {
      const task = await createTask(projectId, { title: title.trim(), priority, assignee });
      onCreated(task);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Не удалось создать задачу');
      setSaving(false);
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
          <Button variant="primary" disabled={!canCreate} loading={saving} onClick={handleCreate}>
            Создать задачу
          </Button>
        </>
      }
    >
      {/* Название */}
      <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
        <FieldLabel>Название</FieldLabel>
        <TextField value={title} onChange={setTitle} placeholder="Что нужно сделать?" autoFocus onEnter={handleCreate} />
      </div>

      {/* Проект */}
      <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
        <FieldLabel>Проект</FieldLabel>
        <div style={{ display: 'flex', flexWrap: 'wrap', gap: 8 }}>
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
          {projects.length === 0 && (
            <span style={{ fontFamily: FONT.sans, fontSize: 12.5, color: C.textMuted }}>Нет проектов</span>
          )}
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
