// Шторка «Версии» (спека character-panel-final-spec §8.2): заголовок со счётчиком «N из 5», список версий
// и «Обучить заново». Подтверждение старта (TRAIN_TEXT.confirmConnected) показывает главная кнопка, не шторка.

import { RefreshCw } from 'lucide-react';
import { Button, Modal, ICON_SIZE, ICON_STROKE, MODAL_W, C, FS, SP } from 'aihome_shell/kit';
import type { ImageEditLoras } from '../api';
import { CharacterLoraVersions } from './CharacterLoraVersions';
import type { LoraVersionsHandle } from './useLoraVersions';
import { TRAIN_TEXT, VERSIONS_TEXT } from './loraTrainTexts';

const DEFAULT_LIMIT = 5;

// Счётчик в заголовке: сколько версий занято из допустимых
export const versionsCounter = (used: number, limit: number | undefined) => `${used} из ${limit ?? DEFAULT_LIMIT}`;

export function CharacterVersionsSheet({ loraList, versions, onRestart, onClose }: {
  loraList: ImageEditLoras | null;
  versions: LoraVersionsHandle;
  // «Обучить заново»: подтверждение и запуск — на стороне главной кнопки
  onRestart: () => void;
  onClose: () => void;
}) {
  const data = versions.data;
  const title = (
    <span style={{ display: 'inline-flex', alignItems: 'baseline', gap: SP.sm, flexWrap: 'wrap' }}>
      {VERSIONS_TEXT.title}
      {data && <span style={{ fontSize: FS.sm, fontWeight: 400, color: C.textMuted }}>{versionsCounter(data.used, data.limit)}</span>}
    </span>
  );
  return (
    <Modal title={title} width={MODAL_W.form} onClose={onClose}>
      <div style={{ display: 'flex', flexDirection: 'column', gap: SP.md, minWidth: 0 }}>
        <CharacterLoraVersions loraList={loraList} versions={versions} hideTitle />
        <div>
          <Button variant="ghost" size="sm" onClick={onRestart}>
            <RefreshCw size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />{TRAIN_TEXT.restart}
          </Button>
        </div>
      </div>
    </Modal>
  );
}
