// Пошаговый мастер создания персоны — единая точка входа (заменяет связку
// PersonaQuickCreate + галерея шаблонов + пустая PersonaForm). Девять шагов:
// Старт (способ+зона) → Основа → Характер → Поведение → Умения и правила →
// Проактивность → Доступ и память → Внешность → Готово.
//
// Черновик персоны создаётся НЕ в конце, а по ходу: путь «по описанию» создаёт
// персону сразу на шаге 1 (quick-create), пути «шаблон»/«с нуля» — в конце шага
// «Основа» (как только заполнено имя). Дальше каждый шаг сохраняет через PUT —
// это уже редактирование существующей персоны, а не первичное создание.
// Шаги «Умения и правила»/«Проактивность» — готовые самостоятельные панели
// студии (PersonaBindingsPanel/PersonaAutomationPanel), просто встроенные сюда:
// у них уже есть собственный AI-подбор и мгновенное сохранение.
//
// Отмена после того, как черновик создан, — с подтверждением и удалением персоны
// (иначе в списке остаются брошенные черновики).

import { useEffect, useRef, useState } from 'react';
import { ChevronLeft, ChevronRight } from 'lucide-react';
import type {
  Persona, PersonaAccess, PersonaContract, PersonaScope,
  PersonaSpecialty, PantheonTemplate, Project,
} from '../../types';
import { api } from '../../lib/api';
import { bumpPersonas, usePersonas } from '../../lib/personas';
import { C, FONT, R } from '../../lib/design';
import { Toolbar, PillSwitch } from '../../components/Toolbar';
import { Field, FieldLabel, TextField, TextArea, Toggle, Button, SegmentedControl, IconButton, ConfirmDialog } from '../../components/ui';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { ModelPicker } from '../../components/ModelPicker';
import { useModels, useModelCaps, modelProvider } from '../../lib/models';
import { effortsForProvider } from '../../lib/effort';
import { AGENT_COLORS, agentDotColor } from '../../components/AgentSelector';
import { PersonaAvatar } from './PersonaAvatar';
import { AvatarCropDialog, type AvatarCropResult } from './AvatarCropDialog';
import { PersonaBindingsPanel } from './PersonaBindingsPanel';
import { PersonaAutomationPanel } from './PersonaAutomationPanel';
import { Stepper } from './stepperUi';
import { PERSONA_TEMPLATES, type PersonaTemplate } from './personaTemplates';

const ALL_TOOL_KEYS = ['tasks', 'notes', 'web'];

const TONE_PRESETS: { label: string; text: string }[] = [
  { label: 'Дружелюбный', text: 'Общайся тепло и дружелюбно, на равных.' },
  { label: 'Деловой', text: 'Держи деловой, профессиональный тон. Формулируй чётко и по существу.' },
  { label: 'Краткий', text: 'Отвечай кратко и по делу, без воды.' },
  { label: 'Ментор', text: 'Выступай как наставник: объясняй причины, задавай наводящие вопросы, помогай разобраться.' },
  { label: 'С юмором', text: 'Добавляй лёгкий уместный юмор, но не в ущерб пользе ответа.' },
];

const WIZARD_STEPS = [
  { n: 1, label: 'Старт' },
  { n: 2, label: 'Основа' },
  { n: 3, label: 'Характер' },
  { n: 4, label: 'Поведение' },
  { n: 5, label: 'Умения' },
  { n: 6, label: 'Проактивность' },
  { n: 7, label: 'Доступ' },
  { n: 8, label: 'Внешность' },
  { n: 9, label: 'Готово' },
];

type Method = 'ai' | 'template' | 'blank';

export function PersonaWizard({ scope, projectId, projects, onOpenStudio, onStartChat, onCancel, onBack, isMobile }: {
  scope: PersonaScope;
  projectId?: string;
  projects: Project[];
  // «Открыть студию персоны» на шаге «Готово»
  onOpenStudio: (p: Persona) => void;
  // «Начать чат» на шаге «Готово» — родитель уже умеет создавать чат персоны (кнопка «Поговорить»)
  onStartChat: (p: Persona) => void;
  onCancel: () => void;
  onBack?: () => void;
  isMobile?: boolean;
}) {
  const models = useModels();
  const [step, setStep] = useState(1);
  const [method, setMethod] = useState<Method>('ai');
  const [aiPrompt, setAiPrompt] = useState('');
  const [selectedTemplateKey, setSelectedTemplateKey] = useState<string | null>(null);
  const [omoTemplates, setOmoTemplates] = useState<PantheonTemplate[] | null>(null);

  useEffect(() => { api.personas.pantheon().then(r => setOmoTemplates(r.templates)).catch(() => {}); }, []);

  const [wizScope, setWizScope] = useState<PersonaScope>(scope);
  const [wizProjectId, setWizProjectId] = useState(projectId ?? projects[0]?.id ?? '');
  useEffect(() => {
    if (wizScope === 'project' && !wizProjectId && projects.length > 0) setWizProjectId(projects[0].id);
  }, [wizScope, wizProjectId, projects]);

  // Персона-черновик: null до первого создания (quick-create на шаге 1 либо
  // create в конце шага 2) — дальше все PUT идут по этому id
  const [persona, setPersona] = useState<Persona | null>(null);
  // Шаги «Умения»/«Проактивность» — готовые панели студии, читающие списки прямо
  // из пропа persona (не только через собственный fetch/событие personas_changed).
  // Наше локальное состояние persona не подписано на глобальный стор, поэтому после
  // мутации внутри панели (addBinding/addAutomation → bumpPersonas) подсовываем им
  // свежую версию из стора, а не свой протухший снимок.
  const allPersonas = usePersonas();
  const livePersona = persona ? (allPersonas.find(p => p.id === persona.id) ?? persona) : null;

  // === Редактируемые поля персоны (по шагам 2-4, 7-8) ===
  const [name, setName] = useState('');
  const [role, setRole] = useState('');
  const [description, setDescription] = useState('');
  const [character, setCharacter] = useState('');
  const [tone, setTone] = useState('');
  const [mustDo, setMustDo] = useState('');
  const [mustNot, setMustNot] = useState('');
  const [outputFormat, setOutputFormat] = useState('');
  const [speechExamples, setSpeechExamples] = useState<string[]>([]);
  const [instructions, setInstructions] = useState('');
  const [advancedOpen, setAdvancedOpen] = useState(false);
  const [model, setModel] = useState('');
  const [effort, setEffort] = useState('');
  const [specialty, setSpecialty] = useState<PersonaSpecialty>('none');
  const [greeting, setGreeting] = useState('');
  const [tools, setTools] = useState<string[]>(ALL_TOOL_KEYS);
  const [access, setAccess] = useState<PersonaAccess>('full');
  const [disallowedText, setDisallowedText] = useState('');
  const [memoryEnabled, setMemoryEnabled] = useState(true);
  const [color, setColor] = useState('orange');

  // AI-характер: генерация/улучшение — не требует существующей персоны
  const [aiCharAction, setAiCharAction] = useState<null | 'generate' | 'improve'>(null);
  const [aiCharError, setAiCharError] = useState<string | null>(null);

  // Аватар (шаг «Внешность»)
  const [canGenerateAvatar, setCanGenerateAvatar] = useState(false);
  const [avatarPrompt, setAvatarPrompt] = useState('');
  const [avatarGenerating, setAvatarGenerating] = useState(false);
  const [avatarError, setAvatarError] = useState<string | null>(null);
  const [avatarCandidates, setAvatarCandidates] = useState<string[]>([]);
  const [avatarSelecting, setAvatarSelecting] = useState<string | null>(null);
  const [cropState, setCropState] = useState<{ src: string; file: File } | null>(null);
  const fileInputRef = useRef<HTMLInputElement | null>(null);
  useEffect(() => { api.personas.avatarCaps().then(c => setCanGenerateAvatar(c.generate)).catch(() => {}); }, []);

  // Шаг «Готово»: живые счётчики умений/правил (не хранятся в локальном persona-снимке)
  const [bindingsCount, setBindingsCount] = useState<number | null>(null);
  const [automationCount, setAutomationCount] = useState<number | null>(null);
  useEffect(() => {
    if (step !== 9 || !persona) return;
    api.personas.bindings(persona.id).then(l => setBindingsCount(l.length)).catch(() => {});
    api.personas.automation(persona.id).then(l => setAutomationCount(l.length)).catch(() => {});
  }, [step, persona]);

  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [showCancelConfirm, setShowCancelConfirm] = useState(false);

  const caps = useModelCaps(model);
  const accentColor = AGENT_COLORS[color] ?? C.accent;

  // FAB AI-хаба сидит в правом нижнем углу поверх всего — у мастера там же кнопка
  // «Далее» в футере. Поднимаем FAB над футером тем же каналом, что ChatPanel — над композером.
  const footerRef = useRef<HTMLDivElement | null>(null);
  useEffect(() => {
    const root = document.documentElement;
    if (step === 9) { root.style.setProperty('--cc-fab-bottom', '20px'); return; }
    const h = footerRef.current?.offsetHeight ?? 64;
    root.style.setProperty('--cc-fab-bottom', `${h + 12}px`);
    return () => { root.style.setProperty('--cc-fab-bottom', '20px'); };
  }, [step]);

  const parseLines = (s: string) => s.split('\n').map(l => l.trim()).filter(Boolean);
  const parseDisallowed = (s: string) => Array.from(new Set(s.split(',').map(t => t.trim()).filter(Boolean)));

  function buildContract(): PersonaContract {
    return {
      character: character.trim() || undefined,
      tone: tone.trim() || undefined,
      mustDo: parseLines(mustDo),
      mustNot: parseLines(mustNot),
      outputFormat: outputFormat.trim() || undefined,
      speechExamples: speechExamples.map(s => s.trim()).filter(Boolean),
      instructions: instructions.trim() || undefined,
    };
  }

  function buildDto() {
    return {
      name: name.trim(),
      role: role.trim() || undefined,
      description: description.trim() || undefined,
      contract: buildContract(),
      systemPrompt: '',
      model: model || undefined,
      effort: effort || undefined,
      scope: wizScope,
      projectId: wizScope === 'project' ? wizProjectId : undefined,
      color,
      greeting: greeting.trim() || undefined,
      memoryEnabled,
      tools,
      access,
      specialty,
      disallowedTools: access === 'custom' ? parseDisallowed(disallowedText) : [],
    };
  }

  // Заполнить локальные поля из уже созданной персоны (после quick-create или после save)
  function hydrateFromPersona(p: Persona) {
    setName(p.name ?? '');
    setRole(p.role ?? '');
    setDescription(p.description ?? '');
    setCharacter(p.contract?.character ?? p.systemPrompt ?? '');
    setTone(p.contract?.tone ?? '');
    setMustDo((p.contract?.mustDo ?? []).join('\n'));
    setMustNot((p.contract?.mustNot ?? []).join('\n'));
    setOutputFormat(p.contract?.outputFormat ?? '');
    setSpeechExamples(p.contract?.speechExamples ?? []);
    setInstructions(p.contract?.instructions ?? '');
    setModel(p.model ?? '');
    setEffort(p.effort ?? '');
    setSpecialty(p.specialty ?? 'none');
    setGreeting(p.greeting ?? '');
    setColor(p.avatar?.color ?? 'orange');
    setTools(p.tools ?? ALL_TOOL_KEYS);
    setAccess(p.access ?? 'full');
    setDisallowedText((p.disallowedTools ?? []).join(', '));
    setMemoryEnabled(p.memoryEnabled ?? true);
  }

  // Предзаполнение из шаблона роли (наш каталог или Пантеон OmO)
  function pickTemplate(t: PersonaTemplate) {
    setSelectedTemplateKey(t.key);
    setName(t.namePlaceholder ?? '');
    setRole(t.role ?? '');
    setDescription(t.description ?? '');
    setCharacter(t.contract.character ?? '');
    setTone(t.contract.tone ?? '');
    setMustDo((t.contract.mustDo ?? []).join('\n'));
    setMustNot((t.contract.mustNot ?? []).join('\n'));
    setOutputFormat(t.contract.outputFormat ?? '');
    setSpeechExamples(t.contract.speechExamples ?? []);
    setInstructions(t.contract.instructions ?? '');
    setGreeting(t.greeting ?? '');
    setColor(t.avatarColor ?? 'orange');
    setTools(t.tools ?? ALL_TOOL_KEYS);
    setAccess(t.access ?? 'full');
    setModel(t.model ?? '');
    setEffort(t.effort ?? '');
    setSpecialty(t.specialty ?? 'none');
  }

  const templates: PersonaTemplate[] = [
    ...PERSONA_TEMPLATES,
    ...(omoTemplates ?? []).map(pantheonToTemplate),
  ];

  // AI-характер: доступно уже на шаге «Характер» — не требует id персоны
  async function runAiCharacter(mode: 'generate' | 'improve') {
    if (aiCharAction) return;
    setAiCharAction(mode);
    setAiCharError(null);
    try {
      const { contract } = await api.personas.aiCharacter({
        name: name.trim() || undefined,
        role: role.trim() || undefined,
        description: description.trim() || undefined,
        current: mode === 'improve' ? JSON.stringify(buildContract()) : undefined,
      });
      setCharacter(contract.character ?? '');
      setTone(contract.tone ?? '');
      setMustDo((contract.mustDo ?? []).join('\n'));
      setMustNot((contract.mustNot ?? []).join('\n'));
      setOutputFormat(contract.outputFormat ?? '');
      setSpeechExamples((contract.speechExamples ?? []).slice(0, 3));
    } catch (e) {
      setAiCharError(e instanceof Error ? e.message : 'Не удалось получить характер');
    } finally {
      setAiCharAction(null);
    }
  }

  const contractFilled = !!(character.trim() || tone.trim() || mustDo.trim() || mustNot.trim()
    || outputFormat.trim() || speechExamples.some(s => s.trim()) || instructions.trim());

  // === Навигация по шагам ===

  const canProceedStep1 = method === 'ai' ? aiPrompt.trim().length > 0 : method === 'template' ? !!selectedTemplateKey : true;
  const canProceedStep2 = name.trim().length > 0 && !(wizScope === 'project' && !wizProjectId);
  const canProceed = step === 1 ? canProceedStep1 : step === 2 ? canProceedStep2 : true;

  async function goNext() {
    setError(null);
    if (step === 1) {
      if (method === 'ai') {
        if (!aiPrompt.trim()) return;
        setBusy(true);
        try {
          const created = await api.personas.quickCreate({
            prompt: aiPrompt.trim(), scope: wizScope,
            projectId: wizScope === 'project' ? wizProjectId : undefined,
          });
          hydrateFromPersona(created);
          setPersona(created);
          bumpPersonas();
          setStep(2);
        } catch (e) {
          setError(e instanceof Error ? e.message : 'Не удалось создать персону. Попробуйте ещё раз.');
        } finally {
          setBusy(false);
        }
        return;
      }
      setStep(2);
      return;
    }
    if (step === 2) {
      if (!canProceedStep2) return;
      setBusy(true);
      try {
        const dto = buildDto();
        const saved = persona ? await api.personas.update(persona.id, dto) : await api.personas.create(dto);
        setPersona(saved);
        bumpPersonas();
        setStep(3);
      } catch (e) {
        setError(e instanceof Error ? e.message : 'Не удалось сохранить персону');
      } finally {
        setBusy(false);
      }
      return;
    }
    if (step === 3 || step === 4 || step === 7 || step === 8) {
      if (persona) {
        setBusy(true);
        try {
          const saved = await api.personas.update(persona.id, buildDto());
          setPersona(saved);
          bumpPersonas();
        } catch (e) {
          setError(e instanceof Error ? e.message : 'Не удалось сохранить персону');
          setBusy(false);
          return;
        }
        setBusy(false);
      }
      setStep(step + 1);
      return;
    }
    // Шаги 5/6 — вложенные панели сохраняют сами по каждому действию
    setStep(step + 1);
  }

  function goBack() {
    if (step > 1) setStep(step - 1);
  }

  function handleCancel() {
    if (!persona) { onCancel(); return; }
    setShowCancelConfirm(true);
  }

  async function confirmCancelDraft() {
    try {
      await api.personas.remove(persona!.id);
      bumpPersonas();
    } catch {
      // даже если удаление черновика не удалось — выходим из мастера
    } finally {
      setShowCancelConfirm(false);
      onCancel();
    }
  }

  // === Аватар ===

  async function generateAvatarCandidates() {
    if (!persona || avatarGenerating) return;
    setAvatarGenerating(true);
    setAvatarError(null);
    setAvatarCandidates([]);
    try {
      const { candidates: files } = await api.personas.generateAvatar(persona.id, { prompt: avatarPrompt, count: 4 });
      setAvatarCandidates(files);
    } catch (e) {
      setAvatarError(e instanceof Error ? e.message : 'Не удалось сгенерировать аватар');
    } finally {
      setAvatarGenerating(false);
    }
  }

  async function chooseAvatarCandidate(file: string) {
    if (!persona || avatarSelecting) return;
    setAvatarSelecting(file);
    setAvatarError(null);
    try {
      const updated = await api.personas.selectAvatar(persona.id, file);
      setPersona(updated);
      bumpPersonas();
      setAvatarCandidates([]);
      setAvatarPrompt('');
    } catch (e) {
      setAvatarError(e instanceof Error ? e.message : 'Не удалось выбрать аватар');
    } finally {
      setAvatarSelecting(null);
    }
  }

  function onAvatarFileChosen(file: File | null) {
    if (!file) return;
    setCropState({ src: URL.createObjectURL(file), file });
  }

  async function applyCrop(result: AvatarCropResult) {
    if (!persona || !cropState) return;
    const updated = await api.personas.uploadAvatar(persona.id, cropState.file, result.blob, result.crop);
    setPersona(updated);
    bumpPersonas();
  }

  function closeCrop() {
    if (cropState) URL.revokeObjectURL(cropState.src);
    setCropState(null);
  }

  // Персона для превью аватара — до создания используем черновик из локальных полей
  const previewPersona: Persona = persona ?? ({
    id: '', ownerId: '', handle: '', scope: wizScope, memoryEnabled,
    createdAt: '', updatedAt: '',
    name: name.trim() || 'Персона',
    avatar: { kind: 'initials', color },
  } as Persona);

  const wordCount = character.trim() ? character.trim().split(/\s+/).length : 0;

  return (
    <div style={{ height: '100%', display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
      <Toolbar isMobile={isMobile} style={{ borderLeft: `3px solid ${accentColor}` }}>
        {onBack && !persona && step === 1 && (
          <IconButton onClick={onBack} title="Назад" size={isMobile ? 'lg' : 'md'}>
            <ChevronLeft size={ICON_SIZE.md} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />
          </IconButton>
        )}
        <div style={{ flex: 1, minWidth: 0, fontFamily: FONT.serif, fontSize: 15, fontWeight: 600, color: C.textHeading, letterSpacing: '-0.01em' }}>
          Новая персона
        </div>
      </Toolbar>
      <div style={{ flex: 'none', height: 2, background: `${accentColor}55` }} />

      <div style={{ flex: 'none', padding: isMobile ? '10px 16px 0' : '12px 24px 0', overflowX: 'auto' }}>
        <Stepper step={step} accent={accentColor} steps={WIZARD_STEPS} onStep={setStep} />
      </div>

      <div style={{ flex: 1, minHeight: 0, overflowY: 'auto' }}>
        <div style={{
          maxWidth: 620, margin: '0 auto', boxSizing: 'border-box',
          padding: isMobile ? '18px 16px 32px' : '20px 24px 40px',
          display: 'flex', flexDirection: 'column', gap: 22, fontFamily: FONT.sans,
        }}>

          {/* === Шаг 1 — Старт === */}
          {step === 1 && (
            <>
              <StepHead title="Как начнём?" subtitle="Выберите, как удобнее — дальше в любом случае сможете всё поправить по шагам." />

              <div style={{ display: 'grid', gridTemplateColumns: isMobile ? '1fr' : '1fr 1fr 1fr', gap: 10 }}>
                <MethodCard active={method === 'ai'} emoji="✨" title="По описанию"
                  desc="ИИ придумает роль, характер, приветствие и подберёт фото" onClick={() => setMethod('ai')} />
                <MethodCard active={method === 'template'} emoji="🗂" title="Из шаблона"
                  desc="Готовая роль с выверенным характером — своя или из команды OmO" onClick={() => setMethod('template')} />
                <MethodCard active={method === 'blank'} emoji="✎" title="С нуля"
                  desc="Пустые поля — настроите всё сами на следующих шагах" onClick={() => setMethod('blank')} />
              </div>

              {method === 'ai' && (
                <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
                  <FieldLabel>Описание</FieldLabel>
                  <TextArea
                    value={aiPrompt} onChange={setAiPrompt} autoGrow minHeight={110}
                    placeholder={'Опишите, кто это и чем будет заниматься… Например: «Личный тренер по бегу: мотивирует, составляет планы тренировок»'}
                  />
                </div>
              )}

              {method === 'template' && (
                <div style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
                  <div style={{ display: 'grid', gridTemplateColumns: isMobile ? '1fr' : '1fr 1fr', gap: 10 }}>
                    {templates.map(t => (
                      <TemplateCard key={t.key} template={t} active={selectedTemplateKey === t.key} onSelect={() => pickTemplate(t)} />
                    ))}
                  </div>
                </div>
              )}

              <div style={{ borderTop: `1px solid ${C.borderLight}`, paddingTop: 20, display: 'flex', flexDirection: 'column', gap: 8 }}>
                <FieldLabel>Зона</FieldLabel>
                <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
                  <PillSwitch<PersonaScope>
                    value={wizScope}
                    onChange={setWizScope}
                    options={[{ value: 'global', label: 'Глобальная' }, { value: 'project', label: 'Проект' }]}
                  />
                  {wizScope === 'project' && (
                    <select value={wizProjectId} onChange={e => setWizProjectId(e.target.value)} style={selectStyle} aria-label="Проект">
                      {projects.length === 0 && <option value="">— нет доступных проектов —</option>}
                      {projects.map(p => <option key={p.id} value={p.id}>{p.name}</option>)}
                    </select>
                  )}
                </div>
              </div>
            </>
          )}

          {/* === Шаг 2 — Основа === */}
          {step === 2 && (
            <>
              <StepHead title="Основа" subtitle="Роль отображается как «Роль (Имя)» везде в интерфейсе." />
              {method === 'ai' && persona && (
                <span style={aiBadge}>✨ предзаполнено ИИ — можно поправить</span>
              )}
              <Field label="Роль">
                <input
                  value={role} onChange={e => setRole(e.target.value)}
                  placeholder="Дизайнер, PM, Тестировщик…" autoFocus
                  style={{
                    width: '100%', boxSizing: 'border-box', border: 'none', outline: 'none', background: 'transparent',
                    fontFamily: FONT.serif, fontSize: isMobile ? 21 : 24, fontWeight: 500, color: C.textHeading, padding: 0, lineHeight: 1.3,
                  }}
                />
              </Field>
              <div style={{ display: 'flex', gap: 12, flexDirection: isMobile ? 'column' : 'row' }}>
                <div style={{ flex: 1, minWidth: 0 }}>
                  <Field label="Имя *"><TextField value={name} onChange={setName} placeholder="Например, Ассистент" /></Field>
                </div>
                <div style={{ flex: 1, minWidth: 0 }}>
                  <Field label="Описание" hint="Короткая подпись под именем в списке">
                    <TextField value={description} onChange={setDescription} placeholder="Чем занимается персона" />
                  </Field>
                </div>
              </div>
            </>
          )}

          {/* === Шаг 3 — Характер === */}
          {step === 3 && (
            <>
              <StepHead title="Характер" subtitle="Манера общения персоны — попадает в системный промпт каждого её хода." />
              <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'flex-end', gap: 8 }}>
                <span style={{ fontSize: 11.5, color: C.textMuted, marginRight: 'auto' }}>{wordCount} {wordPlural(wordCount)}</span>
                <Button variant="ghostAccent" size="sm" loading={aiCharAction === 'generate'} disabled={!!aiCharAction} onClick={() => void runAiCharacter('generate')}>
                  {aiCharAction === 'generate' ? 'Генерирую…' : '✨ Сгенерировать'}
                </Button>
                {contractFilled && (
                  <Button variant="ghostAccent" size="sm" loading={aiCharAction === 'improve'} disabled={!!aiCharAction} onClick={() => void runAiCharacter('improve')}>
                    {aiCharAction === 'improve' ? 'Улучшаю…' : '✨ Улучшить'}
                  </Button>
                )}
              </div>
              <TextArea value={character} onChange={setCharacter} autoGrow minHeight={isMobile ? 140 : 180}
                placeholder={'Ты — …\nОбщаешься …'} style={{ fontSize: 14.5, lineHeight: 1.6 }} />
              {aiCharError && <span style={{ fontSize: 12, color: C.dangerText }}>{aiCharError}</span>}

              <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
                <FieldLabel>Тон</FieldLabel>
                <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6 }}>
                  {TONE_PRESETS.map(p => {
                    const active = tone === p.text;
                    return (
                      <button key={p.label} type="button" onClick={() => setTone(active ? '' : p.text)} title={p.text}
                        style={{ ...presetChip, background: active ? C.accentLight : C.bgWhite, borderColor: active ? C.accent : C.border, color: active ? C.accent : C.textSecondary }}>
                        {p.label}
                      </button>
                    );
                  })}
                </div>
                <TextField value={tone} onChange={setTone} placeholder="Например: тепло и на равных" />
              </div>

              <div>
                <button type="button" onClick={() => setAdvancedOpen(o => !o)} style={disclosureBtn}>
                  <ChevronRight size={13} strokeWidth={2} style={{ transform: advancedOpen ? 'rotate(90deg)' : undefined, transition: 'transform 0.15s' }} />
                  {advancedOpen ? 'Свернуть дополнительные поля' : 'Дополнительно — правила, формат ответов, примеры реплик, регламент'}
                </button>
                {advancedOpen && (
                  <div style={{ marginTop: 14, display: 'flex', flexDirection: 'column', gap: 16 }}>
                    <div style={{ display: 'grid', gridTemplateColumns: isMobile ? '1fr' : '1fr 1fr', gap: 16 }}>
                      <Field label="Всегда" hint="Каждая строка — отдельное правило">
                        <TextArea value={mustDo} onChange={setMustDo} autoGrow minHeight={80} placeholder={'Выноси вывод первым\nУточняй при неясности'} />
                      </Field>
                      <Field label="Никогда" hint="Каждая строка — отдельное правило">
                        <TextArea value={mustNot} onChange={setMustNot} autoGrow minHeight={80} placeholder={'Не отвечай наугад\nНе хвали из вежливости'} />
                      </Field>
                    </div>
                    <Field label="Формат ответов" hint="Структура и объём типового ответа">
                      <TextArea value={outputFormat} onChange={setOutputFormat} autoGrow minHeight={56} placeholder="Краткий вывод, затем аргументы" />
                    </Field>
                    <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
                      <FieldLabel>Примеры реплик</FieldLabel>
                      {speechExamples.map((ex, i) => (
                        <div key={i} style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
                          <div style={{ flex: 1, minWidth: 0 }}>
                            <TextField value={ex} onChange={v => setSpeechExamples(prev => prev.map((p, j) => j === i ? v : p))} placeholder="Реплика от лица персоны" />
                          </div>
                          <button type="button" onClick={() => setSpeechExamples(prev => prev.filter((_, j) => j !== i))} aria-label="Убрать пример" style={exampleRemoveBtn}>×</button>
                        </div>
                      ))}
                      {speechExamples.length < 3 && (
                        <div><button type="button" onClick={() => setSpeechExamples(prev => [...prev, ''])} style={addExampleBtn}>+ пример</button></div>
                      )}
                    </div>
                    <Field label="Инструкция" hint="Полный регламент роли (markdown) — для «тяжёлых» ролей вроде пантеона OmO">
                      <TextArea value={instructions} onChange={setInstructions} autoGrow minHeight={100} maxHeight={320}
                        placeholder="Развёрнутый регламент: протоколы работы, критерии готовности, примеры…" />
                    </Field>
                  </div>
                )}
              </div>
            </>
          )}

          {/* === Шаг 4 — Поведение === */}
          {step === 4 && (
            <>
              <StepHead title="Поведение" subtitle="Модель, приветствие и роль персоны в командных сценариях." />
              <div style={{ display: 'grid', gridTemplateColumns: isMobile ? '1fr' : '1fr 1fr', gap: 18 }}>
                <Field label="Модель"><ModelPicker value={model} options={models} onChange={setModel} /></Field>
                {caps.supportsEffort && (
                  <Field label="Усилие рассуждения" hint="Выше — глубже размышляет, но дольше и дороже.">
                    <SegmentedControl value={effort} options={effortsForProvider(modelProvider(model))} onChange={setEffort} columns={3} />
                  </Field>
                )}
                <Field label="Приветствие" hint="С чего персона начинает разговор">
                  <TextField value={greeting} onChange={setGreeting} placeholder="Привет! Чем помочь?" />
                </Field>
                <Field label="Специальность" hint="Функциональная роль для оркестрации: конвейер, брифинг, статус команды.">
                  <select value={specialty} onChange={e => setSpecialty(e.target.value as PersonaSpecialty)} style={selectStyle} aria-label="Специальность">
                    <option value="none">Не задана</option>
                    <option value="analyst">Аналитик</option>
                    <option value="planner">Планировщик</option>
                    <option value="reviewer">Ревьюер</option>
                    <option value="executor">Исполнитель</option>
                    <option value="secretary">Секретарь</option>
                    <option value="coordinator">Координатор</option>
                    <option value="mentor">Ментор</option>
                    <option value="designer">Дизайнер</option>
                    <option value="consultant">Консультант</option>
                    <option value="librarian">Библиотекарь</option>
                  </select>
                </Field>
              </div>
            </>
          )}

          {/* === Шаг 5 — Умения и правила (панель студии как есть) === */}
          {step === 5 && livePersona && (
            <>
              <StepHead title="Умения и правила" subtitle="Что персона знает и к каким источникам обращается — можно пропустить и настроить позже." />
              <div style={{ margin: '-22px -24px 0' }}>
                <PersonaBindingsPanel persona={livePersona} accent={accentColor} isMobile={!!isMobile} />
              </div>
            </>
          )}

          {/* === Шаг 6 — Проактивность (панель студии как есть) === */}
          {step === 6 && livePersona && (
            <>
              <StepHead title="Проактивность" subtitle="Персона сама реагирует на события — можно пропустить и настроить позже." />
              <div style={{ margin: '-22px -24px 0' }}>
                <PersonaAutomationPanel persona={livePersona} projects={projects} accent={accentColor} isMobile={!!isMobile} />
              </div>
            </>
          )}

          {/* === Шаг 7 — Доступ и память === */}
          {step === 7 && (
            <>
              <StepHead title="Доступ и память" subtitle="Что персоне разрешено менять и запоминает ли она разговоры." />
              <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
                <FieldLabel>Профиль доступа</FieldLabel>
                <SegmentedControl<PersonaAccess>
                  value={access} onChange={setAccess} columns={3}
                  options={[{ value: 'full', label: 'Полный' }, { value: 'readOnly', label: 'Только чтение' }, { value: 'custom', label: 'Свой' }]}
                />
                {access === 'readOnly' && (
                  <span style={{ fontSize: 12.5, color: C.textSecondary, lineHeight: 1.5 }}>
                    Смотрит и советует, но ничего не меняет: без правок файлов, Bash и мутаций задач/заметок
                  </span>
                )}
                {access === 'custom' && (
                  <Field hint="Имена инструментов через запятую, напр. Bash, Edit, mcp__tasks__tasks_delete">
                    <TextArea value={disallowedText} onChange={setDisallowedText} autoGrow minHeight={56} placeholder="Bash, Edit, Write" />
                  </Field>
                )}
              </div>
              <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 12, borderTop: `1px solid ${C.borderLight}`, paddingTop: 20 }}>
                <div>
                  <div style={{ fontSize: 13.5, fontWeight: 600, color: C.textHeading }}>Долгая память</div>
                  <div style={{ fontSize: 12, color: C.textMuted, marginTop: 2 }}>
                    {memoryEnabled ? 'Персона запоминает факты между разговорами' : 'Память выключена'}
                  </div>
                </div>
                <Toggle checked={memoryEnabled} onChange={setMemoryEnabled} />
              </div>
            </>
          )}

          {/* === Шаг 8 — Внешность === */}
          {step === 8 && (
            <>
              <StepHead title="Внешность" subtitle={persona ? 'Персона уже создана — фото генерируется прямо сейчас.' : 'Цвет инициалов — фото станет доступно после сохранения.'} />
              <div style={{ display: 'flex', gap: 18, alignItems: 'center', flexWrap: 'wrap' }}>
                <PersonaAvatar persona={previewPersona} size={64} />
                <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
                  <FieldLabel>Цвет инициалов</FieldLabel>
                  <div style={{ display: 'flex', flexWrap: 'wrap', gap: 8, maxWidth: 260 }}>
                    {Object.keys(AGENT_COLORS).map(key => {
                      const active = key === color;
                      return (
                        <button key={key} type="button" onClick={() => setColor(key)} aria-label={key}
                          style={{
                            width: 28, height: 28, borderRadius: R.full, cursor: 'pointer', background: agentDotColor(key),
                            border: active ? `2px solid ${C.textHeading}` : '2px solid transparent',
                            outline: active ? `2px solid ${C.bgMain}` : 'none', outlineOffset: -4,
                          }} />
                      );
                    })}
                  </div>
                </div>
              </div>

              {persona && canGenerateAvatar && (
                <div style={{ display: 'flex', flexDirection: 'column', gap: 10, borderTop: `1px solid ${C.borderLight}`, paddingTop: 20 }}>
                  <FieldLabel>Фото-аватар</FieldLabel>
                  <TextField value={avatarPrompt} onChange={setAvatarPrompt} placeholder="Опишите внешность (необязательно): рыжий кот в очках" />
                  <div style={{ display: 'flex', gap: 10, flexWrap: 'wrap' }}>
                    <Button variant="ghostAccent" size="sm" loading={avatarGenerating} disabled={avatarGenerating} onClick={() => void generateAvatarCandidates()}>
                      {avatarGenerating ? 'Генерирую 4 варианта…' : '✨ Сгенерировать 4 варианта'}
                    </Button>
                    <Button variant="ghost" size="sm" onClick={() => fileInputRef.current?.click()}>Загрузить своё фото…</Button>
                    <input ref={fileInputRef} type="file" accept="image/jpeg,image/png,image/webp" style={{ display: 'none' }}
                      onChange={e => { onAvatarFileChosen(e.target.files?.[0] ?? null); e.target.value = ''; }} />
                  </div>
                  {avatarCandidates.length > 0 && !avatarGenerating && (
                    <div style={{ display: 'grid', gridTemplateColumns: 'repeat(4, 1fr)', gap: 12, maxWidth: 280 }}>
                      {avatarCandidates.map(file => {
                        const itemBusy = avatarSelecting === file;
                        return (
                          <button key={file} type="button" onClick={() => void chooseAvatarCandidate(file)} disabled={!!avatarSelecting} title="Выбрать этот аватар"
                            style={{
                              position: 'relative', padding: 0, border: `2px solid ${C.border}`, background: C.bgWhite, borderRadius: R.full,
                              cursor: avatarSelecting ? 'default' : 'pointer', aspectRatio: '1 / 1', overflow: 'hidden',
                              opacity: avatarSelecting && !itemBusy ? 0.5 : 1,
                            }}>
                            <img src={api.personas.avatarCandidateUrl(persona.id, file)} alt="Вариант аватара" style={{ width: '100%', height: '100%', objectFit: 'cover', display: 'block' }} />
                            {itemBusy && (
                              <span style={{ position: 'absolute', inset: 0, display: 'flex', alignItems: 'center', justifyContent: 'center', background: 'rgba(0,0,0,0.35)', color: '#fff', fontSize: 11 }}>
                                Применяю…
                              </span>
                            )}
                          </button>
                        );
                      })}
                    </div>
                  )}
                  {avatarError && <span style={{ fontSize: 12, color: C.dangerText }}>{avatarError}</span>}
                </div>
              )}
            </>
          )}

          {/* === Шаг 9 — Готово === */}
          {step === 9 && persona && (
            <>
              <StepHead title="Готово" subtitle="Персона создана. Всё это можно изменить позже в её профиле." />
              <div style={{ background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.xl, padding: 18, display: 'flex', gap: 14, alignItems: 'flex-start' }}>
                <PersonaAvatar persona={persona} size={56} />
                <div style={{ minWidth: 0 }}>
                  <div style={{ fontFamily: FONT.serif, fontSize: 19, fontWeight: 600, color: C.textHeading }}>
                    {persona.role || persona.name} {persona.role && <span style={{ fontFamily: FONT.sans, fontSize: 14, fontWeight: 400, color: C.textSecondary }}>({persona.name})</span>}
                  </div>
                  {persona.description && <div style={{ fontSize: 12.5, color: C.textSecondary, marginTop: 2 }}>{persona.description}</div>}
                  {character.trim() && (
                    <div style={{ fontSize: 12.5, color: C.textMuted, marginTop: 10, lineHeight: 1.5, fontStyle: 'italic' }}>
                      «{character.trim().slice(0, 140)}{character.trim().length > 140 ? '…' : ''}»
                    </div>
                  )}
                  <div style={{ display: 'flex', gap: 6, flexWrap: 'wrap', marginTop: 12 }}>
                    <Tag>{model ? model : 'модель по умолчанию'}{caps.supportsEffort && effort ? ` · ${effort}` : ''}</Tag>
                    <Tag>{wizScope === 'project' ? (projects.find(p => p.id === wizProjectId)?.name ?? 'Проект') : 'Глобальная'}</Tag>
                    <Tag>{access === 'full' ? 'Полный доступ' : access === 'readOnly' ? 'Только чтение' : 'Свой доступ'}</Tag>
                    <Tag>{memoryEnabled ? 'Память включена' : 'Память выключена'}</Tag>
                    {bindingsCount != null && <Tag>{bindingsCount} {bindingsCount === 1 ? 'умение' : 'умений'}</Tag>}
                    {automationCount != null && <Tag>{automationCount} {automationCount === 1 ? 'автоправило' : 'автоправил'}</Tag>}
                  </div>
                </div>
              </div>
              <div style={{ display: 'flex', gap: 10, flexWrap: 'wrap' }}>
                <Button variant="ghost" onClick={() => onOpenStudio(persona)}>Открыть студию персоны →</Button>
                <Button variant="primary" onClick={() => onStartChat(persona)}>Начать чат ✨</Button>
              </div>
            </>
          )}

          {error && <div style={{ fontSize: 12.5, color: C.dangerText }}>{error}</div>}
        </div>
      </div>

      {step !== 9 && (
        <div ref={footerRef} style={{
          flex: 'none', padding: isMobile ? '10px 16px' : '12px 24px', background: C.bgPanel, borderTop: `1px solid ${C.border}`,
        }}>
          <div style={{ maxWidth: 620, margin: '0 auto', display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 10 }}>
            <Button variant="ghost" size="sm" onClick={handleCancel} disabled={busy}>Отмена</Button>
            <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
              {step > 1 && <Button variant="ghost" size="sm" onClick={goBack} disabled={busy}>Назад</Button>}
              <Button variant="primary" size="sm" loading={busy} disabled={!canProceed || busy} onClick={() => void goNext()}>
                {step === 1 && method === 'ai' ? (busy ? 'Генерирую…' : '✨ Сгенерировать и продолжить') : (busy ? 'Сохраняю…' : 'Далее')}
              </Button>
            </div>
          </div>
        </div>
      )}

      {showCancelConfirm && (
        <ConfirmDialog
          title="Отменить создание персоны?"
          subtitle="Черновик будет удалён без возможности восстановления."
          confirmLabel="Удалить черновик"
          confirmVariant="danger"
          onConfirm={() => confirmCancelDraft()}
          onCancel={() => setShowCancelConfirm(false)}
        />
      )}

      {cropState && (
        <AvatarCropDialog src={cropState.src} title="Кадрирование аватара" onApply={applyCrop} onClose={closeCrop} />
      )}
    </div>
  );
}

// Маппинг шаблона пантеона (API) в PersonaTemplate — для карточки и предзаполнения
function pantheonToTemplate(t: PantheonTemplate): PersonaTemplate {
  return {
    key: t.key,
    role: t.role,
    namePlaceholder: t.name,
    description: t.description,
    contract: t.contract,
    greeting: t.greeting,
    avatarColor: t.color,
    tools: t.tools ?? undefined,
    access: t.access,
    model: t.model,
    effort: t.effort,
    specialty: t.specialty,
  };
}

// === Мелкие переиспользуемые под-компоненты ===

function StepHead({ title, subtitle }: { title: string; subtitle: string }) {
  return (
    <div>
      <div style={{ fontFamily: FONT.serif, fontSize: 21, fontWeight: 600, color: C.textHeading, letterSpacing: '-0.01em' }}>{title}</div>
      <div style={{ marginTop: 6, fontSize: 13, color: C.textMuted, lineHeight: 1.5 }}>{subtitle}</div>
    </div>
  );
}

function MethodCard({ active, emoji, title, desc, onClick }: { active: boolean; emoji: string; title: string; desc: string; onClick: () => void }) {
  return (
    <button
      type="button" onClick={onClick}
      style={{
        display: 'flex', flexDirection: 'column', gap: 6, textAlign: 'left',
        background: active ? C.accentLight : C.bgWhite, border: `1.5px solid ${active ? C.accent : C.border}`,
        borderRadius: R.xl, padding: '14px 12px', cursor: 'pointer', fontFamily: FONT.sans,
      }}
    >
      <span style={{ fontSize: 19 }}>{emoji}</span>
      <span style={{ fontSize: 13.5, fontWeight: 700, color: C.textHeading }}>{title}</span>
      <span style={{ fontSize: 11.5, color: C.textMuted, lineHeight: 1.4 }}>{desc}</span>
    </button>
  );
}

function TemplateCard({ template: t, active, onSelect }: { template: PersonaTemplate; active: boolean; onSelect: () => void }) {
  return (
    <button
      type="button" onClick={onSelect}
      style={{
        display: 'flex', alignItems: 'center', gap: 11, textAlign: 'left',
        background: active ? C.accentLight : C.bgWhite, border: `1px solid ${active ? C.accent : C.border}`,
        borderRadius: R.xl, padding: '11px 13px', cursor: 'pointer', fontFamily: FONT.sans, minWidth: 0,
      }}
    >
      <span style={{
        width: 36, height: 36, borderRadius: R.full, flexShrink: 0, background: agentDotColor(t.avatarColor),
        color: '#fff', display: 'flex', alignItems: 'center', justifyContent: 'center', fontSize: 15, fontWeight: 600,
      }}>
        {t.role.slice(0, 1)}
      </span>
      <span style={{ flex: 1, minWidth: 0 }}>
        <span style={{ display: 'block', fontSize: 13.5, fontWeight: 600, color: C.textHeading }}>{t.role}</span>
        <span style={{ display: 'block', fontSize: 12, color: C.textMuted, marginTop: 2, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
          {t.description}
        </span>
      </span>
    </button>
  );
}

function Tag({ children }: { children: React.ReactNode }) {
  return (
    <span style={{ fontSize: 11, fontWeight: 600, background: C.bgSelected, color: C.textSecondary, borderRadius: R.pill, padding: '3px 9px' }}>
      {children}
    </span>
  );
}

function wordPlural(n: number): string {
  const mod10 = n % 10, mod100 = n % 100;
  if (mod10 === 1 && mod100 !== 11) return 'слово';
  if (mod10 >= 2 && mod10 <= 4 && (mod100 < 10 || mod100 >= 20)) return 'слова';
  return 'слов';
}

const aiBadge: React.CSSProperties = {
  display: 'inline-flex', alignSelf: 'flex-start', fontSize: 11, color: C.textMuted,
  background: C.bgSelected, borderRadius: R.pill, padding: '3px 10px',
};

const presetChip: React.CSSProperties = {
  background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.pill,
  padding: '4px 11px', fontSize: 12, cursor: 'pointer', fontFamily: FONT.sans,
  color: C.textSecondary, whiteSpace: 'nowrap', transition: 'background 0.12s, border-color 0.12s',
};

const disclosureBtn: React.CSSProperties = {
  display: 'flex', alignItems: 'center', gap: 6, border: 'none', background: 'none',
  padding: '2px 0', cursor: 'pointer', fontFamily: FONT.sans, fontSize: 12.5,
  color: C.textSecondary, fontWeight: 600,
};

const exampleRemoveBtn: React.CSSProperties = {
  flexShrink: 0, width: 28, height: 28, borderRadius: R.full,
  background: 'transparent', border: `1px solid ${C.border}`, color: C.textMuted,
  cursor: 'pointer', fontSize: 15, lineHeight: 1, fontFamily: FONT.sans,
  display: 'flex', alignItems: 'center', justifyContent: 'center',
};

const addExampleBtn: React.CSSProperties = {
  background: 'transparent', border: 'none', color: C.accent, cursor: 'pointer',
  fontSize: 13, fontWeight: 600, fontFamily: FONT.sans, padding: 0,
};

const selectStyle: React.CSSProperties = {
  width: '100%', boxSizing: 'border-box', background: C.bgWhite, border: `1px solid ${C.border}`,
  borderRadius: R.xl, padding: '10px 13px', fontSize: 14, fontFamily: FONT.sans,
  color: C.textHeading, outline: 'none', cursor: 'pointer',
};
