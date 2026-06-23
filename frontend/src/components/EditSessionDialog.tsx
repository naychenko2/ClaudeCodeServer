import { useState } from 'react';
import type { Session } from '../types';
import { api } from '../lib/api';
import { MODELS } from '../lib/models';
import { C, MODAL_W } from '../lib/design';
import { Modal, ModalActions, Field, TextField, SegmentedControl } from './ui';

interface Props {
  session: Session;
  onSaved: (session: Session) => void;
  onClose: () => void;
}

export function EditSessionDialog({ session, onSaved, onClose }: Props) {
  const [name, setName] = useState(session.name ?? '');
  const [model, setModel] = useState(session.model ?? '');
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const handleSave = async () => {
    setLoading(true);
    setError(null);
    try {
      const updated = await api.sessions.update(session.projectId, session.id, {
        name: name.trim() || null,
        model: model || null,
      });
      onSaved(updated);
      onClose();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Ошибка сохранения');
    } finally {
      setLoading(false);
    }
  };

  return (
    <Modal
      title="Настройки чата"
      width={MODAL_W.form}
      onClose={onClose}
      footer={
        <ModalActions
          confirmLabel={loading ? 'Сохраняем…' : 'Сохранить'}
          onConfirm={handleSave}
          loading={loading}
          onCancel={onClose}
        />
      }
    >
      <Field label="Название">
        <TextField value={name} onChange={setName} placeholder="авто из первого сообщения" autoFocus onEnter={handleSave} />
      </Field>

      <Field label="Модель" hint="Применится со следующего сообщения.">
        <SegmentedControl value={model} options={MODELS} onChange={setModel} columns={2} />
      </Field>

      {error && <p style={{ margin: 0, fontSize: 13, color: C.danger }}>{error}</p>}
    </Modal>
  );
}
