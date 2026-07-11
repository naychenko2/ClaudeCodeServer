import { useState } from 'react';
import type { KnowledgeVisibility } from '../../types';
import { api } from '../../lib/api';
import { bumpKnowledge } from '../../lib/knowledge';
import { Modal, ModalActions, Field, TextField, TextArea } from '../../components/ui';
import { PillSwitch } from '../../components/Toolbar';
import { C, MODAL_W } from '../../lib/design';
import { IconLock, IconGlobe } from './shared';

// Диалог создания базы знаний: название, описание, видимость (личная/публичная).
export function NewKnowledgeBaseDialog({ onClose, onCreated }: {
  onClose: () => void;
  onCreated: (id: string) => void;
}) {
  const [title, setTitle] = useState('');
  const [description, setDescription] = useState('');
  const [visibility, setVisibility] = useState<KnowledgeVisibility>('personal');
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);

  const create = async () => {
    setBusy(true); setErr(null);
    try {
      const res = await api.knowledgeBases.create({
        title: title.trim(),
        description: description.trim() || undefined,
        visibility,
      });
      bumpKnowledge();
      onCreated(res.id);
    } catch (e) {
      setErr(e instanceof Error ? e.message : 'Не удалось создать');
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal
      width={MODAL_W.form}
      title="Новая база знаний"
      subtitle="Самостоятельная — для своих материалов, публичная — для всей команды."
      onClose={onClose}
      footer={
        <ModalActions
          confirmLabel="Создать"
          confirmDisabled={!title.trim() || busy}
          loading={busy}
          onConfirm={create}
          onCancel={onClose}
        />
      }
    >
      <Field label="Название">
        <TextField value={title} onChange={setTitle} placeholder="напр. Конспекты книг" autoFocus onEnter={create} />
      </Field>
      <Field label="Описание (необязательно)">
        <TextArea value={description} onChange={setDescription} placeholder="О чём эта база…" minHeight={64} />
      </Field>
      <Field label="Видимость" hint={visibility === 'personal'
        ? 'Только вы видите и управляете этой базой.'
        : 'Видна всем пользователям; удалять может администратор.'}>
        <PillSwitch<KnowledgeVisibility>
          fill
          value={visibility}
          onChange={setVisibility}
          options={[
            { value: 'personal', label: 'Личная', icon: <IconLock size={14} /> },
            { value: 'public', label: 'Публичная', icon: <IconGlobe size={14} /> },
          ]}
        />
      </Field>
      {err && <div style={{ color: C.danger, fontSize: 13 }}>{err}</div>}
    </Modal>
  );
}
