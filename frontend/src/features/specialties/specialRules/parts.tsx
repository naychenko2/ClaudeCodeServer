import { X } from 'lucide-react';
import { RoutePicker } from '../../../components/RoutePicker';
import { IconButton } from '../../../components/ui';
import { ICON_SIZE, ICON_STROKE } from '../../../components/ui/icons';
import { C, FONT, FS, R, SP } from '../../../lib/design';
import { TIERS, TIER_ORDER, routeTier, type TierKey } from '../../../lib/modelProvidersShared';
import { cellPresetLabel, findPreset, presetIdOf, usePreview, usePresets } from '../../../lib/presets';
import type { LayerReducer } from '../../../lib/presets';
import { modelLabel, type ModelOption } from '../../../lib/models';
import type { SpecialtySettingsLayer } from '../../../types';

// Мелкие детали вкладки «Особые правила», общие для карточки «Любой специальности»,
// групповой карточки, карточки отдельной роли и мастера: строка поля уровня, строка
// «Сейчас пойдёт», заголовок секции и подпись тройки.

export type Scope = 'global' | 'owner' | 'user';

export interface PickerCtx {
  models: ModelOption[];
  tierModels: Record<TierKey, string>;
  ollamaModel?: string;
  savingScope: Scope | null;
  // Контракт редьюсерный (см. presets.saveLayer). Стор сам читает текущий слой и
  // шлёт PUT в нужный scope+userId. Снимок слоя внутрь ctx не передаём — потребители
  // либо пользуются редьюсером, либо берут слой из стора сами (структурный запрет).
  onSaveLayer: (scope: Scope, reducer: LayerReducer,
    userId?: string | null) => Promise<void>;
  // Слой пресетов панели выбора: 'global' — общий (место видят все), undefined — личный.
  // Для чужого слоя («Пользователю…») цепочка обязана быть ОБЩЕЙ, иначе у адресата
  // ссылка будет битой — поэтому там тоже 'global'.
  presetScope?: 'global' | 'user';
  busy: boolean;
  readOnly: boolean;
}

// Строка поля уровня: подпись слева, значение во всю оставшуюся ширину, ✕ справа
// («Вернуть наследование» по одному полю). Пустое значение рисуется пунктиром с
// подписью, откуда оно унаследуется, — это и есть видимое наследование из листа текстов.
export function TierFieldRow({ tier, route, placeholder, ctx, onChange, onClear, onPresetCreated }: {
  tier: TierKey;
  route: string;
  // Подпись пустого поля: «Как «Любая специальность»» / «Как «Модели по умолчанию»» /
  // «Как у владельца»
  placeholder: string;
  ctx: PickerCtx;
  onChange: (route: string) => void;
  // Не задан — ✕ не рисуем (поле и так наследуется)
  onClear?: () => void;
  onPresetCreated?: (presetId: string, scope: Scope, layer: SpecialtySettingsLayer) => void;
}) {
  const presets = usePresets();
  const { label, title } = cellPresetLabel(route, presets, {
    tierModels: ctx.tierModels, ollamaModel: ctx.ollamaModel,
  });
  // Самоссылка: шаг цепочки указывает обратно на этот же уровень — резолвер пропустит
  // его как петлю, цепочка фактически короче (лист текстов, B8)
  const preset = findPreset(presets, presetIdOf(route));
  const selfRef = !!preset && preset.steps.some(s => routeTier(s) === tier);

  return (
    <div style={{ marginTop: SP.sm }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, minWidth: 0 }}>
        <span style={{ width: 60, flexShrink: 0, fontSize: FS.xs, color: C.textMuted }}>
          {TIERS[tier].title}
        </span>
        <RoutePicker
          route={route}
          label={route ? label : ''}
          title={route ? title : undefined}
          placeholder={placeholder}
          models={ctx.models}
          tierModels={ctx.tierModels}
          ollamaModel={ctx.ollamaModel}
          showTiers={false}
          showPresets
          presetScope={ctx.presetScope}
          presetCreation={{
            savingScope: ctx.savingScope,
            onSaveLayer: ctx.onSaveLayer,
            onCreated: onPresetCreated,
          }}
          readOnly={ctx.readOnly}
          busy={ctx.busy}
          fullWidth
          dashed
          onChange={onChange}
        />
        {onClear && route && !ctx.readOnly && (
          <IconButton size="xs" tone="danger" title="Вернуть наследование"
            disabled={ctx.busy} onClick={onClear}>
            <X size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
          </IconButton>
        )}
      </div>
      {selfRef && (
        <div style={{
          fontSize: FS.xs, color: C.warningText, lineHeight: 1.45, margin: '4px 0 0 68px',
        }}>
          Шаг «{TIERS[tier].title}» внутри цепочки указывает обратно на это же поле — он будет пропущен.
        </div>
      )}
    </div>
  );
}

// Строка-итог «Сейчас пойдёт» для специальности: три уровня в одной строке.
// Источник — серверный резолв GET /api/models/preview (второй точки истины на фронте
// нет, ADR-007 §5 п.5), поэтому строка ЧЕСТНА только для того, кто её смотрит: в слое
// «Пользователю…» она не рисуется вовсе (за другого пользователя резолв не посчитать),
// а в общем слое подписана «у вас».
export function WillGoLine({ specialtyKey, scope }: { specialtyKey: string; scope: Scope }) {
  // В чужом слое ключ не передаём вовсе: без него usePreview не шлёт запрос, а иначе
  // мы бы выспрашивали у сервера СВОЙ резолв и рисовали его как чужой
  const key = scope === 'user' ? undefined : specialtyKey;
  const strong = usePreview({ kind: 'specialty', specialtyKey: key, tier: 'strong' });
  const medium = usePreview({ kind: 'specialty', specialtyKey: key, tier: 'medium' });
  const weak = usePreview({ kind: 'specialty', specialtyKey: key, tier: 'weak' });
  if (scope === 'user') return null;
  const data: Record<TierKey, typeof strong> = { strong, medium, weak };
  const parts = TIER_ORDER.map(t => {
    const d = data[t];
    if (!d) return null;
    if (d.preset?.broken) return { tier: t, text: 'цепочка удалена', broken: true };
    if (!d.model) return null;
    return { tier: t, text: modelLabel(d.model), broken: false };
  }).filter((x): x is { tier: TierKey; text: string; broken: boolean } => x !== null);
  if (parts.length === 0) return null;
  return (
    <div style={{
      display: 'flex', alignItems: 'center', gap: 6, flexWrap: 'wrap',
      marginTop: SP.sm, fontSize: FS.xs, color: C.textMuted, lineHeight: 1.5,
    }}>
      <span>{scope === 'global' ? 'Сейчас пойдёт у вас:' : 'Сейчас пойдёт:'}</span>
      {parts.map((p, i) => (
        <span key={p.tier} style={{ display: 'inline-flex', alignItems: 'center', gap: 5 }}>
          {i > 0 && <span style={{ color: C.textMuted }}>·</span>}
          <span>{TIERS[p.tier].title} —</span>
          <span style={{
            fontFamily: FONT.mono, fontSize: FS.xs, fontWeight: 700,
            color: p.broken ? C.warningText : C.textHeading,
            background: C.bgSelected, borderRadius: R.sm, padding: '1px 6px',
          }}>{p.text}</span>
        </span>
      ))}
    </div>
  );
}

// Заголовок секции («Одинаковые наборы · 9 ролей в 4 группах»)
export function SectionTitle({ children }: { children: React.ReactNode }) {
  return (
    <div style={{
      fontFamily: FONT.sans, fontSize: FS.xs, fontWeight: 700, color: C.textMuted,
      textTransform: 'uppercase', letterSpacing: '0.07em', margin: `${SP.lg}px 2px ${SP.sm}px`,
    }}>
      {children}
    </div>
  );
}
