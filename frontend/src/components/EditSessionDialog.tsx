import { useState } from 'react';
import type { Session } from '../types';
import { api } from '../lib/api';
import { useModels, useModelCaps, modelProvider } from '../lib/models';
import { effortsForProvider } from '../lib/effort';
import { C, FONT, MODAL_W } from '../lib/design';
import { Modal, ModalActions, Field, TextField, SegmentedControl, Toggle } from './ui';
import { isNotifySupported, isNotifyEnabled, setNotifyEnabled } from '../lib/notify';
import { SkillsPanel } from './SkillsPanel';

interface Props {
  session: Session;
  onSaved: (session: Session) => void;
  onClose: () => void;
}

type Tab = 'settings' | 'skills';

export function EditSessionDialog({ session, onSaved, onClose }: Props) {
  // Вкладка «Скиллы» доступна только для проектной сессии (SkillsPanel требует projectId)
  const hasSkills = !!session.projectId;
  const [tab, setTab] = useState<Tab>('settings');
  const models = useModels();
  const [name, setName] = useState(session.name ?? '');
  const [model, setModel] = useState(session.model ?? '');
  const [effort, setEffort] = useState(session.effort ?? '');
  const [notifyOn, setNotifyOn] = useState(isNotifyEnabled());
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  // У DeepSeek нет reasoning effort — поле скрываем
  const caps = useModelCaps(model);

  const handleSave = async () => {
    setLoading(true);
    setError(null);
    try {
      const data = {
        name: name.trim() || null,
        model: model || null,
        effort: effort || null,
      };
      // Проектная сессия обновляется через /projects/{id}/sessions,
      // чат вне проекта (нет projectId) — через /chats
      const updated = session.projectId
        ? await api.sessions.update(session.projectId, session.id, data)
        : await api.chats.update(session.id, data);
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
      footer={tab === 'settings'
        ? (
          <ModalActions
            confirmLabel={loading ? 'Сохраняем…' : 'Сохранить'}
            onConfirm={handleSave}
            loading={loading}
            onCancel={onClose}
          />
        )
        : undefined}
    >
      {/* Переключатель вкладок: настройки чата · скиллы и агенты (вкладка «Скиллы» — только у проектной сессии) */}
      {hasSkills && (
      <div style={{ display: 'flex', gap: 4, borderBottom: `1px solid ${C.border}`, marginBottom: 2 }}>
        {([['settings', 'Настройки'], ['skills', 'Скиллы']] as [Tab, string][]).map(([t, label]) => (
          <button
            key={t}
            type="button"
            onClick={() => setTab(t)}
            style={{
              border: 'none', background: 'none', cursor: 'pointer', padding: '6px 4px 9px',
              marginBottom: -1, fontFamily: FONT.sans, fontSize: 13.5,
              fontWeight: tab === t ? 700 : 500,
              color: tab === t ? C.textHeading : C.textMuted,
              borderBottom: `2px solid ${tab === t ? C.accent : 'transparent'}`,
            }}
          >
            {label}
          </button>
        ))}
      </div>
      )}

      {(!hasSkills || tab === 'settings') ? (
        <>
          <Field label="Название">
            <TextField value={name} onChange={setName} placeholder="авто из первого сообщения" autoFocus onEnter={handleSave} />
          </Field>

          <Field label="Модель" hint="Применится со следующего сообщения.">
            <SegmentedControl value={model} options={models} onChange={setModel} columns={2} />
          </Field>

          {caps.supportsEffort && (
            <Field label="Усилие рассуждения" hint="Выше — глубже размышляет, но дольше и дороже.">
              <SegmentedControl value={effort} options={effortsForProvider(modelProvider(model))} onChange={setEffort} columns={3} />
            </Field>
          )}

          {isNotifySupported() && (
            <Field label="Уведомления браузера" hint="Сигнал, когда нужно решение или ход завершён (если вкладка не в фокусе).">
              <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
                <Toggle checked={notifyOn} onChange={async (v) => setNotifyOn(await setNotifyEnabled(v))} />
                <span style={{ fontSize: 13, color: C.textSecondary }}>{notifyOn ? 'Включены' : 'Выключены'}</span>
              </div>
            </Field>
          )}

          {error && <p style={{ margin: 0, fontSize: 13, color: C.danger }}>{error}</p>}
        </>
      ) : (
        <div style={{ height: 'min(58vh, 440px)', margin: '0 -2px', display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
          <SkillsPanel projectId={session.projectId!} />
        </div>
      )}
    </Modal>
  );
}
