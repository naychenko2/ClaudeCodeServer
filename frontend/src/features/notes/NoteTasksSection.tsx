import { useEffect, useState } from 'react';
import type { NoteTask } from '../../types';
import { api } from '../../lib/api';
import { bumpNotes } from '../../lib/notes';
import { C, FONT, R } from '../../lib/design';

// Секция «Задачи из заметки» (флаг notes-task-sync): чекбоксы заметки с возможностью
// промоута в настоящую задачу (появится в календаре) и синхронной отметкой выполнения.
// Пусто (нет чекбоксов) — секция не рендерится.
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
    <div style={{ marginTop: 20, borderTop: `1px solid ${C.border}`, paddingTop: 12 }}>
      <div style={{
        fontFamily: FONT.sans, fontSize: 12, fontWeight: 600, color: C.textSecondary,
        textTransform: 'uppercase', letterSpacing: 0.4, marginBottom: 8,
      }}>
        Задачи из заметки
      </div>
      <div style={{ display: 'flex', flexDirection: 'column', gap: 2 }}>
        {tasks.map(t => (
          <div key={t.line} style={{ display: 'flex', alignItems: 'center', gap: 8, padding: '4px 0' }}>
            <button
              onClick={() => void toggle(t)} disabled={busy === t.line}
              aria-label={t.done ? 'Снять отметку' : 'Отметить выполненной'}
              style={{
                flex: 'none', width: 17, height: 17, borderRadius: 5, padding: 0,
                cursor: busy === t.line ? 'default' : 'pointer',
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
              flex: 1, minWidth: 0, fontSize: 13, fontFamily: FONT.sans,
              color: t.done ? C.textMuted : C.textPrimary,
              textDecoration: t.done ? 'line-through' : 'none',
              overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
            }}>
              {t.text}
            </span>
            {/* Дейт-пикер: чип показывает срок (или «＋ срок»), скрытый нативный date-input
                поверх открывает системный календарь — эмодзи вводить не нужно. */}
            <label
              title={t.due ? 'Изменить срок' : 'Добавить срок'}
              style={{
                position: 'relative', flex: 'none', display: 'inline-flex', alignItems: 'center',
                fontSize: 11, fontFamily: FONT.sans, borderRadius: R.sm, padding: '1px 6px',
                cursor: busy === t.line ? 'default' : 'pointer',
                color: t.due ? C.textSecondary : C.textMuted,
                background: t.due ? C.bgSelected : 'transparent',
                border: t.due ? 'none' : `1px dashed ${C.border}`,
              }}
            >
              {t.due ? `📅 ${t.due}` : '📅 срок'}
              <input
                type="date" value={t.due ?? ''} disabled={busy === t.line}
                onChange={e => void setDue(t, e.target.value)}
                style={{ position: 'absolute', inset: 0, opacity: 0, cursor: 'pointer', width: '100%', height: '100%' }}
              />
            </label>{t.due && (
              <button
                onClick={() => void setDue(t, '')} disabled={busy === t.line}
                title="Убрать срок" aria-label="Убрать срок"
                style={{
                  flex: 'none', border: 'none', background: 'none', color: C.textMuted,
                  cursor: busy === t.line ? 'default' : 'pointer', fontSize: 12, lineHeight: 1, padding: '0 2px',
                }}
              >
                ✕
              </button>
            )}
            {t.taskId ? (
              <span style={{ flex: 'none', fontSize: 11, fontFamily: FONT.sans, color: C.accent }}>
                в задачах
              </span>
            ) : (
              <button
                onClick={() => void promote(t)} disabled={busy === t.line}
                title="Создать настоящую задачу (появится в календаре)"
                style={{
                  flex: 'none', fontSize: 11, fontWeight: 500, color: C.accent, background: C.bgWhite,
                  border: `1px solid ${C.accentMuted}`, borderRadius: R.sm, padding: '2px 8px',
                  cursor: busy === t.line ? 'default' : 'pointer', fontFamily: FONT.sans,
                }}
              >
                В задачи
              </button>
            )}
          </div>
        ))}
      </div>
    </div>
  );
}
