import { useState } from 'react';
import type { Session } from '../types';
import { api } from '../lib/api';
import { MODELS } from '../lib/models';
import { EFFORTS } from '../lib/effort';
import { C, MODAL_W } from '../lib/design';
import { Modal, ModalActions, Field, TextField, SegmentedControl, Toggle } from './ui';
import { isNotifySupported, isNotifyEnabled, setNotifyEnabled } from '../lib/notify';

interface Props {
  session: Session;
  onSaved: (session: Session) => void;
  onClose: () => void;
}

export function EditSessionDialog({ session, onSaved, onClose }: Props) {
  const [name, setName] = useState(session.name ?? '');
  const [model, setModel] = useState(session.model ?? '');
  const [effort, setEffort] = useState(session.effort ?? '');
  const [notifyOn, setNotifyOn] = useState(isNotifyEnabled());
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const handleSave = async () => {
    setLoading(true);
    setError(null);
    try {
      const updated = await api.sessions.update(session.projectId, session.id, {
        name: name.trim() || null,
        model: model || null,
        effort: effort || null,
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

      <Field label="Усилие рассуждения" hint="Выше — глубже размышляет, но дольше и дороже.">
        <SegmentedControl value={effort} options={EFFORTS} onChange={setEffort} columns={3} />
      </Field>

      {isNotifySupported() && (
        <Field label="Уведомления браузера" hint="Сигнал, когда нужно решение или ход завершён (если вкладка не в фокусе).">
          <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
            <Toggle checked={notifyOn} onChange={async (v) => setNotifyOn(await setNotifyEnabled(v))} />
            <span style={{ fontSize: 13, color: C.textSecondary }}>{notifyOn ? 'Включены' : 'Выключены'}</span>
          </div>
        </Field>
      )}

      {error && <p style={{ margin: 0, fontSize: 13, color: C.danger }}>{error}</p>}
    </Modal>
  );
}
