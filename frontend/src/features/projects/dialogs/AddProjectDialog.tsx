import { useState } from 'react';
import type { Project, ProjectGroup } from '../../../types';
import { api } from '../../../lib/api';
import { C, MODAL_W } from '../../../lib/design';
import { Modal, ModalActions, TextField, Field, SegmentedControl } from '../../../components/ui';
import { GroupSelect } from '../GroupSelect';
import { SyncToggleRow } from '../components/SyncToggleRow';

interface Props {
  groups: ProjectGroup[];
  defaultGroupId?: string;
  onSuccess: (project: Project) => void;
  onClose: () => void;
}

type Mode = 'new' | 'existing';

// Единый диалог добавления проекта: сегмент «Новый / Существующий».
//  • Новый — создаём новую папку под путём по умолчанию (path = null).
//  • Существующий — привязываем существующую папку по пути.
export function AddProjectDialog({ groups, defaultGroupId, onSuccess, onClose }: Props) {
  const [mode, setMode] = useState<Mode>('new');
  const [name, setName] = useState('');
  const [path, setPath] = useState('');
  const [groupId, setGroupId] = useState(defaultGroupId ?? '');
  const [sync, setSync] = useState(false);
  const [error, setError] = useState('');

  const handleConfirm = async () => {
    setError('');
    try {
      const rootPath = mode === 'existing' ? (path.trim() || null) : null;
      const p = await api.projects.create(name.trim(), rootPath, false, groupId || null);
      if (sync) api.sync.add(p.id, '', true).catch(() => {});
      onSuccess(p);
    } catch (e: any) {
      setError(e.message);
    }
  };

  return (
    <Modal
      title="Добавить проект"
      width={MODAL_W.form}
      onClose={onClose}
      footer={
        <ModalActions
          confirmLabel={mode === 'existing' ? 'Добавить' : 'Создать'}
          confirmDisabled={!name.trim() || (mode === 'existing' && !path.trim())}
          onConfirm={handleConfirm}
          onCancel={onClose}
        />
      }
    >
      {error && <div style={{ color: C.danger, fontSize: 13 }}>{error}</div>}

      <SegmentedControl<Mode>
        value={mode}
        onChange={setMode}
        options={[{ value: 'new', label: 'Новый' }, { value: 'existing', label: 'Существующий' }]}
      />

      <TextField value={name} onChange={setName} placeholder="Название" autoFocus />

      {mode === 'existing' && (
        <Field label="Путь к папке" hint="Абсолютный путь к существующей папке проекта">
          <TextField value={path} onChange={setPath} placeholder="C:\\Sources\\my-project" mono />
        </Field>
      )}

      {groups.length > 0 && (
        <Field label="Группа">
          <GroupSelect groups={groups} value={groupId} onChange={setGroupId} />
        </Field>
      )}

      <SyncToggleRow enabled={sync} onChange={setSync} />
    </Modal>
  );
}
