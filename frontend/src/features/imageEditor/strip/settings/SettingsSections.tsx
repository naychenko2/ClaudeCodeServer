// Тело вкладки «Настройки» панели «Картинки» (ADR-021 §3): сверху операция, у модели «Авто» —
// режим подбора; варианты и цена — в закреплённом низу панели

import { FS } from 'aihome_shell/kit';
import type { ImageEditCatalog } from '../../api';
import type { ImageThread } from '../../thread/threadsApi';
import { CharacterSection } from './CharacterSection';
import { ModeSection, NoSamplesSection, OpSection } from '../../panel/OpSection';
import { noSamplesHint } from '../../panel/panelOp';
import { isPersonalScope } from '../../scope';
import type { Launch } from './primitives';
import { ModelSection, ProviderSection } from './ProviderSection';
import { BlockedNotice, SizeSection } from './SizeSection';

export function SettingsSections({ projectId, L, catalog, thread, onCharacters }: {
  projectId: string; L: Launch; catalog: ImageEditCatalog; thread: ImageThread | null;
  // Показ персонажей — вкладка той же панели
  onCharacters: () => void;
}) {
  // Операция без промпта образцов не берёт — секция приглушена подписью
  const noSamples = noSamplesHint(L.op, !!L.quickAction);
  return (
    <div data-image-settings="" style={{ fontSize: FS.sm }}>
      <OpSection projectId={projectId} L={L} />
      <ProviderSection L={L} catalog={catalog} />
      <ModelSection L={L} catalog={catalog} />
      <ModeSection projectId={projectId} L={L} />
      {noSamples
        ? <NoSamplesSection title={isPersonalScope(projectId) ? 'Образцы' : 'Персонаж и образцы'} hint={noSamples} />
        : <CharacterSection projectId={projectId} L={L} catalog={catalog} onCharacters={onCharacters} />}
      <SizeSection L={L} thread={thread} />
      <BlockedNotice L={L} />
    </div>
  );
}
