import { useEffect, useState } from 'react';
import type { Project, ProjectGroup } from '../../../types';
import { api } from '../../../lib/api';
import { C, R, FONT, MODAL_W } from '../../../lib/design';
import { Modal } from '../../../components/ui';
import { Check } from 'lucide-react';
import { ICON_SIZE } from '../../../components/ui/icons';
import { invalidateProjectsCache } from '../useAllProjects';
import { FLAGS, useFeature } from '../../../lib/featureFlags';

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
  const spheres = useFeature(FLAGS.spheres);
  // Имя текущей сферы, если у неё есть команда: перенос лишит её доступа к проекту
  const [teamLoss, setTeamLoss] = useState<string | null>(null);
  useEffect(() => {
    if (!spheres || !project.groupId) return;
    let alive = true;
    api.spheres.overview(project.groupId)
      .then(o => { if (alive && o.team.length > 0) setTeamLoss(o.sphere.name); })
      .catch(() => {});
    return () => { alive = false; };
  }, [spheres, project.groupId]);

  const move = async (groupId: string) => {
    if (busy) return;
    if ((project.groupId ?? '') === groupId) { onClose(); return; }
    setBusy(true);
    setError('');
    try {
      const updated = await api.projects.update(project.id, { groupId });
      invalidateProjectsCache(); // кэш списка проектов не должен отставать от мутаций
      onSuccess(updated);
    } catch (e: unknown) { setError(e instanceof Error ? e.message : 'Не удалось переместить'); setBusy(false); }
  };

  const options: { id: string; name: string; color?: string }[] = [
    { id: '', name: spheres ? 'Без сферы' : 'Без группы' },
    ...groups.map(g => ({ id: g.id, name: g.name, color: g.color })),
  ];

  return (
    <Modal title={spheres ? 'Перенести в сферу' : 'Переместить в группу'} width={MODAL_W.confirm} onClose={onClose} footer={null}>
      {error && <div style={{ color: C.danger, fontSize: 13 }}>{error}</div>}
      {teamLoss && (
        <div style={{ fontSize: 12.5, color: C.warningText, lineHeight: 1.45 }}>
          Персоны сферы «{teamLoss}» перестанут видеть этот проект, а их чаты в нём остановятся. В новой сфере проект сразу увидит её команда.
        </div>
      )}
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
              {active && spheres && o.id && (
                <span style={{ fontSize: 11.5, color: C.textMuted }}>сейчас здесь</span>
              )}
              {active && (
                <Check size={ICON_SIZE.sm} strokeWidth={2} color={C.accent} />
              )}
            </button>
          );
        })}
      </div>
    </Modal>
  );
}
