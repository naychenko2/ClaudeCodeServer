import { useEffect, useRef, useState } from 'react';
import { X, Check } from 'lucide-react';
import type { NoteTask, Project } from '../../types';
import { api, bumpNotes, C, FONT, R, ICON_SIZE, ICON_STROKE } from 'aihome_shell/kit';
import { canWriteContent } from '../projects/members/contentRights';

// Секция «Задачи из заметки» (флаг notes-task-sync): чекбоксы заметки с промоутом в
// настоящую задачу (появится в календаре), синхронной отметкой и сроком через дейт-пикер.
// Живёт в правом сайдбаре заметки. Пусто (нет чекбоксов) — не рендерится.
// Право на 'note.tasks' (S2c-F4f): у редактора и читателя все три действия (отметить,
// «В задачи», срок) закрыты на бэке на Write — здесь они ПРЯЧУТСЯ (0 узлов), список
// остаётся только для чтения: текст задачи и статус «в задачах» видны. Проект-источник
// даёт вызывающий (NoteView); undefined — 'personal' или кэш проектов не прогрет → всё
// разрешено (решение по умолчанию — в contentRights.ts).
export function NoteTasksSection({ noteId, version, project }: {
  noteId: string;
  version: number;
  // Проект-источник заметки ('personal' — undefined): право на дверь 'note.tasks'
  project?: Pick<Project, 'myRole'> | null;
}) {
  const canTasks = canWriteContent(project, 'note.tasks');
  const [tasks, setTasks] = useState<NoteTask[] | null>(null);
  const [busy, setBusy] = useState<number | null>(null); // строка, по которой идёт запрос

  useEffect(() => {
    let alive = true;
    api.notes.tasks(noteId)
      .then(t => { if (alive) setTasks(t); })
      .catch(() => { if (alive) setTasks([]); });
    return () => { alive = false; };
  }, [noteId, version]);

  if (!tasks || tasks.length === 0) return null;

  const toggle = async (t: NoteTask) => {
    setBusy(t.line);
    try { await api.notes.toggleTask(noteId, t.line, !t.done); bumpNotes(); }
    catch { /* ignore */ }
    finally { setBusy(null); }
  };
  const promote = async (t: NoteTask) => {
    setBusy(t.line);
    try { await api.notes.promoteTask(noteId, t.line); bumpNotes(); }
    catch { /* ignore */ }
    finally { setBusy(null); }
  };
  // Срок из дейт-пикера: сервер сам впишет 📅 дату в строку заметки (value='' — убрать)
  const setDue = async (t: NoteTask, value: string) => {
    setBusy(t.line);
    try { await api.notes.setNoteTaskDue(noteId, t.line, value || null); bumpNotes(); }
    catch { /* ignore */ }
    finally { setBusy(null); }
  };

  return (
    <div style={{ marginBottom: 16 }}>
      <div style={{
        fontFamily: FONT.sans, fontSize: 11, fontWeight: 700, color: C.textMuted,
        textTransform: 'uppercase', letterSpacing: 0.5, marginBottom: 8,
      }}>
        Задачи из заметки
      </div>
      <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
        {tasks.map(t => {
          const disabled = busy === t.line;
          return (
            <div key={t.line} style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
              {/* Строка 1: галочка + текст (переносится в узком сайдбаре); без права на
                  'note.tasks' чекбокса нет (0 узлов), текст — на всю ширину */}
              <div style={{ display: 'flex', alignItems: 'flex-start', gap: 8 }}>
                {canTasks && (
                  <button
                    onClick={() => void toggle(t)} disabled={disabled}
                    aria-label={t.done ? 'Снять отметку' : 'Отметить выполненной'}
                    style={{
                      flex: 'none', width: 17, height: 17, borderRadius: 5, padding: 0, marginTop: 1,
                      cursor: disabled ? 'default' : 'pointer',
                      border: `1.5px solid ${t.done ? C.accent : C.border}`,
                      background: t.done ? C.accent : C.bgWhite,
                      display: 'flex', alignItems: 'center', justifyContent: 'center',
                    }}
                  >
                    {t.done && (
                      <Check size={11} strokeWidth={ICON_STROKE} color={C.onAccent} />
                    )}
                  </button>
                )}
                <span style={{
                  flex: 1, minWidth: 0, fontSize: 13, fontFamily: FONT.sans, lineHeight: 1.35,
                  color: t.done ? C.textMuted : C.textPrimary,
                  textDecoration: t.done ? 'line-through' : 'none',
                  overflowWrap: 'anywhere',
                }}>
                  {t.text}
                </span>
              </div>
              {/* Строка 2: срок + промоут/статус (под текстом). Без права на 'note.tasks'
                  контролов записи нет вовсе; строка остаётся только ради read-only
                  статуса «в задачах» (он — не запись, он — виден) */}
              {(canTasks || t.taskId) && (
                <div style={{ display: 'flex', alignItems: 'center', gap: 6, paddingLeft: canTasks ? 25 : 0 }}>
                  {canTasks && <DueChip due={t.due} disabled={disabled} onSet={v => void setDue(t, v)} />}
                  {canTasks && t.due && (
                    <button
                      onClick={() => void setDue(t, '')} disabled={disabled}
                      title="Убрать срок" aria-label="Убрать срок"
                      style={{
                        flex: 'none', border: 'none', background: 'none', color: C.textMuted,
                        cursor: disabled ? 'default' : 'pointer', lineHeight: 1, padding: '0 2px', display: 'flex', alignItems: 'center',
                      }}
                    >
                      <X size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
                    </button>
                  )}
                  <span style={{ flex: 1 }} />
                  {t.taskId ? (
                    <span style={{ flex: 'none', fontSize: 11, fontFamily: FONT.sans, color: C.accent }}>
                      в задачах
                    </span>
                  ) : canTasks ? (
                    <button
                      onClick={() => void promote(t)} disabled={disabled}
                      title="Создать настоящую задачу (появится в календаре)"
                      style={{
                        flex: 'none', fontSize: 11, fontWeight: 500, color: C.accent, background: C.bgWhite,
                        border: `1px solid ${C.accentMuted}`, borderRadius: R.sm, padding: '2px 8px',
                        cursor: disabled ? 'default' : 'pointer', fontFamily: FONT.sans,
                      }}
                    >
                      В задачи
                    </button>
                  ) : null}
                </div>
              )}
            </div>
          );
        })}
      </div>
    </div>
  );
}

// Чип-дейт-пикер: вся кнопка кликабельна и открывает системный календарь через
// showPicker() (скрытый date-input держит значение и якорит палитру). Эмодзи не печатаем.
function DueChip({ due, disabled, onSet }: {
  due?: string | null;
  disabled: boolean;
  onSet: (value: string) => void;
}) {
  const ref = useRef<HTMLInputElement>(null);
  const open = () => {
    const el = ref.current;
    if (!el || disabled) return;
    // showPicker вызывается из user-gesture (клик) — работает и на скрытом инпуте
    if (typeof el.showPicker === 'function') { try { el.showPicker(); return; } catch { /* фолбэк ниже */ } }
    el.focus();
    el.click();
  };
  return (
    <span style={{ position: 'relative', flex: 'none', display: 'inline-flex', alignItems: 'center' }}>
      <button
        type="button" onClick={open} disabled={disabled}
        title={due ? 'Изменить срок' : 'Добавить срок'}
        style={{
          fontSize: 11, fontFamily: FONT.sans, borderRadius: R.sm, padding: '2px 8px',
          cursor: disabled ? 'default' : 'pointer',
          color: due ? C.textSecondary : C.textMuted,
          background: due ? C.bgSelected : 'transparent',
          border: due ? `1px solid ${C.border}` : `1px dashed ${C.border}`,
        }}
      >
        {due ? `📅 ${due}` : '📅 срок'}
      </button>
      <input
        autoComplete="off"
        ref={ref} type="date" value={due ?? ''} disabled={disabled}
        onChange={e => onSet(e.target.value)}
        tabIndex={-1} aria-hidden
        style={{ position: 'absolute', left: 8, bottom: 0, width: 1, height: 1, opacity: 0, pointerEvents: 'none', border: 0, padding: 0 }}
      />
    </span>
  );
}
