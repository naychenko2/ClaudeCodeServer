import { useState } from 'react';
import type { Session } from '../types';
import { api } from '../lib/api';
import { useModels, useModelCaps, modelProvider } from '../lib/models';
import { effortsForProvider } from '../lib/effort';
import { C, FONT, MODAL_W } from '../lib/design';
import { Modal, ModalActions, Field, TextField, SegmentedControl, Toggle } from './ui';
import { ModelPicker } from './ModelPicker';
import { isNotifySupported, isNotifyEnabled, setNotifyEnabled } from '../lib/notify';
import { EXPIRY_PRESETS, DEFAULT_EXPIRY, expiresAt, formatExpiryDate } from '../lib/expiry';
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
  // Временный чат: тумблер + срок жизни (пресеты); при выключении срок запоминаем в стейте
  const [temporary, setTemporary] = useState(!!session.expiresAfterMinutes);
  const [ttl, setTtl] = useState(session.expiresAfterMinutes ?? DEFAULT_EXPIRY);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  // Поле effort скрываем, если провайдер модели его не поддерживает
  const caps = useModelCaps(model);

  const handleSave = async () => {
    setLoading(true);
    setError(null);
    try {
      const data = {
        name: name.trim() || null,
        model: model || null,
        effort: effort || null,
        expiresAfterMinutes: temporary ? ttl : null,
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
            <ModelPicker value={model} options={models} onChange={setModel} columns={2} />
          </Field>

          {caps.supportsEffort && (
            <Field label="Усилие рассуждения" hint="Выше — глубже размышляет, но дольше и дороже.">
              <SegmentedControl value={effort} options={effortsForProvider(modelProvider(model))} onChange={setEffort} columns={3} />
            </Field>
          )}

          <Field label="Временный чат" hint="Удалится сам вместе с историей, если не будет активности выбранное время.">
            <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
              <Toggle checked={temporary} onChange={setTemporary} />
              <span style={{ fontSize: 13, color: C.textSecondary }}>
                {temporary ? 'Удаляется автоматически' : 'Хранится бессрочно'}
              </span>
            </div>
            {temporary && (
              <div style={{ marginTop: 10 }}>
                <SegmentedControl
                  value={String(ttl)}
                  options={EXPIRY_PRESETS.map(p => ({ value: String(p.minutes), label: p.label }))}
                  onChange={v => setTtl(Number(v))}
                  columns={4}
                />
                {(() => {
                  // Сохранение перезапускает отсчёт, поэтому для нового срока считаем от «сейчас»
                  const base = session.expiresAfterMinutes === ttl ? session.updatedAt : new Date().toISOString();
                  const at = expiresAt({ updatedAt: base, expiresAfterMinutes: ttl });
                  return at && (
                    <p style={{ margin: '8px 0 0', fontSize: 12, color: C.textMuted }}>
                      Удалится ~{formatExpiryDate(at)}, если не будет активности.
                    </p>
                  );
                })()}
              </div>
            )}
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
        </>
      ) : (
        <div style={{ height: 'min(58vh, 440px)', margin: '0 -2px', display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
          <SkillsPanel projectId={session.projectId!} />
        </div>
      )}
    </Modal>
  );
}
