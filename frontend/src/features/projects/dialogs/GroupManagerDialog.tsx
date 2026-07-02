import { useState } from 'react';
import type { ProjectGroup } from '../../../types';
import { api } from '../../../lib/api';
import { C, R, FONT, GROUP_COLORS, MODAL_W } from '../../../lib/design';
import { Modal, Button } from '../../../components/ui';

interface Props {
  groups: ProjectGroup[];
  onChange: (groups: ProjectGroup[]) => void;   // синхронизация со списком проектов
  onClose: () => void;
}

// Модалка-менеджер групп: создать, переименовать, сменить цвет, поменять порядок, удалить.
export function GroupManagerDialog({ groups, onChange, onClose }: Props) {
  const [list, setList] = useState<ProjectGroup[]>(groups);
  const [paletteFor, setPaletteFor] = useState<string | null>(null);   // id группы с открытой палитрой
  const [confirmDelete, setConfirmDelete] = useState<string | null>(null);
  const [newName, setNewName] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');

  const sync = (next: ProjectGroup[]) => { setList(next); onChange(next); };

  const handleRename = async (id: string, name: string) => {
    const g = list.find(x => x.id === id);
    if (!g || g.name === name) return;
    try {
      const updated = await api.projectGroups.update(id, { name });
      sync(list.map(x => x.id === id ? updated : x));
    } catch (e: any) { setError(e.message); }
  };

  const handleColor = async (id: string, color: string) => {
    setPaletteFor(null);
    try {
      const updated = await api.projectGroups.update(id, { color });
      sync(list.map(x => x.id === id ? updated : x));
    } catch (e: any) { setError(e.message); }
  };

  const handleMove = async (id: string, dir: -1 | 1) => {
    const i = list.findIndex(x => x.id === id);
    const j = i + dir;
    if (i < 0 || j < 0 || j >= list.length) return;
    const next = [...list];
    [next[i], next[j]] = [next[j], next[i]];
    sync(next);   // оптимистично
    try {
      const server = await api.projectGroups.reorder(next.map(x => x.id));
      sync(server);
    } catch (e: any) { setError(e.message); }
  };

  const handleDelete = async (id: string) => {
    setConfirmDelete(null);
    try {
      await api.projectGroups.delete(id);
      sync(list.filter(x => x.id !== id));
    } catch (e: any) { setError(e.message); }
  };

  const handleCreate = async () => {
    const name = newName.trim();
    if (!name || busy) return;
    setBusy(true);
    setError('');
    try {
      const color = GROUP_COLORS[list.length % GROUP_COLORS.length];
      const created = await api.projectGroups.create(name, color);
      sync([...list, created]);
      setNewName('');
    } catch (e: any) { setError(e.message); }
    finally { setBusy(false); }
  };

  return (
    <Modal
      title="Группы проектов"
      subtitle="Организуйте проекты по группам: название, цвет и порядок."
      width={MODAL_W.form}
      onClose={onClose}
      footer={<Button variant="secondary" size="md" fullWidth onClick={onClose}>Готово</Button>}
    >
      {error && <div style={{ color: C.danger, fontSize: 13 }}>{error}</div>}

      {list.length === 0 && (
        <div style={{ fontSize: 13, color: C.textMuted, textAlign: 'center', padding: '6px 0' }}>
          Пока нет ни одной группы
        </div>
      )}

      <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
        {list.map((g, i) => (
          <div key={g.id} style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
            <div style={{
              display: 'flex', alignItems: 'center', gap: 8,
              background: C.bgWhite, border: `1px solid ${C.border}`,
              borderRadius: R.xl, padding: '7px 9px',
            }}>
              {/* Свотч цвета */}
              <button
                onClick={() => setPaletteFor(paletteFor === g.id ? null : g.id)}
                title="Цвет группы"
                style={{
                  width: 22, height: 22, borderRadius: R.sm, flexShrink: 0,
                  background: g.color || C.textMuted, border: `1px solid ${C.border}`, cursor: 'pointer',
                }}
              />
              {/* Имя (инлайн-редактирование по blur/Enter) */}
              <input
                defaultValue={g.name}
                onBlur={e => handleRename(g.id, e.target.value.trim())}
                onKeyDown={e => { if (e.key === 'Enter') (e.target as HTMLInputElement).blur(); }}
                style={{
                  flex: 1, minWidth: 0, border: 'none', background: 'none', outline: 'none',
                  fontSize: 14, fontWeight: 600, color: C.textHeading, fontFamily: FONT.sans,
                }}
              />
              {/* Порядок */}
              <button onClick={() => handleMove(g.id, -1)} disabled={i === 0} title="Выше"
                style={arrowBtn(i === 0)}>
                <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round"><polyline points="18 15 12 9 6 15" /></svg>
              </button>
              <button onClick={() => handleMove(g.id, 1)} disabled={i === list.length - 1} title="Ниже"
                style={arrowBtn(i === list.length - 1)}>
                <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round"><polyline points="6 9 12 15 18 9" /></svg>
              </button>
              {/* Удаление */}
              <button onClick={() => setConfirmDelete(g.id)} title="Удалить группу"
                style={{ ...arrowBtn(false), color: C.textMuted }}>
                <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                  <polyline points="3 6 5 6 21 6" /><path d="M19 6l-1 14a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2L5 6" /><path d="M10 11v6" /><path d="M14 11v6" /><path d="M9 6V4a1 1 0 0 1 1-1h4a1 1 0 0 1 1 1v2" />
                </svg>
              </button>
            </div>

            {/* Палитра цветов */}
            {paletteFor === g.id && (
              <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap', padding: '2px 4px 4px' }}>
                {GROUP_COLORS.map(color => (
                  <button key={color} onClick={() => handleColor(g.id, color)} title={color}
                    style={{
                      width: 26, height: 26, borderRadius: R.md, background: color, cursor: 'pointer',
                      border: g.color === color ? `2px solid ${C.textHeading}` : `1px solid ${C.border}`,
                    }} />
                ))}
              </div>
            )}

            {/* Подтверждение удаления */}
            {confirmDelete === g.id && (
              <div style={{
                display: 'flex', alignItems: 'center', gap: 10, flexWrap: 'wrap',
                background: C.dangerBg, border: `1px solid ${C.dangerBorder}`, borderRadius: R.xl, padding: '9px 12px',
              }}>
                <span style={{ flex: 1, minWidth: 140, fontSize: 12.5, color: C.dangerText }}>
                  Удалить «{g.name}»? Проекты вернутся в список без группы.
                </span>
                <div style={{ display: 'flex', gap: 8 }}>
                  <Button variant="secondary" size="sm" onClick={() => setConfirmDelete(null)}>Отмена</Button>
                  <Button variant="danger" size="sm" onClick={() => handleDelete(g.id)}>Удалить</Button>
                </div>
              </div>
            )}
          </div>
        ))}
      </div>

      {/* Новая группа */}
      <div style={{ display: 'flex', gap: 8 }}>
        <input
          value={newName}
          onChange={e => setNewName(e.target.value)}
          onKeyDown={e => { if (e.key === 'Enter') handleCreate(); }}
          placeholder="Новая группа"
          style={{
            flex: 1, minWidth: 0, boxSizing: 'border-box',
            border: `1px dashed ${C.dashed}`, background: 'none', borderRadius: R.xl,
            padding: '10px 13px', fontSize: 14, color: C.textHeading, fontFamily: FONT.sans, outline: 'none',
          }}
        />
        <Button variant="primary" size="md" onClick={handleCreate} loading={busy} disabled={!newName.trim()}>
          Добавить
        </Button>
      </div>
    </Modal>
  );
}

function arrowBtn(disabled: boolean) {
  return {
    width: 28, height: 28, borderRadius: R.md, flexShrink: 0,
    display: 'flex', alignItems: 'center', justifyContent: 'center',
    background: 'none', border: 'none',
    color: disabled ? C.border : C.textSecondary,
    cursor: disabled ? 'default' : 'pointer',
  } as const;
}
