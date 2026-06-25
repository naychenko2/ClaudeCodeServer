import { useState } from 'react';
import type { Project } from '../../../types';
import { api } from '../../../lib/api';
import { MODAL_W } from '../../../lib/design';
import { Modal, ModalActions, TextField } from '../../../components/ui';
import { SyncToggleRow } from '../components/SyncToggleRow';
import { C } from '../../../lib/design';

interface Props {
  onSuccess: (project: Project) => void;
  onClose: () => void;
}

export function CreateDialog({ onSuccess, onClose }: Props) {
  const [name, setName] = useState('');
  const [sync, setSync] = useState(false);
  const [error, setError] = useState('');

  const handleConfirm = async () => {
    setError('');
    try {
      const p = await api.projects.create(name.trim(), null);
      if (sync) api.sync.add(p.id, '', true).catch(() => {});
      onSuccess(p);
    } catch (e: any) {
      setError(e.message);
    }
  };

  return (
    <Modal
      title="Создать новый проект"
      width={MODAL_W.form}
      onClose={onClose}
      footer={
        <ModalActions
          confirmLabel="Создать"
          onConfirm={handleConfirm}
          onCancel={onClose}
        />
      }
    >
      {error && <div style={{ color: C.danger, fontSize: 13 }}>{error}</div>}
      <TextField value={name} onChange={setName} placeholder="Название" autoFocus />
      <SyncToggleRow enabled={sync} onChange={setSync} />
    </Modal>
  );
}
