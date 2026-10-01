// Секция «Варианты и цена»: счётчик вариантов в пределах модели и цена запуска

import { Minus, Plus } from 'lucide-react';
import { IconButton, C, R, SP } from 'aihome_shell/kit';
import type { ImageEditCatalog } from '../../api';
import { variantsWord } from '../../format';
import { ic, Label, type Launch } from './primitives';

export function CountSection({ L, catalog }: { L: Launch; catalog: ImageEditCatalog }) {
  const count = L.settings.count;
  const maxCount = L.model?.caps?.maxCount ?? catalog.limits.maxCount ?? 4;
  return (
    <>
      <Label>Варианты и цена</Label>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap' }}>
        <span style={{ display: 'inline-flex', alignItems: 'center', border: `1px solid ${C.border}`, borderRadius: R.md, background: C.bgWhite }}>
          <IconButton size="xs" title="Меньше вариантов" ariaLabel="Меньше вариантов" disabled={count <= 1}
            onClick={() => L.setSettings({ count: count - 1 })}>{ic(Minus)}</IconButton>
          <span data-image-count="" style={{ minWidth: 14, textAlign: 'center', fontWeight: 600, color: C.textPrimary }}>{count}</span>
          <IconButton size="xs" title="Больше вариантов" ariaLabel="Больше вариантов" disabled={count >= maxCount}
            onClick={() => L.setSettings({ count: count + 1 })}>{ic(Plus)}</IconButton>
        </span>
        <span data-image-price="" style={{ color: C.textSecondary }}>{variantsWord(count)} · {L.price ?? L.priceLabel}</span>
      </div>
    </>
  );
}
