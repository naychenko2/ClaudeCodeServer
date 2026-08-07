import { useEffect, useMemo, useRef, useState } from 'react';
import type { CSSProperties } from 'react';
import { Modal } from '../../components/ui';
import { C, FONT, FS, MODAL_W } from '../../lib/design';
import { useIsMobile } from '../../lib/breakpoints';
import { api } from '../../lib/api';
import { useModels } from '../../lib/models';
import { useProviderData, type TierKey } from '../../lib/modelProvidersShared';
import { consumeOpenRequest } from '../../lib/modelProvidersNav';
import { updateSpecialtySettings } from '../../lib/presets';
import { QuotasTab } from './QuotasTab';
import { SlotsTab } from './SlotsTab';
import { ApplyTab } from './ApplyTab';
import type { SpecialtySettingsLayer, SpecialtySettingsResponse } from '../../types';

// Раздел «Модели и расход» (редизайн v3, макет docs/mockups/models-spend-v3.html):
// одна модалка с тремя вкладками — «Квоты и деньги», «Модели по умолчанию», «Применение».
// Прежние «Использование» и «Поставщики моделей» растворены здесь: квоты — в первой вкладке,
// слоты/исключения/применение — во второй и третьей. Состояние слоёв специальностей и
// контекст пользователя живут в модалке (как в прежней ModelProvidersTabsModal) и раздаются вкладкам.

type TabKey = 'quotas' | 'slots' | 'apply';

export function ModelsSpendModal({ onClose }: { onClose: () => void }) {
  const isMobile = useIsMobile();
  const [tab, setTab] = useState<TabKey>('quotas');

  // Роль и контекст уровня «Модели по умолчанию»: null = общие (админ) или свои (не-админ)
  const [me, setMe] = useState<Awaited<ReturnType<typeof api.auth.me>> | null>(null);
  const isAdmin = me?.role === 'admin';
  const [contextUserId, setContextUserId] = useState<string | null>(null);
  const data = useProviderData(isAdmin, contextUserId);
  const models = useModels();

  useEffect(() => {
    let cancelled = false;
    api.auth.me().then(d => { if (!cancelled) setMe(d); }).catch(() => { if (!cancelled) setMe(null); });
    return () => { cancelled = true; };
  }, []);

  // Диплинк «Собрать цепочку…» (requestNewPreset из панелей выбора модели): открываем
  // раздел прямо на вкладке «Модели по умолчанию», где цепочки правятся в слотах
  useEffect(() => {
    if (consumeOpenRequest()) setTab('slots');
  }, []);

  // Настройки специальностей и пресетов: глобальный + личный слой. Нужны вкладке слотов
  // (правка цепочки пресета, на который ссылается слот) и блоку «Исключения» (матрица).
  const [settings, setSettings] = useState<SpecialtySettingsResponse | null>(null);
  const [settingsError, setSettingsError] = useState<string | null>(null);
  const [savingScope, setSavingScope] = useState<'global' | 'owner' | null>(null);

  useEffect(() => {
    let cancelled = false;
    api.specialties.getSettings()
      .then(s => { if (!cancelled) { setSettings(s); updateSpecialtySettings(s); } })
      .catch(e => { if (!cancelled) setSettingsError(e instanceof Error ? e.message : 'Не удалось загрузить настройки'); });
    return () => { cancelled = true; };
  }, []);

  // Порядковый номер запроса на слой — правки бьют по одному слою пачкой, ответы могут
  // прийти не в том порядке. Без счётчика устаревший ответ перезаписывал бы актуальный.
  const saveSeqRef = useRef<{ global: number; owner: number }>({ global: 0, owner: 0 });
  const settingsRef = useRef<SpecialtySettingsResponse | null>(null);
  useEffect(() => { settingsRef.current = settings; }, [settings]);

  const mergeSavedLayer = (base: SpecialtySettingsResponse, scope: 'global' | 'owner',
    saved: SpecialtySettingsLayer): SpecialtySettingsResponse => {
    const merged = { ...base, [scope]: saved };
    merged.presets = [
      ...merged.owner.presets.map(p => ({ ...p, scope: 'owner' as const })),
      ...merged.global.presets.map(p => ({ ...p, scope: 'global' as const })),
    ];
    return merged;
  };

  // Оптимистичное сохранение слоя: применяем сразу, при ошибке откатываем.
  const handleSaveLayer = (scope: 'global' | 'owner', next: SpecialtySettingsLayer) => {
    const prev = settings;
    const seq = ++saveSeqRef.current[scope];
    setSettings(s => s ? { ...s, [scope]: next } : s);
    setSavingScope(scope);
    setSettingsError(null);
    const commit = (saved: SpecialtySettingsLayer) => {
      if (saveSeqRef.current[scope] !== seq) return;
      setSettings(s => s ? mergeSavedLayer(s, scope, saved) : s);
      const cur = settingsRef.current;
      if (cur) updateSpecialtySettings(mergeSavedLayer(cur, scope, saved));
    };
    const fail = (e: unknown) => {
      if (saveSeqRef.current[scope] !== seq) return;
      setSettings(prev);
      setSettingsError(e instanceof Error ? e.message : 'Не удалось сохранить');
    };
    const settle = () => { if (saveSeqRef.current[scope] === seq) setSavingScope(null); };
    if (scope === 'global') {
      api.specialties.saveGlobalLayer(next).then(res => commit(res.global)).catch(fail).finally(settle);
    } else {
      api.specialties.saveOwnerLayer(next).then(res => commit(res.owner)).catch(fail).finally(settle);
    }
  };

  const tierModels = useMemo<Record<TierKey, string>>(() => ({
    strong: data.effectiveTierModel('strong'),
    medium: data.effectiveTierModel('medium'),
    weak: data.effectiveTierModel('weak'),
  }), [data]);

  const ollamaModel = data.info?.model ?? undefined;

  const tabs: { key: TabKey; label: string }[] = [
    { key: 'quotas', label: 'Квоты и деньги' },
    // На мобиле полное название не влезает в полосу вкладок — короткий вариант из макета
    { key: 'slots', label: isMobile ? 'Модели' : 'Модели по умолчанию' },
    { key: 'apply', label: 'Применение' },
  ];

  const tabBtnStyle = (active: boolean): CSSProperties => ({
    font: 'inherit', fontFamily: FONT.sans, fontSize: FS.base, fontWeight: 600,
    color: active ? C.accent : C.textSecondary, background: 'transparent',
    border: 'none', borderBottom: `2px solid ${active ? C.accent : 'transparent'}`,
    padding: '10px 12px', cursor: 'pointer', whiteSpace: 'nowrap', flexShrink: 0,
  });

  return (
    <Modal title="Модели и расход" width={MODAL_W.wide} onClose={onClose}>
      <div style={{ display: 'flex', flexDirection: 'column', minHeight: 0 }}>
        {/* Полоса вкладок */}
        <div style={{
          display: 'flex', gap: 2, borderBottom: `1px solid ${C.borderLight}`,
          overflowX: 'auto', flexShrink: 0, margin: '0 -4px',
        }}>
          {tabs.map(t => (
            <button key={t.key} type="button" style={tabBtnStyle(tab === t.key)} onClick={() => setTab(t.key)}>
              {t.label}
            </button>
          ))}
        </div>

        {settingsError && tab !== 'quotas' && (
          <div style={{ margin: '10px 0 0', padding: '7px 10px', borderRadius: 8, fontSize: FS.sm,
            color: C.dangerText, background: C.dangerBg, border: `1px solid ${C.dangerBorder}` }}>
            {settingsError}
          </div>
        )}

        {/* Тело активной вкладки */}
        <div style={{ paddingTop: 12 }}>
          {tab === 'quotas' && <QuotasTab onClose={onClose} />}
          {tab === 'slots' && (
            <SlotsTab
              isAdmin={isAdmin}
              data={data}
              contextUserId={contextUserId}
              onContextUserId={setContextUserId}
              settings={settings}
              models={models}
              tierModels={tierModels}
              ollamaModel={ollamaModel}
              savingScope={savingScope}
              onSaveLayer={handleSaveLayer}
              onGoApply={() => setTab('apply')}
            />
          )}
          {tab === 'apply' && (
            <ApplyTab isAdmin={isAdmin} data={data} models={models} tierModels={tierModels} />
          )}
        </div>
      </div>
    </Modal>
  );
}
