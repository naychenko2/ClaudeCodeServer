import type { ProviderData, TierKey } from '../../lib/modelProvidersShared';
import type { ModelOption } from '../../lib/models';
import type { ResetResult, SpecialtySettingsLayer, SpecialtySettingsResponse } from '../../types';
import { ExceptionsBlock } from './ExceptionsBlock';

// Вкладка «Особые правила для специальностей» (раздел «Модели и расход»).
// До макета v4 здесь лежит ExceptionsBlock в нынешнем виде: рабочая дорога возвращена
// после регрессии (29-строчная заглушка делала вкладку пустой). У не-админа
// ExceptionsBlock сам выбирает слой «Только для меня» — это та же раскладка, что
// была у блока в «Моделях по умолчанию» до переноса. Полный макет v4 (картина по
// уровням + группы) — следующий шаг; до того эта вкладка обязана быть достижимой.

interface SpecialRulesTabProps {
  isAdmin: boolean;
  meUserId: string | null;
  data: ProviderData;
  contextUserId: string | null;
  onContextUserId: (id: string | null) => void;
  settings: SpecialtySettingsResponse | null;
  models: ModelOption[];
  tierModels: Record<TierKey, string>;
  ollamaModel?: string;
  savingScope: 'global' | 'owner' | 'user' | null;
  onSaveLayer: (scope: 'global' | 'owner' | 'user', next: SpecialtySettingsLayer) => Promise<void>;
  onReloadSettings: () => Promise<void>;
  resettingScope: 'global' | 'owner' | 'user' | null;
  onReset: (scope: 'global' | 'owner' | 'user', key?: string) => Promise<ResetResult>;
}

export function SpecialRulesTab(props: SpecialRulesTabProps) {
  const { isAdmin, settings, models, tierModels, ollamaModel, savingScope, onSaveLayer,
    onReloadSettings, resettingScope, onReset } = props;
  return (
    <ExceptionsBlock
      settings={settings}
      isAdmin={isAdmin}
      models={models}
      tierModels={tierModels}
      ollamaModel={ollamaModel}
      savingScope={savingScope}
      onSaveLayer={onSaveLayer}
      onReloadSettings={onReloadSettings}
      resettingScope={resettingScope}
      onReset={onReset}
    />
  );
}
