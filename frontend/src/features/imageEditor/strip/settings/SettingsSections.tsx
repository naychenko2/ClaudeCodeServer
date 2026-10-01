// Тело настроек генерации из секций: его рисуют и карточка над полосой «Картинки», и
// вкладка «Настройки» боковой панели (ADR-021 §3)

import { FS } from 'aihome_shell/kit';
import type { ImageEditCatalog } from '../../api';
import type { ImageThread } from '../../thread/threadsApi';
import { CharacterSection } from './CharacterSection';
import { CountSection } from './CountSection';
import type { Launch } from './primitives';
import { ModelSection, ProviderSection } from './ProviderSection';
import { BlockedNotice, SizeSection } from './SizeSection';

export function SettingsSections({ projectId, L, catalog, isMobile, thread, onCharacterSheet, onCharacters }: {
  projectId: string; L: Launch; catalog: ImageEditCatalog; isMobile: boolean; thread: ImageThread | null;
  onCharacterSheet: () => void;
  // Свой показ персонажей вместо панели рабочей области (вкладка соседней панели)
  onCharacters?: () => void;
}) {
  return (
    <div data-image-settings="" style={{ fontSize: FS.sm }}>
      <ProviderSection L={L} catalog={catalog} />
      <ModelSection L={L} catalog={catalog} />
      <CountSection L={L} catalog={catalog} />
      <CharacterSection projectId={projectId} L={L} catalog={catalog} isMobile={isMobile}
        onCharacterSheet={onCharacterSheet} onCharacters={onCharacters} />
      <SizeSection L={L} thread={thread} />
      <BlockedNotice L={L} />
    </div>
  );
}
