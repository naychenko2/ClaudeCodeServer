import { useState } from 'react';
import type { Project } from '../../../types';
import { api } from '../../../lib/api';
import { useOnline } from '../../../hooks/useOnline';
import { C, MODAL_W } from '../../../lib/design';
import { Modal, ModalActions, TextField } from '../../../components/ui';
import { ProjectSyncToggle } from '../../../components/ProjectSyncToggle';

interface Props {
  project: Project;
  onSuccess: (updated: Project) => void;
  onClose: () => void;
}

export function EditDialog({ project, onSuccess, onClose }: Props) {
  const online = useOnline();
  const [name, setName] = useState(project.name);
  const [path, setPath] = useState(project.rootPath);
  const [error, setError] = useState('');

  const handleConfirm = async () => {
    setError('');
    try {
      const updated = await api.projects.update(project.id, { name: name.trim(), rootPath: path.trim() });
      onSuccess(updated);
    } catch (e: any) {
      setError(e.message);
    }
  };

  return (
    <Modal
      title="Редактировать проект"
      width={MODAL_W.form}
      onClose={onClose}
      footer={
        <ModalActions
          confirmLabel="Сохранить"
          onConfirm={handleConfirm}
          onCancel={onClose}
        />
      }
    >
      {error && <div style={{ color: C.danger, fontSize: 13 }}>{error}</div>}
      <TextField value={name} onChange={setName} placeholder="Название" />
      <TextField value={path} onChange={setPath} placeholder="Путь к папке" mono />
      <ProjectSyncToggle projectId={project.id} online={online} />
    </Modal>
  );
}
