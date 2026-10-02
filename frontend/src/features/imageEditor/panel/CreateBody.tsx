// Тело «Настроек» в режиме «Создать» (панель v5, флаг image-panel-v5): списка операций
// нет — операция одна. На виду персонаж и образцы, что нарисовать — пишут в поле ввода;
// исполнитель — свой выбор режима «Создать»; в «Ещё настройках» — пропорции новой картинки.

import { FS, InlineSegmented, SP } from 'aihome_shell/kit';
import type { ImageEditCatalog } from '../api';
import { OUTPAINT_RATIOS, type OutpaintRatio } from '../editorInputs';
import { BlockedNotice } from '../strip/settings/SizeSection';
import { Label, type Launch } from '../strip/settings/primitives';
import { BodyHint, ExecutorField, MoreSettings, SamplesField } from './BodyParts';
import { getCreateRatio, setCreateRatio } from './panelOp';

const MODEL_RATIO = 'model';

export function CreateBody({ projectId, L, catalog, isMobile, onCharacters }: {
  projectId: string; L: Launch; catalog: ImageEditCatalog; isMobile: boolean; onCharacters: () => void;
}) {
  const ratio = getCreateRatio(projectId);
  return (
    <div data-image-body="create" style={{ fontSize: FS.sm }}>
      <SamplesField projectId={projectId} L={L} catalog={catalog} onCharacters={onCharacters} isMobile={isMobile} />
      <BodyHint>Что нарисовать — в поле ввода чата</BodyHint>
      <ExecutorField L={L} catalog={catalog} isMobile={isMobile} />
      <MoreSettings summary={[`пропорции: ${ratio ?? 'как у модели'}`]} isMobile={isMobile}>
        <Label>Пропорции новой картинки</Label>
        <div style={{ display: 'flex', paddingBottom: SP.xs }}>
          <InlineSegmented<OutpaintRatio | typeof MODEL_RATIO> value={ratio ?? MODEL_RATIO} isMobile={isMobile} touchWidth
            options={[{ value: MODEL_RATIO, label: 'Как у модели' }, ...OUTPAINT_RATIOS.map(r => ({ value: r, label: r }))]}
            onChange={v => setCreateRatio(projectId, v === MODEL_RATIO ? null : v)} />
        </div>
      </MoreSettings>
      <BlockedNotice L={L} />
    </div>
  );
}
