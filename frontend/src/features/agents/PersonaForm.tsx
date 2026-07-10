import { useEffect, useState } from 'react';
import type { Persona, PersonaScope, Project } from '../../types';
import { api } from '../../lib/api';
import { Field, TextField, TextArea, Toggle, Button } from '../../components/ui';
import { PillSwitch } from '../../components/Toolbar';
import { ModelPicker } from '../../components/ModelPicker';
import { useModels } from '../../lib/models';
import { AGENT_COLORS, agentDotColor } from '../../components/AgentSelector';
import { bumpPersonas } from '../../lib/personas';
import { C, FONT, R } from '../../lib/design';
import { PersonaAvatar } from './PersonaAvatar';

function useIsMobile(): boolean {
  const [m, setM] = useState(() => typeof window !== 'undefined' && window.matchMedia('(max-width: 767px)').matches);
  useEffect(() => {
    const mq = window.matchMedia('(max-width: 767px)');
    const h = (e: MediaQueryListEvent) => setM(e.matches);
    mq.addEventListener('change', h);
    return () => mq.removeEventListener('change', h);
  }, []);
  return m;
}

// Инлайн-форма создания/редактирования персоны (без Modal-обёртки).
// Вписана прямо в контентную зону раздела «Агенты». persona=null/undefined — создание.
export function PersonaForm({ persona, projects, onSaved, onCancel, onDelete, defaultScope, defaultProjectId }: {
  persona?: Persona | null;
  projects: Project[];
  onSaved: (p: Persona) => void;
  onCancel?: () => void;
  onDelete?: (p: Persona) => void;
  // Дефолты зоны при создании (persona=null): для проектной панели агентов —
  // сразу «Проект» + id текущего проекта, чтобы агент создавался проектным.
  defaultScope?: PersonaScope;
  defaultProjectId?: string;
}) {
  const isEdit = !!persona;
  const isMobile = useIsMobile();
  const models = useModels();

  const [name, setName] = useState(persona?.name ?? '');
  const [role, setRole] = useState(persona?.role ?? '');
  const [description, setDescription] = useState(persona?.description ?? '');
  const [systemPrompt, setSystemPrompt] = useState(persona?.systemPrompt ?? '');
  const [model, setModel] = useState(persona?.model ?? '');
  const [scope, setScope] = useState<PersonaScope>(persona?.scope ?? defaultScope ?? 'global');
  const [projectId, setProjectId] = useState(persona?.projectId ?? defaultProjectId ?? '');
  const [greeting, setGreeting] = useState(persona?.greeting ?? '');
  const [color, setColor] = useState(persona?.avatar?.color ?? 'orange');
  const [memoryEnabled, setMemoryEnabled] = useState(persona?.memoryEnabled ?? false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Аватар: текущее состояние (обновляется после выбора кандидата), возможность
  // генерации (настроен ли fal), поле промпта и статус генерации.
  const [avatar, setAvatar] = useState<Persona['avatar']>(persona?.avatar ?? { kind: 'initials', color: 'orange' });
  const [canGenerate, setCanGenerate] = useState(false);
  const [showPrompt, setShowPrompt] = useState(false);
  const [avatarPrompt, setAvatarPrompt] = useState('');
  const [generating, setGenerating] = useState(false);
  const [avatarError, setAvatarError] = useState<string | null>(null);
  // Галерея сгенерированных кандидатов (имена файлов) — выбор перекладывает картинку в аватар
  const [candidates, setCandidates] = useState<string[]>([]);
  const [selecting, setSelecting] = useState<string | null>(null);

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

  // Генерация 4 вариантов аватара — показываем сеткой, аватар не меняем до выбора
  const generateAvatar = async () => {
    if (!persona || generating) return;
    setGenerating(true);
    setAvatarError(null);
    setCandidates([]);
    try {
      const { candidates: files } = await api.personas.generateAvatar(persona.id, { prompt: avatarPrompt, count: 4 });
      setCandidates(files);
      setShowPrompt(false);
    } catch (e) {
      setAvatarError(e instanceof Error ? e.message : 'Не удалось сгенерировать аватар');
    } finally {
      setGenerating(false);
    }
  };

  // Выбор кандидата из галереи → становится аватаром персоны
  const chooseCandidate = async (file: string) => {
    if (!persona || selecting) return;
    setSelecting(file);
    setAvatarError(null);
    try {
      const updated = await api.personas.selectAvatar(persona.id, file);
      setAvatar(updated.avatar);
      bumpPersonas();
      setCandidates([]);
      setAvatarPrompt('');
    } catch (e) {
      setAvatarError(e instanceof Error ? e.message : 'Не удалось выбрать аватар');
    } finally {
      setSelecting(null);
    }
  };

  // Дополнение системного промпта заготовкой тона (не дублируем уже добавленную)
  const appendTone = (text: string) => {
    setSystemPrompt(prev => {
      const cur = prev.trimEnd();
      if (cur.includes(text)) return prev;
      return cur ? `${cur}\n${text}` : text;
    });
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
      role: role.trim() || undefined,
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
    <div style={{ height: '100%', display: 'flex', flexDirection: 'column', overflow: 'hidden', background: C.bgMain }}>
      {/* Прокручиваемое тело формы */}
      <div style={{ flex: 1, minHeight: 0, overflowY: 'auto', padding: isMobile ? '20px 16px 28px' : '28px 24px 36px' }}>
        <div style={{ maxWidth: 620, margin: '0 auto', display: 'flex', flexDirection: 'column', gap: 16 }}>
          <Field label="Роль" hint="Главная подпись агента: «Роль (Имя)»">
            <TextField value={role} onChange={setRole} placeholder="Дизайнер, PM, Тестировщик…" autoFocus />
          </Field>

          <Field label="Имя">
            <TextField value={name} onChange={setName} placeholder="Например, Ассистент" />
          </Field>

          <Field label="Описание" hint="Короткая подпись под именем в списке">
            <TextField value={description} onChange={setDescription} placeholder="Чем занимается агент" />
          </Field>

          <Field label="Характер" hint="Системный промпт: тон, роль, правила поведения">
            <TextArea value={systemPrompt} onChange={setSystemPrompt} minHeight={90}
              placeholder="Ты — внимательный ассистент. Отвечай кратко и по делу…" />
            {/* Пресеты тона: клик дополняет промпт заготовкой (пользователь может править текст свободно) */}
            <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6, marginTop: 8 }}>
              {TONE_PRESETS.map(p => (
                <button
                  key={p.label}
                  type="button"
                  onClick={() => appendTone(p.text)}
                  title={p.text}
                  style={presetChip}
                  onMouseEnter={e => { e.currentTarget.style.background = C.accentLight; e.currentTarget.style.borderColor = C.accent; }}
                  onMouseLeave={e => { e.currentTarget.style.background = C.bgWhite; e.currentTarget.style.borderColor = C.border; }}
                >
                  {p.label}
                </button>
              ))}
            </div>
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
                      {generating ? 'Генерирую 4 варианта…' : showPrompt ? '✨ Сгенерировать 4 варианта' : '✨ Сгенерировать аватар'}
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
            {/* Индикатор генерации галереи */}
            {generating && (
              <div style={{ marginTop: 10, fontSize: 12.5, color: C.textSecondary, fontFamily: FONT.sans }}>
                Генерирую 4 варианта…
              </div>
            )}
            {/* Сетка кандидатов 2×2 — клик выбирает аватар */}
            {candidates.length > 0 && !generating && persona && (
              <div style={{ marginTop: 12 }}>
                <div style={{ fontSize: 12, color: C.textMuted, fontFamily: FONT.sans, marginBottom: 8 }}>
                  Выберите вариант:
                </div>
                <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 12, maxWidth: 220 }}>
                  {candidates.map(file => {
                    const itemBusy = selecting === file;
                    return (
                      <button
                        key={file}
                        type="button"
                        onClick={() => chooseCandidate(file)}
                        disabled={!!selecting}
                        title="Выбрать этот аватар"
                        style={{
                          position: 'relative', padding: 0, border: `2px solid ${C.border}`, background: C.bgWhite,
                          borderRadius: R.full, cursor: selecting ? 'default' : 'pointer', aspectRatio: '1 / 1',
                          overflow: 'hidden', opacity: selecting && !itemBusy ? 0.5 : 1,
                          transition: 'border-color 0.15s, transform 0.1s',
                        }}
                        onMouseEnter={e => { if (!selecting) e.currentTarget.style.borderColor = C.accent; }}
                        onMouseLeave={e => { e.currentTarget.style.borderColor = C.border; }}
                      >
                        <img
                          src={api.personas.avatarCandidateUrl(persona.id, file)}
                          alt="Вариант аватара"
                          style={{ width: '100%', height: '100%', objectFit: 'cover', display: 'block' }}
                        />
                        {itemBusy && (
                          <span style={{
                            position: 'absolute', inset: 0, display: 'flex', alignItems: 'center', justifyContent: 'center',
                            background: 'rgba(0,0,0,0.35)', color: '#fff', fontSize: 11, fontFamily: FONT.sans,
                          }}>Применяю…</span>
                        )}
                      </button>
                    );
                  })}
                </div>
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
      </div>

      {/* Панель действий — прижата к низу контентной зоны */}
      <div style={{
        flex: 'none', borderTop: `1px solid ${C.border}`, background: C.bgPanel,
        padding: isMobile ? '10px 16px' : '12px 24px', display: 'flex', alignItems: 'center', gap: 8,
      }}>
        {isEdit && onDelete && (
          <button onClick={() => onDelete(persona!)} style={btnDanger}>Удалить</button>
        )}
        <div style={{ flex: 1 }} />
        {onCancel && (
          <button onClick={onCancel} style={btnGhost}>Отмена</button>
        )}
        <button onClick={save} disabled={!canSave || busy} style={{ ...btnPrimary, opacity: !canSave || busy ? 0.6 : 1 }}>
          {isEdit ? 'Сохранить' : 'Создать'}
        </button>
      </div>
    </div>
  );
}

// Пресеты тона характера — клик добавляет заготовку в системный промпт
const TONE_PRESETS: { label: string; text: string }[] = [
  { label: 'Дружелюбный', text: 'Общайся тепло и дружелюбно, на равных.' },
  { label: 'Деловой', text: 'Держи деловой, профессиональный тон. Формулируй чётко и по существу.' },
  { label: 'Краткий', text: 'Отвечай кратко и по делу, без воды.' },
  { label: 'Ментор', text: 'Выступай как наставник: объясняй причины, задавай наводящие вопросы, помогай разобраться.' },
  { label: 'С юмором', text: 'Добавляй лёгкий уместный юмор, но не в ущерб пользе ответа.' },
];

const presetChip: React.CSSProperties = {
  background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.pill,
  padding: '4px 11px', fontSize: 12, cursor: 'pointer', fontFamily: FONT.sans,
  color: C.textSecondary, whiteSpace: 'nowrap', transition: 'background 0.12s, border-color 0.12s',
};

const selectStyle: React.CSSProperties = {
  width: '100%', boxSizing: 'border-box', background: C.bgWhite, border: `1px solid ${C.border}`,
  borderRadius: R.xl, padding: '10px 13px', fontSize: 14, fontFamily: FONT.sans,
  color: C.textHeading, outline: 'none', cursor: 'pointer',
};
const btnGhost: React.CSSProperties = {
  background: 'transparent', border: `1px solid ${C.border}`, borderRadius: R.lg,
  padding: '9px 16px', fontSize: 13, cursor: 'pointer', fontFamily: FONT.sans, color: C.textSecondary,
};
const btnPrimary: React.CSSProperties = {
  background: C.accent, color: C.onAccent, border: 'none', borderRadius: R.lg,
  padding: '9px 18px', fontSize: 13, fontWeight: 500, cursor: 'pointer', fontFamily: FONT.sans,
};
const btnDanger: React.CSSProperties = {
  background: 'transparent', border: `1px solid ${C.border}`, borderRadius: R.lg,
  padding: '9px 16px', fontSize: 13, cursor: 'pointer', fontFamily: FONT.sans, color: C.dangerText,
};
