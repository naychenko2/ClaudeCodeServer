// Плашка обрезки над холстом (правки без ИИ, ADR-018 §9): общая у окна v2 и попапа v3.

import { Button, SegmentedControl, C, R, SHADOW, SP } from 'aihome_shell/kit';
import { CROP_RATIOS, type CropRatio } from './transforms';

const CROP_LABEL: Record<CropRatio, string> = { free: 'Свободно', '1:1': '1:1', '16:9': '16:9', '9:16': '9:16' };

// Плашка обрезки над холстом: пропорции, «Отмена», «Обрезать»
export function CropBar({ ratio, mobile, onRatio, onCancel, onApply }: {
  ratio: CropRatio;
  mobile: boolean;
  onRatio: (r: CropRatio) => void;
  onCancel: () => void;
  onApply: () => void;
}) {
  return (
    <div data-crop-bar="true" style={{
      display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap', justifyContent: 'center',
      padding: SP.sm, background: C.bgPanel, border: `1px solid ${C.borderLight}`, borderRadius: R.lg, boxShadow: SHADOW.card,
    }}>
      <div style={{ width: mobile ? 280 : 300, maxWidth: '100%' }}>
        <SegmentedControl value={ratio} onChange={onRatio} options={CROP_RATIOS.map(r => ({ value: r, label: CROP_LABEL[r] }))} />
      </div>
      <div style={{ display: 'flex', gap: SP.xs }}>
        <Button size="sm" variant="ghost" onClick={onCancel}>Отмена</Button>
        <Button size="sm" variant="primary" onClick={onApply}>Обрезать</Button>
      </div>
    </div>
  );
}
