import { useState, useEffect } from 'react';
import type { Session, Role } from '../types';
import { api } from '../lib/api';
import { MODELS } from '../lib/models';
import { EFFORTS } from '../lib/effort';
import { C, R, MODAL_W } from '../lib/design';
import { Modal, ModalActions, Field, TextField, TextArea, SegmentedControl } from './ui';
import { RoleAvatar } from './RoleAvatar';
import { type Mode, MODES, MODE_META, ModeIcon, isDangerMode } from '../lib/modes';
import { DangerModeConfirm } from './DangerModeConfirm';
import { useFeature, FLAGS } from '../lib/featureFlags';

interface NewSessionDialogProps {
  projectId: string;
  onCreated: (session: Session, firstMessage?: string) => void;
  onClose: () => void;
}

export function NewSessionDialog({ projectId, onCreated, onClose }: NewSessionDialogProps) {
  const [name, setName] = useState('');
  const [firstMessage, setFirstMessage] = useState('');
  const [mode, setMode] = useState<Mode>('acceptEdits');
  const [pendingMode, setPendingMode] = useState<Mode | null>(null);
  const [model, setModel] = useState('');
  const [effort, setEffort] = useState('');
  const [roles, setRoles] = useState<Role[]>([]);
  const [roleId, setRoleId] = useState('');
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  // Фича «Команда» за фич-флагом — без него выбор члена команды не показываем и не грузим
  const rolesEnabled = useFeature(FLAGS.roles);

  useEffect(() => {
    if (!rolesEnabled) { setRoles([]); return; }
    api.roles.list(projectId).then(setRoles).catch(() => {});
  }, [projectId, rolesEnabled]);

  const selectedRole = roles.find(r => r.id === roleId);

  const handleSubmit = async () => {
    setLoading(true);
    setError(null);
    try {
      // Если имя не задано — берём имя роли (когда выбрана)
      const sessionName = name.trim() || selectedRole?.name || undefined;
      const session = await api.sessions.create(projectId, mode, undefined, sessionName, model || undefined, undefined, effort || undefined, roleId || undefined);
      onCreated(session, firstMessage.trim() || undefined);
      onClose();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Ошибка создания чата');
    } finally {
      setLoading(false);
    }
  };

  return (
    <>
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
      {roles.length > 0 && (
        <Field label="Член команды" hint="Собеседник-персонаж: задаёт характер и компетенции чата.">
          <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6 }}>
            <button type="button" onClick={() => setRoleId('')}
              style={{
                display: 'flex', alignItems: 'center', gap: 7, padding: '6px 11px', borderRadius: R.lg, cursor: 'pointer',
                fontSize: 13, fontWeight: 600, color: roleId === '' ? C.accent : C.textSecondary,
                border: `1px solid ${roleId === '' ? C.accent : C.border}`,
                background: roleId === '' ? C.accentLight : C.bgWhite,
              }}
            >Без сотрудника</button>
            {roles.map(r => {
              const active = r.id === roleId;
              return (
                <button key={r.id} type="button" onClick={() => setRoleId(r.id)}
                  style={{
                    display: 'flex', alignItems: 'center', gap: 7, padding: '5px 11px 5px 6px', borderRadius: R.lg, cursor: 'pointer',
                    fontSize: 13, fontWeight: 600, color: active ? C.accent : C.textHeading,
                    border: `1px solid ${active ? C.accent : C.border}`,
                    background: active ? C.accentLight : C.bgWhite,
                  }}
                >
                  <RoleAvatar name={r.name} avatar={r.avatar} color={r.color} size={22} />
                  {r.name}
                </button>
              );
            })}
          </div>
        </Field>
      )}

      <Field label="Название">
        <TextField value={name} onChange={setName} placeholder={selectedRole ? selectedRole.name : 'авто из первого сообщения'} />
      </Field>

      <Field label="Первое сообщение">
        <TextArea value={firstMessage} onChange={setFirstMessage} placeholder="Опишите задачу…" autoGrow />
      </Field>

      <Field label="Режим">
        <div style={{ display: 'flex', flexDirection: 'column', gap: 3 }}>
          {MODES.map(m => {
            const active = m === mode;
            const danger = MODE_META[m].danger;
            return (
              <button key={m} type="button"
                onClick={() => { if (isDangerMode(m) && m !== mode) setPendingMode(m); else setMode(m); }}
                style={{
                  width: '100%', display: 'flex', alignItems: 'flex-start', gap: 9, padding: '8px 10px',
                  borderRadius: R.md, cursor: 'pointer', textAlign: 'left',
                  border: `1px solid ${active ? C.accent : C.border}`,
                  background: active ? C.accentLight : C.bgWhite,
                }}
              >
                <span style={{ color: danger ? C.danger : active ? C.accent : C.textMuted, display: 'flex', marginTop: 1, flexShrink: 0 }}><ModeIcon mode={m} /></span>
                <span style={{ flex: 1, minWidth: 0 }}>
                  <span style={{ display: 'block', fontSize: 13, fontWeight: 600, color: danger ? C.danger : C.textHeading }}>{MODE_META[m].label}{danger ? ' ⚠️' : ''}</span>
                  <span style={{ display: 'block', fontSize: 11.5, color: C.textMuted, marginTop: 1, lineHeight: 1.35 }}>{MODE_META[m].desc}</span>
                </span>
              </button>
            );
          })}
        </div>
      </Field>

      <Field label="Модель">
        <SegmentedControl value={model} options={MODELS} onChange={setModel} columns={2} />
      </Field>

      <Field label="Усилие рассуждения" hint="Выше — глубже размышляет, но дольше и дороже.">
        <SegmentedControl value={effort} options={EFFORTS} onChange={setEffort} columns={3} />
      </Field>

      {error && <p style={{ margin: 0, fontSize: 13, color: C.danger }}>{error}</p>}
    </Modal>
    {pendingMode && (
      <DangerModeConfirm
        mode={pendingMode}
        onConfirm={() => { setMode(pendingMode); setPendingMode(null); }}
        onCancel={() => setPendingMode(null)}
      />
    )}
    </>
  );
}
