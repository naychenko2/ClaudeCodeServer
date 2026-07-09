import { useEffect, useRef, useState } from 'react';
import type { NoteTask } from '../../types';
import { api } from '../../lib/api';
import { bumpNotes } from '../../lib/notes';
import { C, FONT, R } from '../../lib/design';

// Секция «Задачи из заметки» (флаг notes-task-sync): чекбоксы заметки с промоутом в
// настоящую задачу (появится в календаре), синхронной отметкой и сроком через дейт-пикер.
// Живёт в правом сайдбаре заметки. Пусто (нет чекбоксов) — не рендерится.
export function NoteTasksSection({ noteId, version }: { noteId: string; version: number }) {
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
              {/* Строка 1: галочка + текст (переносится в узком сайдбаре) */}
              <div style={{ display: 'flex', alignItems: 'flex-start', gap: 8 }}>
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
                    <svg width="11" height="11" viewBox="0 0 24 24" fill="none" stroke={C.onAccent} strokeWidth="3" strokeLinecap="round" strokeLinejoin="round">
                      <polyline points="20 6 9 17 4 12" />
                    </svg>
                  )}
                </button>
                <span style={{
                  flex: 1, minWidth: 0, fontSize: 13, fontFamily: FONT.sans, lineHeight: 1.35,
                  color: t.done ? C.textMuted : C.textPrimary,
                  textDecoration: t.done ? 'line-through' : 'none',
                  overflowWrap: 'anywhere',
                }}>
                  {t.text}
                </span>
              </div>
              {/* Строка 2: срок + промоут/статус (под текстом) */}
              <div style={{ display: 'flex', alignItems: 'center', gap: 6, paddingLeft: 25 }}>
                <DueChip due={t.due} disabled={disabled} onSet={v => void setDue(t, v)} />
                {t.due && (
                  <button
                    onClick={() => void setDue(t, '')} disabled={disabled}
                    title="Убрать срок" aria-label="Убрать срок"
                    style={{
                      flex: 'none', border: 'none', background: 'none', color: C.textMuted,
                      cursor: disabled ? 'default' : 'pointer', fontSize: 12, lineHeight: 1, padding: '0 2px',
                    }}
                  >
                    ✕
                  </button>
                )}
                <span style={{ flex: 1 }} />
                {t.taskId ? (
                  <span style={{ flex: 'none', fontSize: 11, fontFamily: FONT.sans, color: C.accent }}>
                    в задачах
                  </span>
                ) : (
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
                )}
              </div>
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
        ref={ref} type="date" value={due ?? ''} disabled={disabled}
        onChange={e => onSet(e.target.value)}
        tabIndex={-1} aria-hidden
        style={{ position: 'absolute', left: 8, bottom: 0, width: 1, height: 1, opacity: 0, pointerEvents: 'none', border: 0, padding: 0 }}
      />
    </span>
  );
}
