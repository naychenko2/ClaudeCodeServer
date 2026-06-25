import { useState } from 'react';
import type { Project } from '../../../types';
import { api } from '../../../lib/api';
import { C, MODAL_W } from '../../../lib/design';
import { Modal, ModalActions } from '../../../components/ui';

interface Props {
  project: Project;
  onSuccess: () => void;
  onClose: () => void;
}

export function DeleteDialog({ project, onSuccess, onClose }: Props) {
  const [error, setError] = useState('');

  const handleConfirm = async () => {
    setError('');
    try {
      await api.projects.delete(project.id);
      onSuccess();
    } catch (e: any) {
      setError(e.message || 'Ошибка удаления');
    }
  };

  return (
    <Modal
      title="Удалить проект?"
      width={MODAL_W.confirm}
      onClose={onClose}
      subtitle={
        <>
          Проект «<strong style={{ color: C.textPrimary, fontWeight: 600 }}>{project.name}</strong>» будет удалён без возможности восстановления. Файлы на диске не затрагиваются.
        </>
      }
      footer={
        <ModalActions
          confirmLabel="Удалить"
          confirmVariant="danger"
          onConfirm={handleConfirm}
          onCancel={onClose}
        />
      }
    >
      {error && <div style={{ color: C.danger, fontSize: 13 }}>{error}</div>}
    </Modal>
  );
}
