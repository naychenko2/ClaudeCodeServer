import { useEffect, useMemo, useRef, useState } from 'react';
import type { CSSProperties } from 'react';
import { Modal } from '../../components/ui';
import { C, FONT, FS, MODAL_W } from '../../lib/design';
import { api } from '../../lib/api';
import { useModels, useProviders } from '../../lib/models';
import { useSpecialtyCatalog } from '../../lib/specialties';
import { updateSpecialtySettings } from '../../lib/presets';
import { consumeOpenRequest, subscribeModelProvidersNav } from '../../lib/modelProvidersNav';
import { useProviderData, buildProviderTiles, type TierKey } from '../../components/modelProvidersShared';
import { ProviderTiles, SlotsSection, ApplySection } from '../../components/ModelProvidersSections';
import { SpecialtiesTab } from './SpecialtiesTab';
import { PresetsTab } from './PresetsTab';
import { ChatPreviewTab } from './ChatPreviewTab';
import type { SpecialtySettingsLayer, SpecialtySettingsResponse } from '../../types';

// Раскладка раздела «Поставщики моделей» (вариант A): одна модалка с горизонтальными
// вкладками. Провайдеры и Применение — прежние уровни легаси-модалки, Специальности/
// Пресеты правил — новые блоки, «В чате» — превью пометок подмены.

type TabKey = 'providers' | 'slots' | 'specialties' | 'presets' | 'apply' | 'chat';

export function ModelProvidersTabsModal({ onClose, isAdmin }: { onClose: () => void; isAdmin: boolean }) {
  // Контекст уровня «Модели по умолчанию»: null = общие (админ) или свои (не-админ)
  const [contextUserId, setContextUserId] = useState<string | null>(null);
  const data = useProviderData(isAdmin, contextUserId);
  const models = useModels();
  const providers = useProviders();
  const catalog = useSpecialtyCatalog();

  // Навигация «Собрать цепочку…»: маунт по запросу — сразу вкладка «Пресеты»
  // (consume в инициализаторе); уже открытая модалка переключается по событию
  const [tab, setTab] = useState<TabKey>(() =>
    consumeOpenRequest() ? 'presets' : (isAdmin ? 'providers' : 'slots'));
  useEffect(() =>
    subscribeModelProvidersNav(() => { if (consumeOpenRequest()) setTab('presets'); }),
  []);

  // Настройки специальностей и пресетов: глобальный + личный слой
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

  // Порядковый номер запроса на слой — правки бьют по одному слою пачкой (каждое поле
  // шлёт свой save), ответы могут прийти не в том порядке. Без счётчика устаревший ответ
  // (уже выправленной пред. попытки) перезаписывал бы актуальный результат и красная
  // плашка «прилипала» бы после того, как причина уже устранена (D3)
  const saveSeqRef = useRef<{ global: number; owner: number }>({ global: 0, owner: 0 });
  // Зеркало settings для обновления общего стора вне апдейтера (см. commit ниже)
  const settingsRef = useRef<SpecialtySettingsResponse | null>(null);
  useEffect(() => { settingsRef.current = settings; }, [settings]);

  // Слияние сохранённого слоя в ответ настроек: PUT отдаёт только слой — объединённый
  // список пресетов пересобираем из слоёв (личные впереди, как EffectivePresetsWithScope)
  const mergeSavedLayer = (base: SpecialtySettingsResponse, scope: 'global' | 'owner',
    saved: SpecialtySettingsLayer): SpecialtySettingsResponse => {
    const merged = { ...base, [scope]: saved };
    merged.presets = [
      ...merged.owner.presets.map(p => ({ ...p, scope: 'owner' as const })),
      ...merged.global.presets.map(p => ({ ...p, scope: 'global' as const })),
    ];
    return merged;
  };

  // Оптимистичное сохранение слоя: применяем сразу, при ошибке откатываем. Ответ бэка
  // несёт нормализованный слой — им и сверяемся после записи.
  const handleSaveLayer = (scope: 'global' | 'owner', next: SpecialtySettingsLayer) => {
    const prev = settings;
    const seq = ++saveSeqRef.current[scope];
    setSettings(s => s ? { ...s, [scope]: next } : s);
    setSavingScope(scope);
    setSettingsError(null);
    const commit = (saved: SpecialtySettingsLayer) => {
      if (saveSeqRef.current[scope] !== seq) return; // устарел — следующая попытка уже в полёте
      // Апдейтер обязан быть ЧИСТЫМ: он исполняется в фазе рендера модалки, и побочный
      // updateSpecialtySettings внутри него дёргал подписчиков стора (PresetOptions в
      // RoutePicker и др.) — «Cannot update a component while rendering a different
      // component» (дефект приёмки). Обновление стора — отдельно, после setState.
      setSettings(s => s ? mergeSavedLayer(s, scope, saved) : s);
      const cur = settingsRef.current;
      if (cur) updateSpecialtySettings(mergeSavedLayer(cur, scope, saved)); // свежие пресеты/ячейки — остальным экранам
    };
    const fail = (e: unknown) => {
      if (saveSeqRef.current[scope] !== seq) return;
      setSettings(prev);
      setSettingsError(e instanceof Error ? e.message : 'Не удалось сохранить');
    };
    const settle = () => { if (saveSeqRef.current[scope] === seq) setSavingScope(null); };
    if (scope === 'global') {
      api.specialties.saveGlobalLayer(next)
        .then(res => commit(res.global)).catch(fail).finally(settle);
    } else {
      api.specialties.saveOwnerLayer(next)
        .then(res => commit(res.owner)).catch(fail).finally(settle);
    }
  };

  const tierModels: Record<TierKey, string> = {
    strong: data.effectiveTierModel('strong'),
    medium: data.effectiveTierModel('medium'),
    weak: data.effectiveTierModel('weak'),
  };
  const ollamaModel = data.info?.model ?? undefined;

  const tiles = useMemo(
    () => buildProviderTiles(providers, models, isAdmin ? data.info : undefined),
    [providers, models, isAdmin, data.info],
  );

  const specCount = catalog ? catalog.filter(e => e.key !== 'none').length : 0;
  const presetCount = settings ? settings.owner.presets.length + settings.global.presets.length : 0;

  const tabs: { key: TabKey; label: string; count?: number }[] = [
    ...(isAdmin ? [{ key: 'providers' as TabKey, label: 'Провайдеры' }] : []),
    { key: 'slots', label: 'Модели по умолчанию' },
    { key: 'specialties', label: 'Специальности', count: specCount || undefined },
    { key: 'presets', label: 'Пресеты', count: presetCount || undefined },
    ...(isAdmin ? [{ key: 'apply' as TabKey, label: 'Применение' }] : []),
    { key: 'chat', label: 'В чате' },
  ];

  const tabBtnStyle = (active: boolean): CSSProperties => ({
    font: 'inherit', fontFamily: FONT.sans, fontSize: FS.base, fontWeight: 600,
    color: active ? C.accent : C.textSecondary, background: 'transparent',
    border: 'none', borderBottom: `2px solid ${active ? C.accent : 'transparent'}`,
    padding: '10px 12px', cursor: 'pointer', whiteSpace: 'nowrap', flexShrink: 0,
  });

  return (
    <Modal
      title="Поставщики моделей"
      subtitle={isAdmin
        ? 'Кто какой моделью работает и что происходит, когда выбранная модель недоступна.'
        : 'Ваши модели, специальности и пресеты. Пустое значение — работает общая настройка.'}
      width={MODAL_W.wide}
      onClose={onClose}
    >
      <div style={{ display: 'flex', flexDirection: 'column', minHeight: 0 }}>
        {/* Полоса вкладок */}
        <div style={{
          display: 'flex', gap: 2, borderBottom: `1px solid ${C.borderLight}`,
          overflowX: 'auto', flexShrink: 0, margin: '0 -4px',
        }}>
          {tabs.map(t => (
            <button key={t.key} type="button" style={tabBtnStyle(tab === t.key)} onClick={() => setTab(t.key)}>
              {t.label}
              {t.count != null && (
                <span style={{
                  fontSize: 10.5, fontWeight: 700, marginLeft: 4,
                  color: tab === t.key ? C.accent : C.textMuted,
                }}>{t.count}</span>
              )}
            </button>
          ))}
        </div>

        {settingsError && (
          <div style={{ margin: '10px 0 0', padding: '7px 10px', borderRadius: 8, fontSize: FS.sm,
            color: C.dangerText, background: C.dangerBg, border: `1px solid ${C.dangerBorder}` }}>
            {settingsError}
          </div>
        )}

        {/* Тело активной вкладки */}
        <div style={{ paddingTop: 12, display: 'flex', flexDirection: 'column', gap: 8 }}>
          {tab === 'providers' && isAdmin && (
            <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
              <ProviderTiles tiles={tiles} />
              <div style={{ fontSize: FS.xs, color: C.textMuted, lineHeight: 1.45, padding: '0 2px' }}>
                Если у провайдера кончился лимит на одной подписке, ответ продолжится
                на другой его подписке — и только потом сменится модель.
              </div>
            </div>
          )}

          {tab === 'slots' && (
            <SlotsSection
              isAdmin={isAdmin}
              data={data}
              contextUserId={contextUserId}
              onContextUserId={setContextUserId}
            />
          )}

          {tab === 'specialties' && (
            catalog === null || settings === null ? (
              <div style={{ color: C.textMuted, fontSize: FS.md, padding: '8px 0' }}>Загрузка…</div>
            ) : (
              <SpecialtiesTab
                catalog={catalog}
                globalLayer={settings.global}
                ownerLayer={settings.owner}
                isAdmin={isAdmin}
                models={models}
                tierModels={tierModels}
                ollamaModel={ollamaModel}
                savingScope={savingScope}
                onSaveLayer={handleSaveLayer}
              />
            )
          )}

          {tab === 'presets' && (
            settings === null ? (
              <div style={{ color: C.textMuted, fontSize: FS.md, padding: '8px 0' }}>Загрузка…</div>
            ) : (
              <PresetsTab
                globalLayer={settings.global}
                ownerLayer={settings.owner}
                isAdmin={isAdmin}
                models={models}
                tierModels={tierModels}
                ollamaModel={ollamaModel}
                savingScope={savingScope}
                onSaveLayer={handleSaveLayer}
                onGoProviders={isAdmin ? () => setTab('providers') : undefined}
              />
            )
          )}

          {tab === 'apply' && isAdmin && <ApplySection isAdmin={isAdmin} data={data} />}

          {tab === 'chat' && <ChatPreviewTab />}
        </div>
      </div>
    </Modal>
  );
}
