import { useEffect, useRef, useState } from 'react';
import type { Project, ProjectGroup } from '../../types';
import { api } from '../../lib/api';
import { C, FS, MODAL_W } from '../../lib/design';
import { Button, ConfirmDialog, Modal } from '../../components/ui';
import { invalidateProjectsCache } from '../projects/useAllProjects';

interface Props {
  project: Project;
  sphere: ProjectGroup;
  onDone: (updated: Project) => void;
  onClose: () => void;
}

// «Убрать из сферы»: подтверждение нужно только когда у сферы есть команда; без команды
// проект убирается сразу, без вопроса.
export function RemoveFromSphereDialog({ project, sphere, onDone, onClose }: Props) {
  const [team, setTeam] = useState<string[] | null>(null);
  const [error, setError] = useState('');
  const started = useRef(false);

  const remove = async () => {
    try {
      const updated = await api.projects.update(project.id, { groupId: '' });
      invalidateProjectsCache();
      onDone(updated);
    } catch (e: unknown) {
      setError(e instanceof Error ? e.message : 'Не удалось убрать проект из сферы');
    }
  };

  useEffect(() => {
    if (started.current) return;
    started.current = true;
    api.spheres.overview(sphere.id)
      .then(o => {
        if (o.team.length === 0) void remove();
        else setTeam(o.team.map(m => m.name));
      })
      .catch(e => setError(e instanceof Error ? e.message : 'Не удалось загрузить сферу'));
    // eslint-disable-next-line react-hooks/exhaustive-deps -- одноразовый запуск при открытии
  }, []);

  if (error) {
    return (
      <Modal title="Не удалось убрать из сферы" width={MODAL_W.confirm} onClose={onClose}
        footer={<Button variant="secondary" size="md" fullWidth onClick={onClose}>Закрыть</Button>}>
        <div style={{ color: C.danger, fontSize: FS.base }}>{error}</div>
      </Modal>
    );
  }
  if (!team) return null;

  return (
    <ConfirmDialog
      title={`Убрать «${project.name}» из сферы «${sphere.name}»?`}
      subtitle={`Персоны сферы — ${team.join(', ')} — перестанут видеть этот проект, а их чаты в нём остановятся. Память проекта останется при нём; то, что уже перенесено в сферу, останется в сфере.`}
      confirmLabel="Убрать из сферы"
      onConfirm={remove}
      onCancel={onClose}
    />
  );
}
