// Редактирование задачи — инлайн-экран вместо деталей (как в макете):
// шапка «Редактирование задачи» с Отмена / ✓ Готово / корзина, ниже поля формы.

import { useState } from 'react';
import type { Task, TaskAssignee, TaskPriority, TaskSubtask, UpdateTaskDto } from '../../types';
import { C, FONT, R } from '../../lib/design';
import { IconButton } from '../../components/ui';
import { Toolbar } from '../../components/Toolbar';
import { MarkdownViewer } from '../../components/MarkdownViewer';
import { api } from '../../lib/api';
import { PRIORITY_LABEL, PRIORITY_ORDER } from '../../lib/tasks';
import { ClaudeBadge, MeBadge, PriorityFlag, SubtaskCheck } from './bits';
import { DueDatePicker } from './DueDatePicker';
import { MarkdownEditor } from './MarkdownEditor';

// Кнопка-чип «сгенерировать с Claude» (описание/подзадачи)
function AiButton({ label, loading, disabled, onClick }: {
  label: string; loading: boolean; disabled?: boolean; onClick: () => void;
}) {
  const inactive = loading || disabled;
  return (
    <button
      onClick={onClick}
      disabled={inactive}
      title={disabled ? 'Сначала заполните название и описание' : `${label} с помощью Claude`}
      style={{
        display: 'inline-flex', alignItems: 'center', gap: 6,
        padding: '4px 11px', cursor: inactive ? 'default' : 'pointer',
        border: `1px solid ${C.accentMuted}`, borderRadius: 999,
        background: C.accentLight, color: C.accent,
        fontFamily: FONT.sans, fontSize: 12, fontWeight: 600,
        opacity: disabled ? 0.5 : 1,
      }}
    >
      {loading
        ? <span className="tool-spinner" style={{ width: 11, height: 11, flexShrink: 0 }} />
        : <ClaudeBadge size={14} />}
      {loading ? 'Claude думает…' : label}
    </button>
  );
}

interface Props {
  task: Task;
  isMobile?: boolean;
  onSave: (dto: UpdateTaskDto) => Promise<void>;
  onCancel: () => void;
  onDelete: () => void;
}

function fieldLabelStyle(): React.CSSProperties {
  return {
    fontFamily: FONT.sans, fontSize: 11.5, fontWeight: 700, color: C.textMuted,
    textTransform: 'uppercase', letterSpacing: '0.06em', marginBottom: 9,
  };
}

export function TaskEditForm({ task, isMobile, onSave, onCancel, onDelete }: Props) {
  const [title, setTitle] = useState(task.title);
  const [priority, setPriority] = useState<TaskPriority>(task.priority);
  const [dueDate, setDueDate] = useState<string | null>(task.dueDate ?? null);
  const [dueTime, setDueTime] = useState<string | null>(task.dueTime ?? null);
  const [assignee, setAssignee] = useState<TaskAssignee>(task.assignee ?? 'me');
  const [description, setDescription] = useState(task.description);
  const [descEditing, setDescEditing] = useState(!task.description);
  const [subtasks, setSubtasks] = useState<TaskSubtask[]>(task.subtasks);
  const [newSubtask, setNewSubtask] = useState('');
  const [labels, setLabels] = useState<string[]>(task.labels);
  const [newLabel, setNewLabel] = useState('');
  const [saving, setSaving] = useState(false);
  const [aiDescLoading, setAiDescLoading] = useState(false);
  const [aiSubsLoading, setAiSubsLoading] = useState(false);
  const [aiError, setAiError] = useState<string | null>(null);

  // Описание от Claude: по названию + контекст проекта (личная — только название)
  const generateDescription = async () => {
    if (!title.trim() || aiDescLoading) return;
    setAiDescLoading(true);
    setAiError(null);
    try {
      const r = await api.tasks.aiDescription(title.trim(), task.projectId ?? null);
      setDescription(r.description);
      setDescEditing(false);   // сразу показываем результат в превью
    } catch (e) {
      setAiError(e instanceof Error ? e.message : 'Не удалось сгенерировать описание');
    } finally {
      setAiDescLoading(false);
    }
  };

  // Подзадачи от Claude: по названию и заполненному описанию
  const generateSubtasks = async () => {
    if (!title.trim() || !description.trim() || aiSubsLoading) return;
    setAiSubsLoading(true);
    setAiError(null);
    try {
      const r = await api.tasks.aiSubtasks(title.trim(), description, task.projectId ?? null);
      const existing = new Set(subtasks.map(s => s.title.toLowerCase()));
      const fresh = r.subtasks.filter(t => !existing.has(t.toLowerCase()));
      setSubtasks(prev => [...prev, ...fresh.map(t => ({ id: '', title: t, isDone: false }))]);
    } catch (e) {
      setAiError(e instanceof Error ? e.message : 'Не удалось сгенерировать подзадачи');
    } finally {
      setAiSubsLoading(false);
    }
  };

  const addSubtask = () => {
    const t = newSubtask.trim();
    if (!t) return;
    setSubtasks(prev => [...prev, { id: '', title: t, isDone: false }]);
    setNewSubtask('');
  };

  const addLabel = () => {
    const l = newLabel.trim();
    if (!l || labels.includes(l)) { setNewLabel(''); return; }
    setLabels(prev => [...prev, l]);
    setNewLabel('');
  };

  const handleSave = async () => {
    if (saving || !title.trim()) return;
    setSaving(true);
    try {
      await onSave({
        title: title.trim(),
        priority,
        dueDate: dueDate ?? '',
        dueTime: dueTime ?? '',
        assignee,
        description,
        subtasks: subtasks.filter(s => s.title.trim()),
        labels,
      });
    } finally {
      setSaving(false);
    }
  };

  const assigneeCard = (value: TaskAssignee, label: string, icon: React.ReactNode) => {
    const active = assignee === value;
    return (
      <button
        onClick={() => setAssignee(value)}
        style={{
          flex: 1, display: 'flex', alignItems: 'center', gap: 10,
          padding: '11px 14px', cursor: 'pointer', boxSizing: 'border-box',
          border: `1px solid ${active ? C.accent : C.border}`,
          borderRadius: R.xl,
          background: active ? C.accentLight : C.bgWhite,
          fontFamily: FONT.sans, fontSize: 13.5, fontWeight: 600, color: C.textPrimary,
          transition: 'border-color 0.12s, background 0.12s',
        }}
      >
        {icon}
        {label}
      </button>
    );
  };

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%', overflow: 'hidden', background: C.bgMain }}>
      {/* Шапка — как тулбар чата */}
      <Toolbar isMobile={isMobile}>
        <div style={{ minWidth: 0, flex: 1 }}>
          <div style={{ fontSize: 17, fontWeight: 600, color: C.textHeading, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
            {isMobile ? 'Редактирование' : 'Редактирование задачи'}
          </div>
          <div style={{ fontFamily: FONT.mono, fontSize: 12, color: C.textMuted, marginTop: 2, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
            {task.title}
          </div>
        </div>
        <button
          onClick={onCancel}
          style={{
            padding: '0 14px', height: 32, cursor: 'pointer',
            border: 'none', borderRadius: R.md,
            background: C.bgSelected, color: C.textSecondary,
            fontFamily: FONT.sans, fontSize: 13, fontWeight: 600,
          }}
        >
          Отмена
        </button>
        <button
          onClick={handleSave}
          disabled={saving || !title.trim()}
          style={{
            display: 'flex', alignItems: 'center', gap: 6,
            padding: '0 14px', height: 32, cursor: saving ? 'default' : 'pointer',
            border: 'none', borderRadius: R.md,
            background: C.accent, color: C.onAccent,
            fontFamily: FONT.sans, fontSize: 13, fontWeight: 600,
            opacity: saving || !title.trim() ? 0.6 : 1,
          }}
        >
          <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.6" strokeLinecap="round" strokeLinejoin="round">
            <polyline points="20 6 9 17 4 12" />
          </svg>
          Готово
        </button>
        <IconButton size="md" tone="danger" onClick={onDelete} title="Удалить задачу">
          <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <path d="M3 6h18M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2m3 0v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6h14" />
          </svg>
        </IconButton>
      </Toolbar>

      {/* Форма */}
      <div style={{ flex: 1, overflowY: 'auto' }}>
        <div style={{ maxWidth: 680, padding: isMobile ? '18px 16px 32px' : '22px 32px 40px', boxSizing: 'border-box' }}>
          {/* Название */}
          <div style={fieldLabelStyle()}>Название</div>
          <input
            value={title}
            onChange={e => setTitle(e.target.value)}
            placeholder="Что нужно сделать?"
            style={{
              width: '100%', boxSizing: 'border-box', marginBottom: 22,
              border: 'none', outline: 'none', background: 'transparent',
              fontFamily: FONT.serif, fontSize: isMobile ? 21 : 24, fontWeight: 500,
              color: C.textHeading, padding: 0, lineHeight: 1.3,
            }}
          />

          {/* Приоритет */}
          <div style={fieldLabelStyle()}>Приоритет</div>
          <div style={{ display: 'flex', gap: 9, marginBottom: 8 }}>
            {PRIORITY_ORDER.map(p => {
              const active = p === priority;
              return (
                <button
                  key={p}
                  onClick={() => setPriority(p)}
                  title={PRIORITY_LABEL[p]}
                  style={{
                    flex: 1, height: 42, cursor: 'pointer', boxSizing: 'border-box',
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
          <div style={{ fontFamily: FONT.sans, fontSize: 13.5, fontWeight: 700, color: C.textHeading, marginBottom: 22 }}>
            {PRIORITY_LABEL[priority]}
          </div>

          {/* Срок */}
          <div style={fieldLabelStyle()}>Срок</div>
          <div style={{ marginBottom: 22 }}>
            <DueDatePicker
              dueDate={dueDate}
              dueTime={dueTime}
              onChange={(d, t) => { setDueDate(d); setDueTime(t); }}
            />
          </div>

          {/* Исполнитель */}
          <div style={fieldLabelStyle()}>Исполнитель</div>
          <div style={{ display: 'flex', gap: 10, marginBottom: 22 }}>
            {assigneeCard('me', 'Я', <MeBadge size={22} />)}
            {assigneeCard('claude', 'Claude', <ClaudeBadge size={22} />)}
          </div>

          {/* Описание */}
          <div style={{ display: 'flex', alignItems: 'center', gap: 10, marginBottom: 9 }}>
            <div style={{ ...fieldLabelStyle(), marginBottom: 0 }}>Описание</div>
            <AiButton
              label={description.trim() ? 'Переписать' : 'Создать описание'}
              loading={aiDescLoading}
              disabled={!title.trim()}
              onClick={generateDescription}
            />
            <div style={{ flex: 1 }} />
            <button
              onClick={() => setDescEditing(v => !v)}
              style={{
                display: 'inline-flex', alignItems: 'center', gap: 5,
                border: 'none', background: 'none', cursor: 'pointer', padding: 0,
                fontFamily: FONT.sans, fontSize: 12.5, fontWeight: 600, color: C.accent,
              }}
            >
              {descEditing ? (
                <>
                  <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.4" strokeLinecap="round"><polyline points="20 6 9 17 4 12" /></svg>
                  Просмотр
                </>
              ) : (
                <>
                  <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                    <path d="M17 3a2.85 2.83 0 1 1 4 4L7.5 20.5 2 22l1.5-5.5Z" />
                  </svg>
                  Редактировать
                </>
              )}
            </button>
          </div>
          <div style={{ marginBottom: 22 }}>
            {descEditing ? (
              <MarkdownEditor
                value={description}
                onChange={setDescription}
                placeholder="Описание задачи (markdown)…"
                minHeight={150}
              />
            ) : (
              <div style={{
                background: C.bgWhite, border: `1px solid ${C.borderLight}`,
                borderRadius: R.xl, padding: '14px 18px', fontSize: 14,
              }}>
                {description
                  ? <MarkdownViewer content={description} />
                  : <span style={{ fontFamily: FONT.sans, fontSize: 13, color: C.textMuted }}>Нет описания</span>}
              </div>
            )}
          </div>

          {aiError && (
            <div style={{ fontFamily: FONT.sans, fontSize: 12.5, color: C.danger, margin: '-12px 0 18px' }}>
              {aiError}
            </div>
          )}

          {/* Подзадачи */}
          <div style={{ display: 'flex', alignItems: 'center', gap: 10, marginBottom: 9 }}>
            <div style={{ ...fieldLabelStyle(), marginBottom: 0 }}>Подзадачи</div>
            <AiButton
              label="Предложить подзадачи"
              loading={aiSubsLoading}
              disabled={!title.trim() || !description.trim()}
              onClick={generateSubtasks}
            />
          </div>
          <div style={{
            background: C.bgWhite, border: `1px solid ${C.borderLight}`, borderRadius: R.xl,
            overflow: 'hidden', marginBottom: 22,
          }}>
            {subtasks.map((s, i) => (
              <div key={s.id || `new-${i}`} style={{
                display: 'flex', alignItems: 'center', gap: 11,
                padding: '11px 14px',
                borderBottom: `1px solid ${C.borderLight}`,
              }}>
                <span
                  onClick={() => setSubtasks(prev => prev.map((x, xi) => xi === i ? { ...x, isDone: !x.isDone } : x))}
                  style={{ cursor: 'pointer', display: 'flex' }}
                >
                  <SubtaskCheck done={s.isDone} />
                </span>
                <span style={{
                  flex: 1, fontFamily: FONT.sans, fontSize: 14,
                  color: s.isDone ? C.textMuted : C.textPrimary,
                  textDecoration: s.isDone ? 'line-through' : 'none',
                }}>
                  {s.title}
                </span>
                <button
                  onClick={() => setSubtasks(prev => prev.filter((_, xi) => xi !== i))}
                  title="Удалить подзадачу"
                  style={{
                    border: 'none', background: 'none', cursor: 'pointer', padding: 4,
                    color: C.textMuted, fontSize: 15, lineHeight: 1, fontFamily: FONT.sans,
                  }}
                >
                  ✕
                </button>
              </div>
            ))}
            {/* Строка добавления */}
            <div style={{ display: 'flex', alignItems: 'center', gap: 11, padding: '11px 14px' }}>
              <div style={{
                width: 20, height: 20, borderRadius: 6, flexShrink: 0, boxSizing: 'border-box',
                border: `1.5px dashed ${C.dashed}`,
              }} />
              <input
                value={newSubtask}
                onChange={e => setNewSubtask(e.target.value)}
                onKeyDown={e => { if (e.key === 'Enter') addSubtask(); }}
                placeholder="Добавить подзадачу"
                style={{
                  flex: 1, border: 'none', outline: 'none', background: 'transparent',
                  fontFamily: FONT.sans, fontSize: 14, color: C.textPrimary,
                }}
              />
              <button
                onClick={addSubtask}
                title="Добавить"
                style={{
                  width: 24, height: 24, padding: 0, cursor: 'pointer',
                  border: `1px solid ${C.border}`, borderRadius: R.md, background: C.bgCard,
                  color: C.textSecondary, display: 'flex', alignItems: 'center', justifyContent: 'center',
                }}
              >
                <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.4" strokeLinecap="round">
                  <line x1="12" y1="5" x2="12" y2="19" /><line x1="5" y1="12" x2="19" y2="12" />
                </svg>
              </button>
            </div>
          </div>

          {/* Метки */}
          <div style={fieldLabelStyle()}>Метки</div>
          {labels.length > 0 && (
            <div style={{ display: 'flex', flexWrap: 'wrap', gap: 7, marginBottom: 10 }}>
              {labels.map(l => (
                <span key={l} style={{
                  display: 'inline-flex', alignItems: 'center', gap: 6,
                  fontFamily: FONT.sans, fontSize: 12, color: C.textSecondary,
                  background: C.bgSelected, padding: '4px 10px', borderRadius: R.sm,
                }}>
                  {l}
                  <span
                    onClick={() => setLabels(prev => prev.filter(x => x !== l))}
                    style={{ cursor: 'pointer', color: C.textMuted, fontSize: 12, lineHeight: 1 }}
                  >
                    ✕
                  </span>
                </span>
              ))}
            </div>
          )}
          <div style={{
            display: 'flex', alignItems: 'center', gap: 8,
            background: C.bgWhite, border: `1px solid ${C.borderLight}`, borderRadius: R.xl,
            padding: '10px 14px',
          }}>
            <input
              value={newLabel}
              onChange={e => setNewLabel(e.target.value)}
              onKeyDown={e => { if (e.key === 'Enter') addLabel(); }}
              placeholder="Добавить метку"
              style={{
                flex: 1, border: 'none', outline: 'none', background: 'transparent',
                fontFamily: FONT.sans, fontSize: 13.5, color: C.textPrimary,
              }}
            />
            <button
              onClick={addLabel}
              style={{
                border: 'none', background: 'none', cursor: 'pointer', padding: 0,
                fontFamily: FONT.sans, fontSize: 13, fontWeight: 600, color: C.accent,
              }}
            >
              Добавить
            </button>
          </div>
        </div>
      </div>
    </div>
  );
}
