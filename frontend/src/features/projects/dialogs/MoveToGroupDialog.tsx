import { useState } from 'react';
import type { Project, ProjectGroup } from '../../../types';
import { api } from '../../../lib/api';
import { C, R, FONT, MODAL_W } from '../../../lib/design';
import { Modal } from '../../../components/ui';

interface Props {
  project: Project;
  groups: ProjectGroup[];
  onSuccess: (updated: Project) => void;
  onClose: () => void;
}

// Выбор группы для проекта: список групп + «Без группы». Клик сразу сохраняет.
export function MoveToGroupDialog({ project, groups, onSuccess, onClose }: Props) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');

  const move = async (groupId: string) => {
    if (busy) return;
    if ((project.groupId ?? '') === groupId) { onClose(); return; }
    setBusy(true);
    setError('');
    try {
      const updated = await api.projects.update(project.id, { groupId });
      onSuccess(updated);
    } catch (e: any) { setError(e.message); setBusy(false); }
  };

  const options: { id: string; name: string; color?: string }[] = [
    { id: '', name: 'Без группы' },
    ...groups.map(g => ({ id: g.id, name: g.name, color: g.color })),
  ];

  return (
    <Modal title="Переместить в группу" width={MODAL_W.confirm} onClose={onClose} footer={null}>
      {error && <div style={{ color: C.danger, fontSize: 13 }}>{error}</div>}
      <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
        {options.map(o => {
          const active = (project.groupId ?? '') === o.id;
          return (
            <button
              key={o.id || 'none'}
              onClick={() => move(o.id)}
              style={{
                display: 'flex', alignItems: 'center', gap: 10, textAlign: 'left',
                background: active ? C.bgSelected : C.bgWhite,
                border: `1px solid ${active ? C.accent : C.border}`,
                borderRadius: R.xl, padding: '11px 13px', cursor: 'pointer',
                fontFamily: FONT.sans, fontSize: 14, color: C.textHeading,
              }}
            >
              <span style={{
                width: 10, height: 10, borderRadius: '50%', flexShrink: 0,
                background: o.color || C.textMuted,
                opacity: o.id ? 1 : 0.35,
              }} />
              <span style={{ flex: 1, minWidth: 0, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
                {o.name}
              </span>
              {active && (
                <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke={C.accent} strokeWidth="2.4" strokeLinecap="round" strokeLinejoin="round">
                  <polyline points="20 6 9 17 4 12" />
                </svg>
              )}
            </button>
          );
        })}
      </div>
    </Modal>
  );
}
