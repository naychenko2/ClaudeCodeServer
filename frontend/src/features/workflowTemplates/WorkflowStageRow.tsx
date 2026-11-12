// Строка этапа шаблона процесса (C1b): присутствие, «кто», промпт, три ячейки моделей,
// сужения прав, порог молчания. Состояния не держит — всё в черновике родителя (onChange).
// Образец — SpecialtyEditView: те же RoutePicker для ячеек и те же примитивы ui-кита.

import { useState } from 'react';
import { ChevronDown, Lock } from 'lucide-react';
import { C, FONT, FS, R, SP } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { Badge, Field, PillSwitch, Select, TextArea, TextField } from '../../components/ui';
import { RoutePicker } from '../../components/RoutePicker';
import { useModels } from '../../lib/models';
import { usePresets, routeDisplayLabel } from '../../lib/presets';
import { TIER_TITLE, type ModelTierKey } from '../../lib/modelTiers';
import { useIsMobile } from '../../lib/breakpoints';
import type { Persona } from '../personas/types';
import type { SpecialtyCatalogEntry } from '../specialties/types';
import { isNonSkippable, PROMPT_LIMIT, STALL_POLICIES } from './workflowTemplateModel';
import { listOrNull, parseList } from './workflowEditorLogic';
import type { WorkflowStage, WorkflowStagePresence } from './types';

// Подписи ролей этапа (ключи — TaskRoles, разрез §0.3).
export const STAGE_ROLE_LABEL: Record<string, string> = {
  cut: 'Нарезка', design: 'Дизайн', plan: 'План', step: 'Шаг', review: 'Ревью',
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

export const NON_SKIPPABLE_HINT = 'Несъёмный этап: без него маршрут не закрыть — его нельзя выключить или сделать необязательным.';

interface Props {
  stage: WorkflowStage;
  // Ошибки валидатора сервера и подсказки модели по этому этапу (показываются под заголовком).
  errors: string[];
  readOnly: boolean;
  specialties: SpecialtyCatalogEntry[];
  personas: Persona[];
  // Ключи MCP, уже выданные (проекту/персоне): шаблон может только сузить, не выдать новое.
  mcpChoices: string[];
  onChange: (next: WorkflowStage) => void;
}

export function WorkflowStageRow({
  stage, errors, readOnly, specialties, personas, mcpChoices, onChange,
}: Props): React.ReactElement {
  const isMobile = useIsMobile();
  const [open, setOpen] = useState(false);
  const fixed = isNonSkippable(stage.role);
  const label = STAGE_ROLE_LABEL[stage.role] ?? stage.role;
  const prompt = stage.prompt ?? '';
  const patch = (p: Partial<WorkflowStage>) => onChange({ ...stage, ...p });
  const mcpOn = stage.mcpAllow ?? [];
  // Ключ, которого уже нет среди выданных, оставляем в списке — иначе правка молча его сотрёт.
  const mcpShown = Array.from(new Set([...mcpChoices, ...mcpOn]));

  return (
    <div style={{
      border: `1px solid ${errors.length ? C.dangerBorder : C.border}`, borderRadius: R.xl,
      background: C.bgWhite, overflow: 'hidden',
    }}>
      <button
        type="button"
        onClick={() => setOpen(o => !o)}
        aria-expanded={open}
        aria-label={`Этап «${label}»`}
        style={{
          width: '100%', display: 'flex', alignItems: 'center', gap: SP.sm, textAlign: 'left',
          padding: `${SP.md}px`, minHeight: 44, background: 'none', border: 'none', cursor: 'pointer',
          fontFamily: FONT.sans, color: C.textHeading,
        }}
      >
        <ChevronDown
          size={ICON_SIZE.sm} strokeWidth={ICON_STROKE}
          style={{ flexShrink: 0, transform: open ? 'none' : 'rotate(-90deg)', transition: 'transform 0.12s' }}
        />
        <span style={{ fontSize: FS.base, fontWeight: 600, flex: 1, minWidth: 0 }}>{label}</span>
        {fixed && (
          <Badge tone="neutral" size="xs" icon={<Lock size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />} title={NON_SKIPPABLE_HINT}>
            несъёмный
          </Badge>
        )}
        <Badge tone={stage.presence === 'on' ? 'success' : stage.presence === 'optional' ? 'warning' : 'neutral'} size="xs">
          {PRESENCE_OPTIONS.find(o => o.value === stage.presence)?.label ?? stage.presence}
        </Badge>
        {errors.length > 0 && <Badge tone="danger" size="xs">{errors.length}</Badge>}
      </button>

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
