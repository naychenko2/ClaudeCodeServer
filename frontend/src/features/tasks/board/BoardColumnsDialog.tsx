// Редактор колонок Kanban-доски проекта: добавить/переименовать/цвет/категория/порядок/удалить.
// Каждая колонка привязана к категории (To-Do/In-Progress/Done) — за ней вся семантика.
// Валидация: имена не пустые и присутствует хотя бы одна колонка каждой категории.

import { useState } from 'react';
import type { CSSProperties } from 'react';
import type { BoardColumn, Project, TaskStatus } from '../../../types';
import { C, FONT, R, SHADOW, Z, MODAL_W, GROUP_COLORS } from '../../../lib/design';
import { STATUS_LABEL, STATUS_ORDER, columnColor } from '../../../lib/tasks';
import { api } from '../../../lib/api';

const CATEGORIES: TaskStatus[] = ['todo', 'inProgress', 'done'];
const PALETTE = [...GROUP_COLORS, C.accent];

function newId(): string {
  try { return crypto.randomUUID(); } catch { return `col-${Date.now()}-${Math.round(Math.random() * 1e6)}`; }
}

export function BoardColumnsDialog({ projectId, columns, taskCounts, onSaved, onClose }: {
  projectId: string;
  columns: BoardColumn[];
  taskCounts?: Record<string, number>;   // число задач по id колонки — для предупреждения при удалении
  onSaved: (project: Project) => void;
  onClose: () => void;
}) {
  const [rows, setRows] = useState<BoardColumn[]>(() => columns.map(c => ({ ...c })));
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [confirmIdx, setConfirmIdx] = useState<number | null>(null);   // индекс колонки, ждущей подтверждения удаления

  const update = (i: number, patch: Partial<BoardColumn>) =>
    setRows(rs => rs.map((r, idx) => (idx === i ? { ...r, ...patch } : r)));
  const remove = (i: number) => { setConfirmIdx(null); setRows(rs => rs.filter((_, idx) => idx !== i)); };
  // Удаление: если в колонке есть задачи — сперва подтверждение (они переедут в дефолтную колонку категории)
  const requestRemove = (i: number) => {
    if ((taskCounts?.[rows[i].id] ?? 0) > 0) setConfirmIdx(i);
    else remove(i);
  };
  const move = (i: number, dir: -1 | 1) => setRows(rs => {
    const j = i + dir;
    if (j < 0 || j >= rs.length) return rs;
    const next = [...rs];
    [next[i], next[j]] = [next[j], next[i]];
    return next;
  });
  const add = () => setRows(rs => [...rs, { id: newId(), name: 'Новая колонка', category: 'todo', color: undefined }]);

  const save = async () => {
    if (rows.some(r => !r.name.trim())) { setError('У каждой колонки должно быть название'); return; }
    for (const cat of CATEGORIES) {
      if (!rows.some(r => r.category === cat)) {
        setError(`Нужна хотя бы одна колонка категории «${STATUS_LABEL[cat]}»`);
        return;
      }
    }
    setSaving(true);
    try {
      const project = await api.projects.updateBoardColumns(projectId, rows.map(r => ({ ...r, name: r.name.trim() })));
      onSaved(project);
    } catch {
      setError('Не удалось сохранить колонки');
      setSaving(false);
    }
  };

  const resetDefault = async () => {
    setSaving(true);
    try {
      const project = await api.projects.updateBoardColumns(projectId, []);
      onSaved(project);
    } catch { setError('Не удалось сбросить'); setSaving(false); }
  };

  return (
    <div
      onClick={onClose}
      style={{ position: 'fixed', inset: 0, zIndex: Z.modal, background: C.overlay, display: 'flex', alignItems: 'center', justifyContent: 'center', padding: 16 }}
    >
      <div
        onClick={e => e.stopPropagation()}
        style={{
          width: MODAL_W.form, maxWidth: '100%', maxHeight: '90vh', display: 'flex', flexDirection: 'column',
          background: C.bgWhite, borderRadius: R.modal, boxShadow: SHADOW.modal, overflow: 'hidden',
        }}
      >
        <div style={{ padding: '18px 20px 12px', flexShrink: 0 }}>
          <h2 style={{ margin: 0, fontFamily: FONT.serif, fontSize: 20, fontWeight: 500, color: C.textHeading }}>Колонки доски</h2>
          <p style={{ margin: '6px 0 0', fontFamily: FONT.sans, fontSize: 12.5, color: C.textMuted, lineHeight: 1.4 }}>
            Каждая колонка привязана к категории — от неё зависит логика готовности, календарь и Claude.
          </p>
        </div>

        <div style={{ flex: 1, overflowY: 'auto', padding: '4px 20px', display: 'flex', flexDirection: 'column', gap: 8 }}>
          {rows.map((r, i) => {
            const count = taskCounts?.[r.id] ?? 0;
            const target = rows.find((x, idx) => idx !== i && x.category === r.category) ?? rows.find((_, idx) => idx !== i);
            return (
            <div key={r.id} style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
              <div style={{ display: 'flex', alignItems: 'center', gap: 8, padding: 8, border: `1px solid ${confirmIdx === i ? C.danger : C.borderLight}`, borderRadius: R.xl, background: C.bgMain }}>
                {/* Реордер */}
                <div style={{ display: 'flex', flexDirection: 'column' }}>
                  <button onClick={() => move(i, -1)} disabled={i === 0} title="Выше" style={arrowStyle(i === 0)}>▲</button>
                  <button onClick={() => move(i, 1)} disabled={i === rows.length - 1} title="Ниже" style={arrowStyle(i === rows.length - 1)}>▼</button>
                </div>
                {/* Цвет */}
                <ColorPicker value={columnColor(r)} onPick={color => update(i, { color })} />
                {/* Название */}
                <input
                  value={r.name}
                  onChange={e => update(i, { name: e.target.value })}
                  placeholder="Название"
                  style={{ flex: 1, minWidth: 0, padding: '7px 9px', border: `1px solid ${C.border}`, borderRadius: R.lg, background: C.bgWhite, color: C.textPrimary, fontFamily: FONT.sans, fontSize: 13 }}
                />
                {/* Категория */}
                <select
                  value={r.category}
                  onChange={e => update(i, { category: e.target.value as TaskStatus })}
                  style={{ padding: '7px 6px', border: `1px solid ${C.border}`, borderRadius: R.lg, background: C.bgWhite, color: C.textSecondary, fontFamily: FONT.sans, fontSize: 12, cursor: 'pointer' }}
                >
                  {STATUS_ORDER.map(s => <option key={s} value={s}>{STATUS_LABEL[s]}</option>)}
                </select>
                {/* Кол-во задач в колонке */}
                {count > 0 && <span style={{ fontFamily: FONT.sans, fontSize: 11, color: C.textMuted, whiteSpace: 'nowrap' }}>{count} зд.</span>}
                {/* Удалить */}
                <button onClick={() => requestRemove(i)} disabled={rows.length <= 1} title="Удалить" style={{ ...arrowStyle(rows.length <= 1), fontSize: 16, width: 28, height: 28 }}>×</button>
              </div>

              {/* Подтверждение удаления непустой колонки */}
              {confirmIdx === i && (
                <div style={{ display: 'flex', alignItems: 'center', gap: 8, padding: '8px 10px', background: C.dangerBg, border: `1px solid ${C.dangerBorder}`, borderRadius: R.lg }}>
                  <span style={{ flex: 1, fontFamily: FONT.sans, fontSize: 12, color: C.dangerText, lineHeight: 1.4 }}>
                    {count} {count === 1 ? 'задача переедет' : 'задач переедут'} в колонку «{target?.name ?? 'первую'}»
                  </span>
                  <button onClick={() => setConfirmIdx(null)} style={{ padding: '5px 10px', cursor: 'pointer', border: `1px solid ${C.border}`, borderRadius: R.sm, background: C.bgWhite, color: C.textPrimary, fontFamily: FONT.sans, fontSize: 12, fontWeight: 600 }}>Отмена</button>
                  <button onClick={() => remove(i)} style={{ padding: '5px 10px', cursor: 'pointer', border: 'none', borderRadius: R.sm, background: C.danger, color: '#fff', fontFamily: FONT.sans, fontSize: 12, fontWeight: 700 }}>Удалить</button>
                </div>
              )}
            </div>
            );
          })}

          <button
            onClick={add}
            style={{ display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 6, padding: '9px', cursor: 'pointer', border: `1px dashed ${C.dashed}`, borderRadius: R.xl, background: 'transparent', color: C.accent, fontFamily: FONT.sans, fontSize: 13, fontWeight: 600 }}
          >
            + Добавить колонку
          </button>
        </div>

        {error && <div style={{ padding: '8px 20px 0', color: C.danger, fontFamily: FONT.sans, fontSize: 12.5 }}>{error}</div>}

        <div style={{ display: 'flex', alignItems: 'center', gap: 8, padding: '14px 20px 18px', flexShrink: 0 }}>
          <button onClick={resetDefault} disabled={saving} style={{ padding: '8px 12px', cursor: 'pointer', border: 'none', background: 'transparent', color: C.textMuted, fontFamily: FONT.sans, fontSize: 12.5, fontWeight: 600 }}>
            Сбросить к дефолтным
          </button>
          <span style={{ flex: 1 }} />
          <button onClick={onClose} disabled={saving} style={{ padding: '9px 16px', cursor: 'pointer', border: `1px solid ${C.border}`, borderRadius: R.lg, background: C.bgWhite, color: C.textPrimary, fontFamily: FONT.sans, fontSize: 13, fontWeight: 600 }}>
            Отмена
          </button>
          <button onClick={save} disabled={saving} style={{ padding: '9px 18px', cursor: 'pointer', border: 'none', borderRadius: R.lg, background: C.accent, color: C.onAccent, fontFamily: FONT.sans, fontSize: 13, fontWeight: 700, boxShadow: SHADOW.button }}>
            {saving ? 'Сохранение…' : 'Сохранить'}
          </button>
        </div>
      </div>
    </div>
  );
}

function arrowStyle(disabled: boolean): CSSProperties {
  return {
    width: 20, height: 16, display: 'flex', alignItems: 'center', justifyContent: 'center',
    border: 'none', background: 'transparent', cursor: disabled ? 'default' : 'pointer',
    color: disabled ? C.borderLight : C.textMuted, fontSize: 9, lineHeight: 1, padding: 0,
  };
}

function ColorPicker({ value, onPick }: { value: string; onPick: (c: string) => void }) {
  const [open, setOpen] = useState(false);
  return (
    <div style={{ position: 'relative' }}>
      <button onClick={() => setOpen(o => !o)} title="Цвет" style={{ width: 22, height: 22, borderRadius: '50%', border: `2px solid ${C.bgWhite}`, boxShadow: `0 0 0 1px ${C.border}`, background: value, cursor: 'pointer', flexShrink: 0 }} />
      {open && (
        <>
          <div onClick={() => setOpen(false)} style={{ position: 'fixed', inset: 0, zIndex: 1 }} />
          <div style={{ position: 'absolute', top: 28, left: 0, zIndex: 2, display: 'grid', gridTemplateColumns: 'repeat(4, 1fr)', gap: 6, padding: 8, background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.lg, boxShadow: SHADOW.dropdown }}>
            {PALETTE.map(c => (
              <button key={c} onClick={() => { onPick(c); setOpen(false); }} style={{ width: 20, height: 20, borderRadius: '50%', border: value === c ? `2px solid ${C.textPrimary}` : `1px solid ${C.border}`, background: c, cursor: 'pointer' }} />
            ))}
          </div>
        </>
      )}
    </div>
  );
}
