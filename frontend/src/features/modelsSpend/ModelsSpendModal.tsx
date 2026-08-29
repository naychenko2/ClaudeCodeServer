import { useCallback, useEffect, useMemo, useState } from 'react';
import type { CSSProperties } from 'react';
import { Modal } from '../../components/ui';
import { C, FONT, FS, MODAL_W } from '../../lib/design';
import { api } from '../../lib/api';
import { useModels } from '../../lib/models';
import { useProviderData, type TierKey } from '../../lib/modelProvidersShared';
import { consumeOpenRequest, consumeDraftRequest } from '../../lib/modelProvidersNav';
import { reloadPresetSettings, saveLayer, useSaveState } from '../../lib/presets';
import type { LayerReducer } from '../../lib/presets';
import { QuotasTab } from './QuotasTab';
import { SlotsTab } from './SlotsTab';
import { ApplyTab } from './ApplyTab';
import { ChainsTab } from './ChainsTab';

// Раздел «Модели и расход» (редизайн v4): одна модалка с четырьмя вкладками —
// Расход / Модели / Применение / Цепочки. Названия короткие: полоса вкладок
// обязана влезать в ширину модалки без горизонтального скролла. Первым идёт «Расход»:
// раздел открывают чаще всего ради денег. Решение владельца 24.08.2026: «Особые
// правила» переехали из этой модалки в раздел «Персоны» (вкладка «Специальности»
// центральной зоны) — модалка осталась про деньги и маршруты. Третий слой
// «Пользователю…» — admin-only на «Цепочках». Прежние «Использование» и «Поставщики
// моделей» растворены здесь: квоты — в последней вкладке, слоты/применение — в
// первых трёх. Локальное состояние модалки — только вкладка и контекст
// пользователя; слои настроек живут в модульном сторе presets.ts
// (useSpecialtySettings + useSaveState), write-стор ключует scope+userId.

type TabKey = 'quotas' | 'slots' | 'apply' | 'chains';
type Scope = 'global' | 'owner' | 'user';

export function ModelsSpendModal({ onClose }: { onClose: () => void }) {
  // Стартовая вкладка — «Расход»: с ним приходят чаще, чем с настройкой моделей.
  // Диплинки (эффект ниже) перекрывают этот дефолт после маунта.
  const [tab, setTab] = useState<TabKey>('quotas');

  // Роль и контекст уровня «Модели по умолчанию»: null = общие (админ) или свои (не-админ)
  const [me, setMe] = useState<Awaited<ReturnType<typeof api.auth.me>> | null>(null);
  const isAdmin = me?.role === 'admin';
  const [contextUserId, setContextUserId] = useState<string | null>(null);
  const data = useProviderData(isAdmin, contextUserId);
  const models = useModels();

  // Флаг «надо начать черновик новой цепочки» (A2): requestNewPreset() из панели выбора
  // модели — переключаемся на вкладку слотов и просим SlotsTab раскрыть первую карточку.
  const [pendingDraft, setPendingDraft] = useState(false);

  useEffect(() => {
    let cancelled = false;
    api.auth.me().then(d => { if (!cancelled) setMe(d); }).catch(() => { if (!cancelled) setMe(null); });
    return () => { cancelled = true; };
  }, []);

  // Диплинк «Собрать цепочку…» (requestNewPreset из панелей выбора модели):
  // открываем раздел прямо на вкладке «Модели» (перекрывая стартовый «Расход»)
  // и просим начать черновик.
  useEffect(() => {
    if (consumeOpenRequest()) setTab('slots');
    if (consumeDraftRequest()) { setTab('slots'); setPendingDraft(true); }
  }, []);

  // Слои настроек специальностей и пресетов — из модульного стора presets.ts.
  // Снимок settings вкладки читают сами (useSpecialtySettings в точках записи —
  // структурный запрет, чтобы ни одна точка записи не получала слой снаружи).
  // saving/resetting/error — write-стор: флаги операций и баннер ошибки модалки.
  const { savingScope, savingUserId, settingsError } = useSaveState();

  // Запись слоя: редьюсерная семантика (стор сам считает next из текущего снимка).
  // Обёртка стабильна через useCallback, чтобы вкладки не перерисовывались на каждом
  // маунте модалки. ADR-012 снял owner/user-слои: userId больше не нужен, запись всегда
  // идёт в общий слой (PUT /specialties/settings/global, admin-only).
  const onSaveLayer = useCallback(
    (_scope: Scope, reducer: LayerReducer): Promise<void> =>
      saveLayer('global', reducer, null),
    [],
  );

  const onReloadSettings = useCallback((): void => { reloadPresetSettings(); }, []);

  const tierModels = useMemo<Record<TierKey, string>>(() => ({
    strong: data.effectiveTierModel('strong'),
    medium: data.effectiveTierModel('medium'),
    weak: data.effectiveTierModel('weak'),
  }), [data]);

  const ollamaModel = data.info?.model ?? undefined;
  const ollamaProvider = data.info?.provider;

  const tabs: { key: TabKey; label: string }[] = [
    { key: 'quotas', label: 'Расход' },
    { key: 'slots', label: 'Модели' },
    { key: 'apply', label: 'Применение' },
    { key: 'chains', label: 'Цепочки' },
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
              meUserId={me?.userId ?? null}
              data={data}
              contextUserId={contextUserId}
              onContextUserId={setContextUserId}
              models={models}
              tierModels={tierModels}
              ollamaModel={ollamaModel}
              ollamaProvider={ollamaProvider}
              savingScope={savingScope}
              savingUserId={savingUserId}
              onSaveLayer={onSaveLayer}
              pendingDraft={pendingDraft}
              onPendingDraftConsumed={() => setPendingDraft(false)}
            />
          )}
          {tab === 'apply' && (
            <ApplyTab
              isAdmin={isAdmin}
              data={data}
              models={models}
              tierModels={tierModels}
              savingScope={savingScope}
              onSaveLayer={onSaveLayer}
            />
          )}
          {tab === 'chains' && (
            <ChainsTab
              isAdmin={isAdmin}
              contextUserId={contextUserId}
              savingScope={savingScope}
              onSaveLayer={onSaveLayer}
              onReloadSettings={onReloadSettings}
              models={models}
              tierModels={tierModels}
              ollamaModel={ollamaModel}
              ollamaProvider={ollamaProvider}
            />
          )}
        </div>
      </div>
    </Modal>
  );
}
