// Поля настройки этапа (F24, макет templates-editor.html T51–T61): имя, «после», присутствие,
// исполнитель, промпт, модели по уровням, уровень по умолчанию, права, порог и действие при молчании.
// Состояний черновика не держит (кроме набираемого текста id/«+ Инструмент» и режима исполнителя):
// правка идёт через onChange родителя. Оболочка (шторка/экран) — в WorkflowStageSettings.

import { useState } from 'react';
import { Trash2 } from 'lucide-react';
import { C, FONT, FS, R, SP } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { Button, Checkbox, Chip, Field, InlineSegmented, PillSwitch, Select, TextArea, TextField, Toggle } from '../../components/ui';
import { RoutePicker } from '../../components/RoutePicker';
import { useModels } from '../../lib/models';
import { usePresets, routeDisplayLabel } from '../../lib/presets';
import { TIER_TITLE, useTierModels, type ModelTierKey } from '../../lib/modelTiers';
import { useIsMobile } from '../../lib/breakpoints';
import type { Persona } from '../personas/types';
import type { SpecialtyCatalogEntry } from '../specialties/types';
import { PROMPT_LIMIT, STALL_POLICIES, STALL_RANGE_ERROR, TITLE_LIMIT, stallInRange, titleRequired } from './workflowTemplateModel';
import { listOrNull, presenceEditable } from './workflowEditorLogic';
import { afterOptions, normalizeStages } from './workflowGraphModel';
import { NON_SKIPPABLE_HINT, STAGE_ROLE_LABEL } from './WorkflowStageRow';
import type { WorkflowStage, WorkflowStagePresence } from './types';

const PRESENCE_OPTIONS: { value: WorkflowStagePresence; label: string }[] = [
  { value: 'on', label: 'Всегда' },
  { value: 'optional', label: 'По решению' },
  { value: 'off', label: 'Нет' },
];

// Три карточки «что делать, когда исполнитель молчит» (макет T60): значения — серверные политики.
const POLICY_CARDS: Record<string, { title: string; text: string }> = {
  'nudge-alert': { title: 'Подтолкнуть и сообщить', text: 'как сейчас: автоход «продолжай» и уведомление' },
  alert: { title: 'Только сообщить', text: 'без автохода — решаете вы' },
  'stop-alert': { title: 'Остановить и сообщить', text: 'как кнопка «Стоп» плюс уведомление' },
};

const TIER_SEG: { value: ModelTierKey | 'inherit'; label: string }[] = [
  { value: 'strong', label: 'Strong' },
  { value: 'medium', label: 'Medium' },
  { value: 'weak', label: 'Weak' },
  { value: 'inherit', label: 'как у спец.' },
];

const TIER_CELL_TITLE: Record<ModelTierKey, string> = { strong: 'Strong', medium: 'Medium', weak: 'Weak' };

const LANE_OPTIONS = [
  { value: 'back', label: 'Бэк' },
  { value: 'front', label: 'Фронт' },
  { value: 'other', label: 'Другое' },
];

// Серверный формат id этапа (разрез 2 §1.1): slug [a-z0-9-]{1,32}.
const STAGE_ID_RE = /^[a-z0-9-]{1,32}$/;

const hintStyle = { fontSize: FS.xs, color: C.textMuted } as const;

export interface StageFieldsProps {
  stage: WorkflowStage;
  // Все этапы шаблона и место выбранного: из них считаются id для «после» и дубли id.
  stages: WorkflowStage[];
  index: number;
  readOnly: boolean;
  specialties: SpecialtyCatalogEntry[];
  personas: Persona[];
  // Ключи MCP, уже выданные (проекту/персоне): шаблон может только сузить, не выдать новое.
  mcpChoices: string[];
  onChange: (next: WorkflowStage) => void;
  onRenameId: (newId: string) => void;
  onRemove: () => void;
}

// Ключ MCP «не выдан персоне»: у выбранной персоны есть привязка этого сервера в режиме «выкл».
// Других данных о выдаче персоне на клиенте нет — остальное отсекает сервер (шаблон лишь сужает).
export function mcpNotGranted(persona: Persona | undefined, key: string): boolean {
  if (!persona) return false;
  return (persona.bindings ?? []).some(b => b.type === 'tool' && (b.target === `mcp:${key}` || b.target === key) && b.mode === 'off');
}

export function StageFields({
  stage, stages, index, readOnly, specialties, personas, mcpChoices, onChange, onRenameId, onRemove,
}: StageFieldsProps): React.ReactElement {
  const isMobile = useIsMobile();
  const fixed = !presenceEditable(stage.role);
  const label = STAGE_ROLE_LABEL[stage.role] ?? stage.role;
  const prompt = stage.prompt ?? '';
  const titleLen = (stage.title ?? '').trim().length;
  const titleNeeded = titleRequired(stages, index);
  const patch = (p: Partial<WorkflowStage>) => onChange({ ...stage, ...p });
  const stageId = stage.id?.trim() || stage.role;
  // Текст поля id, пока человек его набирает (null — показываем настоящий id этапа).
  const [idText, setIdText] = useState<string | null>(null);
  const takenIds = normalizeStages(stages).filter((_, i) => i !== index).map(s => s.id);
  const idError = idText === null ? undefined
    : !STAGE_ID_RE.test(idText.trim()) ? 'Латинские буквы, цифры и дефис, до 32 символов'
    : takenIds.includes(idText.trim()) ? 'Такой id уже есть у другого этапа' : undefined;
  const onIdInput = (v: string) => {
    setIdText(v);
    const t = v.trim();
    // Принимаем только годное имя: ссылки других этапов правятся каскадом сразу.
    if (STAGE_ID_RE.test(t) && !takenIds.includes(t)) onRenameId(t);
  };

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.lg }}>
      <Field
        label={<>Имя этапа <span style={{ fontWeight: 400, color: titleNeeded ? C.dangerText : C.textMuted }}>
          {titleNeeded ? '— обязательно' : '— необязательно'}</span></>}
        hint={titleNeeded
          ? `Этап делит роль и линию с другим этапом — имя обязательно и должно от него отличаться (иначе в списке и на схеме две неотличимые «${label}»).`
          : `Пусто — этап подписан ролью: «${label}». До ${TITLE_LIMIT} символов. Имя станет обязательным, если в шаблоне появится второй этап с той же ролью и линией.`}
        error={titleLen > TITLE_LIMIT ? `Имя длиннее ${TITLE_LIMIT} символов` : undefined}
      >
        <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm }}>
          <div style={{ flex: 1, minWidth: 0 }}>
            <TextField
              value={stage.title ?? ''}
              onChange={v => patch({ title: v })}
              disabled={readOnly}
              placeholder={label}
              invalid={titleLen > TITLE_LIMIT}
            />
          </div>
          <span style={{ ...hintStyle, fontFamily: FONT.mono, flexShrink: 0 }}>{titleLen} / {TITLE_LIMIT}</span>
        </div>
      </Field>

      <Field label="Id этапа" hint="Имя этапа в маршруте: на него ссылаются поля «после». При переименовании ссылки обновятся сами." error={idError}>
        <TextField
          value={idText ?? stageId}
          onChange={onIdInput}
          onBlur={() => setIdText(null)}
          mono
          disabled={readOnly}
          invalid={idError !== undefined}
          placeholder={stage.role}
        />
      </Field>

      <AfterField stage={stage} stages={stages} index={index} readOnly={readOnly} patch={patch} />

      {stage.role === 'step' && (
        <Field label="Линия" hint="Бэк или фронт — по какой линии идёт этот шаг. Не задана — общая.">
          <Select
            value={stage.lane ?? ''}
            onChange={v => patch({ lane: v || null })}
            disabled={readOnly}
            placeholder="Не задана"
            options={LANE_OPTIONS}
            title="Линия шага"
          />
        </Field>
      )}

      <Field label="Есть в маршруте" hint={fixed ? NON_SKIPPABLE_HINT : '«По решению» — модель предложит, человек решит у каждой фичи.'}>
        {fixed ? (
          <div title={NON_SKIPPABLE_HINT} style={{ fontSize: FS.sm, color: C.textSecondary }}>Всегда</div>
        ) : (
          <PillSwitch<WorkflowStagePresence>
            fill={!isMobile}
            value={(PRESENCE_OPTIONS.some(o => o.value === stage.presence) ? stage.presence : 'on') as WorkflowStagePresence}
            onChange={v => !readOnly && patch({ presence: v })}
            options={PRESENCE_OPTIONS}
            isMobile={isMobile}
          />
        )}
      </Field>

      <ExecutorField stage={stage} specialties={specialties} personas={personas} readOnly={readOnly} patch={patch} />

      <Field
        label="Промпт этапа"
        error={prompt.length > PROMPT_LIMIT
          ? `Длиннее ${PROMPT_LIMIT} символов на ${prompt.length - PROMPT_LIMIT} — сократите или вынесите чек-лист в умение исполнителя.`
          : undefined}
      >
        <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xs }}>
          <div style={{ ...hintStyle, display: 'flex', gap: SP.sm, alignItems: 'baseline' }}>
            <span style={{ flex: 1, minWidth: 0 }}>Добавляется секцией <span style={{ fontFamily: FONT.mono }}>## ЭТАП</span> после правил — заменить или вырезать правила нельзя</span>
            <span data-prompt-counter="" style={{ fontFamily: FONT.mono, flexShrink: 0, color: prompt.length > PROMPT_LIMIT ? C.dangerText : C.textMuted }}>
              {prompt.length} / {PROMPT_LIMIT}
            </span>
          </div>
          <TextArea
            value={prompt}
            onChange={v => patch({ prompt: v })}
            readOnly={readOnly}
            autoGrow
            minHeight={80}
            maxHeight={320}
            placeholder="Что добавить к правилам этого этапа"
            style={prompt.length > PROMPT_LIMIT ? { border: `1px solid ${C.dangerBorder}` } : undefined}
          />
        </div>
      </Field>

      <ModelCells stage={stage} readOnly={readOnly} patch={patch} />

      <Field label="Уровень по умолчанию">
        <InlineSegmented<ModelTierKey | 'inherit'>
          value={stage.defaultTier ?? 'inherit'}
          options={TIER_SEG}
          onChange={v => !readOnly && patch({ defaultTier: v === 'inherit' ? null : v })}
          disabled={readOnly}
          isMobile={isMobile}
        />
      </Field>

      <DisallowedField stage={stage} readOnly={readOnly} patch={patch} />

      <McpField stage={stage} mcpChoices={mcpChoices} personas={personas} readOnly={readOnly} patch={patch} />

      <StallFields stage={stage} readOnly={readOnly} patch={patch} />

      {!readOnly && (
        <div>
          <Button
            variant="danger" size="sm" onClick={onRemove}
            leftIcon={<Trash2 size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
          >Убрать этап</Button>
        </div>
      )}
    </div>
  );
}

type Patch = (p: Partial<WorkflowStage>) => void;

// «После этапов»: выбранные — чипы с «×», добавление — выпадающий «+ Этап» из ещё не выбранных.
function AfterField({ stage, stages, index, readOnly, patch }: {
  stage: WorkflowStage; stages: WorkflowStage[]; index: number; readOnly: boolean; patch: Patch;
}): React.ReactElement {
  const after = stage.after ?? null;
  const shown = Array.from(new Set([...afterOptions(stages, index), ...(after ?? [])]));
  const chosen = after ?? [];
  const rest = shown.filter(id => !chosen.includes(id));
  const set = (next: string[]) => patch({ after: next.length > 0 ? next : null });
  return (
    <Field
      label={<>После этапов <span style={{ fontWeight: 400, color: C.textMuted }}>— кого ждёт этот этап</span></>}
      hint={index === 0
        ? 'Первый этап всегда начинает маршрут.'
        : 'Ничего не выбрано — после предыдущего по списку; несколько — ждёт их все.'}
    >
      {index === 0 ? (
        <span style={{ fontSize: FS.sm, color: C.textSecondary }}>С начала маршрута</span>
      ) : (
        <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6, alignItems: 'center' }}>
          {after !== null && after.length === 0 && (
            <Chip onRemove={readOnly ? undefined : () => patch({ after: null })} removeLabel="Убрать «с начала»">с начала</Chip>
          )}
          {chosen.map(id => (
            <Chip key={id} onRemove={readOnly ? undefined : () => set(chosen.filter(x => x !== id))} removeLabel={`Убрать «${id}»`}>
              {id}
            </Chip>
          ))}
          {!readOnly && (rest.length > 0 || after === null || after.length > 0) && (
            <div style={{ minWidth: 140 }}>
              <Select
                value=""
                onChange={v => { if (v === '__start') patch({ after: [] }); else if (v) set([...chosen, v]); }}
                placeholder="+ Этап"
                options={[
                  ...rest.map(id => ({ value: id, label: id })),
                  ...(after === null ? [{ value: '__start', label: 'с начала' }] : []),
                ]}
                title="Добавить этап, который нужно дождаться"
              />
            </div>
          )}
        </div>
      )}
    </Field>
  );
}

// «Исполнитель»: сегмент «Специальность / Персона» + выбор + «Сейчас это …». Выбор одного сбрасывает другое.
function ExecutorField({ stage, specialties, personas, readOnly, patch }: {
  stage: WorkflowStage; specialties: SpecialtyCatalogEntry[]; personas: Persona[]; readOnly: boolean; patch: Patch;
}): React.ReactElement {
  const isMobile = useIsMobile();
  const [picked, setPicked] = useState<'specialty' | 'persona' | null>(null);
  const mode = stage.personaId ? 'persona' : (picked ?? 'specialty');
  const spec = stage.specialty && stage.specialty !== 'none' ? stage.specialty : '';
  const persona = stage.personaId ? personas.find(p => p.id === stage.personaId) : spec ? personas.find(p => p.specialty === spec) : undefined;
  return (
    <Field
      label="Исполнитель"
      hint={persona ? `Сейчас это ${persona.name}.${mode === 'specialty' ? ' Специальность переживёт замену персоны.' : ''}` : 'Специальность или конкретная персона — одно из двух.'}
    >
      <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xs }}>
        <InlineSegmented<'specialty' | 'persona'>
          value={mode}
          options={[{ value: 'specialty', label: 'Специальность' }, { value: 'persona', label: 'Персона' }]}
          onChange={v => {
            if (readOnly) return;
            setPicked(v);
            patch(v === 'specialty' ? { personaId: null } : { specialty: null });
          }}
          disabled={readOnly}
          isMobile={isMobile}
        />
        {mode === 'specialty' ? (
          <Select
            value={spec}
            onChange={v => patch({ specialty: v || null, personaId: null })}
            disabled={readOnly}
            placeholder="Специальность не задана"
            options={specialties.map(s => ({ value: s.key, label: s.label }))}
            title="Специальность этапа"
          />
        ) : (
          <Select
            value={stage.personaId ?? ''}
            onChange={v => patch({ personaId: v || null, specialty: null })}
            disabled={readOnly}
            placeholder="Персона не задана"
            options={personas.map(p => ({ value: p.id, label: p.role ? `${p.role} (${p.name})` : p.name }))}
            title="Персона этапа"
          />
        )}
      </div>
    </Field>
  );
}

// Три колонки Strong/Medium/Weak: пустая — «как у специальности · модель слота», своя — подпись «своя».
function ModelCells({ stage, readOnly, patch }: { stage: WorkflowStage; readOnly: boolean; patch: Patch }): React.ReactElement {
  const isMobile = useIsMobile();
  const models = useModels();
  const presets = usePresets();
  const slots = useTierModels();
  const chainCtx = { tierModels: {} as Record<ModelTierKey, string>, ollamaModel: undefined };
  const cells: Array<{ tier: ModelTierKey; value: string; key: 'tierStrong' | 'tierMedium' | 'tierWeak' }> = [
    { tier: 'strong', value: stage.tierStrong ?? '', key: 'tierStrong' },
    { tier: 'medium', value: stage.tierMedium ?? '', key: 'tierMedium' },
    { tier: 'weak', value: stage.tierWeak ?? '', key: 'tierWeak' },
  ];
  return (
    <Field label="Модели" hint="Пустая ячейка — как у исполнителя (специальность и персона).">
      <div style={{ display: 'grid', gridTemplateColumns: isMobile ? '1fr' : 'repeat(3, minmax(0, 1fr))', gap: SP.sm }}>
        {cells.map(c => {
          const own = c.value !== '';
          const slot = slots[c.tier];
          return (
            <div key={c.tier} data-tier-cell={c.tier} style={{ display: 'flex', flexDirection: 'column', gap: SP.xs, minWidth: 0 }}>
              <span style={{ fontSize: FS.xs, fontWeight: 600, color: C.textSecondary }}>
                {TIER_CELL_TITLE[c.tier]}{own ? ' · своя' : ''}
              </span>
              <RoutePicker
                route={c.value}
                label={own ? routeDisplayLabel(c.value, presets, chainCtx) : ''}
                models={models}
                tierModels={chainCtx.tierModels}
                placeholder={`как у специальности${slot ? ` · ${slot}` : ''}`}
                showTiers={false}
                showPresets
                readOnly={readOnly}
                onChange={v => patch({ [c.key]: v.trim() || null })}
                fullWidth
                dashed={!own}
                cardTitle={TIER_TITLE[c.tier]}
              />
            </div>
          );
        })}
      </div>
    </Field>
  );
}

// «Запрещено на этапе»: чипы с «×» и поле «+ Инструмент» (Enter добавляет).
function DisallowedField({ stage, readOnly, patch }: { stage: WorkflowStage; readOnly: boolean; patch: Patch }): React.ReactElement {
  const list = stage.disallowedTools ?? [];
  const [text, setText] = useState('');
  const add = () => {
    const t = text.trim();
    setText('');
    if (t && !list.includes(t)) patch({ disallowedTools: [...list, t] });
  };
  return (
    <Field label="Запрещено на этапе" hint="Права · этап только сужает права персоны, расширить нельзя.">
      <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6, alignItems: 'center' }}>
        {list.map(t => (
          <Chip
            key={t}
            onRemove={readOnly ? undefined : () => patch({ disallowedTools: listOrNull(list.filter(x => x !== t)) })}
            removeLabel={`Убрать «${t}»`}
          ><span style={{ fontFamily: FONT.mono }}>{t}</span></Chip>
        ))}
        {!readOnly && (
          <div style={{ minWidth: 140, flex: '0 1 180px' }}>
            <TextField value={text} onChange={setText} onEnter={add} onBlur={add} mono placeholder="+ Инструмент" title="Добавить запрещённый инструмент" />
          </div>
        )}
        {readOnly && list.length === 0 && <span style={hintStyle}>Ничего не запрещено.</span>}
      </div>
    </Field>
  );
}

// «Сузить MCP»: переключатель + сетка. Выключен — исполнитель получает все MCP своей персоны (mcpAllow = null).
function McpField({ stage, mcpChoices, personas, readOnly, patch }: {
  stage: WorkflowStage; mcpChoices: string[]; personas: Persona[]; readOnly: boolean; patch: Patch;
}): React.ReactElement {
  const on = stage.mcpAllow ?? [];
  const [narrow, setNarrow] = useState(on.length > 0);
  const persona = stage.personaId ? personas.find(p => p.id === stage.personaId) : undefined;
  // Ключ, которого уже нет среди выданных, оставляем в списке — иначе правка молча его сотрёт.
  const shown = Array.from(new Set([...mcpChoices, ...on]));
  const narrowed = narrow || on.length > 0;
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xs }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm }}>
        <Toggle
          checked={narrowed}
          disabled={readOnly}
          ariaLabel="Сузить MCP"
          onChange={v => {
            setNarrow(v);
            if (!v) patch({ mcpAllow: null });
          }}
        />
        <span style={{ flex: 1, minWidth: 0, fontSize: FS.sm, color: C.textSecondary }}>
          <b style={{ color: C.textHeading }}>Сузить MCP</b> — на этапе только отмеченные; выключите — исполнитель получит все MCP своей персоны
        </span>
      </div>
      {narrowed && (shown.length === 0 ? (
        <span style={hintStyle}>Выданных MCP-серверов нет.</span>
      ) : (
        <div style={{ display: 'flex', flexWrap: 'wrap', gap: `${SP.xs}px ${SP.md}px` }}>
          {shown.map(k => {
            const denied = mcpNotGranted(persona, k);
            const checked = on.includes(k);
            return (
              <label
                key={k}
                data-mcp-key={k}
                title={denied ? 'Не выдан персоне — этап его не добавит' : undefined}
                style={{ display: 'inline-flex', alignItems: 'center', gap: SP.xs, opacity: denied ? 0.5 : 1, cursor: denied || readOnly ? 'default' : 'pointer' }}
              >
                <Checkbox
                  checked={checked}
                  disabled={readOnly || (denied && !checked)}
                  ariaLabel={k}
                  onChange={v => patch({ mcpAllow: listOrNull(v ? [...on, k] : on.filter(x => x !== k)) })}
                />
                <span style={{ fontFamily: FONT.mono, fontSize: FS.sm, color: C.textSecondary }}>{k}</span>
                {denied && <span style={hintStyle}>не выдан персоне</span>}
              </label>
            );
          })}
        </div>
      ))}
    </div>
  );
}

// «Если исполнитель молчит»: порог (5–240, ошибка «От 5 до 240 минут») и три радио-карточки действия.
function StallFields({ stage, readOnly, patch }: { stage: WorkflowStage; readOnly: boolean; patch: Patch }): React.ReactElement {
  const isMobile = useIsMobile();
  const bad = !stallInRange(stage.stallMinutes);
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.md }}>
      <div style={{ fontFamily: FONT.serif, fontSize: FS.base, fontWeight: 600, color: C.textHeading }}>Если исполнитель молчит</div>
      <div style={{ display: 'flex', flexDirection: isMobile ? 'column' : 'row', gap: SP.md, alignItems: 'flex-start' }}>
        <div style={{ flex: 1, minWidth: 0, width: isMobile ? '100%' : undefined }}>
          <Field
            label={<>Порог молчания, мин <span style={{ fontWeight: 400, color: C.textMuted }}>5–240</span></>}
            hint="Пусто — как в проекте: 20 мин. Это порог тишины, а не лимит длины хода."
            error={bad ? STALL_RANGE_ERROR : undefined}
          >
            <TextField
              value={stage.stallMinutes == null ? '' : String(stage.stallMinutes)}
              onChange={v => {
                const n = parseInt(v.replace(/\D/g, ''), 10);
                // Поле набирается по цифре: диапазон проверяет stageErrors, иначе «15» не набрать.
                patch({ stallMinutes: Number.isFinite(n) ? n : null });
              }}
              inputMode="numeric"
              disabled={readOnly}
              invalid={bad}
              placeholder="как в проекте"
            />
          </Field>
        </div>
        <div role="radiogroup" aria-label="Что делать" style={{ flex: 1, minWidth: 0, width: isMobile ? '100%' : undefined, display: 'flex', flexDirection: 'column', gap: 6 }}>
          <span style={{ fontSize: FS.sm, fontWeight: 600, color: C.textSecondary }}>Что делать</span>
          {STALL_POLICIES.map(p => {
            const card = POLICY_CARDS[p];
            const active = stage.stallPolicy === p;
            return (
              <button
                key={p}
                type="button"
                role="radio"
                aria-checked={active}
                disabled={readOnly}
                onClick={() => patch({ stallPolicy: active ? null : p })}
                style={{
                  display: 'flex', alignItems: 'flex-start', gap: SP.sm, textAlign: 'left', padding: `${SP.sm}px ${SP.md}px`,
                  minHeight: 44, borderRadius: R.lg, fontFamily: FONT.sans, cursor: readOnly ? 'default' : 'pointer',
                  border: `1px solid ${active ? C.accent : C.border}`, background: active ? C.accentLight : C.bgWhite,
                }}
              >
                <span aria-hidden style={{
                  flexShrink: 0, marginTop: 3, width: 14, height: 14, borderRadius: R.full, boxSizing: 'border-box',
                  border: `${active ? 4 : 1}px solid ${active ? C.accent : C.border}`, background: C.bgWhite,
                }} />
                <span style={{ minWidth: 0 }}>
                  <span style={{ display: 'block', fontSize: FS.sm, fontWeight: 600, color: C.textHeading }}>{card?.title ?? p}</span>
                  <span style={{ display: 'block', fontSize: FS.xs, color: C.textMuted }}>{card?.text}</span>
                </span>
              </button>
            );
          })}
        </div>
      </div>
    </div>
  );
}
