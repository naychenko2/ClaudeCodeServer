// Экран «Специальности» — центральная зона раздела «Персоны» в режиме specialties
// (волна 4 «Персонализация специальностей»). Роутер между тремя экранами:
//
//   - список ролей (нет roleKey):       SpecialtyListView
//   - визитка роли (roleKey):            SpecialtyRoleView
//   - настройка роли (viewMode === 'edit'): SpecialtyEditView
//
// Шапку с PillSwitch и подзаголовком рисует PersonasPage — этот компонент
// несёт только контент: ошибки стора + текущий экран в белой карточке.

import { useCallback, useEffect, useMemo, useState } from 'react';
import { C, R, SHADOW, SP, MODAL_W } from '../../lib/design';
import { api } from '../../lib/api';
import { useProviderData, type TierKey } from '../../lib/modelProvidersShared';
import {
  loadUserLayer, saveLayer, useSpecialtySettings, useSaveState,
} from '../../lib/presets';
import type { LayerReducer } from '../../lib/presets';
import { reloadSpecialties, useSpecialtyCatalog } from '../../lib/specialties';
import { useMe } from '../../lib/defaultPersona';
import { useIsMobile } from '../../lib/breakpoints';
import { usePersonas } from '../../lib/personas';
import type {
  Persona, SpecialtySettingsLayer, SpecialtySettingsResponse,
} from '../../types';
import { SpecialtyListView } from './SpecialtyListView';
import { SpecialtyRoleView } from './SpecialtyRoleView';
import { SpecialtyEditView } from './SpecialtyEditView';
import type { Scope } from './personaSpecialtyShared';

export interface PersonasSpecialtiesProps {
  roleKey?: string | null;
  viewMode?: 'list' | 'role' | 'edit';
  onNavigateList?: () => void;
  onNavigateRole?: (key: string) => void;
  onNavigateEdit?: (key: string) => void;
}

export function PersonasSpecialties(props: PersonasSpecialtiesProps): React.ReactElement {
  const isMobile = useIsMobile();
  const me = useMe();
  const isAdmin = me.role === 'admin';

  const catalog = useSpecialtyCatalog();
  const settingsAll = useSpecialtySettings();

  // Слой выбирается один раз: для не-админа — всегда owner; для админа —
  // стартуем с owner (он живой, на «Для всех» чаще пусто).
  const [scope, setScope] = useState<Scope | null>(null);
  const activeScope: Scope = scope ?? 'owner';

  // Чужой слой: для админа на слое «user» нужен выбор пользователя +
  // отдельный запрос за user-слоем (бэк отдаёт user-слой ВЫЗЫВАЮЩЕГО).
  const [contextUserId] = useState<string | null>(null);
  useEffect(() => {
    if (activeScope === 'user' && contextUserId) void loadUserLayer(contextUserId);
  }, [activeScope, contextUserId]);

  // Полный список персон — единый источник стопок аватаров на owner и
  // среза в визитке. На global/user персон НЕ показываем (T8: за другого
  // пользователя список был бы про чужих).
  const allPersonas = usePersonas();
  const personasForLayer: Persona[] = activeScope === 'owner' ? allPersonas : [];

  // userLayer: для админа на user-слое подгружаем по требованию через
  // прямой api.specialties.getUserLayer и кладём в локальный state. Это
  // обходит хук useUserLayer (он требует синхронного key=userId при маунте,
  // что неудобно для админских чатов с динамической сменой пользователя).
  const [userLayersById, setUserLayersById] = useState<Record<string, SpecialtySettingsLayer>>({});
  useEffect(() => {
    if (activeScope !== 'user' || !contextUserId) return;
    let cancelled = false;
    api.specialties.getUserLayer(contextUserId)
      .then(r => { if (!cancelled) setUserLayersById(prev => ({ ...prev, [contextUserId]: r.user })); })
      .catch(() => { /* слой не дошёл — баннер покажется из settingsError */ });
    return () => { cancelled = true; };
  }, [activeScope, contextUserId]);

  // После записи saveLayer — перечитываем каталог подписей: эффективные
  // имя/описание зависят от Display, который сейчас пришёл.
  const onSaveLayer = useCallback(
    async (s: Scope, reducer: LayerReducer): Promise<void> => {
      const userId = s === 'user' ? (contextUserId ?? null) : null;
      await saveLayer(s, reducer, userId);
      reloadSpecialties();
    },
    [contextUserId],
  );

  // Слой для текущего scope: для owner — settings.owner, для global —
  // settings.global, для user — локальный userLayer.
  const layerSettings: SpecialtySettingsLayer | null = activeScope === 'user'
    ? (contextUserId ? userLayersById[contextUserId] ?? null : null)
    : settingsAll
      ? (activeScope === 'global' ? settingsAll.global : settingsAll.owner)
      : null;

  const data = useProviderData(isAdmin, contextUserId);
  const { settingsError } = useSaveState();
  // tierModels + data используются в следующих волнах (для подписей моделей).
  // eslint-disable-next-line @typescript-eslint/no-unused-vars
  void data;
  // Сейчас вычисления моделей по уровням не нужны — они появятся в этапе
  // «Матрицы моделей на экране роли». Заглушка, чтобы useMemo остался
  // доступным для последующих волн.
  const _tierModels = useMemo<Record<TierKey, string>>(() => ({
    strong: data.effectiveTierModel('strong'),
    medium: data.effectiveTierModel('medium'),
    weak: data.effectiveTierModel('weak'),
  }), [data]);
  // eslint-disable-next-line @typescript-eslint/no-unused-vars
  void _tierModels;

  // === Роутинг viewMode ===
  const viewMode = props.viewMode ?? 'list';
  const roleKey = props.roleKey ?? null;

  const goList = useCallback(() => props.onNavigateList?.(), [props]);
  const goRole = useCallback((key: string) => props.onNavigateRole?.(key), [props]);
  const goEdit = useCallback((key: string) => props.onNavigateEdit?.(key), [props]);

  return (
    <div style={{
      maxWidth: MODAL_W.wide, marginLeft: 'auto', marginRight: 'auto', width: '100%',
      background: C.bgWhite, border: `1px solid ${C.borderLight}`,
      borderRadius: R.xl, padding: isMobile ? SP.md : SP.lg,
      boxShadow: SHADOW.card,
      boxSizing: 'border-box',
    }}>
      {settingsError && (
        <div style={{
          margin: `0 0 ${SP.sm}px`, padding: '7px 10px', borderRadius: 8, fontSize: 12,
          color: C.dangerText, background: C.dangerBg, border: `1px solid ${C.dangerBorder}`,
        }}>{settingsError}</div>
      )}

      {viewMode === 'list' && (
        <SpecialtyListView
          isAdmin={isAdmin}
          layer={activeScope}
          onLayerChange={setScope}
          catalog={catalog}
          layerSettings={layerSettings}
          personas={personasForLayer}
          onOpenRole={(k) => goRole(k)}
        />
      )}

      {viewMode === 'role' && roleKey && (
        <SpecialtyRoleView
          roleKey={roleKey}
          catalog={catalog ?? []}
          layer={activeScope}
          layerSettings={layerSettings}
          userLayer={activeScope === 'user' ? layerSettings : null}
          personas={personasForLayer.filter(p => p.specialty === roleKey)}
          onBack={goList}
          onEdit={() => goEdit(roleKey)}
        />
      )}

      {viewMode === 'edit' && roleKey && (
        <SpecialtyEditView
          roleKey={roleKey}
          catalog={catalog ?? []}
          layer={activeScope}
          layerSettings={layerSettings}
          userLayer={activeScope === 'user' ? layerSettings : null}
          personas={personasForLayer.filter(p => p.specialty === roleKey)}
          contextUserId={contextUserId}
          onBack={() => goRole(roleKey)}
          onSave={(reducer) => onSaveLayer(activeScope, reducer)}
        />
      )}
    </div>
  );
}

// SpecialtySettingsResponse — локальный тип для компилятора. Полный набор
// полей (maxSubstitutions, presets, user, userId) не используется здесь —
// только owner/global.
export type { SpecialtySettingsResponse };
