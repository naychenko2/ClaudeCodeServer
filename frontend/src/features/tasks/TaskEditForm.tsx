// Редактирование задачи — инлайн-экран вместо деталей (как в макете):
// шапка «Редактирование задачи» с Отмена / ✓ Готово / корзина, ниже поля формы.

import { useEffect, useMemo, useState } from 'react';
import { Check, FilePlus2, Plus, SquarePen, Trash2, X } from 'lucide-react';
import type { Project, Task, TaskAssignee, TaskPriority, TaskRecurrence, TaskRecurrenceType, TaskSubtask, UpdateTaskDto } from '../../types';
import { C, FONT, R } from '../../lib/design';
import { IconButton } from '../../components/ui';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { Toolbar } from '../../components/Toolbar';
import { MarkdownViewer } from '../../components/MarkdownViewer';
import { api } from '../../lib/api';
import { NO_PROJECT_COLOR, NO_PROJECT_LABEL, PRIORITY_LABEL, PRIORITY_ORDER, RECURRENCE_TYPE_LABEL, REMINDER_PRESETS, projectColor, reminderLabel } from '../../lib/tasks';
import { ExtBadge, PriorityFlag, SubtaskCheck } from './bits';
import { DueDatePicker } from './DueDatePicker';
import { ExecutorPicker } from './ExecutorPicker';
import { NoteEditor } from '../notes/NoteEditor';
import { AttachPicker } from '../../components/chat/AttachPicker';
import { Toggle, SegmentedControl, WaitingIndicator } from '../../components/ui';
import { EXPIRY_PRESETS, DEFAULT_EXPIRY } from '../../lib/expiry';
import { useAiJob, runAiJob, resetAiJob } from '../../lib/aiJobStore';

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
  // Время жизни чата исполнения (актуально только при исполнителе Claude/персона)
  const [ttlEnabled, setTtlEnabled] = useState(task.executionExpiresAfterMinutes != null);
  const [ttlMinutes, setTtlMinutes] = useState(task.executionExpiresAfterMinutes ?? DEFAULT_EXPIRY);
  const [description, setDescription] = useState(task.description);
  const [descEditing, setDescEditing] = useState(!task.description);
  // Markdown-итог выполнения (прикрепляет исполнитель; тут — ручная правка пользователем)
  const [result, setResult] = useState(task.resultMarkdown ?? '');
  const [resultEditing, setResultEditing] = useState(false);
  // Ссылки на файлы проекта (только у проектных задач — есть где брать пути)
  const [files, setFiles] = useState<string[]>(task.linkedFiles);
  const [filePickerOpen, setFilePickerOpen] = useState(false);
  // Смена проекта задачи: null = «Личное» (вне проекта)
  const [projects, setProjects] = useState<Project[]>([]);
  const [projectId, setProjectId] = useState<string | null>(task.projectId ?? null);
  useEffect(() => { api.projects.list().then(setProjects).catch(() => {}); }, []);
  // Текущий проект первым в ленте чипов (как в NewTaskDialog)
  const orderedProjects = useMemo(() => {
    const cur = task.projectId;
    if (!cur) return projects;
    const def = projects.find(p => p.id === cur);
    return def ? [def, ...projects.filter(p => p.id !== cur)] : projects;
  }, [projects, task.projectId]);
  const [subtasks, setSubtasks] = useState<TaskSubtask[]>(task.subtasks);
  const [newSubtask, setNewSubtask] = useState('');
  const [labels, setLabels] = useState<string[]>(task.labels);
  const [newLabel, setNewLabel] = useState('');
  const [saving, setSaving] = useState(false);
  // Статус/результат AI-генерации — в aiJobStore по ключу задачи (переживает закрытие
  // модалки правки до ответа: вернувшись к этой же задаче, увидите прогресс/результат).
  const descKey = `tasks:${task.id}:ai-description`;
  const descJob = useAiJob<string>(descKey);
  const aiDescLoading = descJob.status === 'running';
  const subsKey = `tasks:${task.id}:ai-subtasks`;
  const subsJob = useAiJob<string[]>(subsKey);
  const aiSubsLoading = subsJob.status === 'running';
  const [aiError, setAiError] = useState<string | null>(null);

  // Описание от Claude: по названию + контекст проекта (личная — только название)
  const generateDescription = () => {
    if (!title.trim() || aiDescLoading) return;
    setAiError(null);
    runAiJob(descKey, () => api.tasks.aiDescription(title.trim(), task.projectId ?? null).then(r => r.description));
  };

  // Подзадачи от Claude: по названию и заполненному описанию
  const generateSubtasks = () => {
    if (!title.trim() || !description.trim() || aiSubsLoading) return;
    setAiError(null);
    runAiJob(subsKey, () => api.tasks.aiSubtasks(title.trim(), description, task.projectId ?? null).then(r => r.subtasks));
  };

  useEffect(() => {
    if (descJob.status === 'done' && descJob.result != null) {
      setDescription(descJob.result);
      setDescEditing(false);   // сразу показываем результат в превью
      resetAiJob(descKey);
    } else if (descJob.status === 'error') {
      setAiError(descJob.error ?? 'Не удалось сгенерировать описание');
      resetAiJob(descKey);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [descJob.status]);

  useEffect(() => {
    if (subsJob.status === 'done' && subsJob.result != null) {
      const existing = new Set(subtasks.map(s => s.title.toLowerCase()));
      const fresh = subsJob.result.filter(t => !existing.has(t.toLowerCase()));
      setSubtasks(prev => [...prev, ...fresh.map(t => ({ id: '', title: t, isDone: false }))]);
      resetAiJob(subsKey);
    } else if (subsJob.status === 'error') {
      setAiError(subsJob.error ?? 'Не удалось сгенерировать подзадачи');
      resetAiJob(subsKey);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [subsJob.status]);

  // AI-хаб: если в правку вошли по действию из палитры/подсказки — сразу запускаем генерацию
  useEffect(() => {
    if (!pendingAi) return;
    if (pendingAi === 'task.description') generateDescription();
    else if (pendingAi === 'task.subtasks') generateSubtasks();
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
    setAiError(null);
    try {
      const projectChanged = (task.projectId ?? null) !== projectId;
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
        // Время жизни чата исполнения; отрицательное = бессрочно на бэке
        executionExpiresAfterMinutes: ttlEnabled ? ttlMinutes : -1,
        // Смена проекта: '' = сделать личной, guid = привязать; отсутствие = не менять
        ...(projectChanged ? { projectId: projectId ?? '' } : {}),
        description,
        resultMarkdown: result,
        // linkedFiles имеет смысл только у целевой проектной задачи
        ...(projectId ? { linkedFiles: files } : {}),
        subtasks: subtasks.filter(s => s.title.trim()),
        labels,
      });
    } catch (e) {
      setAiError(e instanceof Error ? e.message : 'Не удалось сохранить задачу');
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

          {/* Исполнитель — единый пикер (Я / Claude / персона). projectId = целевой
              проект (state), чтобы список персон обновлялся при смене проекта ниже */}
          <div style={fieldLabelStyle()}>Исполнитель</div>
          <div style={{ marginBottom: 22 }}>
            <ExecutorPicker
              assignee={assignee}
              personaId={personaId || null}
              projectId={projectId}
              onChange={v => { setAssignee(v.assignee); setPersonaId(v.personaId ?? ''); }}
            />
          </div>

          {/* Время жизни чата исполнения — только когда исполнитель не «Я» (чат вообще
              не создаётся для personaId=none/assignee=me) */}
          {assignee === 'claude' && (
            <>
              <div style={fieldLabelStyle()}>Время жизни чата исполнения</div>
              <div style={{ marginBottom: 22 }}>
                <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
                  <Toggle checked={ttlEnabled} onChange={setTtlEnabled} />
                  <span style={{ fontSize: 13, color: C.textSecondary, fontFamily: FONT.sans }}>
                    {ttlEnabled ? 'Удаляется автоматически' : 'Хранится бессрочно'}
                  </span>
                </div>
                {ttlEnabled && (
                  <div style={{ marginTop: 10 }}>
                    <SegmentedControl
                      value={String(ttlMinutes)}
                      options={EXPIRY_PRESETS.map(p => ({ value: String(p.minutes), label: p.label }))}
                      onChange={v => setTtlMinutes(Number(v))}
                      columns={4}
                    />
                  </div>
                )}
              </div>
            </>
          )}

          {/* Проект — лента чипов (как в диалоге создания); «Личное» = вне проекта.
              Смена проекта сбрасывает колонку доски (на бэке) и может требовать смены
              проектной персоны-исполнителя — валидируется при сохранении. */}
          <div style={fieldLabelStyle()}>Проект</div>
          <div className="cc-hide-scrollbar" style={{ display: 'flex', gap: 8, overflowX: 'auto', paddingBottom: 2, marginBottom: 22 }}>
            <button
              onClick={() => setProjectId(null)}
              style={{
                display: 'inline-flex', alignItems: 'center', gap: 7, flexShrink: 0,
                padding: '7px 13px', cursor: 'pointer',
                border: `1px solid ${projectId === null ? C.accent : C.border}`,
                borderRadius: 999,
                background: projectId === null ? C.accentLight : C.bgWhite,
                fontFamily: FONT.sans, fontSize: 13, fontWeight: projectId === null ? 600 : 500,
                color: C.textPrimary, transition: 'border-color 0.12s, background 0.12s',
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
                  onClick={() => setProjectId(p.id)}
                  style={{
                    display: 'inline-flex', alignItems: 'center', gap: 7, flexShrink: 0,
                    padding: '7px 13px', cursor: 'pointer',
                    border: `1px solid ${active ? C.accent : C.border}`,
                    borderRadius: 999,
                    background: active ? C.accentLight : C.bgWhite,
                    fontFamily: FONT.sans, fontSize: 13, fontWeight: active ? 600 : 500,
                    color: C.textPrimary, transition: 'border-color 0.12s, background 0.12s',
                  }}
                >
                  <span style={{ width: 8, height: 8, borderRadius: '50%', background: color.main, flexShrink: 0 }} />
                  {p.name}
                </button>
              );
            })}
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
            {aiDescLoading ? (
              <div style={{
                background: C.bgWhite, border: `1px solid ${C.borderLight}`,
                borderRadius: R.xl, padding: '14px 18px',
              }}>
                <WaitingIndicator hint="Генерирую описание по названию задачи" />
              </div>
            ) : descEditing ? (
              <NoteEditor
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

          {/* Результат — Markdown-итог выполнения (обычно прикрепляет исполнитель; тут можно поправить) */}
          <div style={{ display: 'flex', alignItems: 'center', gap: 10, marginBottom: 9 }}>
            <div style={{ ...fieldLabelStyle(), marginBottom: 0 }}>Результат</div>
            <div style={{ flex: 1 }} />
            <button
              onClick={() => setResultEditing(v => !v)}
              style={{
                display: 'inline-flex', alignItems: 'center', gap: 5,
                border: 'none', background: 'none', cursor: 'pointer', padding: 0,
                fontFamily: FONT.sans, fontSize: 12.5, fontWeight: 600, color: C.accent,
              }}
            >
              {resultEditing ? (
                <>
                  <Check size={12} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />
                  Просмотр
                </>
              ) : (
                <>
                  <SquarePen size={12} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />
                  Редактировать
                </>
              )}
            </button>
          </div>
          <div style={{ marginBottom: 22 }}>
            {resultEditing ? (
              <NoteEditor
                value={result}
                onChange={setResult}
                placeholder="Итог выполнения (markdown)…"
                minHeight={160}
              />
            ) : (
              <div style={{
                background: C.bgWhite, border: `1px solid ${C.borderLight}`,
                borderRadius: R.xl, padding: '14px 18px', fontSize: 14,
              }}>
                {result
                  ? <MarkdownViewer content={result} />
                  : <span style={{ fontFamily: FONT.sans, fontSize: 13, color: C.textMuted }}>Итога ещё нет</span>}
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
          {aiSubsLoading && (
            <div style={{
              background: C.bgWhite, border: `1px solid ${C.borderLight}`,
              borderRadius: R.xl, padding: '14px 18px', marginBottom: 12,
            }}>
              <WaitingIndicator hint="Предлагаю подзадачи по описанию" />
            </div>
          )}
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

          {/* Файлы проекта — только у проектных задач (у личных нет файлового контекста) */}
          {task.projectId && (
            <>
              <div style={fieldLabelStyle()}>Файлы</div>
              {files.length > 0 && (
                <div style={{ display: 'flex', flexDirection: 'column', gap: 7, marginBottom: 10 }}>
                  {files.map(f => (
                    <span
                      key={f}
                      style={{
                        display: 'flex', alignItems: 'center', gap: 10,
                        padding: '9px 12px', background: C.bgWhite,
                        border: `1px solid ${C.borderLight}`, borderRadius: R.xl,
                      }}
                    >
                      <ExtBadge filename={f} />
                      <span style={{
                        flex: 1, fontFamily: FONT.mono, fontSize: 13, color: C.textPrimary,
                        overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
                      }}>
                        {f}
                      </span>
                      <button
                        onClick={() => setFiles(prev => prev.filter(x => x !== f))}
                        title="Убрать файл"
                        style={{
                          border: 'none', background: 'none', cursor: 'pointer', padding: 4,
                          color: C.textMuted, fontSize: 15, lineHeight: 1, fontFamily: FONT.sans,
                          display: 'flex', alignItems: 'center',
                        }}
                      >
                        <X size={13} strokeWidth={ICON_STROKE} />
                      </button>
                    </span>
                  ))}
                </div>
              )}
              <div style={{ marginBottom: 22 }}>
                <button
                  onClick={() => setFilePickerOpen(true)}
                  style={{
                    display: 'inline-flex', alignItems: 'center', gap: 7,
                    padding: '9px 14px', cursor: 'pointer',
                    border: `1px solid ${C.border}`, borderRadius: R.lg,
                    background: C.bgWhite, color: C.textPrimary,
                    fontFamily: FONT.sans, fontSize: 13, fontWeight: 600,
                  }}
                >
                  <FilePlus2 size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
                  {files.length > 0 ? 'Добавить ещё файл' : 'Добавить файл'}
                </button>
              </div>
            </>
          )}
        </div>
      </div>
      {filePickerOpen && task.projectId && (
        <AttachPicker
          projectId={task.projectId}
          selected={files}
          onToggle={p => setFiles(prev => prev.includes(p) ? prev.filter(x => x !== p) : [...prev, p])}
          onClose={() => setFilePickerOpen(false)}
        />
      )}
    </div>
  );
}
