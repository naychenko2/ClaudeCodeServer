import { useEffect, useState } from 'react';
import type { Persona, PersonaScope, Project } from '../../types';
import { api } from '../../lib/api';
import { Modal, Field, TextField, TextArea, Toggle, Button } from '../../components/ui';
import { PillSwitch } from '../../components/Toolbar';
import { ModelPicker } from '../../components/ModelPicker';
import { useModels } from '../../lib/models';
import { AGENT_COLORS, agentDotColor } from '../../components/AgentSelector';
import { bumpPersonas } from '../../lib/personas';
import { C, FONT, R } from '../../lib/design';
import { PersonaAvatar } from './PersonaAvatar';

// Диалог создания/редактирования персоны. persona=undefined — создание.
export function PersonaEditor({ persona, onClose, onSaved }: {
  persona?: Persona;
  onClose: () => void;
  onSaved: (p: Persona) => void;
}) {
  const isEdit = !!persona;
  const models = useModels();

  const [name, setName] = useState(persona?.name ?? '');
  const [description, setDescription] = useState(persona?.description ?? '');
  const [systemPrompt, setSystemPrompt] = useState(persona?.systemPrompt ?? '');
  const [model, setModel] = useState(persona?.model ?? '');
  const [scope, setScope] = useState<PersonaScope>(persona?.scope ?? 'global');
  const [projectId, setProjectId] = useState(persona?.projectId ?? '');
  const [greeting, setGreeting] = useState(persona?.greeting ?? '');
  const [color, setColor] = useState(persona?.avatar?.color ?? 'orange');
  const [memoryEnabled, setMemoryEnabled] = useState(persona?.memoryEnabled ?? false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Аватар (этап 4): текущее состояние аватара (обновляется после генерации),
  // возможность генерации (настроен ли fal), поле промпта и статус генерации.
  const [avatar, setAvatar] = useState<Persona['avatar']>(persona?.avatar ?? { kind: 'initials', color: 'orange' });
  const [canGenerate, setCanGenerate] = useState(false);
  const [showPrompt, setShowPrompt] = useState(false);
  const [avatarPrompt, setAvatarPrompt] = useState('');
  const [generating, setGenerating] = useState(false);
  const [avatarError, setAvatarError] = useState<string | null>(null);

  const [projects, setProjects] = useState<Project[]>([]);
  useEffect(() => { api.projects.list().then(setProjects).catch(() => {}); }, []);

  // Один раз узнаём, доступна ли генерация аватара (fal настроен)
  useEffect(() => { api.personas.avatarCaps().then(c => setCanGenerate(c.generate)).catch(() => {}); }, []);

  // Персона для превью аватара: актуальные имя/аватар из формы. Цвет для инициалов
  // берём из выбранного color; при наличии картинки — kind='image' показывает её.
  const previewPersona: Persona = {
    ...(persona ?? ({
      id: '', ownerId: '', handle: '', scope, memoryEnabled,
      createdAt: '', updatedAt: '',
    } as Persona)),
    name: name.trim() || 'Агент',
    avatar: avatar.kind === 'image' ? { ...avatar, color } : { kind: 'initials', color },
  };

  const generateAvatar = async () => {
    if (!persona || generating) return;
    setGenerating(true);
    setAvatarError(null);
    try {
      const updated = await api.personas.generateAvatar(persona.id, avatarPrompt);
      setAvatar(updated.avatar);
      bumpPersonas();
      setShowPrompt(false);
      setAvatarPrompt('');
    } catch (e) {
      setAvatarError(e instanceof Error ? e.message : 'Не удалось сгенерировать аватар');
    } finally {
      setGenerating(false);
    }
  };

  // При выборе зоны «Проект» без выбранного проекта — подставим первый доступный
  useEffect(() => {
    if (scope === 'project' && !projectId && projects.length > 0) setProjectId(projects[0].id);
  }, [scope, projectId, projects]);

  const canSave = name.trim().length > 0 && !(scope === 'project' && !projectId);

  const save = async () => {
    if (!canSave || busy) return;
    setBusy(true);
    setError(null);
    const dto = {
      name: name.trim(),
      description: description.trim() || undefined,
      systemPrompt: systemPrompt.trim() || undefined,
      model: model || undefined,
      scope,
      projectId: scope === 'project' ? projectId : undefined,
      color,
      greeting: greeting.trim() || undefined,
      memoryEnabled,
    };
    try {
      const saved = isEdit
        ? await api.personas.update(persona!.id, dto)
        : await api.personas.create(dto);
      bumpPersonas();
      onSaved(saved);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Не удалось сохранить агента');
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal width={520} title={isEdit ? 'Редактировать агента' : 'Новый агент'} onClose={onClose}
      footer={
        <div style={{ display: 'flex', gap: 8, justifyContent: 'flex-end' }}>
          <button onClick={onClose} style={btnGhost}>Отмена</button>
          <button onClick={save} disabled={!canSave || busy} style={{ ...btnPrimary, opacity: !canSave || busy ? 0.6 : 1 }}>
            {isEdit ? 'Сохранить' : 'Создать'}
          </button>
        </div>
      }
    >
      <div style={{ display: 'flex', flexDirection: 'column', gap: 16 }}>
        <Field label="Имя">
          <TextField value={name} onChange={setName} placeholder="Например, Ассистент" autoFocus />
        </Field>

        <Field label="Описание" hint="Короткая подпись под именем в списке">
          <TextField value={description} onChange={setDescription} placeholder="Чем занимается агент" />
        </Field>

        <Field label="Характер" hint="Системный промпт: тон, роль, правила поведения">
          <TextArea value={systemPrompt} onChange={setSystemPrompt} minHeight={90}
            placeholder="Ты — внимательный ассистент. Отвечай кратко и по делу…" />
        </Field>

        <Field label="Модель">
          <ModelPicker value={model} options={models} onChange={setModel} />
        </Field>

        <Field label="Зона">
          <PillSwitch<PersonaScope>
            fill
            value={scope}
            onChange={setScope}
            options={[{ value: 'global', label: 'Глобальный' }, { value: 'project', label: 'Проект' }]}
          />
        </Field>

        {scope === 'project' && (
          <Field label="Проект" hint={projects.length === 0 ? 'Нет доступных проектов' : undefined}>
            <select
              value={projectId}
              onChange={e => setProjectId(e.target.value)}
              style={selectStyle}
            >
              {projects.length === 0 && <option value="">—</option>}
              {projects.map(p => <option key={p.id} value={p.id}>{p.name}</option>)}
            </select>
          </Field>
        )}

        <Field label="Приветствие" hint="С чего агент начинает разговор">
          <TextField value={greeting} onChange={setGreeting} placeholder="Привет! Чем помочь?" />
        </Field>

        <Field label="Аватар" hint="Картинка перекрывает инициалы. Цвет ниже — для инициалов">
          <div style={{ display: 'flex', alignItems: 'center', gap: 14 }}>
            <PersonaAvatar persona={previewPersona} size={56} />
            <div style={{ display: 'flex', flexDirection: 'column', gap: 6, alignItems: 'flex-start' }}>
              {canGenerate ? (
                isEdit ? (
                  <Button
                    variant="ghostAccent"
                    size="sm"
                    loading={generating}
                    onClick={() => (showPrompt ? generateAvatar() : setShowPrompt(true))}
                  >
                    {generating ? 'Генерирую…' : showPrompt ? '✨ Сгенерировать' : '✨ Сгенерировать аватар'}
                  </Button>
                ) : (
                  <span style={{ fontSize: 12.5, color: C.textSecondary, fontFamily: FONT.sans }}>
                    Сначала сохраните агента — потом появится генерация аватара
                  </span>
                )
              ) : null}
              {avatarError && (
                <span style={{ fontSize: 12, color: C.dangerText, fontFamily: FONT.sans }}>{avatarError}</span>
              )}
            </div>
          </div>
          {isEdit && canGenerate && showPrompt && (
            <div style={{ marginTop: 10 }}>
              <TextField
                value={avatarPrompt}
                onChange={setAvatarPrompt}
                placeholder="Опишите внешность (необязательно): например, рыжий кот в очках"
              />
            </div>
          )}
        </Field>

        <Field label="Цвет аватара" hint="Используется для инициалов, когда картинки нет">
          <div style={{ display: 'flex', flexWrap: 'wrap', gap: 8 }}>
            {Object.keys(AGENT_COLORS).map(key => {
              const active = key === color;
              return (
                <button
                  key={key}
                  type="button"
                  onClick={() => setColor(key)}
                  aria-label={key}
                  style={{
                    width: 30, height: 30, borderRadius: R.full, cursor: 'pointer',
                    background: agentDotColor(key),
                    border: active ? `2px solid ${C.textHeading}` : `2px solid transparent`,
                    outline: active ? `2px solid ${C.bgWhite}` : 'none',
                    outlineOffset: -4,
                  }}
                />
              );
            })}
          </div>
        </Field>

        <Field label="Долгая память" hint="Агент запоминает факты между разговорами (этап 2)">
          <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
            <Toggle checked={memoryEnabled} onChange={setMemoryEnabled} />
            <span style={{ fontSize: 13, color: C.textSecondary, fontFamily: FONT.sans }}>
              {memoryEnabled ? 'Включена' : 'Выключена'}
            </span>
          </div>
        </Field>

        {error && (
          <div style={{ fontSize: 12.5, color: C.dangerText, fontFamily: FONT.sans }}>{error}</div>
        )}
      </div>
    </Modal>
  );
}

const selectStyle: React.CSSProperties = {
  width: '100%', boxSizing: 'border-box', background: C.bgWhite, border: `1px solid ${C.border}`,
  borderRadius: R.xl, padding: '10px 13px', fontSize: 14, fontFamily: FONT.sans,
  color: C.textHeading, outline: 'none', cursor: 'pointer',
};
const btnGhost: React.CSSProperties = {
  background: 'transparent', border: `1px solid ${C.border}`, borderRadius: R.lg,
  padding: '8px 16px', fontSize: 13, cursor: 'pointer', fontFamily: FONT.sans, color: C.textSecondary,
};
const btnPrimary: React.CSSProperties = {
  background: C.accent, color: C.onAccent, border: 'none', borderRadius: R.lg,
  padding: '8px 16px', fontSize: 13, fontWeight: 500, cursor: 'pointer', fontFamily: FONT.sans,
};
