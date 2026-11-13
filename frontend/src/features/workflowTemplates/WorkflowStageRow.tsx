// Строка этапа шаблона процесса (C1b): присутствие, «кто», промпт, три ячейки моделей,
// сужения прав, порог молчания. Состояния не держит — всё в черновике родителя (onChange).
// Образец — SpecialtyEditView: те же RoutePicker для ячеек и те же примитивы ui-кита.

import { useState } from 'react';
import { ChevronDown, GripVertical, Lock, Trash2 } from 'lucide-react';
import { C, FONT, FS, R, SP } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { Badge, Button, Chip, Field, PillSwitch, Select, TextArea, TextField } from '../../components/ui';
import { RoutePicker } from '../../components/RoutePicker';
import { useModels } from '../../lib/models';
import { usePresets, routeDisplayLabel } from '../../lib/presets';
import { TIER_TITLE, type ModelTierKey } from '../../lib/modelTiers';
import { useIsMobile } from '../../lib/breakpoints';
import type { Persona } from '../personas/types';
import type { SpecialtyCatalogEntry } from '../specialties/types';
import { PROMPT_LIMIT, STALL_POLICIES, TITLE_LIMIT, titleRequired } from './workflowTemplateModel';
import { listOrNull, parseList, presenceEditable } from './workflowEditorLogic';
import { afterOptions, normalizeStages } from './workflowGraphModel';
import { presenceTag } from './workflowStripModel';
import type { WorkflowStage, WorkflowStagePresence } from './types';

// Подписи ролей этапа (ключи — TaskRoles, разрез §0.3).
export const STAGE_ROLE_LABEL: Record<string, string> = {
  cut: 'Разрез', design: 'Дизайн', plan: 'План', step: 'Шаг', review: 'Ревью',
  qa: 'Проверка', final: 'Финал', merge: 'Слияние',
};

const PRESENCE_OPTIONS: { value: WorkflowStagePresence; label: string }[] = [
  { value: 'on', label: 'Включён' },
  { value: 'optional', label: 'По выбору' },
  { value: 'off', label: 'Выключен' },
];

const POLICY_LABEL: Record<string, string> = {
  'nudge-alert': 'Напомнить, затем тревога',
  alert: 'Только тревога',
  'stop-alert': 'Остановить и тревога',
};

const LANE_OPTIONS = [
  { value: 'back', label: 'Бэк' },
  { value: 'front', label: 'Фронт' },
  { value: 'other', label: 'Другое' },
];

// Серверный формат id этапа (разрез 2 §1.1): slug [a-z0-9-]{1,32}.
const STAGE_ID_RE = /^[a-z0-9-]{1,32}$/;

export const NON_SKIPPABLE_HINT = 'Несъёмный этап: без него маршрут не закрыть — его нельзя выключить или сделать необязательным.';

interface Props {
  stage: WorkflowStage;
  // Все этапы шаблона и место этого этапа в списке: из них считаются id для «после» и дубли id.
  stages: WorkflowStage[];
  index: number;
  // Ошибки валидатора сервера и подсказки модели по этому этапу (показываются под заголовком).
  errors: string[];
  readOnly: boolean;
  specialties: SpecialtyCatalogEntry[];
  personas: Persona[];
  // Ключи MCP, уже выданные (проекту/персоне): шаблон может только сузить, не выдать новое.
  mcpChoices: string[];
  onChange: (next: WorkflowStage) => void;
  // Переименование id с каскадом по «после» других этапов (делает родитель: нужен весь список).
  onRenameId: (newId: string) => void;
  onRemove: () => void;
  // Лента (F20): номер колонки (null — выключен), «после» именами, строка «кто», ручка ⠿.
  column?: number | null;
  afterText?: string[];
  who?: string | null;
  grip?: { onPointerDown: (e: React.PointerEvent) => void; onKeyDown: (e: React.KeyboardEvent) => void };
  dragging?: boolean;
  dropHere?: boolean;
}

export function WorkflowStageRow({
  stage, stages, index, errors, readOnly, specialties, personas, mcpChoices, onChange, onRenameId, onRemove,
  column = index + 1, afterText = [], who = null, grip, dragging = false, dropHere = false,
}: Props): React.ReactElement {
  const isMobile = useIsMobile();
  const [open, setOpen] = useState(false);
  const fixed = !presenceEditable(stage.role);
  const label = STAGE_ROLE_LABEL[stage.role] ?? stage.role;
  const prompt = stage.prompt ?? '';
  // Имя этапа для показа (тот же порядок, что nodeTitle в FeatureProcessGraph): имя из шаблона,
  // фолбэк — подпись роли. Роль и id остаются подписью строки: с именем этапы одной роли
  // различимы, без него — как раньше (id, когда он отличается от роли).
  const titleText = (stage.title ?? '').trim();
  const nameShown = titleText || label;
  const titleLen = titleText.length;
  const titleNeeded = titleRequired(stages, index);
  const patch = (p: Partial<WorkflowStage>) => onChange({ ...stage, ...p });
  const mcpOn = stage.mcpAllow ?? [];
  // Ключ, которого уже нет среди выданных, оставляем в списке — иначе правка молча его сотрёт.
  const mcpShown = Array.from(new Set([...mcpChoices, ...mcpOn]));
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
  const after = stage.after ?? null;
  const parentOptions = afterOptions(stages, index);
  const afterShown = Array.from(new Set([...parentOptions, ...(after ?? [])]));
  const toggleAfter = (id: string) => {
    const cur = after ?? [];
    const next = cur.includes(id) ? cur.filter(x => x !== id) : [...cur, id];
    patch({ after: next.length > 0 ? next : null });
  };

  const tag = presenceTag(stage.presence);
  const isOff = stage.presence === 'off';
  const laneText = stage.role === 'step' && stage.lane ? (LANE_OPTIONS.find(l => l.value === stage.lane)?.label ?? stage.lane) : null;

  return (
    <div
      data-stage-index={index}
      style={{
        border: `1px ${isOff ? 'dashed' : 'solid'} ${errors.length ? C.dangerBorder : isOff ? C.dashed : dropHere ? C.accent : C.border}`,
        borderRadius: R.xl, background: isOff ? 'transparent' : C.bgWhite, overflow: 'hidden',
        opacity: dragging ? 0.5 : 1,
      }}
    >
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, padding: `0 ${SP.md}px 0 ${SP.xs}px` }}>
        {!readOnly && (
          <button
            type="button"
            data-stage-grip=""
            aria-label={`Переставить этап «${nameShown}»: стрелки вверх и вниз или перетащите`}
            title="Перетащите (на сенсорном — долгое нажатие) или стрелками вверх/вниз"
            onPointerDown={grip?.onPointerDown}
            onKeyDown={grip?.onKeyDown}
            style={{
              flexShrink: 0, width: 44, minHeight: 44, display: 'flex', alignItems: 'center', justifyContent: 'center',
              background: 'none', border: 'none', cursor: 'grab', color: C.textMuted, touchAction: 'pan-y',
            }}
          >
            <GripVertical size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
          </button>
        )}
        <span
          aria-label={column === null ? 'Нет в маршруте' : `Колонка ${column}`}
          style={{
            flexShrink: 0, minWidth: 22, textAlign: 'center', fontFamily: FONT.mono, fontSize: FS.sm,
            fontWeight: 600, color: column === null ? C.textMuted : C.textSecondary,
          }}
        >{column === null ? '—' : column}</span>
        <button
          type="button"
          onClick={() => setOpen(o => !o)}
          aria-expanded={open}
          aria-label={`Этап «${nameShown}»`}
          style={{
            flex: 1, minWidth: 0, display: 'flex', alignItems: 'center', gap: SP.sm, textAlign: 'left',
            padding: `${SP.sm}px 0`, minHeight: 44, background: 'none', border: 'none', cursor: 'pointer',
            fontFamily: FONT.sans, color: C.textHeading,
          }}
        >
          <span style={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', gap: 2 }}>
            <span style={{ fontSize: FS.base, fontWeight: 600 }}>
              {nameShown}
              {laneText && (
                <span style={{ marginLeft: SP.sm, fontSize: FS.xs, fontWeight: 600, color: C.textSecondary }}>{laneText}</span>
              )}
              <span style={{ marginLeft: SP.sm, fontFamily: FONT.mono, fontSize: FS.xs, fontWeight: 400, color: C.textMuted }}>
                {stage.role}{stageId !== stage.role ? ` · ${stageId}` : ''}
              </span>
            </span>
            <span style={{ fontSize: FS.xs, color: C.textMuted, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
              {isOff ? 'нет в этом шаблоне' : who}
            </span>
            {!isOff && afterText.length > 0 && (
              <span style={{ fontSize: FS.xs, color: C.textMuted }}>после: {afterText.join(', ')}</span>
            )}
          </span>
          <ChevronDown
            size={ICON_SIZE.sm} strokeWidth={ICON_STROKE}
            style={{ flexShrink: 0, transform: open ? 'none' : 'rotate(-90deg)', transition: 'transform 0.12s' }}
          />
        </button>
        {fixed && (
          <span title={NON_SKIPPABLE_HINT} aria-label="Несъёмный этап" style={{ display: 'flex', color: C.textMuted }}>
            <Lock size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
          </span>
        )}
        <Badge tone={tag.tone} size="xs">{tag.text}</Badge>
        {errors.length > 0 && <Badge tone="danger" size="xs">{errors.length}</Badge>}
      </div>

      {errors.length > 0 && (
        <div role="alert" style={{
          padding: `0 ${SP.md}px ${SP.sm}px`, fontSize: FS.xs, color: C.dangerText, lineHeight: 1.5,
        }}>
          {errors.map((e, i) => <div key={i}>{e}</div>)}
        </div>
      )}

      {open && (
        <div style={{
          borderTop: `1px solid ${C.borderLight}`, padding: SP.md,
          display: 'flex', flexDirection: 'column', gap: SP.lg,
        }}>
          <Field label="Присутствие" hint={fixed ? NON_SKIPPABLE_HINT : 'По выбору — оркестратор может предложить пропустить этап; выключенный не запускается.'}>
            {fixed ? (
              <div title={NON_SKIPPABLE_HINT} style={{ fontSize: FS.sm, color: C.textSecondary }}>Включён всегда</div>
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

          <Field label="Id этапа" hint="Имя этапа в маршруте: на него ссылаются поля «после». При переименовании ссылки обновятся сами." error={idError}>
            <TextField
              value={idText ?? stageId}
              onChange={onIdInput}
              onBlur={() => setIdText(null)}
              mono
              disabled={readOnly}
              placeholder={stage.role}
            />
          </Field>

          <Field
            label="Имя этапа"
            hint={titleNeeded
              ? `Этап делит роль и линию с другим этапом — имя обязательно и должно от него отличаться (иначе в списке и на схеме две неотличимые «${label}»). До ${TITLE_LIMIT} символов.`
              : `Имя для человека: видно в списке этапов и на схеме маршрута. До ${TITLE_LIMIT} символов.`}
            error={titleLen > TITLE_LIMIT ? `Имя длиннее ${TITLE_LIMIT} символов` : undefined}
          >
            <TextField
              value={stage.title ?? ''}
              onChange={v => patch({ title: v })}
              disabled={readOnly}
              placeholder={label}
            />
          </Field>

          <Field
            label="После"
            hint={index === 0
              ? 'Первый этап всегда начинает маршрут.'
              : 'Этапы, которых этот ждёт. Ничего не выбрано — после предыдущего по списку; несколько — ждёт их все.'}
          >
            {index === 0 ? (
              <span style={{ fontSize: FS.sm, color: C.textSecondary }}>С начала маршрута</span>
            ) : (
              <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6 }}>
                <Chip
                  variant="toggle" selected={after !== null && after.length === 0} disabled={readOnly}
                  onClick={() => !readOnly && patch({ after: after !== null && after.length === 0 ? null : [] })}
                >с начала</Chip>
                {afterShown.map(id => (
                  <Chip
                    key={id} variant="toggle" selected={(after ?? []).includes(id)} disabled={readOnly}
                    onClick={() => !readOnly && toggleAfter(id)}
                  >{id}</Chip>
                ))}
              </div>
            )}
          </Field>

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

          <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
            <Field label="Кто работает" hint="Специальность или конкретная персона — одно из двух. Выбор одного сбрасывает другое.">
              <Select
                value={stage.specialty && stage.specialty !== 'none' ? stage.specialty : ''}
                onChange={v => patch({ specialty: v || null, personaId: v ? null : stage.personaId })}
                disabled={readOnly}
                placeholder="Специальность не задана"
                options={specialties.map(s => ({ value: s.key, label: s.label }))}
                title="Специальность этапа"
              />
            </Field>
            <Select
              value={stage.personaId ?? ''}
              onChange={v => patch({ personaId: v || null, specialty: v ? null : stage.specialty })}
              disabled={readOnly}
              placeholder="Персона не задана"
              options={personas.map(p => ({ value: p.id, label: p.role ? `${p.role} (${p.name})` : p.name }))}
              title="Персона этапа"
            />
          </div>

          <Field
            label="Промпт этапа"
            hint={`${prompt.length} / ${PROMPT_LIMIT} — добавляется к правилам хода и не вытесняет их`}
            error={prompt.length > PROMPT_LIMIT ? `Длиннее ${PROMPT_LIMIT} символов` : undefined}
          >
            <TextArea
              value={prompt}
              onChange={v => patch({ prompt: v })}
              readOnly={readOnly}
              autoGrow
              minHeight={80}
              maxHeight={320}
              placeholder="Что добавить к правилам этого этапа"
            />
          </Field>

          <StageModelCells stage={stage} readOnly={readOnly} onChange={patch} />

          <Field label="Запрещённые инструменты" hint="Через запятую. Только сужает права персоны, расширить нельзя.">
            <TextField
              value={(stage.disallowedTools ?? []).join(', ')}
              onChange={v => patch({ disallowedTools: listOrNull(parseList(v)) })}
              disabled={readOnly}
              placeholder="tasks, web"
            />
          </Field>

          <Field label="MCP" hint="Только из уже выданных. Ничего не выбрано — без сужения.">
            {mcpShown.length === 0 ? (
              <span style={{ fontSize: FS.xs, color: C.textMuted }}>Выданных MCP-серверов нет.</span>
            ) : (
              <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6 }}>
                {mcpShown.map(k => {
                  const on = mcpOn.includes(k);
                  return (
                    <button
                      key={k}
                      type="button"
                      aria-pressed={on}
                      disabled={readOnly}
                      onClick={() => patch({ mcpAllow: listOrNull(on ? mcpOn.filter(x => x !== k) : [...mcpOn, k]) })}
                      style={{
                        padding: '6px 12px', borderRadius: R.pill, minHeight: 32,
                        cursor: readOnly ? 'default' : 'pointer',
                        fontFamily: FONT.sans, fontSize: FS.sm, fontWeight: 600,
                        border: `1px solid ${on ? C.accent : C.border}`,
                        background: on ? C.accentLight : C.bgWhite,
                        color: on ? C.accent : C.textSecondary,
                      }}
                    >{k}</button>
                  );
                })}
              </div>
            )}
          </Field>

          <div style={{ display: 'flex', flexDirection: isMobile ? 'column' : 'row', gap: SP.md }}>
            <div style={{ flex: 1, minWidth: 0 }}>
              <Field label="Порог молчания, мин" hint="5–240; пусто — по умолчанию из конфига. Больше 240 клампится сервером.">
                <TextField
                  value={stage.stallMinutes == null ? '' : String(stage.stallMinutes)}
                  onChange={v => {
                    const n = parseInt(v.replace(/\D/g, ''), 10);
                    // Поле набирается по цифре: кламп — при сохранении, иначе «15» не набрать.
                    patch({ stallMinutes: Number.isFinite(n) ? n : null });
                  }}
                  inputMode="numeric"
                  disabled={readOnly}
                  placeholder="по умолчанию"
                />
              </Field>
            </div>
            <div style={{ flex: 1, minWidth: 0 }}>
              <Field label="Политика молчания">
                <Select
                  value={stage.stallPolicy ?? ''}
                  onChange={v => patch({ stallPolicy: v || null })}
                  disabled={readOnly}
                  placeholder="По умолчанию"
                  options={STALL_POLICIES.map(p => ({ value: p, label: POLICY_LABEL[p] ?? p }))}
                />
              </Field>
            </div>
          </div>

          {!readOnly && (
            <div>
              <Button
                variant="danger" size="sm" onClick={onRemove}
                leftIcon={<Trash2 size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
              >Убрать этап</Button>
            </div>
          )}
        </div>
      )}
    </div>
  );
}

// Три ячейки моделей — тот же RoutePicker и те же пропсы, что у специальности (без превью резолва).
function StageModelCells({ stage, readOnly, onChange }: {
  stage: WorkflowStage;
  readOnly: boolean;
  onChange: (p: Partial<WorkflowStage>) => void;
}): React.ReactElement {
  const isMobile = useIsMobile();
  const models = useModels();
  const presets = usePresets();
  const chainCtx = { tierModels: {} as Record<ModelTierKey, string>, ollamaModel: undefined };
  const cells: Array<{ tier: ModelTierKey; value: string; key: 'tierStrong' | 'tierMedium' | 'tierWeak' }> = [
    { tier: 'strong', value: stage.tierStrong ?? '', key: 'tierStrong' },
    { tier: 'medium', value: stage.tierMedium ?? '', key: 'tierMedium' },
    { tier: 'weak', value: stage.tierWeak ?? '', key: 'tierWeak' },
  ];
  return (
    <Field label="Модели по уровням" hint="Пустая ячейка наследуется от персоны и специальности.">
      <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
        {cells.map(c => (
          <div key={c.tier} style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
            <span style={{ width: 64, flexShrink: 0, fontSize: FS.sm, color: C.textSecondary }}>{TIER_TITLE[c.tier]}</span>
            <div style={{ flex: 1, minWidth: 0, display: 'flex' }}>
              <RoutePicker
                route={c.value}
                label={c.value ? routeDisplayLabel(c.value, presets, chainCtx) : ''}
                models={models}
                tierModels={chainCtx.tierModels}
                placeholder={isMobile ? 'не задано' : 'Наследуется'}
                showTiers={false}
                showPresets
                readOnly={readOnly}
                onChange={v => onChange({ [c.key]: v.trim() || null })}
                fullWidth
                dashed={!c.value}
              />
            </div>
          </div>
        ))}
      </div>
    </Field>
  );
}
