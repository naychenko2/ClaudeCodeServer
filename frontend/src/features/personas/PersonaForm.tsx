import { forwardRef, useEffect, useImperativeHandle, useMemo, useState } from 'react';
import type { Persona, PersonaContract, PersonaMemoryEntry, PersonaMemoryType, PersonaScope, Project } from '../../types';
import { api } from '../../lib/api';
import { Field, FieldLabel, TextField, TextArea, Toggle, Button, SegmentedControl } from '../../components/ui';
import { PillSwitch } from '../../components/Toolbar';
import { ModelPicker } from '../../components/ModelPicker';
import { useModels, useModelCaps, modelProvider } from '../../lib/models';
import { effortsForProvider } from '../../lib/effort';
import { AGENT_COLORS, agentDotColor } from '../../components/AgentSelector';
import { bumpPersonas } from '../../lib/personas';
import { C, FONT, R, SHADOW } from '../../lib/design';
import { SectionLabel } from '../tasks/bits';
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

// Императивный API формы для тулбара-родителя: сохранить / удалить.
export interface PersonaFormHandle {
  save: () => Promise<void>;
  remove: () => void;
}

// Состояние формы, которое родитель отражает в кнопках тулбара
export interface PersonaFormStatus {
  canSave: boolean;
  saving: boolean;
  dirty: boolean;
}

// Предзаполнение формы создания из шаблона персоны (см. personaTemplates.ts)
export interface PersonaFormInitial {
  role?: string;
  description?: string;
  contract?: PersonaContract;
  greeting?: string;
  color?: string;
  tools?: string[];
}

interface PersonaFormProps {
  persona?: Persona | null;
  projects: Project[];
  onSaved: (p: Persona) => void;
  onDelete?: (p: Persona) => void;
  // Предзаполнение при создании (выбор шаблона); для существующей персоны игнорируется
  initial?: PersonaFormInitial;
  // Родитель подписывается на состояние формы, чтобы включать/выключать кнопки тулбара
  onStatus?: (s: PersonaFormStatus) => void;
  // Живой цвет персоны — чтобы тулбар/полоса перекрашивались мгновенно при выборе цвета
  onColorChange?: (color: string) => void;
  // Клик по «Открыть память →» в summary-карточке памяти — родитель переключит вид на «Память»
  onOpenMemory?: () => void;
  // Дефолты зоны при создании (persona=null): для проектной панели персон —
  // сразу «Проект» + id текущего проекта, чтобы персона создавалась проектной.
  defaultScope?: PersonaScope;
  defaultProjectId?: string;
}

// Инлайн-форма создания/редактирования персоны (без Modal-обёртки).
// Редизайн «Студия-профиль»: ОДНА центрированная колонка с плоскими секциями,
// разделёнными тонкой линией (паттерн TaskEditForm/FeatureFlagsModal),
// character-first (идентичность → характер → поведение → память). Кнопки действий
// (сохранить/удалить/отмена) живут в тулбаре РОДИТЕЛЯ: форма экспонирует
// save()/remove() через ref и сообщает своё состояние через onStatus.
export const PersonaForm = forwardRef<PersonaFormHandle, PersonaFormProps>(function PersonaForm(
  { persona, projects, onSaved, onDelete, initial, onStatus, onColorChange, onOpenMemory, defaultScope, defaultProjectId }, ref,
) {
  const isEdit = !!persona;
  const isMobile = useIsMobile();
  const models = useModels();

  const [name, setName] = useState(persona?.name ?? '');
  const [role, setRole] = useState(persona?.role ?? initial?.role ?? '');
  const [description, setDescription] = useState(persona?.description ?? initial?.description ?? '');
  // Контракт характера (P1) по слотам. Legacy-персона (без contract) — её systemPrompt
  // предзаполняет слот «Характер» и при сохранении переезжает в контракт.
  const legacyMigrated = !!persona && !persona.contract && !!(persona.systemPrompt ?? '').trim();
  const [character, setCharacter] = useState(
    persona ? (persona.contract?.character ?? persona.systemPrompt ?? '') : (initial?.contract?.character ?? ''));
  const [tone, setTone] = useState(persona ? (persona.contract?.tone ?? '') : (initial?.contract?.tone ?? ''));
  // «Всегда»/«Никогда» — textarea «строка = пункт»
  const [mustDo, setMustDo] = useState(
    ((persona ? persona.contract?.mustDo : initial?.contract?.mustDo) ?? []).join('\n'));
  const [mustNot, setMustNot] = useState(
    ((persona ? persona.contract?.mustNot : initial?.contract?.mustNot) ?? []).join('\n'));
  const [outputFormat, setOutputFormat] = useState(
    persona ? (persona.contract?.outputFormat ?? '') : (initial?.contract?.outputFormat ?? ''));
  const [speechExamples, setSpeechExamples] = useState<string[]>(
    (persona ? persona.contract?.speechExamples : initial?.contract?.speechExamples) ?? []);
  const [model, setModel] = useState(persona?.model ?? '');
  const [effort, setEffort] = useState(persona?.effort ?? '');
  const [scope, setScope] = useState<PersonaScope>(persona?.scope ?? defaultScope ?? 'global');
  const [projectId, setProjectId] = useState(persona?.projectId ?? defaultProjectId ?? '');
  const [greeting, setGreeting] = useState(persona?.greeting ?? initial?.greeting ?? '');
  const [color, setColor] = useState(persona?.avatar?.color ?? initial?.color ?? 'orange');
  // Возможности персоны: массив включённых ключей. null у персоны = все включены.
  const [tools, setTools] = useState<string[]>(
    persona ? (persona.tools ?? ALL_TOOL_KEYS) : (initial?.tools ?? ALL_TOOL_KEYS));
  const [memoryEnabled, setMemoryEnabled] = useState(persona?.memoryEnabled ?? false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Аватар: текущее состояние (обновляется после выбора кандидата), возможность
  // генерации (настроен ли fal), поле промпта и статус генерации.
  const [avatar, setAvatar] = useState<Persona['avatar']>(persona?.avatar ?? { kind: 'initials', color: initial?.color ?? 'orange' });
  const [canGenerate, setCanGenerate] = useState(false);
  const [avatarPrompt, setAvatarPrompt] = useState('');
  const [generating, setGenerating] = useState(false);
  const [avatarError, setAvatarError] = useState<string | null>(null);
  // Галерея сгенерированных кандидатов (имена файлов) — выбор перекладывает картинку в аватар
  const [candidates, setCandidates] = useState<string[]>([]);
  const [selecting, setSelecting] = useState<string | null>(null);
  // Инлайн-панель «Внешность» под hero (раскрывается по ✎ на аватаре)
  const [showAppearance, setShowAppearance] = useState(false);
  const [avatarHover, setAvatarHover] = useState(false);

  // AI-редактирование характера: активное действие (генерация/улучшение), ошибка,
  // общий поповер с необязательным уточняющим промптом для обоих режимов.
  const [aiAction, setAiAction] = useState<null | 'generate' | 'improve'>(null);
  const [aiError, setAiError] = useState<string | null>(null);
  const [aiInstruction, setAiInstruction] = useState('');
  const [aiPopover, setAiPopover] = useState<null | 'generate' | 'improve'>(null);

  // Сводка долгой памяти (для summary-карточки) — считаем из записей по типу
  const [memoryEntries, setMemoryEntries] = useState<PersonaMemoryEntry[] | null>(null);

  // Возможности провайдера выбранной модели — показываем «Усилие рассуждения» только если поддерживается
  const caps = useModelCaps(model);

  // Акцент персоны из выбранного цвета — им красим роль в hero и (через onColorChange) тулбар
  const accentColor = AGENT_COLORS[color] ?? C.accent;

  // Один раз узнаём, доступна ли генерация аватара (fal настроен)
  useEffect(() => { api.personas.avatarCaps().then(c => setCanGenerate(c.generate)).catch(() => {}); }, []);

  // Сводка памяти: грузим один раз при редактировании существующей персоны
  useEffect(() => {
    if (!persona) { setMemoryEntries(null); return; }
    let alive = true;
    api.personas.memory(persona.id).then(list => { if (alive) setMemoryEntries(list); }).catch(() => { if (alive) setMemoryEntries([]); });
    return () => { alive = false; };
  }, [persona]);

  // Живой цвет — в родителя (перекраска полосы/тулбара)
  useEffect(() => { onColorChange?.(color); }, [color, onColorChange]);

  // Персона для превью аватара: актуальные имя/аватар из формы. Цвет для инициалов
  // берём из выбранного color; при наличии картинки — kind='image' показывает её.
  const previewPersona: Persona = {
    ...(persona ?? ({
      id: '', ownerId: '', handle: '', scope, memoryEnabled,
      createdAt: '', updatedAt: '',
    } as Persona)),
    name: name.trim() || 'Персона',
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

  // Разбор textarea «строка = пункт» в список правил
  const parseLines = (s: string) => s.split('\n').map(l => l.trim()).filter(Boolean);

  // Текущий контракт из стейтов формы — для сохранения и как current при AI-улучшении
  const buildContract = (): PersonaContract => ({
    character: character.trim() || undefined,
    tone: tone.trim() || undefined,
    mustDo: parseLines(mustDo),
    mustNot: parseLines(mustNot),
    outputFormat: outputFormat.trim() || undefined,
    speechExamples: speechExamples.map(s => s.trim()).filter(Boolean),
  });

  // Заполнен ли хоть один слот контракта — от этого зависит доступность «Улучшить»
  const contractFilled = !!(character.trim() || tone.trim() || mustDo.trim() || mustNot.trim()
    || outputFormat.trim() || speechExamples.some(s => s.trim()));

  // Ответ AI заполняет ВСЕ слоты контракта (перезаписывает текущие)
  const applyContract = (c: PersonaContract) => {
    setCharacter(c.character ?? '');
    setTone(c.tone ?? '');
    setMustDo((c.mustDo ?? []).join('\n'));
    setMustNot((c.mustNot ?? []).join('\n'));
    setOutputFormat(c.outputFormat ?? '');
    setSpeechExamples((c.speechExamples ?? []).slice(0, 3));
  };

  // AI-характер: генерация с нуля (по роли/имени/описанию) или улучшение текущего
  // контракта (уходит сериализованным JSON). Необязательный уточняющий промпт — в обоих режимах.
  const runAiCharacter = async (mode: 'generate' | 'improve') => {
    if (aiAction) return;
    setAiAction(mode);
    setAiError(null);
    try {
      const { contract } = await api.personas.aiCharacter({
        name: name.trim() || undefined,
        role: role.trim() || undefined,
        description: description.trim() || undefined,
        current: mode === 'improve' ? JSON.stringify(buildContract()) : undefined,
        instruction: aiInstruction.trim() || undefined,
      });
      applyContract(contract);
      setAiInstruction('');
      setAiPopover(null);
    } catch (e) {
      setAiError(e instanceof Error
        ? e.message
        : mode === 'generate' ? 'Не удалось сгенерировать характер' : 'Не удалось улучшить характер');
    } finally {
      setAiAction(null);
    }
  };

  // При выборе зоны «Проект» без выбранного проекта — подставим первый доступный
  useEffect(() => {
    if (scope === 'project' && !projectId && projects.length > 0) setProjectId(projects[0].id);
  }, [scope, projectId, projects]);

  const canSave = name.trim().length > 0 && !(scope === 'project' && !projectId);

  // Снимок редактируемых полей — для вычисления «есть несохранённые правки» (dirty)
  const snapshot = JSON.stringify({
    name: name.trim(), role: role.trim(), description: description.trim(),
    contract: {
      character: character.trim(), tone: tone.trim(),
      mustDo: parseLines(mustDo), mustNot: parseLines(mustNot),
      outputFormat: outputFormat.trim(),
      speechExamples: speechExamples.map(s => s.trim()).filter(Boolean),
    },
    model, effort, scope,
    projectId: scope === 'project' ? projectId : '',
    color, greeting: greeting.trim(), memoryEnabled,
    tools: [...tools].sort(),
  });
  // Исходный снимок считается от предзаполненного состояния: у legacy-персоны
  // слот «Характер» = systemPrompt, поэтому сама миграция не делает форму dirty.
  const initialSnapshot = useMemo(() => {
    const s = persona?.scope ?? defaultScope ?? 'global';
    return JSON.stringify({
      name: (persona?.name ?? '').trim(),
      role: (persona?.role ?? '').trim(),
      description: (persona?.description ?? '').trim(),
      contract: {
        character: (persona?.contract?.character ?? persona?.systemPrompt ?? '').trim(),
        tone: (persona?.contract?.tone ?? '').trim(),
        mustDo: persona?.contract?.mustDo ?? [],
        mustNot: persona?.contract?.mustNot ?? [],
        outputFormat: (persona?.contract?.outputFormat ?? '').trim(),
        speechExamples: (persona?.contract?.speechExamples ?? []).map(x => x.trim()).filter(Boolean),
      },
      model: persona?.model ?? '',
      effort: persona?.effort ?? '',
      scope: s,
      projectId: s === 'project' ? (persona?.projectId ?? defaultProjectId ?? '') : '',
      color: persona?.avatar?.color ?? 'orange',
      greeting: (persona?.greeting ?? '').trim(),
      memoryEnabled: persona?.memoryEnabled ?? false,
      tools: [...(persona ? (persona.tools ?? ALL_TOOL_KEYS) : ALL_TOOL_KEYS)].sort(),
    });
  }, [persona, defaultScope, defaultProjectId]);
  const dirty = snapshot !== initialSnapshot;

  const save = async () => {
    if (!canSave || busy) return;
    setBusy(true);
    setError(null);
    const dto = {
      name: name.trim(),
      role: role.trim() || undefined,
      description: description.trim() || undefined,
      // Характер — только контрактом; legacy-поле systemPrompt чистим при каждом сохранении
      contract: buildContract(),
      systemPrompt: '',
      model: model || undefined,
      effort: effort || undefined,
      scope,
      projectId: scope === 'project' ? projectId : undefined,
      color,
      greeting: greeting.trim() || undefined,
      memoryEnabled,
      // Всегда явный список: полный набор бэкенд нормализует в «без ограничений»
      tools,
    };
    try {
      const saved = isEdit
        ? await api.personas.update(persona!.id, dto)
        : await api.personas.create(dto);
      bumpPersonas();
      onSaved(saved);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Не удалось сохранить персону');
    } finally {
      setBusy(false);
    }
  };

  // Отдаём тулбару-родителю императивные действия
  useImperativeHandle(ref, () => ({
    save,
    remove: () => { if (persona && onDelete) onDelete(persona); },
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }), [save, persona, onDelete]);

  // Сообщаем родителю состояние формы (для кнопок тулбара)
  useEffect(() => {
    onStatus?.({ canSave, saving: busy, dirty });
  }, [canSave, busy, dirty, onStatus]);

  // Плоская секция без фона/рамки; начиная со второй — разделитель сверху
  // (паттерн FeatureFlagsModal: borderTop borderLight + отступ)
  const section: React.CSSProperties = {
    borderTop: `1px solid ${C.borderLight}`, paddingTop: 22,
  };

  const wordCount = character.trim() ? character.trim().split(/\s+/).length : 0;

  // === Секция 1 — Идентичность (hero) ===
  const heroSection = (
    <div>
      <div style={{ display: 'flex', gap: 18, alignItems: 'flex-start', flexDirection: isMobile ? 'column' : 'row' }}>
        {/* Крупный аватар с ✎-оверлеем при ховере */}
        <div
          style={{ position: 'relative', flexShrink: 0, alignSelf: isMobile ? 'center' : 'flex-start' }}
          onMouseEnter={() => setAvatarHover(true)}
          onMouseLeave={() => setAvatarHover(false)}
        >
          <PersonaAvatar persona={previewPersona} size={80} />
          <button
            type="button"
            onClick={() => setShowAppearance(v => !v)}
            aria-label="Изменить внешность"
            title="Изменить внешность"
            style={{
              position: 'absolute', right: -2, bottom: -2, width: 28, height: 28, borderRadius: R.full,
              border: `2px solid ${C.bgMain}`, background: showAppearance ? accentColor : C.bgWhite,
              color: showAppearance ? '#fff' : C.textSecondary, cursor: 'pointer',
              display: 'flex', alignItems: 'center', justifyContent: 'center',
              opacity: showAppearance || avatarHover || isMobile ? 1 : 0, transition: 'opacity 0.15s, background 0.15s',
              boxShadow: SHADOW.thumb,
            }}
          >
            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
              <path d="M12 20h9" /><path d="M16.5 3.5a2.12 2.12 0 0 1 3 3L7 19l-4 1 1-4Z" />
            </svg>
          </button>
        </div>

        {/* Роль (крупно, serif, в цвет персоны) / Имя / Описание */}
        <div style={{ flex: 1, minWidth: 0, width: isMobile ? '100%' : undefined, display: 'flex', flexDirection: 'column', gap: 12 }}>
          <div style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
            <FieldLabel>Роль *</FieldLabel>
            {/* Крупный serif-ввод продукта — как заголовок в TaskEditForm */}
            <input
              value={role}
              onChange={e => setRole(e.target.value)}
              placeholder="Дизайнер, PM, Тестировщик…"
              autoFocus
              style={{
                width: '100%', boxSizing: 'border-box',
                border: 'none', outline: 'none', background: 'transparent',
                fontFamily: FONT.serif, fontSize: isMobile ? 21 : 24, fontWeight: 500,
                color: C.textHeading, padding: 0, lineHeight: 1.3,
              }}
            />
          </div>
          <div style={{ display: 'flex', gap: 12, flexDirection: isMobile ? 'column' : 'row' }}>
            <div style={{ flex: 1, minWidth: 0 }}>
              <Field label="Имя">
                <TextField value={name} onChange={setName} placeholder="Например, Ассистент" />
              </Field>
            </div>
            <div style={{ flex: 1, minWidth: 0 }}>
              <Field label="Описание" hint="Короткая подпись под именем в списке">
                <TextField value={description} onChange={setDescription} placeholder="Чем занимается персона" />
              </Field>
            </div>
          </div>
        </div>
      </div>

      {/* Инлайн-панель «Внешность» — под hero, раскрывается по ✎ */}
      {showAppearance && (
        <div style={{
          marginTop: 18, paddingTop: 18, borderTop: `1px solid ${C.borderLight}`,
          display: 'flex', gap: 24, flexDirection: isMobile ? 'column' : 'row',
        }}>
          {/* Генерация фото-аватара */}
          <div style={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', gap: 10 }}>
            <FieldLabel>Фото-аватар</FieldLabel>
            {!isEdit ? (
              <span style={{ fontSize: 12.5, color: C.textSecondary, fontFamily: FONT.sans, lineHeight: 1.5 }}>
                Генерация появится после сохранения персоны. Пока можно выбрать цвет инициалов.
              </span>
            ) : !canGenerate ? (
              <span style={{ fontSize: 12.5, color: C.textSecondary, fontFamily: FONT.sans, lineHeight: 1.5 }}>
                Генерация недоступна (fal не настроен). Доступен выбор цвета инициалов.
              </span>
            ) : (
              <>
                <TextField value={avatarPrompt} onChange={setAvatarPrompt}
                  placeholder="Опишите внешность (необязательно): рыжий кот в очках" />
                <div>
                  <Button variant="ghostAccent" size="sm" loading={generating} disabled={generating} onClick={generateAvatar}>
                    {generating ? 'Генерирую 4 варианта…' : '✨ Сгенерировать 4 варианта'}
                  </Button>
                </div>
                {candidates.length > 0 && !generating && persona && (
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
                            transition: 'border-color 0.15s',
                          }}
                          onMouseEnter={e => { if (!selecting) e.currentTarget.style.borderColor = accentColor; }}
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
                )}
                {avatarError && (
                  <span style={{ fontSize: 12, color: C.dangerText, fontFamily: FONT.sans }}>{avatarError}</span>
                )}
              </>
            )}
          </div>

          {/* Палитра цвета инициалов */}
          <div style={{ flex: 'none', display: 'flex', flexDirection: 'column', gap: 10 }}>
            <FieldLabel>Цвет инициалов</FieldLabel>
            <div style={{ display: 'flex', flexWrap: 'wrap', gap: 8, maxWidth: 190 }}>
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
                      outline: active ? `2px solid ${C.bgMain}` : 'none',
                      outlineOffset: -4,
                    }}
                  />
                );
              })}
            </div>
          </div>
        </div>
      )}
    </div>
  );

  // === Секция 2 — Характер: контракт по слотам (P1) ===
  const characterSection = (
    <div style={section}>
      <div style={{ display: 'flex', alignItems: 'baseline', justifyContent: 'space-between', gap: 10, marginBottom: 4 }}>
        <div style={{ display: 'flex', alignItems: 'baseline', gap: 10, minWidth: 0, flexWrap: 'wrap' }}>
          <SectionLabel>Характер</SectionLabel>
          {legacyMigrated && (
            <span
              style={legacyBadge}
              title="Текст характера перенесён из старого единого промпта — при сохранении он станет структурированным контрактом"
            >
              перенесено из старого формата
            </span>
          )}
        </div>
        <span style={{ fontSize: 11.5, color: C.textMuted, fontFamily: FONT.sans, flexShrink: 0 }}>
          {wordCount} {wordPlural(wordCount)}
        </span>
      </div>

      {/* Липкая мини-панель AI-действий: заполняют все слоты контракта разом */}
      <div style={{
        position: 'sticky', top: 0, zIndex: 2, background: C.bgMain,
        margin: '0 0 12px', padding: '8px 0', borderBottom: `1px solid ${C.borderLight}`,
        display: 'flex', gap: 8, alignItems: 'center', justifyContent: 'flex-end',
      }}>
        <div style={{ display: 'flex', gap: 8, alignItems: 'center', flexShrink: 0, position: 'relative' }}>
          <Button variant="ghostAccent" size="sm" loading={aiAction === 'generate'} disabled={!!aiAction}
            onClick={() => setAiPopover(v => v === 'generate' ? null : 'generate')}>
            {aiAction === 'generate' ? 'Генерирую…' : '✨ Сгенерировать'}
          </Button>
          {contractFilled && (
            <Button variant="ghostAccent" size="sm" loading={aiAction === 'improve'} disabled={!!aiAction}
              onClick={() => setAiPopover(v => v === 'improve' ? null : 'improve')}>
              {aiAction === 'improve' ? 'Улучшаю…' : '✨ Улучшить'}
            </Button>
          )}
          {/* Общий поповер обоих режимов: необязательный уточняющий промпт + запуск */}
          {aiPopover && (
            <>
              {/* Клик вне поповера — закрыть */}
              <div style={{ position: 'fixed', inset: 0, zIndex: 10 }} onClick={() => setAiPopover(null)} />
              <div style={{
                position: 'absolute', top: 'calc(100% + 6px)', right: 0, zIndex: 11, width: 300, maxWidth: '80vw',
                background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.xl, padding: 10,
                display: 'flex', flexDirection: 'column', gap: 8, boxShadow: SHADOW.dropdown,
              }}>
                <span style={{ fontSize: 12, color: C.textSecondary, fontFamily: FONT.sans }}>
                  {aiPopover === 'generate' ? 'Пожелание к характеру (необязательно)' : 'Что изменить? (необязательно)'}
                </span>
                <TextField value={aiInstruction} onChange={setAiInstruction}
                  placeholder={aiPopover === 'generate'
                    ? 'Например: ироничный наставник, любит метафоры'
                    : 'Например: добавить строгости'}
                  onEnter={() => void runAiCharacter(aiPopover)} />
                <div style={{ display: 'flex', justifyContent: 'flex-end' }}>
                  <Button variant="primary" size="sm" loading={!!aiAction} disabled={!!aiAction}
                    onClick={() => runAiCharacter(aiPopover)}>
                    {aiAction ? (aiPopover === 'generate' ? 'Генерирую…' : 'Улучшаю…')
                      : (aiPopover === 'generate' ? 'Сгенерировать' : 'Улучшить')}
                  </Button>
                </div>
              </div>
            </>
          )}
        </div>
      </div>

      {/* Слот «Характер»: манера общения свободным текстом + плейсхолдер-скелет при пустом */}
      <div style={{ position: 'relative' }}>
        <TextArea value={character} onChange={setCharacter}
          autoGrow
          minHeight={isMobile ? 160 : 200}
          style={{ fontSize: 14.5, lineHeight: 1.6 }} />
        {!character && (
          <div style={{
            position: 'absolute', top: 10, left: 13, right: 13, pointerEvents: 'none',
            fontSize: 14.5, lineHeight: 1.6, color: C.textMuted, fontFamily: FONT.sans,
            display: 'flex', flexDirection: 'column', gap: 10,
          }}>
            <span>Ты — …</span>
            <span style={{ opacity: 0.75 }}>Общаешься …</span>
          </div>
        )}
      </div>
      {aiError && (
        <span style={{ display: 'block', marginTop: 8, fontSize: 12, color: C.dangerText, fontFamily: FONT.sans }}>{aiError}</span>
      )}

      {/* Слот «Тон»: пресеты как single-select — выбор пишет текст в редактируемое поле */}
      <div style={{ marginTop: 18, display: 'flex', flexDirection: 'column', gap: 8 }}>
        <FieldLabel>Тон</FieldLabel>
        <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6 }}>
          {TONE_PRESETS.map(p => {
            const active = tone === p.text;
            return (
              <button
                key={p.label}
                type="button"
                onClick={() => setTone(active ? '' : p.text)}
                title={p.text}
                style={{
                  ...presetChip,
                  background: active ? C.accentLight : C.bgWhite,
                  borderColor: active ? C.accent : C.border,
                  color: active ? C.accent : C.textSecondary,
                }}
                onMouseEnter={e => { e.currentTarget.style.background = C.accentLight; e.currentTarget.style.borderColor = C.accent; }}
                onMouseLeave={e => {
                  e.currentTarget.style.background = active ? C.accentLight : C.bgWhite;
                  e.currentTarget.style.borderColor = active ? C.accent : C.border;
                }}
              >
                {p.label}
              </button>
            );
          })}
        </div>
        <TextField value={tone} onChange={setTone} placeholder="Например: тепло и на равных" />
      </div>

      {/* Слоты «Всегда»/«Никогда»: textarea «строка = пункт» */}
      <div style={{ marginTop: 18, display: 'grid', gridTemplateColumns: isMobile ? '1fr' : '1fr 1fr', gap: 18 }}>
        <Field label="Всегда" hint="Каждая строка — отдельное правило">
          <TextArea value={mustDo} onChange={setMustDo} autoGrow minHeight={88}
            placeholder={'Выноси вывод первым\nУточняй при неясности'} />
        </Field>
        <Field label="Никогда" hint="Каждая строка — отдельное правило">
          <TextArea value={mustNot} onChange={setMustNot} autoGrow minHeight={88}
            placeholder={'Не отвечай наугад\nНе хвали из вежливости'} />
        </Field>
      </div>

      {/* Слот «Формат ответов» */}
      <div style={{ marginTop: 18 }}>
        <Field label="Формат ответов" hint="Структура и объём типового ответа">
          <TextArea value={outputFormat} onChange={setOutputFormat} autoGrow minHeight={56}
            placeholder="Краткий вывод, затем аргументы; списки — только где они уместны" />
        </Field>
      </div>

      {/* Слот «Примеры реплик»: до 3 образцов стиля */}
      <div style={{ marginTop: 18, display: 'flex', flexDirection: 'column', gap: 8 }}>
        <FieldLabel>Примеры реплик</FieldLabel>
        {speechExamples.map((example, i) => (
          <div key={i} style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
            <div style={{ flex: 1, minWidth: 0 }}>
              <TextField
                value={example}
                onChange={v => setSpeechExamples(prev => prev.map((p, j) => (j === i ? v : p)))}
                placeholder="Реплика от лица персоны"
              />
            </div>
            <button
              type="button"
              onClick={() => setSpeechExamples(prev => prev.filter((_, j) => j !== i))}
              aria-label="Убрать пример"
              title="Убрать пример"
              style={exampleRemoveBtn}
            >
              ×
            </button>
          </div>
        ))}
        {speechExamples.length < 3 && (
          <div>
            <button
              type="button"
              onClick={() => setSpeechExamples(prev => [...prev, ''])}
              style={addExampleBtn}
            >
              + пример
            </button>
          </div>
        )}
      </div>
    </div>
  );

  // === Секция 3 — Поведение и контекст (сетка 2×2) ===
  const behaviorSection = (
    <div style={section}>
      <SectionLabel style={{ marginBottom: 14 }}>Поведение и контекст</SectionLabel>
      <div style={{ display: 'grid', gridTemplateColumns: isMobile ? '1fr' : '1fr 1fr', gap: 18 }}>
        <Field label="Модель">
          <ModelPicker value={model} options={models} onChange={setModel} />
        </Field>

        {caps.supportsEffort && (
          <Field label="Усилие рассуждения" hint="Выше — глубже размышляет, но дольше и дороже.">
            <SegmentedControl value={effort} options={effortsForProvider(modelProvider(model))} onChange={setEffort} columns={3} />
          </Field>
        )}

        <Field label="Зона">
          <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
            <PillSwitch<PersonaScope>
              fill
              value={scope}
              onChange={setScope}
              options={[{ value: 'global', label: 'Глобальный' }, { value: 'project', label: 'Проект' }]}
            />
            {scope === 'project' && (
              <select
                value={projectId}
                onChange={e => setProjectId(e.target.value)}
                style={selectStyle}
                aria-label="Проект"
              >
                {projects.length === 0 && <option value="">— нет доступных проектов —</option>}
                {projects.map(p => <option key={p.id} value={p.id}>{p.name}</option>)}
              </select>
            )}
          </div>
        </Field>

        <Field label="Приветствие" hint="С чего персона начинает разговор">
          <TextField value={greeting} onChange={setGreeting} placeholder="Привет! Чем помочь?" />
        </Field>
      </div>
    </div>
  );

  // === Секция 3.5 — Возможности (инструменты per-persona) ===
  const toggleTool = (key: string) =>
    setTools(prev => prev.includes(key) ? prev.filter(t => t !== key) : [...prev, key]);

  const toolsSection = (
    <div style={section}>
      <SectionLabel style={{ marginBottom: 4 }}>Возможности</SectionLabel>
      <div style={{ fontSize: 12.5, color: C.textMuted, fontFamily: FONT.sans, marginBottom: 14 }}>
        Какими инструментами персона может пользоваться в чате
      </div>
      <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
        {TOOL_OPTIONS.map(t => (
          <div key={t.key} style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 12 }}>
            <div style={{ minWidth: 0 }}>
              <div style={{ fontSize: 13.5, fontWeight: 600, color: C.textHeading, fontFamily: FONT.sans }}>{t.title}</div>
              <div style={{ fontSize: 12, color: C.textMuted, fontFamily: FONT.sans, marginTop: 1 }}>{t.hint}</div>
            </div>
            <Toggle checked={tools.includes(t.key)} onChange={() => toggleTool(t.key)} />
          </div>
        ))}
      </div>
    </div>
  );

  // === Секция 4 — Долгая память (summary) ===
  const memorySection = (
    <div style={section}>
      <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 12 }}>
        <div>
          <SectionLabel style={{ marginBottom: 10 }}>Долгая память</SectionLabel>
          <span style={{ fontSize: 12.5, color: C.textSecondary, fontFamily: FONT.sans }}>
            {memoryEnabled ? 'Персона запоминает факты между разговорами' : 'Память выключена'}
          </span>
        </div>
        <Toggle checked={memoryEnabled} onChange={setMemoryEnabled} />
      </div>

      {isEdit ? (
        <div style={{ marginTop: 14, display: 'flex', flexDirection: 'column', gap: 12 }}>
          {/* Счётчики по типам */}
          <div style={{ display: 'flex', gap: 10, flexWrap: 'wrap' }}>
            {MEMORY_TYPES.map(t => (
              <div key={t.type} style={memoryStat}>
                <span style={{ width: 8, height: 8, borderRadius: R.full, background: t.color, flexShrink: 0 }} />
                <span style={{ fontSize: 13, fontWeight: 700, color: C.textHeading, fontFamily: FONT.sans }}>
                  {memoryEntries ? memoryEntries.filter(e => e.type === t.type).length : '—'}
                </span>
                <span style={{ fontSize: 12, color: C.textMuted, fontFamily: FONT.sans }}>{t.title}</span>
              </div>
            ))}
          </div>
          {/* Превью одной записи */}
          {memoryEntries && memoryEntries.length > 0 && (
            <div style={{
              fontSize: 12.5, color: C.textSecondary, fontFamily: FONT.sans, lineHeight: 1.5,
              background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.md, padding: '8px 10px',
              overflow: 'hidden', textOverflow: 'ellipsis', display: '-webkit-box', WebkitLineClamp: 2, WebkitBoxOrient: 'vertical',
            }}>
              «{memoryEntries[0].text}»
            </div>
          )}
          {onOpenMemory && (
            <div>
              <button type="button" onClick={onOpenMemory} style={openMemoryBtn}>Открыть память →</button>
            </div>
          )}
        </div>
      ) : (
        <div style={{ marginTop: 12, fontSize: 12.5, color: C.textMuted, fontFamily: FONT.sans }}>
          Память доступна после сохранения персоны.
        </div>
      )}
    </div>
  );

  return (
    <div style={{ height: '100%', overflowY: 'auto', background: C.bgMain }}>
      <div style={{
        maxWidth: 680, margin: '0 auto', boxSizing: 'border-box',
        padding: isMobile ? '18px 16px 32px' : '22px 32px 40px',
        display: 'flex', flexDirection: 'column', gap: 28,
      }}>
        {heroSection}
        {characterSection}
        {behaviorSection}
        {toolsSection}
        {memorySection}
        {error && (
          <div style={{ fontSize: 12.5, color: C.dangerText, fontFamily: FONT.sans }}>{error}</div>
        )}
      </div>
    </div>
  );
});

// Возможности персоны: ключи и подписи тумблеров. Полный набор = «без ограничений».
const ALL_TOOL_KEYS = ['tasks', 'notes', 'web'];
const TOOL_OPTIONS: { key: string; title: string; hint: string }[] = [
  { key: 'tasks', title: 'Задачи', hint: 'Ведёт ваши задачи через инструменты задач' },
  { key: 'notes', title: 'Заметки', hint: 'Читает и пишет в базу знаний' },
  { key: 'web', title: 'Веб', hint: 'Ищет и читает страницы в интернете' },
];

// Пресеты тона — single-select: выбор записывает текст в редактируемый слот «Тон»
const TONE_PRESETS: { label: string; text: string }[] = [
  { label: 'Дружелюбный', text: 'Общайся тепло и дружелюбно, на равных.' },
  { label: 'Деловой', text: 'Держи деловой, профессиональный тон. Формулируй чётко и по существу.' },
  { label: 'Краткий', text: 'Отвечай кратко и по делу, без воды.' },
  { label: 'Ментор', text: 'Выступай как наставник: объясняй причины, задавай наводящие вопросы, помогай разобраться.' },
  { label: 'С юмором', text: 'Добавляй лёгкий уместный юмор, но не в ущерб пользе ответа.' },
];

// Типы памяти для summary-счётчиков
const MEMORY_TYPES: { type: PersonaMemoryType; title: string; color: string }[] = [
  { type: 'semantic', title: 'Факты', color: C.accent },
  { type: 'episodic', title: 'Эпизоды', color: C.info },
  { type: 'procedural', title: 'Приёмы', color: C.success },
];

function wordPlural(n: number): string {
  const mod10 = n % 10, mod100 = n % 100;
  if (mod10 === 1 && mod100 !== 11) return 'слово';
  if (mod10 >= 2 && mod10 <= 4 && (mod100 < 10 || mod100 >= 20)) return 'слова';
  return 'слов';
}

// Бейдж legacy-персоны: характер перенесён из старого единого промпта
const legacyBadge: React.CSSProperties = {
  fontSize: 11, color: C.textMuted, fontFamily: FONT.sans,
  background: C.bgPanel, border: `1px solid ${C.borderLight}`, borderRadius: R.pill,
  padding: '2px 8px', whiteSpace: 'nowrap',
};

// «×» у примера реплики
const exampleRemoveBtn: React.CSSProperties = {
  flexShrink: 0, width: 28, height: 28, borderRadius: R.full,
  background: 'transparent', border: `1px solid ${C.border}`, color: C.textMuted,
  cursor: 'pointer', fontSize: 15, lineHeight: 1, fontFamily: FONT.sans,
  display: 'flex', alignItems: 'center', justifyContent: 'center',
};

// «+ пример» под списком реплик
const addExampleBtn: React.CSSProperties = {
  background: 'transparent', border: 'none', color: C.accent, cursor: 'pointer',
  fontSize: 13, fontWeight: 600, fontFamily: FONT.sans, padding: 0,
};

const presetChip: React.CSSProperties = {
  background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.pill,
  padding: '4px 11px', fontSize: 12, cursor: 'pointer', fontFamily: FONT.sans,
  color: C.textSecondary, whiteSpace: 'nowrap', transition: 'background 0.12s, border-color 0.12s',
};

const memoryStat: React.CSSProperties = {
  display: 'inline-flex', alignItems: 'center', gap: 7,
  background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.lg, padding: '7px 12px',
};

const openMemoryBtn: React.CSSProperties = {
  background: 'transparent', border: 'none', color: C.accent, cursor: 'pointer',
  fontSize: 13, fontWeight: 600, fontFamily: FONT.sans, padding: 0,
};

const selectStyle: React.CSSProperties = {
  width: '100%', boxSizing: 'border-box', background: C.bgWhite, border: `1px solid ${C.border}`,
  borderRadius: R.xl, padding: '10px 13px', fontSize: 14, fontFamily: FONT.sans,
  color: C.textHeading, outline: 'none', cursor: 'pointer',
};
