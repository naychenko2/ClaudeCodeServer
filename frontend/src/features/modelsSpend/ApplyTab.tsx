import { useState } from 'react';
import { RotateCcw, Zap } from 'lucide-react';
import { IconButton, Toggle } from '../../components/ui';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { routeLabel, type ProviderData, type TierKey } from '../../lib/modelProvidersShared';
import { RoutePicker } from '../../components/RoutePicker';
import { EffectiveLine } from '../../components/EffectiveLine';
import { invalidateEffectiveLines, resolvePlacePreset, stepsWord, usePresets, useSpecialtySettings } from '../../lib/presets';
import type { LayerReducer } from '../../lib/presets';
import { C, FS, R } from '../../lib/design';
import { localEngineLabel } from '../../lib/localEngine';
import { ImageGenSection } from './ImageGenSection';
import { api } from '../../lib/api';
import { loadModels, type ModelOption } from '../../lib/models';
import type { AppSettings, OllamaActionInfo } from '../../types';

// Вкладка «Применение» (макет models-spend-v3.html §3): стратегия («Автоматически» /
// «Бесплатно · локальная») + таблица «Кто что выполняет» — группы мест, выбор маршрута
// (слот/модель/локаль/пресет) в каждой строке, тумблер для утреннего брифа.
// Переписанный ApplySection/ActionRow из прежнего ModelProvidersSections.tsx — теперь на едином RoutePicker.

const BRIEFING_KEY = 'daily-briefing';

interface ApplyTabProps {
  isAdmin: boolean;
  data: ProviderData;
  models: ModelOption[];
  tierModels: Record<TierKey, string>;
  savingScope: 'global' | 'owner' | 'user' | null;
  onSaveLayer: (scope: 'global' | 'owner' | 'user', reducer: LayerReducer,
    userId?: string | null) => Promise<void>;
}

export function ApplyTab({ isAdmin, data, models, tierModels, savingScope, onSaveLayer }: ApplyTabProps) {
  const { info, setInfo, globalSettings, setGlobalSettings } = data;
  const [busy, setBusy] = useState<string | null>(null);
  const [presetBusy, setPresetBusy] = useState<'tiers' | 'tiers-local' | null>(null);
  const [briefingBusy, setBriefingBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const briefingOn = globalSettings?.dailyBriefingEnabled ?? true;
  const ollamaOn = info?.enabled ?? false;

  if (!isAdmin) {
    // Не-админу назначение мест недоступно, но чем рисуются иконки и аватары —
    // показываем: диалоги генерации подписывают провайдера, и вопрос «кто рисует» общий.
    return <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
      <div style={{ fontSize: FS.sm, color: C.textMuted, padding: '8px 0' }}>
        Назначение мест настраивает администратор. Ваши уровни — на вкладке «Модели по умолчанию».
      </div>
      <ImageGenSection isAdmin={false} titleStyle={lvlStyle} />
    </div>;
  }
  if (info === undefined) {
    return <div style={{ color: C.textMuted, fontSize: FS.md, padding: '8px 0' }}>Загрузка…</div>;
  }

  async function toggleBriefing(v: boolean) {
    const prev = globalSettings;
    setBriefingBusy(true); setError(null);
    setGlobalSettings(s => s ? { ...s, dailyBriefingEnabled: v } : s);
    try { const saved = await api.settings.save({ dailyBriefingEnabled: v } as Partial<AppSettings>); setGlobalSettings(saved); }
    catch (e) { setGlobalSettings(prev); setError(e instanceof Error ? e.message : 'Не удалось сохранить'); }
    finally { setBriefingBusy(false); }
  }

  async function applyPreset(key: 'tiers' | 'tiers-local') {
    setPresetBusy(key); setError(null);
    try {
      await api.localActions.applyPreset(key);
      const d = await api.usage.get().catch(() => undefined);
      setInfo(d?.ollama ?? { enabled: false, actions: [] });
      // Назначения мест изменились — строки «Сейчас пойдёт» нужно пересчитать свежим
      // серверным резолвом, иначе они покажут старую модель до переоткрытия модалки
      invalidateEffectiveLines();
    } catch (e) { setError(e instanceof Error ? e.message : 'Не удалось применить цепочку'); }
    finally { setPresetBusy(null); }
  }

  const patch = (a: OllamaActionInfo) =>
    setInfo(prev => prev ? { ...prev, actions: prev.actions.map(x => x.key === a.key ? a : x) } : prev);

  async function pick(a: OllamaActionInfo, route: string) {
    setBusy(a.key); setError(null);
    patch({ ...a, route, preset: null, routedToOllama: route === 'local', source: 'admin' });
    try {
      const res = await api.localActions.setRoute(a.key, route);
      patch({ ...a, route: res.route, preset: res.preset ?? null, routedToOllama: res.route === 'local',
        source: res.source as OllamaActionInfo['source'] });
      void loadModels();
      // Кэш effective-line ключуется по actionKey и сам по себе не видит смену маршрута —
      // сбрасываем, чтобы подсказка перерисовалась сразу, а не после переоткрытия модалки
      invalidateEffectiveLines();
    } catch (e) { patch(a); setError(e instanceof Error ? e.message : 'Не удалось сохранить'); }
    finally { setBusy(null); }
  }

  async function reset(a: OllamaActionInfo) {
    setBusy(a.key); setError(null);
    try {
      const res = await api.localActions.reset(a.key);
      patch({ ...a, route: res.route, preset: res.preset ?? null, routedToOllama: res.route === 'local',
        source: res.source as OllamaActionInfo['source'] });
      invalidateEffectiveLines();
    } catch (e) { setError(e instanceof Error ? e.message : 'Не удалось сбросить'); }
    finally { setBusy(null); }
  }

  const actions = info.actions ?? [];
  const groups: string[] = [];
  for (const a of actions) if (!groups.includes(a.group)) groups.push(a.group);

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
      {/* Стратегия */}
      <div style={lvlStyle}>Стратегия</div>
      <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 8 }}>
        <StrategyCard
          active={!ollamaOn} disabled={presetBusy !== null} loading={presetBusy === 'tiers'}
          title="Автоматически"
          desc="Каждому месту — уровень по сложности. Быстрый старт, баланс цены и качества."
          onClick={() => applyPreset('tiers')}
        />
        <StrategyCard
          active={ollamaOn} disabled={presetBusy !== null || !info.enabled} loading={presetBusy === 'tiers-local'}
          title="Бесплатно · с локальной"
          desc={info.enabled
            ? `Мелкие задачи — на локальной модели ${localEngineLabel(info.provider)}, сложным — облачная по уровню.`
            : `Пометьте провайдера «бесплатным» в конфиге или настройте ${localEngineLabel(info.provider)} — и стратегия появится.`}
          onClick={() => applyPreset('tiers-local')}
        />
      </div>
      <div style={{ fontSize: FS.xs, color: C.textMuted, lineHeight: 1.4, padding: '0 2px' }}>
        Стратегия применяется разом ко всем местам ниже. {ollamaOn ? 'Локальная модель включена.' : 'Локальная модель не настроена.'}
      </div>

      {/* Картинки — отдельная настройка: генератор и модель для иконок и аватаров */}
      <ImageGenSection isAdmin titleStyle={lvlStyle} />

      {/* Группы мест */}
      {groups.map(g => (
        <div key={g}>
          <div style={lvlStyle}>{g}</div>
          <div style={{ background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.lg, overflow: 'hidden' }}>
            {actions.filter(a => a.group === g).map((a, i) => (
              <ActionRow
                key={a.key}
                action={a}
                first={i === 0}
                busy={busy === a.key}
                tierModels={tierModels}
                ollamaModel={info.model ?? undefined}
                ollamaProvider={info.provider}
                models={models}
                savingScope={savingScope}
                onSaveLayer={onSaveLayer}
                onPick={route => pick(a, route)}
                onReset={() => reset(a)}
                enabled={a.key === BRIEFING_KEY ? briefingOn : undefined}
                onToggleEnabled={a.key === BRIEFING_KEY ? toggleBriefing : undefined}
                toggleBusy={a.key === BRIEFING_KEY && briefingBusy}
              />
            ))}
          </div>
        </div>
      ))}

      {!info.enabled && (
        <div style={{ padding: '9px 11px', borderRadius: R.md, fontSize: FS.sm, lineHeight: 1.5,
          color: C.textSecondary, background: C.bgInset, border: `1px solid ${C.border}` }}>
          Локальная модель не настроена — шаг локали в цепочке пропускается.
        </div>
      )}
      {error && (
        <div style={{ padding: '7px 10px', borderRadius: R.sm, fontSize: FS.sm,
          color: C.dangerText, background: C.dangerBg, border: `1px solid ${C.dangerBorder}` }}>{error}</div>
      )}
    </div>
  );
}

const lvlStyle = {
  fontFamily: "'Hanken Grotesk', sans-serif", fontSize: FS.xs, fontWeight: 700, color: C.textMuted,
  textTransform: 'uppercase', letterSpacing: '0.07em', margin: '12px 2px 6px',
} as const;

function StrategyCard({ active, disabled, loading, title, desc, onClick }: {
  active: boolean; disabled: boolean; loading: boolean; title: string; desc: string; onClick: () => void;
}) {
  return (
    <button type="button" onClick={onClick} disabled={disabled || loading}
      style={{
        textAlign: 'left', font: 'inherit', cursor: disabled ? 'default' : 'pointer',
        background: C.bgWhite, border: `1px solid ${active ? C.accent : C.border}`, borderRadius: R.xl,
        padding: '12px 13px', opacity: disabled && !loading ? 0.55 : 1,
        boxShadow: active ? 'var(--shadow-focus)' : 'none',
      }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
        <span style={{
          width: 14, height: 14, borderRadius: '50%', border: `2px solid ${active ? C.accent : C.dashed}`,
          flexShrink: 0, background: active ? C.accent : 'transparent',
          boxShadow: active ? `inset 0 0 0 2px ${C.bgWhite}` : 'none',
        }} />
        <span style={{ fontSize: FS.base, fontWeight: 700, color: C.textHeading }}>{loading ? 'Применяю…' : title}</span>
      </div>
      <div style={{ fontSize: FS.xs, color: C.textMuted, lineHeight: 1.45, marginTop: 6 }}>{desc}</div>
    </button>
  );
}

// === Строка места каталога ===
function ActionRow({ action: a, first, busy, tierModels, ollamaModel, ollamaProvider, models, savingScope,
  onSaveLayer, onPick, onReset, enabled, onToggleEnabled, toggleBusy }: {
  action: OllamaActionInfo;
  first: boolean;
  busy: boolean;
  tierModels: Record<TierKey, string>;
  ollamaModel?: string;
  ollamaProvider?: string;
  models: ModelOption[];
  savingScope: 'global' | 'owner' | 'user' | null;
  // Контракт редьюсерный (см. presets.saveLayer). ActionRow напрямую не пишет — он
  // только прокидывает onSaveLayer в PresetCreationCtx (RoutePicker/PresetOptions).
  onSaveLayer: (scope: 'global' | 'owner' | 'user', reducer: LayerReducer,
    userId?: string | null) => Promise<void>;
  onPick: (route: string) => void;
  onReset: () => void;
  enabled?: boolean;
  onToggleEnabled?: (v: boolean) => void;
  toggleBusy?: boolean;
}) {
  // Снимок слоя берём сами из стора — снаружи не получаем (структурный запрет).
  const settings = useSpecialtySettings();
  const presets = usePresets();
  const settingsLoaded = settings !== null;
  const overridden = a.source === 'admin';
  const route = a.route ?? '';
  const localOnStrong = !!a.requiresStrong && route === 'local';
  const { preset, broken: brokenPreset } = resolvePlacePreset(route, a.preset, presets, settingsLoaded);
  // Места каталога — общие для всех, поэтому личный пресет сюда не подставить
  // (бэк 400 по global-слою): валидация ручного ввода тоже проверяет только общие.
  const globalPresetIds = presets.filter(p => p.scope === 'global').map(p => p.id);

  const triggerLabel = brokenPreset
    ? 'Цепочка удалена — по умолчанию'
    : preset ? `${preset.name} · ${stepsWord(preset.steps.length)}` : routeLabel(route, ollamaModel, tierModels);

  return (
    <div className="cc-mprov-row" style={{
      display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 10,
      padding: '8px 12px', borderTop: first ? 'none' : `1px solid ${C.borderLight}`, transition: 'background 0.12s',
    }}>
      <div style={{ display: 'flex', flexDirection: 'column', gap: 3, minWidth: 0, flex: 1 }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 4, minWidth: 0 }}>
          <span style={{ fontSize: FS.base, fontWeight: 600, color: C.textHeading,
            overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{a.title}</span>
          {a.requiresStrong && (
            <span title={localOnStrong ? 'Нужна сильная модель — локальная будет пропущена' : 'Нужна сильная модель — локаль не подойдёт'}>
              <Zap size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ color: localOnStrong ? C.dangerText : C.accent }} />
            </span>
          )}
          {overridden && (
            <IconButton size="xs" tone="muted" onClick={onReset} disabled={busy} title="Переопределено — вернуть значение из конфигурации">
              <RotateCcw size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
            </IconButton>
          )}
        </div>
        {/* «Сейчас пойдёт» — серверный резолв места */}
        <EffectiveLine ctx={{ kind: 'action', actionKey: a.key }} />
      </div>

      <div style={{ display: 'flex', alignItems: 'center', gap: 8, flexShrink: 0 }}>
        {onToggleEnabled && (
          <span title="Присылать утренний бриф по расписанию. Собрать вручную можно и при выключенном">
            <Toggle checked={enabled ?? true} onChange={onToggleEnabled} disabled={toggleBusy}
              ariaLabel={`${a.title} — выполнять по расписанию`} width={34} height={20} />
          </span>
        )}
        <RoutePicker
          route={route}
          label={triggerLabel}
          models={models}
          tierModels={tierModels}
          ollamaModel={ollamaModel}
          ollamaProvider={ollamaProvider}
          allowLocal={!a.agentic}
          showTiers
          showPresets
          // Место каталога общее для всех пользователей: LocalActionsAdminController.Set
          // валидирует preset:{id} только по Global.Presets, личная ссылка тут всегда 400.
          // presetScope="global" ограничивает и список выбора, и слой inline-созданной
          // цепочки — иначе «Собрать цепочку…» заводила бы ЛИЧНЫЙ пресет, который потом
          // не сохранить (MAJOR 2, ревью 65d8df66). Правка backend-валидации не нужна:
          // фронт просто не даёт создать/выбрать то, что бэкенд всё равно отклонит.
          presetScope="global"
          presetCreation={settings ? {
            savingScope,
            onSaveLayer: (scope, reducer, userId) => onSaveLayer(scope, reducer, userId),
          } : undefined}
          busy={busy}
          onChange={onPick}
          manual={{ models, presetIds: globalPresetIds }}
        />
      </div>
    </div>
  );
}
