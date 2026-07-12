// Детали задачи: центральная колонка (чипы, markdown-описание, подзадачи,
// метки, связанная сессия, файлы) + колонка «Статус» справа (десктоп/планшет)
// или закреплённый сегмент статуса снизу (мобила). Режим редактирования —
// инлайн, вместо деталей (TaskEditForm).

import { useEffect, useMemo, useState } from 'react';
import type { Project, Session, Task, TaskStatus, UpdateTaskDto } from '../../types';
import { C, FONT, R, SHADOW } from '../../lib/design';
import { Button, IconButton, Modal, BackButton } from '../../components/ui';
import { Toolbar } from '../../components/Toolbar';
import { MarkdownViewer } from '../../components/MarkdownViewer';
import { api } from '../../lib/api';
import {
  NO_PROJECT_COLOR, NO_PROJECT_LABEL, PRIORITY_LABEL, STATUS_DOT, STATUS_LABEL,
  deleteTask, dueLabel, projectColor, projectInitial, recurrenceLabel, reminderLabel, updateTask,
} from '../../lib/tasks';
import {
  ClaudeBadge, DueChip, ExtBadge, LabelChip, MeBadge,
  PriorityFlag, SectionLabel, SubtaskCheck,
} from './bits';
import { TaskEditForm } from './TaskEditForm';
import { TaskPersonaBadge } from './TaskPersonaBadge';

interface Props {
  task: Task;
  // null/undefined — личная задача (вне проекта): секции сессии/файлов скрыты
  project?: Project | null;
  isMobile?: boolean;
  // Открыть сразу в режиме редактирования (свежесозданная задача).
  // Работает через key={task.id} у родителя — состояние инициализируется при монтировании.
  startInEdit?: boolean;
  onBack?: () => void;                          // мобила: ‹ назад
  onClose?: () => void;                         // модальный режим (личная задача из календаря): ✕
  onOpenSession?: (sessionId: string) => void;  // переход в связанный диалог
  onOpenFile?: (path: string) => void;
  onDeleted: () => void;
}

const STATUS_SEQUENCE: TaskStatus[] = ['todo', 'inProgress', 'done'];

// Чип в ряду под заголовком (белая пилюля с рамкой)
function HeaderChip({ children, urgent }: { children: React.ReactNode; urgent?: boolean }) {
  return (
    <span style={{
      display: 'inline-flex', alignItems: 'center', gap: 6,
      padding: '5px 11px', borderRadius: 999,
      border: `1px solid ${urgent ? C.dangerBorder : C.border}`,
      background: urgent ? C.dangerBg : C.bgWhite,
      fontFamily: FONT.sans, fontSize: 12.5, fontWeight: 600,
      color: urgent ? C.danger : C.textPrimary, whiteSpace: 'nowrap',
    }}>
      {children}
    </span>
  );
}

export function TaskDetailsPane({ task, project, isMobile, startInEdit, onBack, onClose, onOpenSession, onOpenFile, onDeleted }: Props) {
  const [editing, setEditing] = useState(!!startInEdit);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [deleting, setDeleting] = useState(false);
  const [sessions, setSessions] = useState<Session[]>([]);
  const [executing, setExecuting] = useState(false);
  const [execError, setExecError] = useState<string | null>(null);
  // AI-хаб: отложенное AI-действие, которое форма правки выполнит при монтировании
  const [pendingAi, setPendingAi] = useState<'task.description' | 'task.subtasks' | null>(null);

  // Имя связанной сессии (только у проектных задач)
  useEffect(() => {
    if (!task.linkedSessionId || !project) return;
    api.sessions.list(project.id).then(setSessions).catch(() => {});
  }, [project, task.linkedSessionId]);

  const linkedSession = useMemo(
    () => sessions.find(s => s.id === task.linkedSessionId) ?? null,
    [sessions, task.linkedSessionId],
  );

  const color = projectColor(project ? project.id : null);
  const doneSubs = task.subtasks.filter(s => s.isDone).length;

  const setStatus = (status: TaskStatus) => { void updateTask(task.id, { status }); };

  const toggleSubtask = (subtaskId: string) => {
    void updateTask(task.id, {
      subtasks: task.subtasks.map(s => s.id === subtaskId ? { ...s, isDone: !s.isDone } : s),
    });
  };

  const handleSave = async (dto: UpdateTaskDto) => {
    await updateTask(task.id, dto);
    setEditing(false);
  };

  const handleDelete = async () => {
    setDeleting(true);
    try {
      await deleteTask(task.id);
      onDeleted();
    } finally {
      setDeleting(false);
      setConfirmDelete(false);
    }
  };

  // Блок «название + мета» в шапке — как в тулбаре чата (название чата + модель/режим)
  const headerTitleBlock = (
    <div style={{ minWidth: 0, flex: 1 }}>
      <div style={{
        fontSize: 17, fontWeight: 600, color: C.textHeading,
        overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
        textDecoration: task.status === 'done' ? 'line-through' : 'none',
      }}>
        {task.title}
      </div>
      <div style={{
        fontFamily: FONT.mono, fontSize: 12, color: C.textMuted, marginTop: 2,
        overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
      }}>
        {project ? project.name : NO_PROJECT_LABEL} · {STATUS_LABEL[task.status]} · {PRIORITY_LABEL[task.priority]}
        {task.dueDate && ` · ${dueLabel(task.dueDate)}${task.dueTime ? ` ${task.dueTime}` : ''}`}
      </div>
    </div>
  );

  // Запуск Claude-исполнителя: сессия создаётся на бэке, стор обновится по task_changed
  const handleExecute = async () => {
    if (executing) return;
    setExecuting(true);
    setExecError(null);
    try {
      await api.tasks.execute(task.id);
    } catch (e) {
      setExecError(e instanceof Error ? e.message : 'Не удалось запустить выполнение');
    } finally {
      setExecuting(false);
    }
  };

  // Живая сессия по задаче уже идёт — не показываем кнопку повторного запуска
  const claudeRunning = !!task.claudeStartedAt && !task.claudeResult && task.status !== 'done';

  // AI-хаб: контекстные действия задачи из палитры/подсказки. «Выполнить» — тот же
  // обработчик, что и кнопка (со стейтом «Запуск…»); генерация — вход в правку + авто-запуск.
  useEffect(() => {
    const onRun = (e: Event) => {
      const action = (e as CustomEvent<{ action?: string }>).detail?.action;
      if (action === 'task.execute') {
        if (task.assignee === 'claude' && task.status !== 'done' && !claudeRunning) void handleExecute();
      } else if (action === 'task.description' || action === 'task.subtasks') {
        setPendingAi(action);
        setEditing(true);
      }
    };
    window.addEventListener('cc-ai-run', onRun);
    return () => window.removeEventListener('cc-ai-run', onRun);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [task.id, task.assignee, task.status, claudeRunning, executing]);
  const executeButton = task.assignee === 'claude' && task.status !== 'done' && !claudeRunning && (
    <button
      onClick={handleExecute}
      disabled={executing}
      title="Создать чат и поручить задачу Claude"
      style={{
        display: 'flex', alignItems: 'center', gap: 6, flexShrink: 0,
        padding: '0 14px', height: 32, cursor: executing ? 'default' : 'pointer',
        border: 'none', borderRadius: R.md,
        background: C.accent, color: C.onAccent,
        fontFamily: FONT.sans, fontSize: 13, fontWeight: 600,
        opacity: executing ? 0.6 : 1,
      }}
    >
      {executing
        ? <span className="tool-spinner" style={{ width: 12, height: 12, flexShrink: 0 }} />
        : (
          <svg width="12" height="12" viewBox="0 0 24 24" fill="currentColor" stroke="none">
            <polygon points="6 3 20 12 6 21" />
          </svg>
        )}
      {executing ? 'Запуск…' : 'Выполнить с Claude'}
    </button>
  );

  const editButton = (
    <button
      onClick={() => setEditing(true)}
      style={{
        display: 'flex', alignItems: 'center', gap: 6, flexShrink: 0,
        padding: '0 14px', height: 32, cursor: 'pointer',
        border: `1px solid ${C.border}`, borderRadius: R.md,
        background: C.bgCard, color: C.textPrimary,
        fontFamily: FONT.sans, fontSize: 13, fontWeight: 600,
      }}
    >
      <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
        <path d="M17 3a2.85 2.83 0 1 1 4 4L7.5 20.5 2 22l1.5-5.5Z" />
      </svg>
      Изменить
    </button>
  );

  const deleteConfirmModal = confirmDelete && (
    <Modal
      title="Удалить задачу?"
      subtitle={`«${task.title}» будет удалена безвозвратно.`}
      width={380}
      onClose={() => setConfirmDelete(false)}
      footer={
        <>
          <Button variant="secondary" onClick={() => setConfirmDelete(false)}>Отмена</Button>
          <Button variant="danger" loading={deleting} onClick={handleDelete}>Удалить</Button>
        </>
      }
    />
  );

  if (editing) {
    return (
      <>
        <TaskEditForm
          task={task}
          isMobile={isMobile}
          onSave={handleSave}
          onCancel={() => { setEditing(false); setPendingAi(null); }}
          onDelete={() => setConfirmDelete(true)}
          pendingAi={pendingAi}
          onPendingConsumed={() => setPendingAi(null)}
        />
        {deleteConfirmModal}
      </>
    );
  }

  // Кнопка статуса в правой колонке (десктоп)
  const statusRailButton = (s: TaskStatus) => {
    const active = task.status === s;
    return (
      <button
        key={s}
        onClick={() => setStatus(s)}
        style={{
          display: 'flex', alignItems: 'center', gap: 9,
          width: '100%', boxSizing: 'border-box',
          padding: '12px 15px', cursor: 'pointer',
          border: `1px solid ${active ? C.accent : C.border}`,
          borderRadius: R.xl,
          background: active ? C.accent : C.bgWhite,
          color: active ? C.onAccent : C.textPrimary,
          fontFamily: FONT.sans, fontSize: 13.5, fontWeight: 600,
          boxShadow: active ? SHADOW.button : 'none',
          transition: 'background 0.12s, border-color 0.12s',
        }}
      >
        {!active && <span style={{ width: 8, height: 8, borderRadius: '50%', background: STATUS_DOT[s], flexShrink: 0 }} />}
        {STATUS_LABEL[s]}
      </button>
    );
  };

  const content = (
    <div style={{ maxWidth: 680, padding: isMobile ? '16px 16px 32px' : '20px 32px 40px', boxSizing: 'border-box' }}>
      {/* Чип проекта (у личной задачи — нейтральная точка + «Личное») */}
      <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 12 }}>
        {project ? (
          <div style={{
            width: 22, height: 22, borderRadius: 7, flexShrink: 0,
            background: color.soft, color: color.main,
            fontFamily: FONT.sans, fontSize: 11, fontWeight: 700,
            display: 'flex', alignItems: 'center', justifyContent: 'center',
          }}>
            {projectInitial(project.name)}
          </div>
        ) : (
          <span style={{ width: 8, height: 8, borderRadius: '50%', background: NO_PROJECT_COLOR.main, flexShrink: 0, marginLeft: 4 }} />
        )}
        <span style={{ fontFamily: FONT.sans, fontSize: 13, fontWeight: 600, color: C.textSecondary }}>
          {project ? project.name : NO_PROJECT_LABEL}
        </span>
      </div>

      {/* Заголовок */}
      <h1 style={{
        margin: '0 0 14px',
        fontFamily: FONT.serif, fontSize: isMobile ? 22 : 26, fontWeight: 500,
        color: C.textHeading, lineHeight: 1.25,
        textDecoration: task.status === 'done' ? 'line-through' : 'none',
        opacity: task.status === 'done' ? 0.65 : 1,
      }}>
        {task.title}
      </h1>

      {execError && (
        <div style={{ fontFamily: FONT.sans, fontSize: 12.5, color: C.danger, marginBottom: 12 }}>
          {execError}
        </div>
      )}

      {/* Claude сейчас работает над задачей */}
      {claudeRunning && (
        <div style={{
          display: 'inline-flex', alignItems: 'center', gap: 8, marginBottom: 12,
          padding: '6px 12px', borderRadius: 999,
          border: `1px solid ${C.accentMuted}`, background: C.accentLight,
          fontFamily: FONT.sans, fontSize: 12.5, fontWeight: 600, color: C.accent,
        }}>
          <span className="tool-spinner" style={{ width: 11, height: 11, flexShrink: 0 }} />
          Claude работает над задачей
        </div>
      )}

      {/* Ряд чипов */}
      <div style={{ display: 'flex', flexWrap: 'wrap', gap: 8, marginBottom: 22 }}>
        <HeaderChip>
          <span style={{ width: 8, height: 8, borderRadius: '50%', background: STATUS_DOT[task.status], flexShrink: 0 }} />
          {STATUS_LABEL[task.status]}
        </HeaderChip>
        <HeaderChip>
          <PriorityFlag priority={task.priority} size={12} />
          {PRIORITY_LABEL[task.priority]}
        </HeaderChip>
        {task.dueDate && <DueChip task={task} withTime fontSize={12.5} />}
        {task.dueDate && task.reminderMinutes != null && (
          <HeaderChip>
            <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke={C.warning} strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" style={{ flexShrink: 0 }}>
              <path d="M6 8a6 6 0 0 1 12 0c0 7 3 9 3 9H3s3-2 3-9" />
              <path d="M10.3 21a1.94 1.94 0 0 0 3.4 0" />
            </svg>
            {reminderLabel(task.reminderMinutes)}
          </HeaderChip>
        )}
        {task.recurrence && (
          <HeaderChip>
            <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke={C.info} strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" style={{ flexShrink: 0 }}>
              <path d="M17 1l4 4-4 4" />
              <path d="M3 11V9a4 4 0 0 1 4-4h14" />
              <path d="M7 23l-4-4 4-4" />
              <path d="M21 13v2a4 4 0 0 1-4 4H3" />
            </svg>
            {recurrenceLabel(task.recurrence)}
          </HeaderChip>
        )}
        {task.assignee && (
          <HeaderChip>
            {task.assignee === 'claude' ? <ClaudeBadge size={17} /> : <MeBadge size={17} />}
            {task.assignee === 'claude' ? 'Claude' : 'Я'}
          </HeaderChip>
        )}
        {/* Персона-исполнитель: от чьего лица работает Claude */}
        {task.personaId && (
          <HeaderChip>
            <TaskPersonaBadge personaId={task.personaId} size={17} />
          </HeaderChip>
        )}
      </div>

      {/* Описание */}
      {task.description && (
        <div style={{ marginBottom: 26 }}>
          <SectionLabel style={{ marginBottom: 10 }}>Что нужно сделать</SectionLabel>
          <MarkdownViewer content={task.description} />
        </div>
      )}

      {/* Подзадачи */}
      {task.subtasks.length > 0 && (
        <div style={{ marginBottom: 26 }}>
          <div style={{ display: 'flex', alignItems: 'center', gap: 10, marginBottom: 10 }}>
            <SectionLabel>Подзадачи</SectionLabel>
            <span style={{ fontFamily: FONT.sans, fontSize: 12, color: C.textMuted, flexShrink: 0 }}>
              {doneSubs}/{task.subtasks.length}
            </span>
            <div style={{ flex: 1, height: 5, background: C.bgSelected, borderRadius: 3, overflow: 'hidden' }}>
              <div style={{
                height: '100%', width: `${(doneSubs / task.subtasks.length) * 100}%`,
                background: C.success, borderRadius: 3, transition: 'width 0.2s',
              }} />
            </div>
          </div>
          <div style={{ background: C.bgWhite, border: `1px solid ${C.borderLight}`, borderRadius: R.xl, overflow: 'hidden' }}>
            {task.subtasks.map((s, i) => (
              <div
                key={s.id}
                onClick={() => toggleSubtask(s.id)}
                style={{
                  display: 'flex', alignItems: 'center', gap: 11,
                  padding: '12px 15px', cursor: 'pointer',
                  borderBottom: i < task.subtasks.length - 1 ? `1px solid ${C.borderLight}` : 'none',
                }}
              >
                <SubtaskCheck done={s.isDone} />
                <span style={{
                  fontFamily: FONT.sans, fontSize: 14,
                  color: s.isDone ? C.textMuted : C.textPrimary,
                  textDecoration: s.isDone ? 'line-through' : 'none',
                }}>
                  {s.title}
                </span>
              </div>
            ))}
          </div>
        </div>
      )}

      {/* Результат — Markdown-итог выполнения (прикрепляет исполнитель) */}
      {task.resultMarkdown && (
        <div style={{ marginBottom: 26 }}>
          <SectionLabel style={{ marginBottom: 10 }}>Результат</SectionLabel>
          <div style={{
            background: C.bgWhite, border: `1px solid ${C.borderLight}`,
            borderRadius: R.xl, padding: '14px 18px',
          }}>
            <MarkdownViewer content={task.resultMarkdown} />
          </div>
        </div>
      )}

      {/* Метки */}
      {task.labels.length > 0 && (
        <div style={{ marginBottom: 26 }}>
          <SectionLabel style={{ marginBottom: 10 }}>Метки</SectionLabel>
          <div style={{ display: 'flex', flexWrap: 'wrap', gap: 7 }}>
            {task.labels.map(l => <LabelChip key={l} label={l} fontSize={12} />)}
          </div>
        </div>
      )}

      {/* Связанная сессия (только у проектных задач) */}
      {project && task.linkedSessionId && (
        <div style={{ marginBottom: 26 }}>
          <SectionLabel style={{ marginBottom: 10 }}>Связанная сессия</SectionLabel>
          <button
            onClick={() => onOpenSession?.(task.linkedSessionId!)}
            style={{
              display: 'flex', alignItems: 'center', gap: 11,
              width: '100%', boxSizing: 'border-box', textAlign: 'left',
              padding: '11px 14px', cursor: onOpenSession ? 'pointer' : 'default',
              background: C.bgWhite, border: `1px solid ${C.borderLight}`, borderRadius: R.xl,
            }}
          >
            <div style={{
              width: 34, height: 34, borderRadius: R.lg, flexShrink: 0,
              background: C.infoBg,
              display: 'flex', alignItems: 'center', justifyContent: 'center',
            }}>
              <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke={C.info} strokeWidth="2" strokeLinecap="round">
                <path d="M21 11.5a8.5 8.5 0 0 1-12 7.7L3 21l1.8-6A8.5 8.5 0 1 1 21 11.5z" />
              </svg>
            </div>
            <div style={{ flex: 1, minWidth: 0 }}>
              <div style={{ fontFamily: FONT.sans, fontSize: 13.5, fontWeight: 700, color: C.textPrimary, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                {linkedSession?.name || 'Диалог'}
              </div>
              <div style={{ fontFamily: FONT.sans, fontSize: 12, color: C.textMuted }}>Открыть диалог</div>
            </div>
            <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke={C.textMuted} strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" style={{ flexShrink: 0 }}>
              <polyline points="9 18 15 12 9 6" />
            </svg>
          </button>
        </div>
      )}

      {/* Файлы (только у проектных задач) */}
      {project && task.linkedFiles.length > 0 && (
        <div style={{ marginBottom: 26 }}>
          <SectionLabel style={{ marginBottom: 10 }}>Файлы</SectionLabel>
          <div style={{ display: 'flex', flexDirection: 'column', gap: 7 }}>
            {task.linkedFiles.map(f => (
              <button
                key={f}
                onClick={() => onOpenFile?.(f)}
                style={{
                  display: 'flex', alignItems: 'center', gap: 10,
                  width: '100%', boxSizing: 'border-box', textAlign: 'left',
                  padding: '10px 13px', cursor: onOpenFile ? 'pointer' : 'default',
                  background: C.bgWhite, border: `1px solid ${C.borderLight}`, borderRadius: R.xl,
                }}
              >
                <ExtBadge filename={f} />
                <span style={{ fontFamily: FONT.mono, fontSize: 13, color: C.textPrimary, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                  {f}
                </span>
              </button>
            ))}
          </div>
        </div>
      )}
    </div>
  );

  // === Мобила: полноэкранно, статус-сегмент закреплён снизу ===
  if (isMobile) {
    return (
      <div style={{ display: 'flex', flexDirection: 'column', height: '100%', overflow: 'hidden', background: C.bgMain }}>
        {/* Шапка — как тулбар чата: назад + название/мета + изменить */}
        <Toolbar isMobile>
          <BackButton onClick={onBack ?? (() => {})} style={{ flex: 1, minWidth: 0 }} title="Назад к списку">
            {headerTitleBlock}
          </BackButton>
          {executeButton}
          {editButton}
        </Toolbar>

        <div style={{ flex: 1, overflowY: 'auto' }}>{content}</div>

        {/* Статус — закреплён снизу */}
        <div style={{
          flexShrink: 0, padding: '10px 16px',
          paddingBottom: 'calc(10px + env(safe-area-inset-bottom))',
          borderTop: `1px solid ${C.divider}`, background: C.bgMain,
        }}>
          <SectionLabel style={{ marginBottom: 8 }}>Статус задачи</SectionLabel>
          <div style={{ display: 'flex', gap: 3, background: C.bgSelected, borderRadius: R.lg, padding: 3 }}>
            {STATUS_SEQUENCE.map(s => {
              const active = task.status === s;
              return (
                <button
                  key={s}
                  onClick={() => setStatus(s)}
                  style={{
                    flex: 1, display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 5,
                    padding: '9px 4px', border: 'none', borderRadius: R.lg - 2, cursor: 'pointer',
                    background: active ? C.accent : 'transparent',
                    color: active ? C.onAccent : C.textSecondary,
                    fontFamily: FONT.sans, fontSize: 12.5, fontWeight: 600, whiteSpace: 'nowrap',
                    transition: 'background 0.15s, color 0.15s',
                  }}
                >
                  {s === 'done' && (
                    <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.6" strokeLinecap="round" strokeLinejoin="round">
                      <polyline points="20 6 9 17 4 12" />
                    </svg>
                  )}
                  {STATUS_LABEL[s]}
                </button>
              );
            })}
          </div>
        </div>

        {deleteConfirmModal}
      </div>
    );
  }

  // === Десктоп/планшет: шапка-тулбар (как в чате) + центр + правая колонка «Статус» ===
  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%', overflow: 'hidden', background: C.bgMain }}>
      {/* Шапка — как тулбар чата: название/мета слева, действия справа */}
      <Toolbar>
        {headerTitleBlock}
        {executeButton}
        {editButton}
        <IconButton size="md" tone="danger" onClick={() => setConfirmDelete(true)} title="Удалить задачу">
          <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <path d="M3 6h18M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2m3 0v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6h14" />
          </svg>
        </IconButton>
        {onClose && (
          <IconButton size="md" onClick={onClose} title="Закрыть">
            <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round">
              <line x1="18" y1="6" x2="6" y2="18" /><line x1="6" y1="6" x2="18" y2="18" />
            </svg>
          </IconButton>
        )}
      </Toolbar>

      <div style={{ flex: 1, minHeight: 0, display: 'flex' }}>
        {/* Центральная колонка */}
        <div style={{ flex: 1, minWidth: 0, overflowY: 'auto' }}>{content}</div>

        {/* Правая колонка «Статус» */}
        <div style={{
          width: 264, flexShrink: 0, boxSizing: 'border-box',
          borderLeft: `1px solid ${C.borderLight}`,
          padding: '20px 22px', overflowY: 'auto',
        }}>
          <SectionLabel style={{ marginBottom: 12 }}>Статус</SectionLabel>
          <div style={{ display: 'flex', flexDirection: 'column', gap: 9 }}>
            {STATUS_SEQUENCE.map(statusRailButton)}
          </div>
        </div>
      </div>

      {deleteConfirmModal}
    </div>
  );
}
