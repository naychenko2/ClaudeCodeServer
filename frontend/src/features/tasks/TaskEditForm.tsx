// Редактирование задачи — инлайн-экран вместо деталей (как в макете):
// шапка «Редактирование задачи» с Отмена / ✓ Готово / корзина, ниже поля формы.

import { useEffect, useState } from 'react';
import { Check, Plus, SquarePen, Trash2, X } from 'lucide-react';
import type { Task, TaskAssignee, TaskPriority, TaskRecurrence, TaskRecurrenceType, TaskSubtask, UpdateTaskDto } from '../../types';
import { C, FONT, R } from '../../lib/design';
import { IconButton } from '../../components/ui';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { Toolbar } from '../../components/Toolbar';
import { MarkdownViewer } from '../../components/MarkdownViewer';
import { api } from '../../lib/api';
import { PRIORITY_LABEL, PRIORITY_ORDER, RECURRENCE_TYPE_LABEL, REMINDER_PRESETS, reminderLabel } from '../../lib/tasks';
import { PriorityFlag, SubtaskCheck } from './bits';
import { DueDatePicker } from './DueDatePicker';
import { ExecutorPicker } from './ExecutorPicker';
import { MarkdownEditor } from './MarkdownEditor';

interface Props {
  task: Task;
  isMobile?: boolean;
  onSave: (dto: UpdateTaskDto) => Promise<void>;
  onCancel: () => void;
  onDelete: () => void;
  // AI-хаб: авто-запуск генерации при входе в правку из палитры/подсказки
  pendingAi?: 'task.description' | 'task.subtasks' | null;
  onPendingConsumed?: () => void;
}

function fieldLabelStyle(): React.CSSProperties {
  return {
    fontFamily: FONT.sans, fontSize: 11.5, fontWeight: 700, color: C.textMuted,
    textTransform: 'uppercase', letterSpacing: '0.06em', marginBottom: 9,
  };
}

// Чипы напоминания — визуально как быстрые чипы срока (DueDatePicker)
function reminderChipStyle(active: boolean): React.CSSProperties {
  return {
    display: 'inline-flex', alignItems: 'center', gap: 6,
    padding: '7px 13px', cursor: 'pointer',
    border: `1px solid ${active ? C.accent : C.border}`,
    borderRadius: R.lg,
    background: active ? C.accentLight : C.bgWhite,
    fontFamily: FONT.sans, fontSize: 13, fontWeight: active ? 600 : 500,
    color: active ? C.accent : C.textPrimary,
    transition: 'border-color 0.12s, background 0.12s',
    whiteSpace: 'nowrap',
  };
}

export function TaskEditForm({ task, isMobile, onSave, onCancel, onDelete, pendingAi, onPendingConsumed }: Props) {
  const [title, setTitle] = useState(task.title);
  const [priority, setPriority] = useState<TaskPriority>(task.priority);
  const [dueDate, setDueDate] = useState<string | null>(task.dueDate ?? null);
  const [dueTime, setDueTime] = useState<string | null>(task.dueTime ?? null);
  const [reminder, setReminder] = useState<number | null>(task.reminderMinutes ?? null);
  // Кастомный офсет: значение + единица (перемножаются в минуты)
  const [customReminderOpen, setCustomReminderOpen] = useState(false);
  const [customReminderValue, setCustomReminderValue] = useState('2');
  const [customReminderUnit, setCustomReminderUnit] = useState<1 | 60 | 1440>(60);
  // Повторение: null — не повторяется
  const [recurrence, setRecurrence] = useState<TaskRecurrence | null>(task.recurrence ?? null);
  const [assignee, setAssignee] = useState<TaskAssignee>(task.assignee ?? 'me');
  // Персона-исполнитель ('' — обычный Claude); выбирается единым пикером исполнителя
  const [personaId, setPersonaId] = useState(task.personaId ?? '');
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

  // AI-хаб: если в правку вошли по действию из палитры/подсказки — сразу запускаем генерацию
  useEffect(() => {
    if (!pendingAi) return;
    if (pendingAi === 'task.description') void generateDescription();
    else if (pendingAi === 'task.subtasks') void generateSubtasks();
    onPendingConsumed?.();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [pendingAi]);

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
        // Без срока напоминание не имеет смысла; -1 = очистить на бэке
        reminderMinutes: dueDate && reminder !== null ? reminder : -1,
        // Повторение тоже требует срока; type 'none' = убрать на бэке
        recurrence: dueDate && recurrence ? recurrence : { type: 'none', interval: 1 },
        assignee,
        // Персона-исполнитель имеет смысл только у Claude; '' = убрать на бэке
        personaId: assignee === 'claude' && personaId ? personaId : '',
        description,
        subtasks: subtasks.filter(s => s.title.trim()),
        labels,
      });
    } finally {
      setSaving(false);
    }
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
          <Check size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
          Готово
        </button>
        <IconButton size="md" tone="danger" onClick={onDelete} title="Удалить задачу">
          <Trash2 size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
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

          {/* Напоминание — только при заданном сроке */}
          {dueDate && (
            <>
              <div style={fieldLabelStyle()}>Напоминание</div>
              <div style={{ display: 'flex', flexWrap: 'wrap', gap: 8, marginBottom: 8 }}>
                <button
                  onClick={() => { setReminder(null); setCustomReminderOpen(false); }}
                  style={reminderChipStyle(reminder === null)}
                >
                  Без напоминания
                </button>
                {REMINDER_PRESETS.map(m => (
                  <button
                    key={m}
                    onClick={() => { setReminder(m); setCustomReminderOpen(false); }}
                    style={reminderChipStyle(reminder === m)}
                  >
                    {reminderLabel(m)}
                  </button>
                ))}
                <button
                  onClick={() => setCustomReminderOpen(v => !v)}
                  style={reminderChipStyle(
                    customReminderOpen || (reminder !== null && !REMINDER_PRESETS.includes(reminder as never)))}
                >
                  {reminder !== null && !REMINDER_PRESETS.includes(reminder as never)
                    ? reminderLabel(reminder)
                    : 'Свой…'}
                </button>
              </div>
              {customReminderOpen && (
                <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 8 }}>
                  <span style={{ fontFamily: FONT.sans, fontSize: 13, color: C.textSecondary }}>За</span>
                  <input
                    value={customReminderValue}
                    onChange={e => setCustomReminderValue(e.target.value.replace(/\D/g, '').slice(0, 3))}
                    inputMode="numeric"
                    style={{
                      width: 52, boxSizing: 'border-box', padding: '7px 10px', textAlign: 'center',
                      border: `1px solid ${C.border}`, borderRadius: R.lg, outline: 'none',
                      background: C.bgWhite, fontFamily: FONT.sans, fontSize: 13, color: C.textPrimary,
                    }}
                  />
                  {([[1, 'мин'], [60, 'ч'], [1440, 'дн']] as const).map(([mult, label]) => (
                    <button
                      key={mult}
                      onClick={() => setCustomReminderUnit(mult)}
                      style={reminderChipStyle(customReminderUnit === mult)}
                    >
                      {label}
                    </button>
                  ))}
                  <button
                    onClick={() => {
                      const n = parseInt(customReminderValue, 10);
                      if (!n) return;
                      setReminder(n * customReminderUnit);
                      setCustomReminderOpen(false);
                    }}
                    style={{
                      border: 'none', background: 'none', cursor: 'pointer', padding: 0,
                      fontFamily: FONT.sans, fontSize: 13, fontWeight: 600, color: C.accent,
                    }}
                  >
                    Применить
                  </button>
                </div>
              )}
              <div style={{ marginBottom: 14 }} />
            </>
          )}

          {/* Повторение — только при заданном сроке */}
          {dueDate && (
            <>
              <div style={fieldLabelStyle()}>Повторение</div>
              <div style={{ display: 'flex', flexWrap: 'wrap', gap: 8, marginBottom: 8 }}>
                <button
                  onClick={() => setRecurrence(null)}
                  style={reminderChipStyle(recurrence === null)}
                >
                  Нет
                </button>
                {(Object.keys(RECURRENCE_TYPE_LABEL) as Exclude<TaskRecurrenceType, 'none'>[]).map(t => (
                  <button
                    key={t}
                    onClick={() => setRecurrence(prev => ({
                      type: t,
                      interval: prev?.interval ?? 1,
                      weekdays: t === 'weekly' ? prev?.weekdays : undefined,
                      until: prev?.until,
                    }))}
                    style={reminderChipStyle(recurrence?.type === t)}
                  >
                    {RECURRENCE_TYPE_LABEL[t]}
                  </button>
                ))}
              </div>
              {recurrence && recurrence.type === 'weekly' && (
                <div style={{ display: 'flex', gap: 6, marginBottom: 8 }}>
                  {(['Пн', 'Вт', 'Ср', 'Чт', 'Пт', 'Сб', 'Вс'] as const).map((label, i) => {
                    const day = i + 1;
                    const active = recurrence.weekdays?.includes(day) ?? false;
                    return (
                      <button
                        key={day}
                        onClick={() => setRecurrence(prev => prev && ({
                          ...prev,
                          weekdays: active
                            ? prev.weekdays?.filter(d => d !== day)
                            : [...(prev.weekdays ?? []), day],
                        }))}
                        style={{
                          ...reminderChipStyle(active),
                          padding: '6px 0', flex: 1, justifyContent: 'center', fontSize: 12,
                        }}
                      >
                        {label}
                      </button>
                    );
                  })}
                </div>
              )}
              {recurrence && (
                <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 8, flexWrap: 'wrap' }}>
                  <span style={{ fontFamily: FONT.sans, fontSize: 13, color: C.textSecondary }}>Каждые</span>
                  <input
                    value={String(recurrence.interval)}
                    onChange={e => {
                      const n = parseInt(e.target.value.replace(/\D/g, '').slice(0, 2), 10);
                      setRecurrence(prev => prev && ({ ...prev, interval: n || 1 }));
                    }}
                    inputMode="numeric"
                    style={{
                      width: 44, boxSizing: 'border-box', padding: '7px 10px', textAlign: 'center',
                      border: `1px solid ${C.border}`, borderRadius: R.lg, outline: 'none',
                      background: C.bgWhite, fontFamily: FONT.sans, fontSize: 13, color: C.textPrimary,
                    }}
                  />
                  <span style={{ fontFamily: FONT.sans, fontSize: 13, color: C.textSecondary }}>
                    {{ daily: 'дн', weekly: 'нед', monthly: 'мес', yearly: 'г', none: '' }[recurrence.type]}
                    {' · до'}
                  </span>
                  <input
                    type="date"
                    value={recurrence.until ?? ''}
                    onChange={e => setRecurrence(prev => prev && ({ ...prev, until: e.target.value || undefined }))}
                    style={{
                      boxSizing: 'border-box', padding: '6px 10px',
                      border: `1px solid ${C.border}`, borderRadius: R.lg, outline: 'none',
                      background: C.bgWhite, fontFamily: FONT.sans, fontSize: 13, color: C.textPrimary,
                    }}
                  />
                  {recurrence.until && (
                    <button
                      onClick={() => setRecurrence(prev => prev && ({ ...prev, until: undefined }))}
                      style={{
                        border: 'none', background: 'none', cursor: 'pointer', padding: 0,
                        fontFamily: FONT.sans, fontSize: 13, fontWeight: 600, color: C.accent,
                      }}
                    >
                      Бессрочно
                    </button>
                  )}
                </div>
              )}
              <div style={{ marginBottom: 14 }} />
            </>
          )}

          {/* Исполнитель — единый пикер (Я / Claude / персона) */}
          <div style={fieldLabelStyle()}>Исполнитель</div>
          <div style={{ marginBottom: 22 }}>
            <ExecutorPicker
              assignee={assignee}
              personaId={personaId || null}
              projectId={task.projectId ?? null}
              onChange={v => { setAssignee(v.assignee); setPersonaId(v.personaId ?? ''); }}
            />
          </div>

          {/* Описание */}
          <div style={{ display: 'flex', alignItems: 'center', gap: 10, marginBottom: 9 }}>
            <div style={{ ...fieldLabelStyle(), marginBottom: 0 }}>Описание</div>
            {/* Генерация описания (AI) — через AI-палитру (⌘/Ctrl+K) */}
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
                  <Check size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
                  Просмотр
                </>
              ) : (
                <>
                  <SquarePen size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
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
            {/* Генерация подзадач (AI) — через AI-палитру (⌘/Ctrl+K) */}
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
                  aria-label="Удалить подзадачу"
                  title="Удалить подзадачу"
                  style={{
                    border: 'none', background: 'none', cursor: 'pointer', padding: 4,
                    color: C.textMuted, lineHeight: 1, fontFamily: FONT.sans, display: 'flex', alignItems: 'center',
                  }}
                >
                  <X size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
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
                <Plus size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
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
                    style={{ cursor: 'pointer', color: C.textMuted, lineHeight: 1, display: 'flex', alignItems: 'center' }}
                  >
                    <X size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
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
