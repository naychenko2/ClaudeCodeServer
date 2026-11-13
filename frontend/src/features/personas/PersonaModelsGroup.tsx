import type { ReactNode } from 'react';
import { Badge, Button, SegmentedControl } from '../../components/ui';
import { C, FONT, FS, R, SP } from '../../lib/design';
import { TIER_ORDER, TIER_TITLE, type ModelTierKey } from '../../lib/modelTiers';

// Группа «Модели» формы персоны (макет persona-inheritance.html, P18–P23): карточка с
// переключателем «Как у специальности / Своё». При наследовании — три отключённые строки со
// значением специальности (пунктир), при «Своём» — рабочие выбиратели; пустая ячейка по-прежнему
// берётся у специальности. Состояние и сетевые вызовы — в PersonaForm.

export interface PersonaModelsGroupProps {
  own: boolean;
  onModeChange: (own: boolean) => void;
  // Идёт запрос сброса: переключатель и «Сбросить» выключены, повторная отправка не уходит
  busy?: boolean;
  // Модель — деньги арендатора: участнику всё выключено
  readOnly?: boolean;
  readOnlyHint?: string;
  // Сколько ячеек заданы персоной
  ownCount: number;
  cells: Record<ModelTierKey, string>;
  // Короткая подпись унаследованного значения: «Opus 5.5 · спец.»
  inheritedLabel: (t: ModelTierKey) => string;
  renderPicker: (t: ModelTierKey) => ReactNode;
  renderPreview: (t: ModelTierKey) => ReactNode;
  onReset: (t: ModelTierKey) => void;
  footer?: ReactNode;
}

const rowLabel = { width: 64, flexShrink: 0, fontSize: FS.base, color: C.textSecondary, fontFamily: FONT.sans } as const;

export function PersonaModelsGroup(p: PersonaModelsGroupProps) {
  const locked = !!p.readOnly || !!p.busy;
  return (
    <div data-testid="models-group" style={{
      background: p.own ? C.bgWhite : C.bgCard,
      border: `1px solid ${C.border}`, borderRadius: R.lg, padding: SP.lg,
    }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap' }}>
        <span style={{ fontFamily: FONT.sans, fontSize: FS.md, fontWeight: 700, color: C.textPrimary }}>Модели</span>
        {p.own && <Badge tone="plan" size="xs">{`своё · ${p.ownCount} из 3`}</Badge>}
        <span style={{ flex: 1 }} />
        <div style={{ minWidth: 260 }}>
          <SegmentedControl<'inherited' | 'own'>
            value={p.own ? 'own' : 'inherited'}
            onChange={v => p.onModeChange(v === 'own')}
            columns={2}
            options={[
              { value: 'inherited', label: 'Как у специальности', disabled: locked },
              { value: 'own', label: 'Своё', disabled: locked },
            ]}
          />
        </div>
      </div>

      {p.readOnly && p.readOnlyHint && (
        <div style={{ fontFamily: FONT.sans, fontSize: FS.sm, color: C.textMuted, marginTop: SP.sm }}>{p.readOnlyHint}</div>
      )}
      {p.own && (
        <div data-testid="models-own-hint" style={{ fontFamily: FONT.sans, fontSize: FS.sm, color: C.textMuted, marginTop: SP.sm }}>
          ↳ пустая ячейка по-прежнему берётся у специальности
        </div>
      )}

      <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm, marginTop: SP.md }}>
        {TIER_ORDER.map(t => (
          <div key={t} style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
            <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm }}>
              <span style={rowLabel}>{TIER_TITLE[t]}</span>
              {p.own ? (
                <>
                  <div style={{ flex: 1, minWidth: 0, display: 'flex' }}>{p.renderPicker(t)}</div>
                  {p.cells[t] && (
                    <Button variant="ghostAccent" size="xs" disabled={locked} onClick={() => p.onReset(t)}>Сбросить</Button>
                  )}
                </>
              ) : (
                <div data-testid={`models-inherited-${t}`} aria-disabled="true" style={{
                  flex: 1, minWidth: 0, display: 'flex', alignItems: 'center',
                  padding: `${SP.sm}px ${SP.md}px`, borderRadius: R.md,
                  background: C.bgInset, border: `1px dashed ${C.border}`,
                  fontFamily: FONT.sans, fontSize: FS.base, color: C.textMuted,
                }}>
                  {p.inheritedLabel(t)}
                </div>
              )}
            </div>
            <div style={{ paddingLeft: 72 }}>{p.renderPreview(t)}</div>
          </div>
        ))}
        {p.footer}
      </div>
    </div>
  );
}
