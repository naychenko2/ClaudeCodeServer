import { useState } from 'react';
import type { Project } from '../../../types';
import { api } from '../../../lib/api';
import { C, MODAL_W } from '../../../lib/design';
import { Modal, ModalActions, TextField } from '../../../components/ui';
import { SyncToggleRow } from '../components/SyncToggleRow';

interface Props {
  onSuccess: (project: Project) => void;
  onClose: () => void;
}

export function AddExistingDialog({ onSuccess, onClose }: Props) {
  const [name, setName] = useState('');
  const [path, setPath] = useState('');
  const [sync, setSync] = useState(false);
  const [error, setError] = useState('');

  const handleConfirm = async () => {
    setError('');
    try {
      const p = await api.projects.create(name.trim(), path.trim() || null);
      if (sync) api.sync.add(p.id, '', true).catch(() => {});
      onSuccess(p);
    } catch (e: any) {
      setError(e.message);
    }
  };

  return (
    <Modal
      title="Добавить существующий проект"
      width={MODAL_W.form}
      onClose={onClose}
      footer={
        <ModalActions
          confirmLabel="Добавить"
          onConfirm={handleConfirm}
          onCancel={onClose}
        />
      }
    >
      {error && <div style={{ color: C.danger, fontSize: 13 }}>{error}</div>}
      <TextField value={name} onChange={setName} placeholder="Название" autoFocus />
      <TextField value={path} onChange={setPath} placeholder="Путь к папке" mono />
      <SyncToggleRow enabled={sync} onChange={setSync} />
    </Modal>
  );
}
