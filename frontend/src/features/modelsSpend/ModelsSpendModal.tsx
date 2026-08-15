import { useEffect, useMemo, useRef, useState } from 'react';
import type { CSSProperties } from 'react';
import { Modal } from '../../components/ui';
import { C, FONT, FS, MODAL_W } from '../../lib/design';
import { api } from '../../lib/api';
import { useModels } from '../../lib/models';
import { useProviderData, type TierKey } from '../../lib/modelProvidersShared';
import { consumeOpenRequest, consumeDraftRequest } from '../../lib/modelProvidersNav';
import { updateSpecialtySettings } from '../../lib/presets';
import { useSpecialtyCatalog } from '../../lib/specialties';
import { coverageOf, pickStartScope } from './specialRules/model';
import { QuotasTab } from './QuotasTab';
import { SlotsTab } from './SlotsTab';
import { SpecialRulesTab } from './SpecialRulesTab';
import { ApplyTab } from './ApplyTab';
import { ChainsTab } from './ChainsTab';
import type { SpecialtySettingsLayer, SpecialtySettingsResponse, ResetResult } from '../../types';

// Раздел «Модели и расход» (редизайн v4): одна модалка с пятью вкладками —
// Расход / Модели / Правила / Применение / Цепочки. Названия короткие: полоса вкладок
// обязана влезать в ширину модалки без горизонтального скролла. Первым идёт «Расход»:
// раздел открывают чаще всего ради денег. Решение владельца 14.08.2026: «Особые правила» стали
// самостоятельной вкладкой (переехали из ExceptionsBlock в SlotsTab), отдельный
// третий слой «Пользователю…» — admin-only на «Особых правилах» и «Цепочках».
// Прежние «Использование» и «Поставщики моделей» растворены здесь: квоты — в пятой
// вкладке, слоты/применение — в первых трёх. Состояние слоёв специальностей и
// контекст пользователя живут в модалке и раздаются вкладкам.

type TabKey = 'quotas' | 'slots' | 'specialty' | 'apply' | 'chains';
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

  // Настройки специальностей и пресетов: глобальный + личный слой. Нужны вкладке слотов
  // (правка цепочки пресета, на который ссылается слот) и блоку «Исключения» (матрица).
  const [settings, setSettings] = useState<SpecialtySettingsResponse | null>(null);
  const [settingsError, setSettingsError] = useState<string | null>(null);
  const [savingScope, setSavingScope] = useState<Scope | null>(null);

  useEffect(() => {
    let cancelled = false;
    api.specialties.getSettings()
      .then(s => { if (!cancelled) { setSettings(s); updateSpecialtySettings(s); } })
      .catch(e => { if (!cancelled) setSettingsError(e instanceof Error ? e.message : 'Не удалось загрузить настройки'); });
    return () => { cancelled = true; };
  }, []);

  // Порядковый номер запроса на слой — правки бьют по одному слою пачкой, ответы могут
  // прийти не в том порядке. Без счётчика устаревший ответ перезаписывал бы актуальный.
  const saveSeqRef = useRef<{ global: number; owner: number; user: number }>({ global: 0, owner: 0, user: 0 });
  const settingsRef = useRef<SpecialtySettingsResponse | null>(null);
  useEffect(() => { settingsRef.current = settings; }, [settings]);

  const mergeSavedLayer = (base: SpecialtySettingsResponse, scope: Scope,
    saved: SpecialtySettingsLayer): SpecialtySettingsResponse => {
    const merged = { ...base, [scope]: saved };
    merged.presets = [
      ...(merged.owner?.presets ?? []).map(p => ({ ...p, scope: 'owner' as const })),
      ...(merged.global?.presets ?? []).map(p => ({ ...p, scope: 'global' as const })),
      ...(merged.user?.presets ?? []).map(p => ({ ...p, scope: 'user' as const })),
    ];
    return merged;
  };

  // Оптимистичное сохранение слоя: применяем сразу, при ошибке откатываем. Возвращает
  // промис PUT — вызывающие, которым важен порядок (PresetOptions.savePreset: назначить
  // место можно только ПОСЛЕ того, как пресет реально попал на сервер), на нём ждут.
  const handleSaveLayer = (scope: Scope, next: SpecialtySettingsLayer): Promise<void> => {
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
    const req = scope === 'global'
      ? api.specialties.saveGlobalLayer(next).then(res => commit(res.global))
      : scope === 'owner'
        ? api.specialties.saveOwnerLayer(next).then(res => commit(res.owner))
        : api.specialties.saveUserLayer(contextUserId!, next).then(res => commit(res.user));
    return req.catch(e => { fail(e); throw e; }).finally(settle);
  };

  // Сброс исключений/слотов (серверный reset): busy держится до конца перезагрузки
  // настроек, а не до ответа POST — иначе клик по ячейке в этом окне уйдёт PUT'ом
  // старого слоя и воскресит только что удалённые записи.
  const [resettingScope, setResettingScope] = useState<Scope | null>(null);

  // Перечитать настройки после сброса и обновить модульный стор пресетов (без него
  // подписи в ячейках останутся от старого снимка); invalidateEffectiveLines дёргать
  // отдельно не нужно — updateSpecialtySettings делает это сама.
  const onReloadSettings = (): Promise<void> =>
    api.specialties.getSettings().then(s => { setSettings(s); updateSpecialtySettings(s); });

  const onReset = (scope: Scope, key?: string): Promise<ResetResult> => {
    setResettingScope(scope);
    const userId = scope === 'user' ? (contextUserId ?? undefined) : undefined;
    return api.specialties.reset(scope, key, userId)
      .then(res => onReloadSettings().then(() => res))
      .finally(() => setResettingScope(null));
  };

  const tierModels = useMemo<Record<TierKey, string>>(() => ({
    strong: data.effectiveTierModel('strong'),
    medium: data.effectiveTierModel('medium'),
    weak: data.effectiveTierModel('weak'),
  }), [data]);

  const ollamaModel = data.info?.model ?? undefined;

  // Бейдж вкладки «Особые правила» — ОХВАТ слоя («9 из 14»), а не число заданных полей:
  // при полном покрытии счётчик полей перестаёт нести сигнал (лист текстов, п. 5;
  // решение владельца 14.08.2026). Считается по тому слою, на котором вкладка откроется.
  const specialtyCatalog = useSpecialtyCatalog();
  const specialtyBadge = useMemo(() => {
    if (!settings || !specialtyCatalog) return null;
    const startScope = pickStartScope(settings, specialtyCatalog, isAdmin);
    const { configured, total } = coverageOf(settings[startScope], specialtyCatalog);
    return configured > 0 ? `${configured} из ${total}` : null;
  }, [settings, specialtyCatalog, isAdmin]);

  const tabs: { key: TabKey; label: string; badge?: string | null }[] = [
    { key: 'quotas', label: 'Расход' },
    { key: 'slots', label: 'Модели' },
    // «Правила» — отдельная вкладка (решение владельца 14.08.2026). Видна всем:
    // у не-админа она открывается на слое «Только для меня» — тот же блок был раньше
    // в «Моделях». Admin-only «Пользователю…» живёт уже внутри самой вкладки.
    { key: 'specialty', label: 'Правила', badge: specialtyBadge },
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
              {t.badge && (
                <span style={{
                  marginLeft: 4, fontSize: FS.xs, fontWeight: 700,
                  color: tab === t.key ? C.accent : C.textMuted,
                }}>{t.badge}</span>
              )}
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
              settings={settings}
              models={models}
              tierModels={tierModels}
              ollamaModel={ollamaModel}
              savingScope={savingScope}
              onSaveLayer={handleSaveLayer}
              pendingDraft={pendingDraft}
              onPendingDraftConsumed={() => setPendingDraft(false)}
            />
          )}
          {tab === 'specialty' && (
            <SpecialRulesTab
              isAdmin={isAdmin}
              meUserId={me?.userId ?? null}
              data={data}
              contextUserId={contextUserId}
              onContextUserId={setContextUserId}
              settings={settings}
              models={models}
              tierModels={tierModels}
              ollamaModel={ollamaModel}
              savingScope={savingScope}
              onSaveLayer={handleSaveLayer}
              onReloadSettings={onReloadSettings}
              resettingScope={resettingScope}
              onReset={onReset}
            />
          )}
          {tab === 'apply' && (
            <ApplyTab
              isAdmin={isAdmin}
              data={data}
              models={models}
              tierModels={tierModels}
              settings={settings}
              savingScope={savingScope}
              onSaveLayer={handleSaveLayer}
            />
          )}
          {tab === 'chains' && (
            <ChainsTab
              isAdmin={isAdmin}
              contextUserId={contextUserId}
              settings={settings}
              savingScope={savingScope}
              onSaveLayer={handleSaveLayer}
              onReloadSettings={onReloadSettings}
              models={models}
              tierModels={tierModels}
              ollamaModel={ollamaModel}
            />
          )}
        </div>
      </div>
    </Modal>
  );
}
