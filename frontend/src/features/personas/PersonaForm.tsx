import { forwardRef, useEffect, useImperativeHandle, useMemo, useRef, useState } from 'react';
import { Pencil, Trash2 } from 'lucide-react';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import type { Persona, PersonaAccess, PersonaContract, PersonaMemoryEntry, PersonaMemoryType, PersonaScope, PersonaSpecialty, PersonaWorkingFocus, Project } from '../../types';
import { api } from '../../lib/api';
import { Field, FieldLabel, TextField, TextArea, Toggle, Button, SegmentedControl, Menu, MenuItem, WaitingIndicator } from '../../components/ui';
import { useAiJob, runAiJob, resetAiJob } from '../../lib/aiJobStore';
import { PillSwitch } from '../../components/Toolbar';
import { ModelPicker } from '../../components/ModelPicker';
import { useModels, useModelCaps, modelProvider } from '../../lib/models';
import { effortsForProvider } from '../../lib/effort';
import { AGENT_COLORS, agentDotColor } from '../../components/AgentSelector';
import { bumpPersonas } from '../../lib/personas';
import { C, FONT, R, SHADOW } from '../../lib/design';
import { useIsMobile } from '../../lib/breakpoints';
import { SectionLabel } from '../tasks/bits';
import { PersonaAvatar } from './PersonaAvatar';
import { AvatarCropDialog, type AvatarCropResult } from './AvatarCropDialog';

// Транслит кириллицы — та же таблица, что в backend PersonaManager.Translit: без неё
// slug русского имени рассинхронится с сохранённым на бэке (превью ≠ факт).
const TRANSLIT: Record<string, string> = {
  а: 'a', б: 'b', в: 'v', г: 'g', д: 'd', е: 'e', ё: 'e', ж: 'zh', з: 'z', и: 'i', й: 'y',
  к: 'k', л: 'l', м: 'm', н: 'n', о: 'o', п: 'p', р: 'r', с: 's', т: 't', у: 'u', ф: 'f',
  х: 'h', ц: 'ts', ч: 'ch', ш: 'sh', щ: 'sch', ъ: '', ы: 'y', ь: '', э: 'e', ю: 'yu', я: 'ya',
};

// Slug для @handle — зеркалит backend Slugify. live=true не обрезает хвостовой дефис,
// чтобы его можно было печатать (финальную обрезку сделает бэкенд при сохранении).
function slugifyHandle(s: string, live = false): string {
  let out = '';
  let prevDash = false;
  for (const ch of s.trim().toLowerCase()) {
    if (/[a-z0-9]/.test(ch)) { out += ch; prevDash = false; }
    else if (ch in TRANSLIT) { const t = TRANSLIT[ch]; if (t) { out += t; prevDash = false; } }
    else if (!prevDash && out.length > 0) { out += '-'; prevDash = true; }
  }
  return live ? out : out.replace(/-+$/, '');
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
  name?: string;
  role?: string;
  description?: string;
  contract?: PersonaContract;
  greeting?: string;
  color?: string;
  tools?: string[];
  // Профиль доступа из шаблона (напр. Ревьюер — только чтение)
  access?: PersonaAccess;
  // Дефолтная модель/усилие из шаблона (алиасы 'opus'|'sonnet'|'haiku'; effort 'high')
  model?: string;
  effort?: string;
  // Специальность (функциональная роль) из шаблона — предзаполняет селектор
  specialty?: PersonaSpecialty;
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
  // Переход во вкладку «Знания» из плашки-переадресации (фича persona-bindings)
  onOpenKnowledge?: () => void;
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
  // Возможности переехали во вкладку «Знания» — вместо тумблеров показываем
  // плашку-переадресацию. Прежний блок тумблеров оставлен как ветка-легаси.
  const bindingsEnabled = true;

  const [name, setName] = useState(persona?.name ?? initial?.name ?? '');
  // Ручной @handle. При создании авто-подставляется из имени, пока пользователь не тронул поле.
  const [handle, setHandle] = useState(persona?.handle ?? '');
  const [handleEdited, setHandleEdited] = useState(false);
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
  // Полный регламент роли (длинный markdown) — для «тяжёлых» ролей вроде пантеона OmO
  const [instructions, setInstructions] = useState(
    persona ? (persona.contract?.instructions ?? '') : (initial?.contract?.instructions ?? ''));
  // Пустая инструкция свёрнута в кнопку «+ инструкция» — раскрытие по клику
  const [instructionsOpen, setInstructionsOpen] = useState(false);
  const [model, setModel] = useState(persona?.model ?? initial?.model ?? '');
  const [effort, setEffort] = useState(persona?.effort ?? initial?.effort ?? '');
  // Специальность (функциональная роль) для оркестрации — конвейер/брифинг/статус/память
  const [specialty, setSpecialty] = useState<PersonaSpecialty>(
    persona?.specialty ?? initial?.specialty ?? 'none');
  const [scope, setScope] = useState<PersonaScope>(persona?.scope ?? defaultScope ?? 'global');
  const [projectId, setProjectId] = useState(persona?.projectId ?? defaultProjectId ?? '');
  const [greeting, setGreeting] = useState(persona?.greeting ?? initial?.greeting ?? '');
  const [color, setColor] = useState(persona?.avatar?.color ?? initial?.color ?? 'orange');
  // Возможности персоны: массив включённых ключей. null у персоны = все включены.
  const [tools, setTools] = useState<string[]>(
    persona ? (persona.tools ?? ALL_TOOL_KEYS) : (initial?.tools ?? ALL_TOOL_KEYS));
  // Профиль доступа (P6) + свой список запретов (для custom, через запятую)
  const [access, setAccess] = useState<PersonaAccess>(
    persona ? (persona.access ?? 'full') : (initial?.access ?? 'full'));
  // Исполнитель в сабагентах: write-набор (файлы + Bash) в файловом сабагенте; только при full
  const [subagentExecutor, setSubagentExecutor] = useState(persona?.subagentExecutor ?? false);
  const [disallowedText, setDisallowedText] = useState(
    (persona?.disallowedTools ?? []).join(', '));
  const [memoryEnabled, setMemoryEnabled] = useState(persona?.memoryEnabled ?? false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Аватар: текущее состояние (обновляется после выбора кандидата), возможность
  // генерации (настроен ли fal), поле промпта. Статус/результат генерации — в
  // aiJobStore (переживает уход со страницы во время ожидания fal.ai).
  const [avatar, setAvatar] = useState<Persona['avatar']>(persona?.avatar ?? { kind: 'initials', color: initial?.color ?? 'orange' });
  const [canGenerate, setCanGenerate] = useState(false);
  const [avatarPrompt, setAvatarPrompt] = useState('');
  const avatarKey = `personas:${persona?.id ?? '_new'}:avatar-generate`;
  const avatarJob = useAiJob<string[]>(avatarKey);
  const generating = avatarJob.status === 'running';
  const candidates = avatarJob.status === 'done' ? (avatarJob.result ?? []) : [];
  const avatarGenError = avatarJob.status === 'error' ? avatarJob.error ?? null : null;
  const [selectError, setSelectError] = useState<string | null>(null);
  const [selecting, setSelecting] = useState<string | null>(null);
  // Инлайн-панель «Внешность» под hero (открывается из мини-меню ✎)
  const [showAppearance, setShowAppearance] = useState(false);
  const [avatarHover, setAvatarHover] = useState(false);
  // Мини-меню ✎: сгенерировать / загрузить файл / перекроить / цвет и инициалы
  const [avatarMenu, setAvatarMenu] = useState(false);
  // Кроп: источник картинки (objectURL выбранного файла или URL оригинала) + режим
  const [cropState, setCropState] = useState<null | { src: string; mode: 'upload' | 'recrop'; file?: File }>(null);
  const fileInputRef = useRef<HTMLInputElement | null>(null);

  // AI-редактирование характера: статус/результат в aiJobStore (переживает уход со
  // страницы), общий поповер с необязательным уточняющим промптом для обоих режимов.
  const characterKey = `personas:${persona?.id ?? '_new'}:character-generate`;
  const characterJob = useAiJob<{ mode: 'generate' | 'improve'; contract: PersonaContract }>(characterKey);
  const characterBusy = characterJob.status === 'running';
  const [aiError, setAiError] = useState<string | null>(null);
  const [aiInstruction, setAiInstruction] = useState('');
  const [aiPopover, setAiPopover] = useState<null | 'generate' | 'improve'>(null);

  // Сводка долгой памяти (для summary-карточки) — считаем из записей по типу
  const [memoryEntries, setMemoryEntries] = useState<PersonaMemoryEntry[] | null>(null);
  // Рабочий фокус («что я сейчас делаю») — карточка в секции памяти
  const [focus, setFocus] = useState<PersonaWorkingFocus | null>(null);

  // Возможности провайдера выбранной модели — показываем «Усилие рассуждения» только если поддерживается
  const caps = useModelCaps(model);

  // Акцент персоны из выбранного цвета — им красим роль в hero и (через onColorChange) тулбар
  const accentColor = AGENT_COLORS[color] ?? C.accent;

  // Один раз узнаём, доступна ли генерация аватара (fal настроен)
  useEffect(() => { api.personas.avatarCaps().then(c => setCanGenerate(c.generate)).catch(() => {}); }, []);

  // Сводка памяти + рабочий фокус: грузим при редактировании существующей персоны
  useEffect(() => {
    if (!persona) { setMemoryEntries(null); setFocus(null); return; }
    let alive = true;
    api.personas.memory(persona.id).then(list => { if (alive) setMemoryEntries(list); }).catch(() => { if (alive) setMemoryEntries([]); });
    api.personas.focus(persona.id).then(f => { if (alive) setFocus(f); }).catch(() => { if (alive) setFocus(null); });
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

  // Генерация 4 вариантов аватара — показываем сеткой, аватар не меняем до выбора.
  // Статус/результат в aiJobStore — переживает уход со страницы во время ожидания fal.ai.
  const generateAvatar = () => {
    if (!persona || generating) return;
    runAiJob<string[]>(avatarKey, () => api.personas
      .generateAvatar(persona.id, { prompt: avatarPrompt, count: 4 })
      .then(r => r.candidates));
  };

  // Выбор кандидата из галереи → становится аватаром персоны
  const chooseCandidate = async (file: string) => {
    if (!persona || selecting) return;
    setSelecting(file);
    setSelectError(null);
    try {
      const updated = await api.personas.selectAvatar(persona.id, file);
      setAvatar(updated.avatar);
      bumpPersonas();
      resetAiJob(avatarKey);
      setAvatarPrompt('');
    } catch (e) {
      setSelectError(e instanceof Error ? e.message : 'Не удалось выбрать аватар');
    } finally {
      setSelecting(null);
    }
  };

  // Выбор файла для загрузки аватара → диалог кропа поверх objectURL
  const onAvatarFileChosen = (file: File | null) => {
    if (!file) return;
    setCropState({ src: URL.createObjectURL(file), mode: 'upload', file });
  };

  // Применение кропа: загрузка (оригинал + квадрат) или перекроп сохранённого оригинала
  const applyCrop = async (result: AvatarCropResult) => {
    if (!persona || !cropState) return;
    const updated = cropState.mode === 'upload' && cropState.file
      ? await api.personas.uploadAvatar(persona.id, cropState.file, result.blob, result.crop)
      : await api.personas.recropAvatar(persona.id, result.blob, result.crop);
    setAvatar(updated.avatar);
    bumpPersonas();
  };

  const closeCrop = () => {
    if (cropState?.mode === 'upload') URL.revokeObjectURL(cropState.src);
    setCropState(null);
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
    instructions: instructions.trim() || undefined,
  });

  // Заполнен ли хоть один слот контракта — от этого зависит доступность «Улучшить»
  const contractFilled = !!(character.trim() || tone.trim() || mustDo.trim() || mustNot.trim()
    || outputFormat.trim() || speechExamples.some(s => s.trim()) || instructions.trim());

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
  // Статус/результат — в aiJobStore, применяются эффектом ниже (переживает уход со страницы).
  const runAiCharacter = (mode: 'generate' | 'improve') => {
    if (characterBusy) return;
    setAiError(null);
    runAiJob(characterKey, () => api.personas.aiCharacter({
      name: name.trim() || undefined,
      role: role.trim() || undefined,
      description: description.trim() || undefined,
      current: mode === 'improve' ? JSON.stringify(buildContract()) : undefined,
      instruction: aiInstruction.trim() || undefined,
    }).then(r => ({ mode, contract: r.contract })));
  };

  useEffect(() => {
    if (characterJob.status === 'done' && characterJob.result) {
      applyContract(characterJob.result.contract);
      setAiInstruction('');
      setAiPopover(null);
      resetAiJob(characterKey);
    } else if (characterJob.status === 'error') {
      setAiError(characterJob.error ?? 'Не удалось обновить характер');
      resetAiJob(characterKey);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [characterJob.status]);

  // AI-хаб: контекстные действия из палитры делегируются сюда — над открытой персоной,
  // переиспользуя те же обработчики, что и ✨-кнопки формы.
  useEffect(() => {
    const onRun = (e: Event) => {
      const action = (e as CustomEvent<{ action?: string }>).detail?.action;
      if (!action) return;
      if (action === 'persona.character') {
        // Есть заполненный контракт — улучшаем, иначе генерируем с нуля (как кнопки тулбара)
        runAiCharacter(contractFilled ? 'improve' : 'generate');
      } else if (action === 'persona.avatar') {
        // Раскрываем панель «Внешность», чтобы кандидаты/сообщение были видны, и запускаем генерацию
        setShowAppearance(true);
        generateAvatar();
      }
    };
    window.addEventListener('cc-ai-run', onRun);
    return () => window.removeEventListener('cc-ai-run', onRun);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [persona, contractFilled, characterBusy, generating]);

  // При выборе зоны «Проект» без выбранного проекта — подставим первый доступный
  useEffect(() => {
    if (scope === 'project' && !projectId && projects.length > 0) setProjectId(projects[0].id);
  }, [scope, projectId, projects]);

  // Авто-подстановка handle из имени — только при создании и пока поле не трогали вручную
  useEffect(() => {
    if (!isEdit && !handleEdited) setHandle(slugifyHandle(name));
  }, [name, isEdit, handleEdited]);

  const canSave = name.trim().length > 0 && !(scope === 'project' && !projectId);

  // Снимок редактируемых полей — для вычисления «есть несохранённые правки» (dirty)
  const snapshot = JSON.stringify({
    name: name.trim(), handle: handle.trim(), role: role.trim(), description: description.trim(),
    contract: {
      character: character.trim(), tone: tone.trim(),
      mustDo: parseLines(mustDo), mustNot: parseLines(mustNot),
      outputFormat: outputFormat.trim(),
      speechExamples: speechExamples.map(s => s.trim()).filter(Boolean),
      instructions: instructions.trim(),
    },
    model, effort, scope,
    projectId: scope === 'project' ? projectId : '',
    color, greeting: greeting.trim(), memoryEnabled,
    tools: [...tools].sort(),
    access,
    specialty,
    disallowed: access === 'custom' ? parseDisallowed(disallowedText) : [],
    subagentExecutor: access === 'full' ? subagentExecutor : false,
  });
  // Исходный снимок считается от предзаполненного состояния: у legacy-персоны
  // слот «Характер» = systemPrompt, поэтому сама миграция не делает форму dirty.
  const initialSnapshot = useMemo(() => {
    const s = persona?.scope ?? defaultScope ?? 'global';
    return JSON.stringify({
      name: (persona?.name ?? '').trim(),
      handle: (persona?.handle ?? '').trim(),
      role: (persona?.role ?? '').trim(),
      description: (persona?.description ?? '').trim(),
      contract: {
        character: (persona?.contract?.character ?? persona?.systemPrompt ?? '').trim(),
        tone: (persona?.contract?.tone ?? '').trim(),
        mustDo: persona?.contract?.mustDo ?? [],
        mustNot: persona?.contract?.mustNot ?? [],
        outputFormat: (persona?.contract?.outputFormat ?? '').trim(),
        speechExamples: (persona?.contract?.speechExamples ?? []).map(x => x.trim()).filter(Boolean),
        instructions: (persona?.contract?.instructions ?? '').trim(),
      },
      model: persona?.model ?? '',
      effort: persona?.effort ?? '',
      scope: s,
      projectId: s === 'project' ? (persona?.projectId ?? defaultProjectId ?? '') : '',
      color: persona?.avatar?.color ?? 'orange',
      greeting: (persona?.greeting ?? '').trim(),
      memoryEnabled: persona?.memoryEnabled ?? false,
      tools: [...(persona ? (persona.tools ?? ALL_TOOL_KEYS) : ALL_TOOL_KEYS)].sort(),
      access: persona ? (persona.access ?? 'full') : (initial?.access ?? 'full'),
      specialty: persona ? (persona.specialty ?? 'none') : (initial?.specialty ?? 'none'),
      disallowed: (persona?.access ?? 'full') === 'custom' ? (persona?.disallowedTools ?? []) : [],
      subagentExecutor: (persona?.access ?? 'full') === 'full' ? (persona?.subagentExecutor ?? false) : false,
    });
  }, [persona, defaultScope, defaultProjectId, initial?.access, initial?.specialty]);
  const dirty = snapshot !== initialSnapshot;

  const save = async () => {
    if (!canSave || busy) return;
    setBusy(true);
    setError(null);
    const dto = {
      name: name.trim(),
      // Создание: пусто → авто из имени. Правка: шлём только при изменении ("" = сброс к авто)
      handle: isEdit
        ? (handle.trim() !== (persona?.handle ?? '') ? handle.trim() : undefined)
        : (handle.trim() || undefined),
      role: role.trim() || undefined,
      description: description.trim() || undefined,
      // Характер — только контрактом; legacy-поле systemPrompt чистим при каждом сохранении
      contract: buildContract(),
      systemPrompt: '',
      // Правка: шлём значение как есть — "" сбрасывает модель/усилие к дефолту (бэкенд: ""→null).
      // Создание: пусто → не пишем (иначе сохранилась бы пустая строка вместо null).
      model: isEdit ? model : (model || undefined),
      effort: isEdit ? effort : (effort || undefined),
      scope,
      projectId: scope === 'project' ? projectId : undefined,
      color,
      greeting: greeting.trim() || undefined,
      memoryEnabled,
      // Всегда явный список: полный набор бэкенд нормализует в «без ограничений»
      tools,
      // Профиль доступа: свой список запретов уходит только при custom
      access,
      // Специальность шлём всегда (включая 'none' — сброс); update на бэке None ставит явно
      specialty,
      disallowedTools: access === 'custom' ? parseDisallowed(disallowedText) : [],
      // Исполнитель в сабагентах: имеет смысл только при полном профиле (бэкенд тоже гасит)
      subagentExecutor: access === 'full' ? subagentExecutor : false,
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
            onClick={() => setAvatarMenu(v => !v)}
            aria-label="Изменить внешность"
            title="Изменить внешность"
            style={{
              position: 'absolute', right: -2, bottom: -2, width: 28, height: 28, borderRadius: R.full,
              border: `2px solid ${C.bgMain}`, background: avatarMenu || showAppearance ? accentColor : C.bgWhite,
              color: avatarMenu || showAppearance ? '#fff' : C.textSecondary, cursor: 'pointer',
              display: 'flex', alignItems: 'center', justifyContent: 'center',
              opacity: avatarMenu || showAppearance || avatarHover || isMobile ? 1 : 0, transition: 'opacity 0.15s, background 0.15s',
              boxShadow: SHADOW.thumb,
            }}
          >
            <Pencil size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />
          </button>

          {/* Мини-меню внешности: генерация / загрузка файла / перекроп / цвет */}
          {avatarMenu && (
            <Menu onClose={() => setAvatarMenu(false)} align="left" top={82} minWidth={220}>
              <MenuItem
                label="✨ Сгенерировать"
                onClick={() => { setAvatarMenu(false); setShowAppearance(true); }}
              />
              <MenuItem
                label={isEdit ? 'Загрузить файл…' : 'Загрузить файл… (после сохранения)'}
                disabled={!isEdit}
                onClick={() => { setAvatarMenu(false); fileInputRef.current?.click(); }}
              />
              {isEdit && avatar.originalFile && persona && (
                <MenuItem
                  label="Перекроить"
                  onClick={() => {
                    setAvatarMenu(false);
                    const src = api.personas.avatarOriginalUrl({ ...persona, avatar });
                    if (src) setCropState({ src, mode: 'recrop' });
                  }}
                />
              )}
              <MenuItem
                label="Цвет и инициалы"
                onClick={() => { setAvatarMenu(false); setShowAppearance(true); }}
              />
            </Menu>
          )}

          {/* Скрытый input выбора файла аватара */}
          <input
            ref={fileInputRef}
            type="file"
            accept="image/jpeg,image/png,image/webp"
            style={{ display: 'none' }}
            onChange={e => {
              onAvatarFileChosen(e.target.files?.[0] ?? null);
              e.target.value = '';   // повторный выбор того же файла снова даёт change
            }}
          />
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
          <div style={{ maxWidth: isMobile ? '100%' : 320 }}>
            <Field label="@handle" hint="Латиницей — по нему @упоминают в чате; пусто = из имени">
              <div style={{
                display: 'flex', alignItems: 'center', gap: 2,
                border: `1px solid ${C.border}`, borderRadius: R.md, background: C.bgWhite, paddingLeft: 10,
              }}>
                <span style={{ color: C.textSecondary, fontFamily: FONT.mono, fontSize: 13 }}>@</span>
                <input
                  value={handle}
                  onChange={e => { setHandle(slugifyHandle(e.target.value, true)); setHandleEdited(true); }}
                  placeholder="masha"
                  style={{
                    flex: 1, minWidth: 0, border: 'none', outline: 'none', background: 'transparent',
                    fontFamily: FONT.mono, fontSize: 13, color: C.textHeading, padding: '8px 10px 8px 2px',
                  }}
                />
              </div>
            </Field>
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
                {generating && (
                  <WaitingIndicator hint="Обычно занимает 10–30 секунд" />
                )}
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
                {(avatarGenError || selectError) && (
                  <span style={{ fontSize: 12, color: C.dangerText, fontFamily: FONT.sans }}>{avatarGenError || selectError}</span>
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
        display: 'flex', flexDirection: 'column', gap: 8,
      }}>
        <div style={{ display: 'flex', gap: 8, alignItems: 'center', justifyContent: 'flex-end', position: 'relative' }}>
          <Button variant="ghostAccent" size="sm" disabled={characterBusy}
            onClick={() => setAiPopover(v => v === 'generate' ? null : 'generate')}>
            ✨ Сгенерировать
          </Button>
          {contractFilled && (
            <Button variant="ghostAccent" size="sm" disabled={characterBusy}
              onClick={() => setAiPopover(v => v === 'improve' ? null : 'improve')}>
              ✨ Улучшить
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
                  onEnter={() => runAiCharacter(aiPopover)} />
                <div style={{ display: 'flex', justifyContent: 'flex-end' }}>
                  <Button variant="primary" size="sm" loading={characterBusy} disabled={characterBusy}
                    onClick={() => runAiCharacter(aiPopover)}>
                    {characterBusy ? (aiPopover === 'generate' ? 'Генерирую…' : 'Улучшаю…')
                      : (aiPopover === 'generate' ? 'Сгенерировать' : 'Улучшить')}
                  </Button>
                </div>
              </div>
            </>
          )}
        </div>
        {characterBusy && (
          <WaitingIndicator hint={aiPopover === 'improve' ? 'Улучшаю характер персоны' : 'Придумываю характер персоны'} />
        )}
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

      {/* Слот «Инструкция»: полный регламент роли. Показываем свёрнуто, если пусто —
          обычным персонам он не нужен, а у «тяжёлых» ролей (пантеон) приходит из шаблона */}
      <div style={{ marginTop: 18 }}>
        {instructions.trim() || instructionsOpen ? (
          <Field label="Инструкция" hint="Полный регламент роли (markdown) — попадает в системный промпт после остальных слотов">
            <TextArea value={instructions} onChange={setInstructions} autoGrow minHeight={120} maxHeight={360}
              placeholder="Развёрнутый регламент: протоколы работы, критерии готовности, примеры…" />
          </Field>
        ) : (
          <button type="button" onClick={() => setInstructionsOpen(true)} style={addExampleBtn}>
            + инструкция (полный регламент роли)
          </button>
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

        <Field label="Специальность" hint="Функциональная роль для оркестрации: конвейер ролей, голос брифинга, статус команды.">
          <select
            value={specialty}
            onChange={e => setSpecialty(e.target.value as PersonaSpecialty)}
            style={selectStyle}
            aria-label="Специальность"
          >
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
            <option value="tester">Тестировщик</option>
          </select>
        </Field>
      </div>
    </div>
  );

  // === Секция 3.5 — Возможности (инструменты per-persona) ===
  const toggleTool = (key: string) =>
    setTools(prev => prev.includes(key) ? prev.filter(t => t !== key) : [...prev, key]);

  // При включённой фиче persona-bindings источники/инструменты настраиваются во
  // вкладке «Знания» — здесь блок «Возможности» не показываем.
  const toolsSection = bindingsEnabled ? null : (
    <div style={section}>
      <SectionLabel style={{ marginBottom: 4 }}>Возможности</SectionLabel>
      <div style={{ fontSize: 12.5, color: C.textMuted, fontFamily: FONT.sans, marginBottom: 14 }}>
        Какими инструментами персона может пользоваться в чате
      </div>

      {/* Профиль доступа (P6): full / readOnly / custom */}
      <div style={{ display: 'flex', flexDirection: 'column', gap: 8, marginBottom: 18 }}>
        <FieldLabel>Профиль доступа</FieldLabel>
        <SegmentedControl<PersonaAccess>
          value={access}
          onChange={setAccess}
          columns={3}
          options={[
            { value: 'full', label: 'Полный' },
            { value: 'readOnly', label: 'Только чтение' },
            { value: 'custom', label: 'Свой' },
          ]}
        />
        {access === 'readOnly' && (
          <span style={{ fontSize: 12.5, color: C.textSecondary, fontFamily: FONT.sans, lineHeight: 1.5 }}>
            Смотрит и советует, но ничего не меняет: без правок файлов, Bash и мутаций задач/заметок
          </span>
        )}
        {access === 'custom' && (
          <Field hint="Имена инструментов через запятую, напр. Bash, Edit, mcp__tasks__tasks_delete">
            <TextArea value={disallowedText} onChange={setDisallowedText} autoGrow minHeight={56}
              placeholder="Bash, Edit, Write" />
          </Field>
        )}
        {access === 'full' && (
          <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 12, marginTop: 4 }}>
            <div style={{ minWidth: 0 }}>
              <div style={{ fontSize: 13.5, fontWeight: 600, color: C.textHeading, fontFamily: FONT.sans }}>
                Исполнитель в сабагентах
              </div>
              <div style={{ fontSize: 12, color: C.textMuted, fontFamily: FONT.sans, marginTop: 1 }}>
                Вызванная как субагент, может править файлы и запускать команды — иначе только консультирует
              </div>
            </div>
            <Toggle checked={subagentExecutor} onChange={() => setSubagentExecutor(v => !v)} />
          </div>
        )}
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
          {/* Рабочий фокус — «что я сейчас делаю» (рабочая память, вне записей) */}
          {focus && (
            <div style={focusCard}>
              <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 10 }}>
                <span style={{ fontSize: 11, fontWeight: 700, letterSpacing: 0.4, textTransform: 'uppercase', color: C.accent, fontFamily: FONT.sans }}>
                  Текущий фокус · {timeAgo(focus.updatedAt)}
                </span>
                <button
                  type="button"
                  style={openMemoryBtn}
                  onClick={() => {
                    if (!persona) return;
                    api.personas.clearFocus(persona.id).then(() => setFocus(null)).catch(() => {});
                  }}
                >
                  Сбросить
                </button>
              </div>
              <div style={{ fontSize: 13.5, fontWeight: 600, color: C.textHeading, fontFamily: FONT.sans, marginTop: 6 }}>
                {focus.what}
              </div>
              <div style={{ fontSize: 12.5, color: C.textSecondary, fontFamily: FONT.sans, marginTop: 3, lineHeight: 1.5 }}>
                Статус: {focus.status}
                {focus.nextStep && <> · Следующий шаг: {focus.nextStep}</>}
              </div>
            </div>
          )}
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

  // === Опасная зона (только мобилка) — «Удалить» перенесён из тулбара в конец формы:
  // на мобиле в тулбаре нет ⋯-меню, удаление живёт здесь, отдельной секцией.
  const dangerSection = isMobile && isEdit && onDelete && persona ? (
    <div style={section}>
      <SectionLabel style={{ marginBottom: 6 }}>Опасная зона</SectionLabel>
      <div style={{ fontSize: 12.5, color: C.textMuted, fontFamily: FONT.sans, lineHeight: 1.5, marginBottom: 14 }}>
        Удаление необратимо: пропадут характер, память и настройки персоны. Уже созданные разговоры в чатах останутся.
      </div>
      <button type="button" onClick={() => onDelete(persona)} style={dangerBtn}>
        <Trash2 size={16} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} /> Удалить персону
      </button>
    </div>
  ) : null;

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
        {dangerSection}
        {error && (
          <div style={{ fontSize: 12.5, color: C.dangerText, fontFamily: FONT.sans }}>{error}</div>
        )}
      </div>

      {/* Диалог кропа: загрузка нового файла или перекроп сохранённого оригинала */}
      {cropState && (
        <AvatarCropDialog
          src={cropState.src}
          initial={cropState.mode === 'recrop' ? avatar.crop : null}
          title={cropState.mode === 'recrop' ? 'Перекроить аватар' : 'Кадрирование аватара'}
          onApply={applyCrop}
          onClose={closeCrop}
        />
      )}
    </div>
  );
});

// Разбор списка запретов «через запятую» (custom-профиль) — пустые куски выбрасываются
function parseDisallowed(s: string): string[] {
  return Array.from(new Set(s.split(',').map(t => t.trim()).filter(Boolean)));
}

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

// Карточка рабочего фокуса — в ряду memoryStat-карточек, но с акцентной рамкой
const focusCard: React.CSSProperties = {
  background: C.bgWhite, border: `1px solid ${C.border}`, borderLeft: `3px solid ${C.accent}`,
  borderRadius: R.lg, padding: '10px 12px',
};

// Давность «N мин/ч/дн назад» для метки фокуса
function timeAgo(iso: string): string {
  const ms = Date.now() - new Date(iso).getTime();
  if (!Number.isFinite(ms) || ms < 0) return 'только что';
  const min = Math.floor(ms / 60_000);
  if (min < 1) return 'только что';
  if (min < 60) return `${min} мин назад`;
  const h = Math.floor(min / 60);
  if (h < 24) return `${h} ч назад`;
  const d = Math.floor(h / 24);
  return `${d} дн назад`;
}

const openMemoryBtn: React.CSSProperties = {
  background: 'transparent', border: 'none', color: C.accent, cursor: 'pointer',
  fontSize: 13, fontWeight: 600, fontFamily: FONT.sans, padding: 0,
};

// Кнопка «Удалить персону» в «Опасной зоне» формы (мобилка) — аутлайн-danger,
// full-width; не солидная красная, чтобы не спорить с «Сохранить» в тулбаре.
const dangerBtn: React.CSSProperties = {
  width: '100%', display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 8,
  padding: '11px 18px', borderRadius: R.xl, border: `1px solid ${C.dangerBorder}`,
  background: C.dangerBg, color: C.dangerText, fontSize: 14, fontWeight: 600,
  fontFamily: FONT.sans, cursor: 'pointer',
};

const selectStyle: React.CSSProperties = {
  width: '100%', boxSizing: 'border-box', background: C.bgWhite, border: `1px solid ${C.border}`,
  borderRadius: R.xl, padding: '10px 13px', fontSize: 14, fontFamily: FONT.sans,
  color: C.textHeading, outline: 'none', cursor: 'pointer',
};
