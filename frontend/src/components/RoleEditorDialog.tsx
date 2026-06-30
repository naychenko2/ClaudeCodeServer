import { useState, useEffect } from 'react';
import type { Role, AgentInfo } from '../types';
import { api } from '../lib/api';
import { MODELS } from '../lib/models';
import { EFFORTS } from '../lib/effort';
import { C, R, MODAL_W } from '../lib/design';
import { Modal, ModalActions, Field, TextField, TextArea, SegmentedControl } from './ui';
import { RoleAvatar } from './RoleAvatar';

// Пресеты для быстрого выбора (можно ввести любой эмодзи руками)
const EMOJI_PRESETS = ['🔧', '🎨', '🧠', '🗄️', '📊', '🧪', '🚀', '📝', '🔬', '🛡️', '⚙️', '🤝'];
const COLOR_PRESETS = ['#D97757', '#6C5CB0', '#3E7CA6', '#5E8B4E', '#C9923E', '#B4452F', '#7A6A58', '#2A8C82'];

interface Props {
  projectId: string;
  role?: Role;                       // задан → режим редактирования
  onSaved: (role: Role) => void;
  onClose: () => void;
}

export function RoleEditorDialog({ projectId, role, onSaved, onClose }: Props) {
  const [name, setName] = useState(role?.name ?? '');
  const [title, setTitle] = useState(role?.title ?? '');
  const [avatar, setAvatar] = useState(role?.avatar ?? '');
  const [color, setColor] = useState(role?.color || COLOR_PRESETS[0]);
  const [persona, setPersona] = useState(role?.persona ?? '');
  const [agentNames, setAgentNames] = useState<string[]>(role?.agentNames ?? []);
  const [systemPrompt, setSystemPrompt] = useState(role?.systemPrompt ?? '');
  const [model, setModel] = useState(role?.model ?? '');
  const [effort, setEffort] = useState(role?.effort ?? '');
  const [agents, setAgents] = useState<AgentInfo[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    api.skills.list(projectId).then(d => setAgents(d.agents)).catch(() => {});
  }, [projectId]);

  const toggleAgent = (fileName: string) => {
    setAgentNames(prev =>
      prev.includes(fileName) ? prev.filter(a => a !== fileName) : [...prev, fileName]
    );
  };

  const handleSubmit = async () => {
    if (!name.trim()) { setError('Укажите имя роли'); return; }
    setLoading(true);
    setError(null);
    const payload = {
      name: name.trim(), title: title.trim(), avatar: avatar.trim(), color,
      persona, agentNames, systemPrompt: systemPrompt.trim() || undefined,
      model: model || undefined, effort: effort || undefined,
    };
    try {
      const saved = role
        ? await api.roles.update(projectId, role.id, payload)
        : await api.roles.create(projectId, payload);
      onSaved(saved);
      onClose();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Ошибка сохранения роли');
    } finally {
      setLoading(false);
    }
  };

  return (
    <Modal
      title={role ? 'Редактировать роль' : 'Новая роль'}
      subtitle="Собеседник-персонаж: имя и характер плюс компетенции из агентов проекта."
      width={MODAL_W.form}
      onClose={onClose}
      footer={
        <ModalActions
          confirmLabel={loading ? 'Сохраняем…' : (role ? 'Сохранить' : 'Создать роль')}
          onConfirm={handleSubmit}
          loading={loading}
          onCancel={onClose}
        />
      }
    >
      {/* Превью аватара + имя/должность */}
      <div style={{ display: 'flex', alignItems: 'center', gap: 12 }}>
        <RoleAvatar name={name || '?'} avatar={avatar} color={color} size={48} />
        <div style={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', gap: 8 }}>
          <TextField value={name} onChange={setName} placeholder="Имя (напр. Игорь)" />
          <TextField value={title} onChange={setTitle} placeholder="Должность (напр. Backend-разработчик)" />
        </div>
      </div>

      <Field label="Аватар (эмодзи)" hint="Пусто → в кружке будут инициалы имени.">
        <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6, alignItems: 'center' }}>
          {EMOJI_PRESETS.map(e => (
            <button key={e} type="button" onClick={() => setAvatar(e)}
              style={{
                width: 34, height: 34, borderRadius: R.md, cursor: 'pointer', fontSize: 18,
                background: avatar === e ? C.accentLight : C.bgWhite,
                border: `1px solid ${avatar === e ? C.accent : C.border}`,
                display: 'flex', alignItems: 'center', justifyContent: 'center',
              }}
            >{e}</button>
          ))}
          <button type="button" onClick={() => setAvatar('')}
            title="Без эмодзи (инициалы)"
            style={{
              minWidth: 34, height: 34, padding: '0 10px', borderRadius: R.md, cursor: 'pointer', fontSize: 12,
              background: avatar === '' ? C.accentLight : C.bgWhite,
              border: `1px solid ${avatar === '' ? C.accent : C.border}`,
              color: C.textSecondary, fontWeight: 600,
            }}
          >Aa</button>
        </div>
      </Field>

      <Field label="Цвет">
        <div style={{ display: 'flex', flexWrap: 'wrap', gap: 8 }}>
          {COLOR_PRESETS.map(c => (
            <button key={c} type="button" onClick={() => setColor(c)}
              style={{
                width: 28, height: 28, borderRadius: '50%', cursor: 'pointer', background: c,
                border: color === c ? `2px solid ${C.textHeading}` : '2px solid transparent',
                boxShadow: color === c ? '0 0 0 2px #FFF inset' : 'none',
              }}
            />
          ))}
        </div>
      </Field>

      <Field label="Характер и стиль речи" hint="Как роль себя ведёт и разговаривает.">
        <TextArea value={persona} onChange={setPersona} autoGrow minHeight={60}
          placeholder="Напр.: дотошный, любит чистый код, отвечает по делу, без воды…" />
      </Field>

      <Field label="Компетенции (агенты)" hint="Тела выбранных агентов попадут в системный промпт роли.">
        {agents.length === 0 ? (
          <span style={{ fontSize: 12.5, color: C.textMuted }}>
            В проекте нет агентов (.claude/agents). Роль будет работать на характере и доп. промпте.
          </span>
        ) : (
          <div style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
            {agents.map(a => {
              const checked = agentNames.includes(a.fileName);
              return (
                <button key={a.fileName} type="button" onClick={() => toggleAgent(a.fileName)}
                  style={{
                    display: 'flex', alignItems: 'flex-start', gap: 9, padding: '8px 10px',
                    borderRadius: R.md, cursor: 'pointer', textAlign: 'left', width: '100%',
                    border: `1px solid ${checked ? C.accent : C.border}`,
                    background: checked ? C.accentLight : C.bgWhite,
                  }}
                >
                  <span style={{
                    width: 16, height: 16, borderRadius: 4, flexShrink: 0, marginTop: 1,
                    border: `1.5px solid ${checked ? C.accent : C.dashed}`,
                    background: checked ? C.accent : 'transparent',
                    display: 'flex', alignItems: 'center', justifyContent: 'center',
                  }}>
                    {checked && (
                      <svg width="10" height="10" viewBox="0 0 24 24" fill="none" stroke={C.onAccent} strokeWidth="3.5" strokeLinecap="round" strokeLinejoin="round"><polyline points="20 6 9 17 4 12" /></svg>
                    )}
                  </span>
                  <span style={{ flex: 1, minWidth: 0 }}>
                    <span style={{ display: 'block', fontSize: 13, fontWeight: 600, color: C.textHeading }}>
                      {a.color && <span style={{ display: 'inline-block', width: 7, height: 7, borderRadius: '50%', background: a.color, marginRight: 6 }} />}
                      {a.name}
                    </span>
                    {a.description && (
                      <span style={{ display: 'block', fontSize: 11.5, color: C.textMuted, marginTop: 1, lineHeight: 1.35, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{a.description}</span>
                    )}
                  </span>
                </button>
              );
            })}
          </div>
        )}
      </Field>

      <Field label="Доп. инструкции" hint="Опционально — свободный промпт поверх агентов.">
        <TextArea value={systemPrompt} onChange={setSystemPrompt} autoGrow minHeight={48}
          placeholder="Особые правила конкретно для этой роли…" />
      </Field>

      <Field label="Модель по умолчанию">
        <SegmentedControl value={model} options={MODELS} onChange={setModel} columns={2} />
      </Field>

      <Field label="Усилие рассуждения">
        <SegmentedControl value={effort} options={EFFORTS} onChange={setEffort} columns={3} />
      </Field>

      {error && <p style={{ margin: 0, fontSize: 13, color: C.danger }}>{error}</p>}
    </Modal>
  );
}
