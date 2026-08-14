import type { ProviderData, TierKey } from '../../lib/modelProvidersShared';
import type { ModelOption } from '../../lib/models';
import type { ResetResult, SpecialtySettingsLayer, SpecialtySettingsResponse } from '../../types';

// Заглушка под полный макет v4 (см. docs/mockups/model-settings-v4/special-rules-groups.html).
// TODO(Kira v4): три пропорциональные «картины по уровням», группы одинаковых наборов,
// отдельные роли, «Любая специальность», мастер «Добавить правило», «Сейчас пойдёт»,
// admin-only «Пользователю…», бейдж = покрытие «N из M», пустой «Для всех» →
// дефолт открытия на «Только для меня».

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

export function SpecialRulesTab(_props: SpecialRulesTabProps) {
  return null;
}