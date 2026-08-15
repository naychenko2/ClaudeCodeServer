import { useState } from 'react';
import { Button, InlineSegmented } from '../../../components/ui';
import { RoutePicker } from '../../../components/RoutePicker';
import { C, FONT, FS, R, SHADOW, SP } from '../../../lib/design';
import { TIERS, TIER_ORDER, routeLabel, type TierKey } from '../../../lib/modelProvidersShared';
import { cellPresetLabel, findPreset, presetIdOf, usePresets } from '../../../lib/presets';
import { isEmptyTriple, type RoleRow } from './model';
import type { PickerCtx, Scope } from './parts';

// Мастер «Новое правило»: три помеченных шага в ОДНОЙ панели, без переходов между
// экранами (макет v4). Панель встаёт под списком, а не модалкой поверх модалки:
// вложенность на вкладке ограничена двумя уровнями, а модалка-в-модалке была бы третьим.
//
// Строка «Сейчас пойдёт» здесь считается не серверным превью, а головой выбранного
// значения: превью резолвит СОХРАНЁННОЕ состояние и на несохранённом черновике врало бы.
// Голова цепочки — не второй резолв, а чтение первого шага выбранного значения.

export function AddRuleWizard({ roles, scope, ctx, onCancel, onSave }: {
  roles: RoleRow[];
  scope: Scope;
  ctx: PickerCtx;
  onCancel: () => void;
  onSave: (specialtyKey: string, tier: TierKey, route: string) => void;
}) {
  const presets = usePresets();
  const [specialty, setSpecialty] = useState<string | null>(null);
  const [tier, setTier] = useState<TierKey>('strong');
  const [route, setRoute] = useState('');

  const labelCtx = { tierModels: ctx.tierModels, ollamaModel: ctx.ollamaModel };
  const { label } = cellPresetLabel(route, presets, labelCtx);

  // Что реально уйдёт первым: у цепочки — её первый шаг, у модели — она сама
  const preset = findPreset(presets, presetIdOf(route));
  const head = preset ? (preset.steps[0] ?? '') : route;
  const willGo = head ? routeLabel(head, ctx.ollamaModel, ctx.tierModels) : '';

  // Пометка роли в списке: заполнены все три поля / часть из них
  const markOf = (r: RoleRow): string => {
    if (isEmptyTriple(r.triple)) return '';
    return r.triple.every(v => v) ? ' · задано' : ' · частично';
  };

  const canSave = !!specialty && !!route && !ctx.busy;

  return (
    <div style={{
      marginTop: SP.md, border: `1px solid ${C.accentMuted}`, borderRadius: R.xl,
      background: C.bgCard, padding: `${SP.md}px 14px`, boxShadow: SHADOW.card,
    }}>
      <div style={{ fontSize: FS.base, fontWeight: 700, color: C.textHeading }}>Новое правило</div>

      <Step no={1} title="Для какой специальности">
        <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6 }}>
          {roles.map(r => {
            const on = specialty === r.key;
            return (
              <button key={r.key} type="button" onClick={() => setSpecialty(r.key)} style={{
                font: 'inherit', fontFamily: FONT.sans, fontSize: FS.sm, fontWeight: 600,
                cursor: 'pointer', padding: '5px 10px', borderRadius: R.max,
                border: `1px solid ${on ? C.accent : C.border}`,
                background: on ? C.accent : C.bgWhite,
                color: on ? C.onAccent : C.textSecondary,
              }}>
                {r.label}
                {markOf(r) && (
                  <span style={{ opacity: 0.75, fontWeight: 400 }}>{markOf(r)}</span>
                )}
              </button>
            );
          })}
        </div>
      </Step>

      <Step no={2} title="Какой уровень">
        <InlineSegmented<TierKey>
          value={tier}
          options={TIER_ORDER.map(t => ({ value: t, label: TIERS[t].title }))}
          disabled={ctx.busy}
          onChange={setTier}
        />
      </Step>

      <Step no={3} title="Чем закрыть">
        <RoutePicker
          route={route}
          label={route ? label : ''}
          placeholder="Выбрать модель или цепочку"
          models={ctx.models}
          tierModels={ctx.tierModels}
          ollamaModel={ctx.ollamaModel}
          showTiers={false}
          showPresets
          presetScope={ctx.presetScope}
          // onCreated здесь СОЗНАТЕЛЬНО не передаётся: с ним PresetOptions отдаёт слой
          // вызывающему и сам значение не назначает — черновик мастера остался бы пустым.
          // Без него панель сохраняет цепочку и возвращает её сюда через onChange.
          presetCreation={{
            settings: ctx.settings,
            savingScope: ctx.savingScope,
            onSaveLayer: ctx.onSaveLayer,
          }}
          busy={ctx.busy}
          readOnly={ctx.readOnly}
          fullWidth
          onChange={setRoute}
        />
        {willGo && (
          <div style={{
            display: 'flex', alignItems: 'center', gap: 7, flexWrap: 'wrap',
            marginTop: SP.sm, fontSize: FS.xs, color: C.textMuted,
          }}>
            <span>Сейчас пойдёт:</span>
            <span style={{
              fontFamily: FONT.mono, fontSize: FS.xs, fontWeight: 700, color: C.textHeading,
              background: C.bgSelected, borderRadius: R.sm, padding: '1px 6px',
            }}>{willGo}</span>
          </div>
        )}
      </Step>

      <div style={{ display: 'flex', justifyContent: 'flex-end', gap: SP.sm, marginTop: SP.md }}>
        <Button size="sm" variant="ghost" disabled={ctx.busy} onClick={onCancel}>Отмена</Button>
        <Button size="sm" variant="primary" disabled={!canSave}
          onClick={() => specialty && onSave(specialty, tier, route)}>
          Сохранить правило
        </Button>
      </div>
      {scope === 'global' && (
        <div style={{ fontSize: FS.xs, color: C.textMuted, marginTop: 6, lineHeight: 1.45 }}>
          Правило сработает для всех пользователей, кроме тех, кто задал своё.
        </div>
      )}
    </div>
  );
}

function Step({ no, title, children }: { no: number; title: string; children: React.ReactNode }) {
  return (
    <div style={{ display: 'flex', gap: 10, marginTop: 10 }}>
      <span style={{
        width: 20, height: 20, borderRadius: R.full, flexShrink: 0, marginTop: 1,
        background: C.accent, color: C.onAccent, fontSize: FS.xs, fontWeight: 700,
        display: 'flex', alignItems: 'center', justifyContent: 'center',
      }}>{no}</span>
      <div style={{ flex: 1, minWidth: 0 }}>
        <div style={{ fontSize: FS.sm, fontWeight: 600, color: C.textHeading, marginBottom: 5 }}>
          {title}
        </div>
        {children}
      </div>
    </div>
  );
}
