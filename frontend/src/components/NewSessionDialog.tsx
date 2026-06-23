import { useState } from 'react';
import type { Session } from '../types';
import { api } from '../lib/api';
import { MODELS } from '../lib/models';
import { C, MODAL_W } from '../lib/design';
import { Modal, ModalActions, Field, TextField, TextArea, SegmentedControl } from './ui';

interface NewSessionDialogProps {
  projectId: string;
  onCreated: (session: Session, firstMessage?: string) => void;
  onClose: () => void;
}

type Mode = 'auto' | 'plan' | 'ask';

const MODES: { value: Mode; label: string }[] = [
  { value: 'auto', label: '⚡ Авто' },
  { value: 'plan', label: '📋 План' },
  { value: 'ask', label: '❓ Спросить' },
];

export function NewSessionDialog({ projectId, onCreated, onClose }: NewSessionDialogProps) {
  const [name, setName] = useState('');
  const [firstMessage, setFirstMessage] = useState('');
  const [mode, setMode] = useState<Mode>('auto');
  const [model, setModel] = useState('');
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const handleSubmit = async () => {
    setLoading(true);
    setError(null);
    try {
      const sessionName = name.trim() || undefined;
      const session = await api.sessions.create(projectId, mode, undefined, sessionName, model || undefined);
      onCreated(session, firstMessage.trim() || undefined);
      onClose();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Ошибка создания чата');
    } finally {
      setLoading(false);
    }
  };

  return (
    <Modal
      title="Новый чат"
      subtitle="Настройте режим и модель — или просто опишите задачу."
      width={MODAL_W.form}
      onClose={onClose}
      footer={
        <ModalActions
          confirmLabel={loading ? 'Создаём…' : 'Создать и начать'}
          onConfirm={handleSubmit}
          loading={loading}
          onCancel={onClose}
        />
      }
    >
      <Field label="Название">
        <TextField value={name} onChange={setName} placeholder="авто из первого сообщения" />
      </Field>

      <Field label="Первое сообщение">
        <TextArea value={firstMessage} onChange={setFirstMessage} placeholder="Опишите задачу…" autoGrow />
      </Field>

      <Field label="Режим">
        <SegmentedControl value={mode} options={MODES} onChange={setMode} columns={3} />
      </Field>

      <Field label="Модель">
        <SegmentedControl value={model} options={MODELS} onChange={setModel} columns={2} />
      </Field>

      {error && <p style={{ margin: 0, fontSize: 13, color: C.danger }}>{error}</p>}
    </Modal>
  );
}
