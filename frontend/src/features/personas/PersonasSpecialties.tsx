// Экран «Специальности» — центральная зона раздела «Персоны» в режиме specialties.
// Переехал из вкладки «Правила» модалки «Модели и расход» (24.08.2026): настройка
// ролей живёт рядом с персонами, а модалка осталась про деньги и маршруты.
//
// Сам экран — тонкая обёртка над SpecialRulesTab: всю инициализацию данных
// (useProviderData, useModels, useSaveState, useSpecialtySettings) делаем здесь,
// SpecialRulesTab остаётся переносимым без изменений. Обёртка даёт централизованную
// точку для будущих правок (например, срез «Кто работает по этой роли» в карточках).
//
// Шапку с PillSwitch и подзаголовком рисует PersonasPage — этот компонент несёт
// только контент: ошибки стора + SpecialRulesTab в белой карточке.

import { useCallback, useEffect, useMemo, useState } from 'react';
import { C, R, SHADOW, SP, MODAL_W } from '../../lib/design';
import { api } from '../../lib/api';
import { useModels } from '../../lib/models';
import { useProviderData, type TierKey } from '../../lib/modelProvidersShared';
import { loadUserLayer, reloadPresetSettings, resetLayer, saveLayer, useSaveState } from '../../lib/presets';
import type { LayerReducer } from '../../lib/presets';
import { useMe } from '../../lib/defaultPersona';
import { useIsMobile } from '../../lib/breakpoints';
import type { ResetResult } from '../../types';
import { SpecialRulesTab } from '../specialties/SpecialRulesTab';

type Scope = 'global' | 'owner' | 'user';

export function PersonasSpecialties() {
  const isMobile = useIsMobile();
  const me = useMe();
  const isAdmin = me.role === 'admin';

  // Роль и контекст уровня «Модели по умолчанию»: null = общие (админ) или свои (не-админ)
  const [authMe, setAuthMe] = useState<{ userId: string | null } | null>(null);
  useEffect(() => {
    let cancelled = false;
    api.auth.me().then(d => { if (!cancelled) setAuthMe({ userId: d.userId ?? null }); }).catch(() => { if (!cancelled) setAuthMe({ userId: null }); });
    return () => { cancelled = true; };
  }, []);
  const meUserId = authMe?.userId ?? me.userId ?? null;

  const [contextUserId, setContextUserId] = useState<string | null>(null);
  const data = useProviderData(isAdmin, contextUserId);
  const models = useModels();

  const { savingScope, savingUserId, settingsError, resettingScope } = useSaveState();

  // БЛОКЕР-1: подгружаем чужой user-слой при смене contextUserId. Без этого база для
  // записи в user-слой — пустой шаблон, и PUT затрёт specialties/presets реального
  // пользователя одним новым значением. SpecialRulesTab дополнительно ставит gate
  // hasUserLayer на user-scope и отказывает с сообщением, если слой не дошёл.
  useEffect(() => {
    if (contextUserId) void loadUserLayer(contextUserId);
  }, [contextUserId]);

  // Редьюсерная запись слоя: оборачиваем в стабильный callback, чтобы SpecialRulesTab
  // не перерисовывался на каждом монтировании. userId для user-scope берём из
  // локального contextUserId — вызывающий его явно не передаёт.
  const onSaveLayer = useCallback(
    (scope: Scope, reducer: LayerReducer): Promise<void> =>
      saveLayer(scope, reducer, scope === 'user' ? (contextUserId ?? null) : null),
    [contextUserId],
  );

  const onReset = useCallback(
    (scope: Scope, key?: string): Promise<ResetResult> =>
      resetLayer(scope, key, scope === 'user' ? (contextUserId ?? undefined) : undefined),
    [contextUserId],
  );

  const onReloadSettings = useCallback((): void => { reloadPresetSettings(); }, []);

  const tierModels = useMemo<Record<TierKey, string>>(() => ({
    strong: data.effectiveTierModel('strong'),
    medium: data.effectiveTierModel('medium'),
    weak: data.effectiveTierModel('weak'),
  }), [data]);

  const ollamaModel = data.info?.model ?? undefined;

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

      <SpecialRulesTab
        isAdmin={isAdmin}
        meUserId={meUserId}
        data={data}
        contextUserId={contextUserId}
        onContextUserId={setContextUserId}
        models={models}
        tierModels={tierModels}
        ollamaModel={ollamaModel}
        savingScope={savingScope}
        savingUserId={savingUserId}
        onSaveLayer={onSaveLayer}
        onReloadSettings={onReloadSettings}
        resettingScope={resettingScope}
        onReset={onReset}
      />
    </div>
  );
}