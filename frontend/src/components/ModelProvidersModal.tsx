import { useEffect, useLayoutEffect, useRef, useState } from 'react';
import { ChevronDown, RotateCcw, Zap, Boxes } from 'lucide-react';
import { Modal, IconButton, Toggle, Button, Menu, MenuItem } from './ui';
import { ModelPicker } from './ModelPicker';
import { ICON_SIZE, ICON_STROKE } from './ui/icons';
import { api, type ModelTiers } from '../lib/api';
import { C, FONT, FS, R, SP, SHADOW, Z, MODAL_W } from '../lib/design';
import { useModels, useProviders, modelLabel, providerLabel, modelProvider,
  loadModels, type ProviderCapabilities, type ModelOption } from '../lib/models';
import type { OllamaUsageInfo, OllamaActionInfo, AppSettings, UserProfile } from '../types';

interface Props {
  onClose: () => void;
  isAdmin: boolean;
}

// Диалог «Поставщики моделей»: два режима.
//   Обычный пользователь — только уровень 2 «Модели по умолчанию»: личные слоты
//   strong/medium/weak с наследованием глобальных значений. Пустой слот = «как у всех».
//   Администратор — полный диалог (провайдеры, слоты, применение) + у уровня 2
//   компактный селектор контекста: общие настройки или любой пользователь. Уровни 1 и 3
//   всегда глобальные, при выборе конкретного пользователя уровень 3 подписан об этом.

// Ненавязчивая hover-подсветка строки действия — через инжектиый класс (как в IconButton),
// без per-row состояния (строк в списке много, группами по разделам).
const ROW_CLASS = 'cc-mprov-row';
if (typeof document !== 'undefined' && !document.getElementById('cc-mprov-row-style')) {
  const el = document.createElement('style');
  el.id = 'cc-mprov-row-style';
  el.textContent = `.${ROW_CLASS}:hover{background:${C.bgSelected};}`;
  document.head.appendChild(el);
}

const groupHeaderStyle: React.CSSProperties = {
  fontFamily: FONT.sans, fontSize: FS.xs, fontWeight: 700, color: C.textMuted,
  textTransform: 'uppercase', letterSpacing: '0.06em', margin: '14px 2px 6px',
};

// Заголовок уровня (uppercase-метка слева)
function levelTitleStyle(): React.CSSProperties {
  return {
    display: 'flex', alignItems: 'center', gap: 6,
    fontFamily: FONT.sans, fontSize: FS.xs, fontWeight: 700, color: C.textMuted,
    textTransform: 'uppercase', letterSpacing: '0.07em', margin: '0 2px 8px',
  };
}

// Ключ действия утреннего брифа (LocalActionCatalog.DailyBriefing) — у него, помимо выбора
// исполнителя, есть тумблер «присылать ли по расписанию» (AppSettings.DailyBriefingEnabled).
const BRIEFING_KEY = 'daily-briefing';

// Прессеты быстрой настройки мест применения моделей. Старые пресеты (recommended/balanced/free/local)
// убраны из UI — бэк их оставляет, но фронт больше не шлёт.
type PresetKey = 'tiers' | 'tiers-local';

// Слоты моделей инстанса: три именованные модели, на которые ссылаются назначения мест.
// field — имя поля AppSettings (патчится по одному), route — значение назначения.
type TierKey = 'strong' | 'medium' | 'weak';
const TIERS: Record<TierKey, { title: string; hint: string; field: keyof AppSettings; route: string }> = {
  strong: { title: 'Сильная', hint: 'Чаты, сложные задачи', field: 'modelTierStrong', route: 'tier:strong' },
  medium: { title: 'Средняя', hint: 'Сабагенты, тяжёлый фон', field: 'modelTierMedium', route: 'tier:medium' },
  weak: { title: 'Слабая', hint: 'Теги, заголовки, мелочь', field: 'modelTierWeak', route: 'tier:weak' },
};
const TIER_ORDER: TierKey[] = ['strong', 'medium', 'weak'];

// Единая подпись состояния слота. Для личных/пользовательских слотов пустое значение
// показывает наследование от глобального слота: «Как у всех · {modelLabel}». Для глобального
// контекста пустой слот — «не задана — решает CLI».
function tierSubtitle(model: string, inheritedModel?: string | null): string {
  if (model) {
    const provider = providerLabel(modelProvider(model));
    return provider ? `${modelLabel(model)} · ${provider}` : modelLabel(model);
  }
  if (inheritedModel) {
    return `Как у всех · ${modelLabel(inheritedModel)}`;
  }
  if (inheritedModel === undefined) {
    return 'не задана — решает CLI';
  }
  return 'Как у всех · не задана — решает CLI';
}

// Статус адаптера → цвет точки (только дизайн-токены)
const DOT_COLOR: Record<'active' | 'inactive' | 'offline', string> = {
  active: C.success,
  inactive: C.textMuted,
  offline: C.warning,
};

// Провайдер годится для чипса-быстрого выбора: настроен и несёт полную тройку слотов.
function hasTierTriple(caps: ProviderCapabilities): boolean {
  return caps.tierStrong != null && caps.tierMedium != null && caps.tierWeak != null;
}

export function ModelProvidersModal({ onClose, isAdmin }: Props) {
  const [info, setInfo] = useState<OllamaUsageInfo | undefined>(undefined);
  const [busy, setBusy] = useState<string | null>(null);
  const [presetBusy, setPresetBusy] = useState<PresetKey | null>(null);
  const [chipBusy, setChipBusy] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const models = useModels();
  const providers = useProviders();

  // Глобальные настройки инстанса: слоты тиров (уровень 2 в режиме «Общие») +
  // тумблер утреннего брифа (уровень 3). Для обычного пользователя — только источник
  // подписей «как у всех» и для расчёта эффективного слабого слота.
  const [globalSettings, setGlobalSettings] = useState<AppSettings | null>(null);

  // Личные слоты обычного пользователя.
  const [ownTiers, setOwnTiers] = useState<ModelTiers | null>(null);

  // Для админа: список пользователей и выбранный контекст уровня 2.
  const [users, setUsers] = useState<UserProfile[]>([]);
  const [selectedUserId, setSelectedUserId] = useState<string | null>(null);
  const [userTiers, setUserTiers] = useState<ModelTiers | null>(null);
  const [contextMenuOpen, setContextMenuOpen] = useState(false);

  const [briefingBusy, setBriefingBusy] = useState(false);
  const [defaultBusy, setDefaultBusy] = useState<TierKey | null>(null);
  const [editingTier, setEditingTier] = useState<TierKey | null>(null);
  const briefingOn = globalSettings?.dailyBriefingEnabled ?? true;

  // Уровень 1 свёрнут по умолчанию (однострочная сводка), уровень 2 развёрнут.
  const [providersExpanded, setProvidersExpanded] = useState(false);

  // Активные значения слотов для текущего контекста уровня 2.
  function selectedTiers(): ModelTiers | null {
    if (!isAdmin) return ownTiers;
    if (selectedUserId) return userTiers;
    if (!globalSettings) return null;
    return {
      strong: globalSettings.modelTierStrong ?? null,
      medium: globalSettings.modelTierMedium ?? null,
      weak: globalSettings.modelTierWeak ?? null,
    };
  }

  // Глобальные слоты — для подписей наследования и для уровня 3 (он всегда глобальный).
  function globalTiers(): ModelTiers | null {
    if (!globalSettings) return null;
    return {
      strong: globalSettings.modelTierStrong ?? null,
      medium: globalSettings.modelTierMedium ?? null,
      weak: globalSettings.modelTierWeak ?? null,
    };
  }

  function tierModel(t: TierKey): string {
    return selectedTiers()?.[t] ?? '';
  }

  function globalTierModel(t: TierKey): string {
    return globalTiers()?.[t] ?? '';
  }

  // Эффективная модель слота для текущего контекста: личный слот выбранного
  // пользователя, если задан, иначе глобальный. Ей подписываются места уровня 3 —
  // сами назначения общие для всех, а модель за слотом зависит от контекста.
  // `||`, а не `??`: saveTier(t,'') оптимистично кладёт пустую строку (null придёт
  // лишь с ответом сервера), и `??` пропустил бы её как валидную — подпись мигнула
  // бы на «не задана» вместо глобальной. Так же трактует пустую строку tierSubtitle.
  function effectiveTierModel(t: TierKey): string {
    return selectedTiers()?.[t] || globalTiers()?.[t] || '';
  }

  // Оптимистично: применяем сразу, при ошибке возвращаем прежнее значение. Шлём ТОЛЬКО своё
  // поле — PUT /api/settings патчит присланное, поэтому наш (возможно устаревший) снимок
  // не откатывает соседние настройки, изменённые тем временем с другого экрана.
  function toggleBriefing(v: boolean) {
    const prev = globalSettings;
    setBriefingBusy(true);
    setError(null);
    setGlobalSettings(s => s ? { ...s, dailyBriefingEnabled: v } : s);
    api.settings.save({ dailyBriefingEnabled: v })
      .then(saved => setGlobalSettings(saved))
      .catch(e => {
        setGlobalSettings(prev);
        setError(e instanceof Error ? e.message : 'Не удалось сохранить');
      })
      .finally(() => setBriefingBusy(false));
  }

  // Сохранение слота. Для личных/пользовательских слотов — через свои эндпоинты,
  // для общих — через /api/settings. После записи перечитываем каталог моделей: /api/models
  // отдаёт резолвнутые назначения мест, по которым пикеры подписывают пункт «По умолчанию».
  function saveTier(t: TierKey, model: string) {
    const prev = selectedTiers();
    const globalPrev = globalSettings;
    setDefaultBusy(t);
    setError(null);
    setEditingTier(null);

    // Оптимистично применяем.
    if (!isAdmin) setOwnTiers(s => s ? { ...s, [t]: model } : s);
    else if (selectedUserId) setUserTiers(s => s ? { ...s, [t]: model } : s);
    else setGlobalSettings(s => s ? { ...s, [TIERS[t].field]: model } : s);

    const rollback = () => {
      if (!isAdmin) setOwnTiers(prev);
      else if (selectedUserId) setUserTiers(prev);
      else setGlobalSettings(globalPrev);
    };

    const patch: Partial<ModelTiers> = { [t]: model };
    if (!isAdmin) {
      api.meModelTiers.save(patch)
        .then(saved => { setOwnTiers(saved); void loadModels(); })
        .catch(e => { rollback(); setError(e instanceof Error ? e.message : 'Не удалось сохранить'); })
        .finally(() => setDefaultBusy(null));
    } else if (selectedUserId) {
      api.adminUserModelTiers.save(selectedUserId, patch)
        .then(saved => { setUserTiers(saved); void loadModels(); })
        .catch(e => { rollback(); setError(e instanceof Error ? e.message : 'Не удалось сохранить'); })
        .finally(() => setDefaultBusy(null));
    } else {
      api.settings.save({ [TIERS[t].field]: model })
        .then(saved => { setGlobalSettings(saved); void loadModels(); })
        .catch(e => { rollback(); setError(e instanceof Error ? e.message : 'Не удалось сохранить'); })
        .finally(() => setDefaultBusy(null));
    }
  }

  // Оптимистично проставляем все три слота тройкой провайдера одним PATCH.
  function isChipActive(caps: ProviderCapabilities): boolean {
    const tiers = selectedTiers();
    return tiers != null &&
      caps.tierStrong === tiers.strong &&
      caps.tierMedium === tiers.medium &&
      caps.tierWeak === tiers.weak;
  }

  async function applyProviderChip(caps: ProviderCapabilities) {
    const tiers = selectedTiers();
    if (!tiers || chipBusy === caps.provider || isChipActive(caps)) return;
    const prev = tiers;
    const globalPrev = globalSettings;
    setChipBusy(caps.provider);
    setError(null);
    const patch: ModelTiers = {
      strong: caps.tierStrong!,
      medium: caps.tierMedium!,
      weak: caps.tierWeak!,
    };

    if (!isAdmin) setOwnTiers(patch);
    else if (selectedUserId) setUserTiers(patch);
    else setGlobalSettings(s => s ? {
      ...s,
      modelTierStrong: patch.strong,
      modelTierMedium: patch.medium,
      modelTierWeak: patch.weak,
    } : s);

    try {
      let saved: ModelTiers | AppSettings;
      if (!isAdmin) saved = await api.meModelTiers.save(patch);
      else if (selectedUserId) saved = await api.adminUserModelTiers.save(selectedUserId, patch);
      else saved = await api.settings.save({
        modelTierStrong: patch.strong,
        modelTierMedium: patch.medium,
        modelTierWeak: patch.weak,
      });
      if (!isAdmin) setOwnTiers(saved as ModelTiers);
      else if (selectedUserId) setUserTiers(saved as ModelTiers);
      else setGlobalSettings(saved as AppSettings);
      await loadModels();
    } catch (e) {
      if (!isAdmin) setOwnTiers(prev);
      else if (selectedUserId) setUserTiers(prev);
      else setGlobalSettings(globalPrev);
      setError(e instanceof Error ? e.message : 'Не удалось применить');
    } finally {
      setChipBusy(null);
    }
  }

  useEffect(() => {
    let cancelled = false;
    if (isAdmin) {
      api.usage.get()
        .then(d => { if (!cancelled) setInfo(d.ollama ?? { enabled: false, actions: [] }); })
        .catch(() => { if (!cancelled) setInfo({ enabled: false, actions: [] }); });
      api.users.list()
        .then(list => { if (!cancelled) setUsers(list); })
        .catch(() => { /* список пользователей не критичен для диалога */ });
    }
    api.settings.get()
      .then(s => { if (!cancelled) setGlobalSettings(s); })
      .catch(() => { /* слоты останутся пустыми, тумблер — включённым */ });
    if (!isAdmin) {
      api.meModelTiers.get()
        .then(t => { if (!cancelled) setOwnTiers(t); })
        .catch(() => { /* остаёмся на пустых слотах */ });
    }
    return () => { cancelled = true; };
  }, [isAdmin]);

  // Загрузка слотов выбранного пользователя для админа. Сброс слотов и режима
  // редактирования делает обработчик выбора в селекторе (не setState в теле
  // effect — иначе каскад ре-рендеров, на что ругается react-hooks/set-state-in-effect).
  useEffect(() => {
    if (!isAdmin || !selectedUserId) return;
    let cancelled = false;
    api.adminUserModelTiers.get(selectedUserId)
      .then(t => { if (!cancelled) setUserTiers(t); })
      .catch(() => { if (!cancelled) setUserTiers(null); });
    return () => { cancelled = true; };
  }, [isAdmin, selectedUserId]);

  const ollamaOn = info?.enabled ?? false;

  async function applyPreset(key: PresetKey) {
    setPresetBusy(key);
    setError(null);
    try {
      await api.localActions.applyPreset(key);
      const d = await api.usage.get().catch(() => undefined);
      setInfo(d?.ollama ?? { enabled: false, actions: [] });
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Не удалось применить пресет');
    } finally {
      setPresetBusy(null);
    }
  }

  const patch = (a: OllamaActionInfo) =>
    setInfo(prev => prev ? { ...prev, actions: prev.actions.map(x => x.key === a.key ? a : x) } : prev);

  // Оптимистично: сразу применяем, при ошибке возвращаем прежнее значение
  async function pick(a: OllamaActionInfo, route: string) {
    setBusy(a.key);
    setError(null);
    patch({ ...a, route, routedToOllama: route === 'local', source: 'admin' });
    try {
      const res = await api.localActions.setRoute(a.key, route);
      patch({ ...a, route: res.route, routedToOllama: res.route === 'local',
        source: res.source as OllamaActionInfo['source'] });
    } catch (e) {
      patch(a);
      setError(e instanceof Error ? e.message : 'Не удалось сохранить');
    } finally {
      setBusy(null);
    }
  }

  async function reset(a: OllamaActionInfo) {
    setBusy(a.key);
    setError(null);
    try {
      const res = await api.localActions.reset(a.key);
      patch({ ...a, route: res.route, routedToOllama: res.route === 'local',
        source: res.source as OllamaActionInfo['source'] });
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Не удалось сбросить');
    } finally {
      setBusy(null);
    }
  }

  const actions = info?.actions ?? [];
  const groups: string[] = [];
  for (const a of actions) if (!groups.includes(a.group)) groups.push(a.group);

  // === Уровень 1: плитка адаптеров (только admin) ===
  const ollamaTile = info ? {
    name: 'Ollama',
    status: (info.enabled ? 'active' : 'offline') as 'active' | 'inactive' | 'offline',
    statusLabel: info.enabled ? (info.model ? `Локальная · ${info.model}` : 'Активен') : 'Офлайн',
    count: undefined as number | undefined,
  } : null;
  const providerTiles = providers.map(p => ({
    name: p.caps.displayName || providerLabel(p.key),
    status: (p.caps.configured === false ? 'inactive' : 'active') as 'active' | 'inactive' | 'offline',
    statusLabel: p.caps.configured === false ? 'Не настроен' : 'Активен',
    count: models.filter(m => (m.provider ?? modelProvider(m.value)) === p.key).length || undefined,
  }));
  const tiles = [...providerTiles, ...(ollamaTile ? [ollamaTile] : [])];
  const activeNames = tiles.filter(t => t.status === 'active').map(t => t.name);
  const inactiveCount = tiles.filter(t => t.status !== 'active').length;
  const summaryText = activeNames.length
    ? `Активны: ${activeNames.join(', ')}${inactiveCount ? ` · ${inactiveCount} не настроены` : ''}`
    : (inactiveCount ? `${inactiveCount} не настроены` : 'Нет данных');

  // Предохранитель: тяжёлая модель в слабом слоте правит фоновой мелочью (теги, заголовки,
  // сводки) — это десятки вызовов в день. Тир угадываем по id, как в ModelIcon.
  const heavyWeak = /opus|fable|ultra|\bmax\b|\bpro\b|reasoner/i.test(effectiveTierModel('weak'));

  // Провайдеры, для которых можно нарисовать чипс быстрого выбора тройки.
  const chipProviders = providers
    .map(p => p.caps)
    .filter(c => c.configured !== false && hasTierTriple(c));

  const selectedUser = users.find(u => u.id === selectedUserId);
  const contextLabel = selectedUserId
    ? (selectedUser?.displayName?.trim() || selectedUser?.username || 'Пользователь')
    : 'Общие (все пользователи)';

  return (
    <Modal
      title="Поставщики моделей"
      subtitle={isAdmin
        ? 'Модели для чатов и фоновых задач. Настройка общая и личная.'
        : 'Ваши модели для чатов. Пустой слот — используется общая модель.'}
      width={MODAL_W.form}
      onClose={onClose}
    >
      {isAdmin && (
        <>
          {/* === Уровень 1: Провайдеры (свёрнут по умолчанию) === */}
          <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
            <div style={levelTitleStyle()}>Провайдеры</div>
            <button
              type="button"
              onClick={() => setProvidersExpanded(o => !o)}
              style={{
                display: 'flex', alignItems: 'center', gap: 9, width: '100%', textAlign: 'left',
                padding: '10px 12px', borderRadius: R.lg, background: C.bgWhite,
                border: `1px solid ${C.border}`, cursor: 'pointer', transition: 'border-color 0.15s',
              }}
            >
              <Boxes size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} style={{ color: C.textMuted, flexShrink: 0 }} />
              <span style={{ flex: 1, minWidth: 0, fontSize: FS.base, fontWeight: 500, color: C.textHeading,
                overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                {summaryText}
              </span>
              <ChevronDown size={ICON_SIZE.xs} strokeWidth={ICON_STROKE}
                style={{ flexShrink: 0, color: C.textMuted, transform: providersExpanded ? 'rotate(180deg)' : 'none',
                  transition: 'transform 0.15s' }} />
            </button>

            {providersExpanded && (
              <div style={{ display: 'grid', gridTemplateColumns: 'repeat(2, 1fr)', gap: 8 }}>
                {tiles.map((t, i) => (
                  <div key={i} style={{
                    background: C.bgCard, border: `1px solid ${C.border}`, borderRadius: R.lg,
                    padding: '10px 12px', display: 'flex', flexDirection: 'column', gap: 4,
                  }}>
                    <div style={{ fontSize: FS.base, fontWeight: 600, color: C.textHeading }}>{t.name}</div>
                    <div style={{ display: 'flex', alignItems: 'center', gap: 5,
                      fontSize: FS.sm, color: C.textSecondary }}>
                      <span style={{
                        width: 6, height: 6, borderRadius: R.full, flexShrink: 0,
                        background: DOT_COLOR[t.status],
                      }} />
                      <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                        {t.statusLabel}
                      </span>
                      {t.count != null && t.count > 0 && (
                        <span style={{ marginLeft: 'auto', fontSize: FS.xs, color: C.textMuted }}>
                          {t.count} мод.
                        </span>
                      )}
                    </div>
                    {t.status === 'inactive' && (
                      <span title="Ключ провайдера настраивается в appsettings.Local.json на сервере"
                        style={{ fontSize: FS.xs, color: C.textMuted, alignSelf: 'flex-start', marginTop: 1 }}>
                        Настроить →
                      </span>
                    )}
                  </div>
                ))}
              </div>
            )}
          </div>
        </>
      )}

      {/* === Уровень 2: Модели по умолчанию (развёрнуты) === */}
      <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
        {isAdmin ? (
          <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 12 }}>
            <div style={levelTitleStyle()}>Модели по умолчанию</div>
            <div style={{ position: 'relative' }}>
              <Button
                variant="ghost"
                size="sm"
                leftIcon={
                  <ChevronDown size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ color: C.textMuted, flexShrink: 0 }} />
                }
                onClick={() => setContextMenuOpen(true)}
              >
                {contextLabel}
              </Button>
              {contextMenuOpen && (
                <Menu onClose={() => setContextMenuOpen(false)} align="right" top={34} minWidth={200}>
                  <MenuItem
                    label="Общие (все пользователи)"
                    onClick={() => { setUserTiers(null); setSelectedUserId(null); setContextMenuOpen(false); setEditingTier(null); }}
                  />
                  {users.map(u => (
                    <MenuItem
                      key={u.id}
                      label={u.displayName?.trim() || u.username}
                      onClick={() => { setUserTiers(null); setSelectedUserId(u.id); setContextMenuOpen(false); setEditingTier(null); }}
                    />
                  ))}
                </Menu>
              )}
            </div>
          </div>
        ) : (
          <div style={levelTitleStyle()}>Модели по умолчанию</div>
        )}

        {isAdmin && selectedUserId && (
          <div style={{ fontSize: FS.xs, color: C.textMuted, lineHeight: 1.4, padding: '0 2px' }}>
            Назначения мест общие для всех; модели за слотами показаны для выбранного пользователя
          </div>
        )}

        {chipProviders.length > 0 && (
          <div style={{ display: 'flex', flexWrap: 'wrap', gap: 8 }}>
            {chipProviders.map(caps => {
              const active = isChipActive(caps);
              const busy = chipBusy === caps.provider;
              const triple = [caps.tierStrong, caps.tierMedium, caps.tierWeak]
                .filter((x): x is string => x != null)
                .map(modelLabel)
                .join(' / ');
              return (
                <Button
                  key={caps.provider}
                  variant={active ? 'primary' : 'ghost'}
                  size="sm"
                  pill
                  disabled={busy || !selectedTiers()}
                  loading={busy}
                  onClick={() => applyProviderChip(caps)}
                  title={active
                    ? 'Текущая тройка слотов совпадает с этим провайдером'
                    : 'Проставить все три слота моделями этого провайдера'}
                  style={{ whiteSpace: 'nowrap' }}
                >
                  <span>{caps.displayName || providerLabel(caps.provider)}</span>
                  <span style={{ fontSize: FS.xs, fontWeight: 500, opacity: 0.85 }}>{triple}</span>
                </Button>
              );
            })}
          </div>
        )}

        <div style={{ fontSize: FS.xs, color: C.textMuted, lineHeight: 1.4, padding: '0 2px' }}>
          На эти три модели ссылаются назначения мест — меняешь модель слота, меняются
          все места, назначенные на него.
        </div>

        {TIER_ORDER.map(t => {
          const model = tierModel(t);
          const editing = editingTier === t;
          const rowBusy = defaultBusy === t;
          const inheritedModel = isAdmin && !selectedUserId ? undefined : globalTierModel(t);
          return (
            <div key={t} style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
              <div style={{
                display: 'flex', alignItems: 'center', gap: 10, padding: '11px 14px',
                background: C.bgCard, border: `1px solid ${editing ? C.accent : C.border}`,
                borderRadius: R.xl,
              }}>
                <div style={{ flex: 1, minWidth: 0 }}>
                  <div style={{ fontSize: FS.md, fontWeight: 600, color: C.textHeading,
                    overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                    {TIERS[t].title}
                  </div>
                  <div style={{ fontSize: FS.xs, color: C.textMuted, marginTop: 2,
                    overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                    {tierSubtitle(model, inheritedModel)} · {TIERS[t].hint}
                  </div>
                </div>
                {model && (
                  <IconButton
                    size="xs"
                    tone="muted"
                    title={selectedUserId || !isAdmin ? 'Вернуть общую модель' : 'Очистить слот (решит CLI)'}
                    disabled={rowBusy}
                    onClick={() => saveTier(t, '')}
                  >
                    <RotateCcw size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
                  </IconButton>
                )}
                <Button
                  variant="ghost"
                  size="sm"
                  onClick={() => setEditingTier(editing ? null : t)}
                  disabled={rowBusy}
                >
                  {editing ? 'Отмена' : 'Сменить'}
                </Button>
              </div>
              {editing && (
                <ModelPicker
                  value={model}
                  options={models}
                  onChange={m => saveTier(t, m)}
                  collapsible={false}
                  // Здесь модели слотов и выбираются: пункт «По умолчанию» ссылался бы
                  // сам на себя — сброса слота из UI сознательно нет
                  hideDefault
                />
              )}
            </div>
          );
        })}

        {/* Оформление — как у соседней подсказки про выключенную локаль (уровень 3):
            два блока одной роли в одном окне обязаны выглядеть одинаково */}
        {heavyWeak && (
          <div style={{
            padding: '9px 11px', borderRadius: R.md, fontSize: FS.sm,
            lineHeight: 1.5, color: C.textSecondary, background: C.bgInset,
            border: `1px solid ${C.border}`,
          }}>
            В слабый слот выбрана тяжёлая модель — на неё пойдут теги, заголовки чатов
            и сводки, а это десятки вызовов в день. Дешевле поставить сюда лёгкую модель
            или локальную.
          </div>
        )}
      </div>

      {/* === Уровень 3: Применение моделей (только admin) === */}
      {isAdmin && (info === undefined ? (
        <div style={{ color: C.textMuted, fontSize: FS.md, padding: '8px 0' }}>Загрузка…</div>
      ) : (
        <>
          <div style={{ display: 'flex', flexDirection: 'column' }}>
            <div style={levelTitleStyle()}>Применение моделей</div>

            {/* Панель быстрой настройки: автоматически или с локальной моделью */}
            <div style={{
              display: 'flex', flexDirection: 'column', gap: 8,
              background: C.bgCard, border: `1px solid ${C.border}`, borderRadius: R.xl,
              padding: '10px 12px',
            }}>
              <div style={{ display: 'flex', gap: 8 }}>
                <Button
                  variant="primary"
                  size="sm"
                  fullWidth
                  loading={presetBusy === 'tiers'}
                  disabled={presetBusy !== null}
                  onClick={() => applyPreset('tiers')}
                  title="Проставить всем местам ниже слот по сложности функции"
                >
                  {presetBusy === 'tiers' ? 'Применяю…' : 'Назначить модели автоматически'}
                </Button>
                {ollamaOn && (
                  <Button
                    variant="ghostFilled"
                    size="sm"
                    fullWidth
                    loading={presetBusy === 'tiers-local'}
                    disabled={presetBusy !== null}
                    onClick={() => applyPreset('tiers-local')}
                    title="Мелкие задачи — на локальной модели"
                  >
                    {presetBusy === 'tiers-local' ? 'Применяю…' : 'С локальной моделью'}
                  </Button>
                )}
              </div>
              <div style={{ fontSize: FS.xs, color: C.textMuted, lineHeight: 1.4 }}>
                Проставляет всем местам ниже слот по сложности функции. Вторая кнопка видна только
                когда настроена локальная модель.
              </div>
            </div>

            <div style={{ display: 'flex', alignItems: 'center', gap: 6, margin: '10px 2px 2px' }}>
              <Zap size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ color: C.warning, flexShrink: 0 }} />
              <span style={{ fontSize: FS.xs, color: C.textMuted, lineHeight: 1.4 }}>
                — задаче нужна сильная модель, локальная не подойдёт: для неё подбирается AI или облачная.
              </span>
            </div>

            {!info.enabled && (
              <div style={{ padding: '9px 11px', margin: '10px 0 0', borderRadius: R.md, fontSize: FS.sm,
                lineHeight: 1.5, color: C.textSecondary, background: C.bgInset, border: `1px solid ${C.border}` }}>
                Локальная модель не настроена — шаг локали в цепочке пропускается.
              </div>
            )}

            {error && (
              <div style={{ margin: '10px 0 0', padding: '7px 10px', borderRadius: R.sm, fontSize: FS.sm,
                color: C.dangerText, background: C.dangerBg, border: `1px solid ${C.dangerBorder}` }}>
                {error}
              </div>
            )}

            {groups.map(g => (
              <div key={g}>
                <div style={groupHeaderStyle}>{g}</div>
                <div style={{ background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.lg, overflow: 'hidden' }}>
                  {actions.filter(a => a.group === g).map((a, i) => (
                    <ActionRow
                      key={a.key}
                      action={a}
                      first={i === 0}
                      busy={busy === a.key}
                      ollamaModel={info.model ?? undefined}
                      tierModels={{ strong: effectiveTierModel('strong'), medium: effectiveTierModel('medium'), weak: effectiveTierModel('weak') }}
                      models={models}
                      onPick={route => pick(a, route)}
                      onReset={() => reset(a)}
                      enabled={a.key === BRIEFING_KEY ? briefingOn : undefined}
                      onToggleEnabled={a.key === BRIEFING_KEY ? toggleBriefing : undefined}
                      toggleBusy={a.key === BRIEFING_KEY && briefingBusy}
                      toggleTitle="Присылать утренний бриф по расписанию. Собрать вручную можно и при выключенном"
                    />
                  ))}
                </div>
              </div>
            ))}
          </div>
        </>
      ))}
    </Modal>
  );
}

// Компактная карточка-опция «Локальная модель» / «По умолчанию» наверху панели — тот же стиль
// строки-карточки, что и ModelRow в ModelPicker (имя + подпись), но без импорта внутреннего
// компонента (не экспортируется) — минимальное дублирование стиля.
function QuickOptionCard({ title, subtitle, active, onClick }: {
  title: string; subtitle: string; active: boolean; onClick: () => void;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      style={{
        width: '100%', display: 'flex', flexDirection: 'column', gap: 2,
        padding: '8px 10px', borderRadius: R.md, cursor: 'pointer', textAlign: 'left',
        border: `1px solid ${active ? C.accent : C.border}`,
        background: active ? C.accentLight : C.bgWhite,
      }}
    >
      <span style={{ fontSize: FS.md, fontWeight: 600, color: active ? C.textHeading : C.textPrimary, fontFamily: FONT.sans }}>
        {title}
      </span>
      <span style={{ fontSize: FS.xs, color: C.textMuted, lineHeight: 1.35 }}>
        {subtitle}
      </span>
    </button>
  );
}

// Слот назначения ("tier:*", легаси "default"/"claude" ≙ средняя) или null для прочих маршрутов
function routeTier(route: string | null | undefined): TierKey | null {
  const r = route ?? '';
  if (r === 'default' || r === 'claude' || r === 'tier:medium') return 'medium';
  if (r === 'tier:strong') return 'strong';
  if (r === 'tier:weak') return 'weak';
  return null;
}

// Человекочитаемая подпись текущего выбора триггера. У слота показываем и его имя, и модель
// за ним — иначе «Сильная» ничего не говорит о том, что реально поедет. Модель берётся той
// же функцией, что и в строке уровня 2 (tierSubtitle) — формулировка одна.
function routeLabel(route: string | null | undefined, ollamaModel?: string,
  tierModels?: Record<TierKey, string>): string {
  const r = route ?? '';
  if (r === 'local') return `Локальная${ollamaModel ? ` · ${ollamaModel}` : ''}`;
  const tier = routeTier(r);
  if (tier) {
    const m = tierModels?.[tier] ?? '';
    return m ? `${TIERS[tier].title} · ${modelLabel(m)}` : `${TIERS[tier].title} · не задана`;
  }
  return modelLabel(r);
}

const PANEL_W = 320;
const PANEL_MAX_H = 340;

// Одна строка места: название (+ кнопка сброса, если переопределено админом) слева,
// кастомный дропдаун-исполнитель справа — триггер-кнопка + всплывающая панель с карточками
// трёх слотов и «Локальная», плюс полный ModelPicker (карточки моделей с описаниями).
// У агентных мест (чаты, персоны) «Локальная» скрыта — им нужны инструменты CLI.
// У отдельных действий (утренний бриф) перед дропдауном есть тумблер «выполнять ли вообще» —
// он приходит пропсами enabled/onToggleEnabled; остальные строки его не показывают.
function ActionRow({ action: a, first, busy, ollamaModel, tierModels, models, onPick, onReset,
  enabled, onToggleEnabled, toggleBusy, toggleTitle }: {
  action: OllamaActionInfo;
  first: boolean;
  busy: boolean;
  ollamaModel?: string;
  tierModels: Record<TierKey, string>;
  models: ModelOption[];
  onPick: (route: string) => void;
  onReset: () => void;
  enabled?: boolean;
  onToggleEnabled?: (v: boolean) => void;
  toggleBusy?: boolean;
  toggleTitle?: string;
}) {
  const [open, setOpen] = useState(false);
  const [pos, setPos] = useState<{ top: number; left: number; maxHeight: number } | null>(null);
  const rootRef = useRef<HTMLDivElement>(null);
  const triggerRef = useRef<HTMLButtonElement>(null);
  const overridden = a.source === 'admin';
  const route = a.route ?? '';
  const activeTier = routeTier(route);
  const selectColor = a.routedToOllama ? C.accent : C.textSecondary;
  // «Сильному» действию выбрана локаль — по факту пойдёт фолбэк (локаль пропускается)
  const localOnStrong = a.requiresStrong && route === 'local';

  // Клик вне панели / Escape — закрыть
  useEffect(() => {
    if (!open) return;
    const onDown = (e: MouseEvent) => {
      if (rootRef.current && !rootRef.current.contains(e.target as Node)) setOpen(false);
    };
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') setOpen(false); };
    document.addEventListener('mousedown', onDown);
    document.addEventListener('keydown', onKey);
    return () => {
      document.removeEventListener('mousedown', onDown);
      document.removeEventListener('keydown', onKey);
    };
  }, [open]);

  // Позиция панели — от прямоугольника триггера, поверх любых overflow:hidden контейнеров
  // (карточки групп их имеют). Раскрытие вниз, если снизу достаточно места, иначе вверх.
  useLayoutEffect(() => {
    if (!open || !triggerRef.current) return;
    const rect = triggerRef.current.getBoundingClientRect();
    const vw = window.innerWidth, vh = window.innerHeight;
    let left = rect.right - PANEL_W;
    left = Math.max(12, Math.min(left, vw - PANEL_W - 12));
    const spaceBelow = vh - rect.bottom - 12;
    const spaceAbove = rect.top - 12;
    let top: number, maxHeight: number;
    if (spaceBelow >= 220 || spaceBelow >= spaceAbove) {
      top = rect.bottom + 6;
      maxHeight = Math.max(160, Math.min(PANEL_MAX_H, spaceBelow - 6));
    } else {
      maxHeight = Math.max(160, Math.min(PANEL_MAX_H, spaceAbove - 6));
      top = rect.top - 6 - maxHeight;
    }
    setPos({ top, left, maxHeight });
  }, [open]);

  // Выбор из панели: '' из ModelPicker невозможен (пункт скрыт), приходит слот или модель
  const pick = (v: string) => { onPick(v); setOpen(false); };
  // Для подсветки в ModelPicker: конкретная модель — её id, слот/локаль — ничего
  const pickerValue = activeTier || route === 'local' ? '' : route;

  return (
    <div
      className={ROW_CLASS}
      style={{
        display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 10,
        padding: '7px 12px', borderTop: first ? 'none' : `1px solid ${C.borderLight}`,
        transition: 'background 0.12s',
      }}
    >
      <div style={{ display: 'flex', alignItems: 'center', gap: 4, minWidth: 0 }}>
        <span style={{ fontSize: FS.sm, color: C.textSecondary, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
          {a.title}
        </span>
        {a.requiresStrong && (
          <span
            style={{ display: 'inline-flex', flexShrink: 0 }}
            title={localOnStrong
              ? 'Нужна сильная модель — локальная будет пропущена, пойдёт AI'
              : 'Нужна сильная модель — локальная не подойдёт'}
          >
            <Zap size={ICON_SIZE.xs} strokeWidth={ICON_STROKE}
              style={{ color: localOnStrong ? C.dangerText : C.accent }} />
          </span>
        )}
        {overridden && (
          <IconButton
            size="xs"
            tone="muted"
            onClick={onReset}
            disabled={busy}
            title="Переопределено — вернуть значение из конфигурации"
          >
            <RotateCcw size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
          </IconButton>
        )}
      </div>

      <div ref={rootRef} style={{ position: 'relative', flexShrink: 0, display: 'flex', alignItems: 'center', gap: 8 }}>
        {onToggleEnabled && (
          <span style={{ display: 'inline-flex' }} title={toggleTitle}>
            <Toggle
              checked={enabled ?? true}
              onChange={onToggleEnabled}
              disabled={toggleBusy}
              ariaLabel={`${a.title} — выполнять по расписанию`}
              width={34}
              height={20}
            />
          </span>
        )}
        <button
          ref={triggerRef}
          type="button"
          onClick={() => setOpen(o => !o)}
          disabled={busy}
          title="С чего начинать действие; дальше — локальная модель, затем AI"
          style={{
            display: 'flex', alignItems: 'center', gap: 6, maxWidth: 230,
            fontFamily: FONT.sans, fontSize: FS.xs,
            padding: '4px 8px 4px 9px', borderRadius: R.md,
            cursor: busy ? 'default' : 'pointer', opacity: busy ? 0.5 : 1,
            color: selectColor, background: C.bgWhite,
            border: `1px solid ${open ? C.accent : (a.routedToOllama ? C.accent : C.border)}`,
            outline: 'none', transition: 'border-color 0.15s, box-shadow 0.15s',
            boxShadow: open ? SHADOW.focus : 'none',
          }}
        >
          <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', minWidth: 0 }}>
            {routeLabel(a.route, ollamaModel, tierModels)}
          </span>
          <ChevronDown
            size={ICON_SIZE.xs} strokeWidth={ICON_STROKE}
            style={{ flexShrink: 0, color: selectColor, transform: open ? 'rotate(180deg)' : 'none', transition: 'transform 0.15s' }}
          />
        </button>

        {open && pos && (
          <div
            style={{
              position: 'fixed', top: pos.top, left: pos.left,
              width: PANEL_W, maxWidth: 'calc(100vw - 24px)', maxHeight: pos.maxHeight,
              overflowY: 'auto', display: 'flex', flexDirection: 'column', gap: 6,
              background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.xl,
              boxShadow: SHADOW.dropdown, padding: 8, zIndex: Z.dropdown,
            }}
          >
            {/* Три слота сверху — обычный выбор; локаль ниже и только фоновым местам */}
            {TIER_ORDER.map(t => (
              <QuickOptionCard
                key={t}
                title={TIERS[t].title}
                subtitle={tierSubtitle(tierModels[t])}
                active={activeTier === t}
                onClick={() => pick(TIERS[t].route)}
              />
            ))}
            {!a.agentic && (
              <QuickOptionCard
                title="Локальная модель"
                subtitle={ollamaModel ? `Ollama · ${ollamaModel}` : 'не настроена'}
                active={route === 'local'}
                onClick={() => pick('local')}
              />
            )}
            <div style={{ borderTop: `1px solid ${C.borderLight}`, margin: '2px 0' }} />
            <ModelPicker
              value={pickerValue}
              options={models}
              onChange={pick}
              collapsible={false}
              // Агентному месту нужны инструменты CLI — direct:-модели ему не годятся
              includeDirect={!a.agentic}
              // Слоты — отдельные карточки выше; в списке был бы дубль
              hideDefault
            />
          </div>
        )}
      </div>
    </div>
  );
}
