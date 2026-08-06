import { useEffect, useLayoutEffect, useRef, useState } from 'react';
import { ChevronDown, Link2, RotateCcw, Zap } from 'lucide-react';
import { IconButton, Toggle, Button, Menu, MenuItem } from './ui';
import { ModelPicker } from './ModelPicker';
import { PresetOptions } from './PresetOptions';
import { QuickOptionCard } from './QuickOptionCard';
import { EffectiveLine } from '../features/modelProviders/EffectiveLine';
import { ICON_SIZE, ICON_STROKE } from './ui/icons';
import { api, type ModelTiers } from '../lib/api';
import { C, FONT, FS, R, SP, SHADOW, Z } from '../lib/design';
import { useModels, useProviders, modelLabel, providerLabel, loadModels,
  type ProviderCapabilities, type ModelOption } from '../lib/models';
import {
  chainSummary, findPreset, invalidateEffectiveLines, isPresetRoute, presetIdOf,
  presetRoute, presetValueLabel, resolvePlacePreset, stepsWord, usePresets,
  useSpecialtySettings,
} from '../lib/presets';
import type { OllamaActionInfo, AppSettings } from '../types';
import {
  ROW_CLASS, groupHeaderStyle, levelTitleStyle, BRIEFING_KEY,
  TIERS, TIER_ORDER, tierSubtitle, DOT_COLOR, hasTierTriple, routeTier, routeLabel,
  type PresetKey, type TierKey, type ProviderData, type ProviderTile,
} from './modelProvidersShared';

// Компоненты раздела «Поставщики моделей»: три уровня (провайдеры, слоты, применение)
// вынесены в секции, из которых собирается вкладочная раскладка раздела. Оптимистичные
// правки с откатом, контекст пользователя.
// Константы и хелперы — в modelProvidersShared.ts (требование react-refresh).

// === Уровень 1: плитки провайдеров ===

// Развёрнутая сетка плиток (вкладка «Провайдеры» новой раскладки и раскрытый уровень 1 легаси)
export function ProviderTiles({ tiles }: { tiles: ProviderTile[] }) {
  return (
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
  );
}

// === Уровень 2: Модели по умолчанию (слоты) ===

export function SlotsSection({ isAdmin, data, contextUserId, onContextUserId }: {
  isAdmin: boolean;
  data: ProviderData;
  contextUserId: string | null;
  onContextUserId: (id: string | null) => void;
}) {
  const models = useModels();
  const providers = useProviders();
  const presets = usePresets();
  const { globalSettings, setGlobalSettings, setOwnTiers, setUserTiers,
    selectedTiers, globalTiers } = data;

  const [error, setError] = useState<string | null>(null);
  const [chipBusy, setChipBusy] = useState<string | null>(null);
  const [defaultBusy, setDefaultBusy] = useState<TierKey | null>(null);
  const [editingTier, setEditingTier] = useState<TierKey | null>(null);
  const [contextMenuOpen, setContextMenuOpen] = useState(false);

  function tierModel(t: TierKey): string {
    return selectedTiers?.[t] ?? '';
  }

  function globalTierModel(t: TierKey): string {
    return globalTiers?.[t] ?? '';
  }

  // Сохранение слота. Для личных/пользовательских слотов — через свои эндпоинты,
  // для общих — через /api/settings. После записи перечитываем каталог моделей: /api/models
  // отдаёт резолвнутые назначения мест, по которым пикеры подписывают пункт «По умолчанию».
  function saveTier(t: TierKey, model: string) {
    const prev = selectedTiers;
    const globalPrev = globalSettings;
    setDefaultBusy(t);
    setError(null);
    setEditingTier(null);

    // Оптимистично применяем.
    if (!isAdmin) setOwnTiers(s => s ? { ...s, [t]: model } : s);
    else if (contextUserId) setUserTiers(s => s ? { ...s, [t]: model } : s);
    else setGlobalSettings(s => s ? { ...s, [TIERS[t].field]: model } : s);

    const rollback = () => {
      if (!isAdmin) setOwnTiers(prev);
      else if (contextUserId) setUserTiers(prev);
      else setGlobalSettings(globalPrev);
    };

    const patch: Partial<ModelTiers> = { [t]: model };
    if (!isAdmin) {
      api.meModelTiers.save(patch)
        .then(saved => { setOwnTiers(saved); void loadModels(); invalidateEffectiveLines(); })
        .catch(e => { rollback(); setError(e instanceof Error ? e.message : 'Не удалось сохранить'); })
        .finally(() => setDefaultBusy(null));
    } else if (contextUserId) {
      api.adminUserModelTiers.save(contextUserId, patch)
        .then(saved => { setUserTiers(saved); void loadModels(); invalidateEffectiveLines(); })
        .catch(e => { rollback(); setError(e instanceof Error ? e.message : 'Не удалось сохранить'); })
        .finally(() => setDefaultBusy(null));
    } else {
      api.settings.save({ [TIERS[t].field]: model })
        .then(saved => { setGlobalSettings(saved); void loadModels(); invalidateEffectiveLines(); })
        .catch(e => { rollback(); setError(e instanceof Error ? e.message : 'Не удалось сохранить'); })
        .finally(() => setDefaultBusy(null));
    }
  }

  // Оптимистично проставляем все три слота тройкой провайдера одним PATCH.
  function isChipActive(caps: ProviderCapabilities): boolean {
    const tiers = selectedTiers;
    return tiers != null &&
      caps.tierStrong === tiers.strong &&
      caps.tierMedium === tiers.medium &&
      caps.tierWeak === tiers.weak;
  }

  async function applyProviderChip(caps: ProviderCapabilities) {
    const tiers = selectedTiers;
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
    else if (contextUserId) setUserTiers(patch);
    else setGlobalSettings(s => s ? {
      ...s,
      modelTierStrong: patch.strong,
      modelTierMedium: patch.medium,
      modelTierWeak: patch.weak,
    } : s);

    try {
      let saved: ModelTiers | AppSettings;
      if (!isAdmin) saved = await api.meModelTiers.save(patch);
      else if (contextUserId) saved = await api.adminUserModelTiers.save(contextUserId, patch);
      else saved = await api.settings.save({
        modelTierStrong: patch.strong,
        modelTierMedium: patch.medium,
        modelTierWeak: patch.weak,
      });
      if (!isAdmin) setOwnTiers(saved as ModelTiers);
      else if (contextUserId) setUserTiers(saved as ModelTiers);
      else setGlobalSettings(saved as AppSettings);
      await loadModels();
    } catch (e) {
      if (!isAdmin) setOwnTiers(prev);
      else if (contextUserId) setUserTiers(prev);
      else setGlobalSettings(globalPrev);
      setError(e instanceof Error ? e.message : 'Не удалось применить');
    } finally {
      setChipBusy(null);
    }
  }

  // Подпись слота: у пресета — имя и порядок шагов, у битой ссылки — честная пометка
  // (место ведёт себя как пустое), у пустого личного слота — наследование общего.
  function slotSubtitle(route: string, inheritedModel?: string | null): string {
    const ctx = {
      tierModels: { strong: data.effectiveTierModel('strong'), medium: data.effectiveTierModel('medium'), weak: data.effectiveTierModel('weak') },
      ollamaModel: data.info?.model ?? undefined,
    };
    if (isPresetRoute(route)) {
      const p = findPreset(presets, presetIdOf(route));
      return p ? `${p.name} · ${chainSummary(p, ctx)}` : 'Пресет удалён — работает настройка по умолчанию';
    }
    if (route) return tierSubtitle(route);
    if (inheritedModel) {
      return isPresetRoute(inheritedModel)
        ? `Как у всех · ${presetValueLabel(inheritedModel, presets)}`
        : tierSubtitle('', inheritedModel);
    }
    return tierSubtitle('', inheritedModel);
  }

  // Самоссылка: слот уровня T указывает на пресет, внутри которого есть шаг «уровень T»
  // (уровни в пресете разворачиваются из этих же слотов) — бэкенд пропустит такой шаг
  function selfRefStep(route: string, t: TierKey): boolean {
    const p = findPreset(presets, presetIdOf(route));
    return !!p && p.steps.some(s => routeTier(s) === t);
  }

  // Провайдеры, для которых можно нарисовать чипс быстрого выбора тройки.
  const chipProviders = providers
    .map(p => p.caps)
    .filter(c => c.configured !== false && hasTierTriple(c));

  // Предохранитель: тяжёлая модель в слабом слоте правит фоновой мелочью (теги, заголовки,
  // сводки) — это десятки вызовов в день. Тир угадываем по id, как в ModelIcon.
  const heavyWeak = /opus|fable|ultra|\bmax\b|\bpro\b|reasoner/i.test(data.effectiveTierModel('weak'));

  const selectedUser = data.users.find(u => u.id === contextUserId);
  const contextLabel = contextUserId
    ? (selectedUser?.displayName?.trim() || selectedUser?.username || 'Пользователь')
    : 'Общие (все пользователи)';

  return (
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
                  onClick={() => { setUserTiers(null); onContextUserId(null); setContextMenuOpen(false); setEditingTier(null); }}
                />
                {data.users.map(u => (
                  <MenuItem
                    key={u.id}
                    label={u.displayName?.trim() || u.username}
                    onClick={() => { setUserTiers(null); onContextUserId(u.id); setContextMenuOpen(false); setEditingTier(null); }}
                  />
                ))}
              </Menu>
            )}
          </div>
        </div>
      ) : (
        <div style={levelTitleStyle()}>Модели по умолчанию</div>
      )}

      {isAdmin && contextUserId && (
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
                disabled={busy || !selectedTiers}
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
        const inheritedModel = isAdmin && !contextUserId ? undefined : globalTierModel(t);
        const selfRef = selfRefStep(model, t);
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
                  {slotSubtitle(model, inheritedModel)} · {TIERS[t].hint}
                </div>
              </div>
              {model && (
                <IconButton
                  size="xs"
                  tone="muted"
                  title={contextUserId || !isAdmin ? 'Вернуть общую модель' : 'Очистить слот (решит CLI)'}
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
            {selfRef && (
              <div style={{
                fontSize: FS.xs, color: C.warningText, lineHeight: 1.45, padding: '0 2px',
              }}>
                Шаг «{TIERS[t].title}» внутри пресета указывает обратно на эту же настройку —
                он будет пропущен.
              </div>
            )}
            {editing && (
              <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
                <PresetOptions
                  value={model}
                  onPick={m => saveTier(t, m)}
                  ctx={{
                    tierModels: { strong: data.effectiveTierModel('strong'), medium: data.effectiveTierModel('medium'), weak: data.effectiveTierModel('weak') },
                    ollamaModel: data.info?.model ?? undefined,
                  }}
                />
                <ModelPicker
                  value={isPresetRoute(model) ? '' : model}
                  options={models}
                  onChange={m => saveTier(t, m)}
                  collapsible={false}
                  // Здесь модели слотов и выбираются: пункт «По умолчанию» ссылался бы
                  // сам на себя — сброса слота из UI сознательно нет
                  hideDefault
                />
              </div>
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

      {error && (
        <div style={{ padding: '7px 10px', borderRadius: R.sm, fontSize: FS.sm,
          color: C.dangerText, background: C.dangerBg, border: `1px solid ${C.dangerBorder}` }}>
          {error}
        </div>
      )}
    </div>
  );
}

// === Уровень 3: Применение моделей (таблица «Кто что выполняет») ===

export function ApplySection({ isAdmin, data }: {
  isAdmin: boolean;
  data: ProviderData;
}) {
  const models = useModels();
  const { info, setInfo, globalSettings, setGlobalSettings } = data;
  const [busy, setBusy] = useState<string | null>(null);
  const [presetBusy, setPresetBusy] = useState<PresetKey | null>(null);
  const [briefingBusy, setBriefingBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const briefingOn = globalSettings?.dailyBriefingEnabled ?? true;
  const ollamaOn = info?.enabled ?? false;

  async function toggleBriefing(v: boolean) {
    const prev = globalSettings;
    setBriefingBusy(true);
    setError(null);
    setGlobalSettings(s => s ? { ...s, dailyBriefingEnabled: v } : s);
    try {
      const saved = await api.settings.save({ dailyBriefingEnabled: v });
      setGlobalSettings(saved);
    } catch (e) {
      setGlobalSettings(prev);
      setError(e instanceof Error ? e.message : 'Не удалось сохранить');
    } finally {
      setBriefingBusy(false);
    }
  }

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
    // preset сбрасываем: до ответа сервера пресет покажет разбор route (он ещё сырой)
    patch({ ...a, route, preset: null, routedToOllama: route === 'local', source: 'admin' });
    try {
      const res = await api.localActions.setRoute(a.key, route);
      patch({ ...a, route: res.route, preset: res.preset ?? null, routedToOllama: res.route === 'local',
        source: res.source as OllamaActionInfo['source'] });
      invalidateEffectiveLines();
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
      patch({ ...a, route: res.route, preset: res.preset ?? null, routedToOllama: res.route === 'local',
        source: res.source as OllamaActionInfo['source'] });
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Не удалось сбросить');
    } finally {
      setBusy(null);
    }
  }

  if (!isAdmin) return null;
  if (info === undefined) {
    return <div style={{ color: C.textMuted, fontSize: FS.md, padding: '8px 0' }}>Загрузка…</div>;
  }

  const actions = info.actions ?? [];
  const groups: string[] = [];
  for (const a of actions) if (!groups.includes(a.group)) groups.push(a.group);

  return (
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
          — так помечены места, где работа сложная: локальная модель её не потянет,
          поэтому им подбирается облачная.
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
                tierModels={{ strong: data.effectiveTierModel('strong'), medium: data.effectiveTierModel('medium'), weak: data.effectiveTierModel('weak') }}
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
  );
}

// Компактная карточка-опция вынесена в QuickOptionCard.tsx (ею пользуется и группа
// «Пресеты»); реэкспорт оставлен для существующих импортов (RoutePicker и др.)
export { QuickOptionCard };

const PANEL_W = 320;
const PANEL_MAX_H = 340;

// Одна строка места: название (+ кнопка сброса, если переопределено админом) слева,
// кастомный дропдаун-исполнитель справа — триггер-кнопка + всплывающая панель с карточками
// трёх слотов и «Локальная», плюс полный ModelPicker (карточки моделей с описаниями).
// У агентных мест (чаты, персоны) «Локальная» скрыта — им нужны инструменты CLI.
// У отдельных действий (утренний бриф) перед дропдауном есть тумблер «выполнять ли вообще» —
// он приходит пропсами enabled/onToggleEnabled; остальные строки его не показывают.
export function ActionRow({ action: a, first, busy, ollamaModel, tierModels, models, onPick, onReset,
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
  const presets = usePresets();
  const settingsLoaded = useSpecialtySettings() !== null;
  const overridden = a.source === 'admin';
  const route = a.route ?? '';
  const activeTier = routeTier(route);
  const selectColor = a.routedToOllama ? C.accent : C.textSecondary;
  // «Сильному» действию выбрана локаль — по факту пойдёт фолбэк (локаль пропускается)
  const localOnStrong = a.requiresStrong && route === 'local';
  // Выбранный пресет: с нового бэка — поле preset в ответе места (route при этом
  // развёрнут в первый шаг и ссылки не несёт); на переходный период распознаём
  // ссылку в самом route. name=null в поле — битая ссылка (пресет удалён)
  const { preset, broken: brokenPreset, presetId } =
    resolvePlacePreset(route, a.preset, presets, settingsLoaded);

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

  // Выбор из панели: '' из ModelPicker невозможен (пункт скрыт), приходит слот,
  // модель, локаль или preset-ссылка
  const pick = (v: string) => { onPick(v); setOpen(false); };
  // Для подсветки в ModelPicker: конкретная модель — её id, слот/локаль/пресет — ничего
  const pickerValue = activeTier || route === 'local' || presetId ? '' : route;
  const chainCtx = { tierModels, ollamaModel };
  // Подпись триггера: пресет — имя + длина цепочки, битая ссылка — честная пометка
  const triggerLabel = brokenPreset
    ? 'Пресет удалён — работает настройка по умолчанию'
    : preset
      ? `${preset.name} · ${stepsWord(preset.steps.length)}`
      : routeLabel(route, ollamaModel, tierModels);
  const triggerTitle = brokenPreset
    ? 'Пресет удалён — работает настройка по умолчанию. Нажмите, чтобы выбрать другой'
    : preset
      ? `Сейчас пойдёт: ${chainSummary(preset, chainCtx)}`
      : 'С чего начинать действие; дальше — локальная модель, затем AI';

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
          title={triggerTitle}
          style={{
            display: 'flex', alignItems: 'center', gap: 6, maxWidth: 230,
            fontFamily: FONT.sans, fontSize: FS.xs,
            padding: '4px 8px 4px 9px', borderRadius: R.md,
            cursor: busy ? 'default' : 'pointer', opacity: busy ? 0.5 : 1,
            color: brokenPreset ? C.textMuted : selectColor, background: C.bgWhite,
            border: `1px solid ${open ? C.accent : (a.routedToOllama ? C.accent : C.border)}`,
            outline: 'none', transition: 'border-color 0.15s, box-shadow 0.15s',
            boxShadow: open ? SHADOW.focus : 'none',
          }}
        >
          {preset && (
            <Link2 size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ flexShrink: 0, color: C.textMuted }} />
          )}
          <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', minWidth: 0 }}>
            {triggerLabel}
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
            <EffectiveLine ctx={{ kind: 'action', actionKey: a.key }} />
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
            {/* value — ссылка на пресет (в route он развёрнут в первый шаг),
                иначе подсветка выбранного пресета пропадала бы */}
            <PresetOptions value={presetId ? presetRoute(presetId) : route} onPick={pick} ctx={chainCtx} scope="global" />
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
