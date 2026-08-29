import { ArrowDown, ArrowUp, Plus, X } from 'lucide-react';
import { IconButton } from './ui';
import { ICON_SIZE, ICON_STROKE } from './ui/icons';
import { RoutePicker } from './RoutePicker';
import { chainStepLabel } from '../lib/presets';
import { TIERS, type TierKey } from '../lib/modelProvidersShared';
import { C, FS, R } from '../lib/design';
import type { ModelOption } from '../lib/models';

// Общий редактор шагов цепочки фолбэка: список RoutePicker-строк с ↑↓✕ и кнопка
// добавления. Один визуальный язык для двух мест правки цепочки — слота («Модели
// по умолчанию», SlotsTab.ChainEditor) и inline-сборки нового пресета (PresetOptions).
// Пресет в пресет не вкладывается — шаги здесь всегда без showPresets у RoutePicker.
export function ChainStepsEditor({ steps, onChange, models, tierModels, ollamaModel, ollamaProvider,
  readOnly = false, busy = false, maxSteps = 5, addLabel }: {
  steps: string[];
  onChange: (steps: string[]) => void;
  models: ModelOption[];
  tierModels: Record<TierKey, string>;
  ollamaModel?: string;
  ollamaProvider?: string;
  readOnly?: boolean;
  busy?: boolean;
  maxSteps?: number;
  // Подпись кнопки добавления первого шага (пустая цепочка); по умолчанию — общая
  addLabel?: string;
}) {
  const labelCtx = { tierModels, ollamaModel, ollamaProvider };

  const moveStep = (i: number, dir: -1 | 1) => {
    const j = i + dir;
    if (j < 0 || j >= steps.length) return;
    const next = [...steps];
    [next[i], next[j]] = [next[j], next[i]];
    onChange(next);
  };
  const removeStep = (i: number) => onChange(steps.filter((_, j) => j !== i));
  const setStep = (i: number, v: string) => onChange(steps.map((s, j) => j === i ? v : s));
  const addStep = () => onChange([...steps, TIERS.medium.route]);

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
      {steps.length > 0 && (
        <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
          {steps.map((step, i) => (
            <div key={i} style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
              <span style={{ fontSize: FS.xs, color: C.textMuted, width: 14, textAlign: 'right', flexShrink: 0 }}>{i + 1}</span>
              <div style={{ flex: 1, minWidth: 0 }}>
                <RoutePicker
                  route={step}
                  label={chainStepLabel(step, labelCtx)}
                  models={models}
                  tierModels={tierModels}
                  ollamaModel={ollamaModel}
                  ollamaProvider={ollamaProvider}
                  allowLocal
                  readOnly={readOnly}
                  busy={busy}
                  onChange={v => setStep(i, v)}
                />
              </div>
              {!readOnly && (
                <span style={{ display: 'flex', gap: 2, flexShrink: 0 }}>
                  <IconButton size="xs" tone="muted" title="Выше" disabled={busy || i === 0} onClick={() => moveStep(i, -1)}>
                    <ArrowUp size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
                  </IconButton>
                  <IconButton size="xs" tone="muted" title="Ниже" disabled={busy || i === steps.length - 1} onClick={() => moveStep(i, 1)}>
                    <ArrowDown size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
                  </IconButton>
                  <IconButton size="xs" tone="muted" title={steps.length === 1 ? 'Пустая цепочка не сохранится' : 'Убрать шаг'}
                    disabled={busy || steps.length === 1} onClick={() => removeStep(i)}>
                    <X size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
                  </IconButton>
                </span>
              )}
            </div>
          ))}
        </div>
      )}

      {!readOnly && (
        <button type="button" disabled={busy || steps.length >= maxSteps} onClick={addStep} style={addStepBtnStyle}>
          <Plus size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
          {steps.length === 0 ? (addLabel ?? 'Добавьте первый шаг →') : 'Добавить модель'}
        </button>
      )}
      {steps.length >= maxSteps && (
        <div style={{ fontSize: FS.xs, color: C.textMuted, lineHeight: 1.4 }}>
          В цепочке не больше пяти моделей
        </div>
      )}
    </div>
  );
}

const addStepBtnStyle = {
  display: 'inline-flex', alignItems: 'center', gap: 6, alignSelf: 'flex-start',
  font: 'inherit', fontSize: FS.xs, fontWeight: 600, color: C.accent,
  background: C.accentLight, border: `1px dashed ${C.accentMuted}`, borderRadius: R.md,
  padding: '6px 10px', cursor: 'pointer',
} as const;
