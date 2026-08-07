import { useEffect, useMemo, useState } from 'react';
import { ArrowDown, ArrowUp, ChevronDown, Link2, Plus, X } from 'lucide-react';
import { Button, IconButton } from '../../components/ui';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import {
  TIERS, TIER_ORDER, routeTier, routeLabel,
  type ProviderData, type TierKey,
} from '../../lib/modelProvidersShared';
import { RoutePicker } from '../../components/RoutePicker';
import {
  chainStepLabel, chainSummary, isPresetRoute, presetIdOf, presetRoute,
  presetValueLabel, usePresets, invalidateEffectiveLines,
} from '../../lib/presets';
import { api, type ModelTiers } from '../../lib/api';
import { cloneLayer, newPresetId } from '../../lib/specialties';
import { loadModels, modelLabel, providerLabel, modelProvider, type ModelOption } from '../../lib/models';
import { C, FS, R, SP } from '../../lib/design';
import type { AppSettings, SpecialtySettingsLayer, SpecialtySettingsResponse } from '../../types';
import { ExceptionsBlock } from './ExceptionsBlock';

// Вкладка «Модели по умолчанию» (макет models-spend-v3.html §2). Три слота strong/medium/weak:
// маршрутная карточка уровня — название слева, стрелка, чип выбранной модели/пресета,
// счётчик мест справа. Раскрытый слот показывает шаги цепочки фолбэка с кнопками ↑↓✕ —
// цепочка правится прямо в слоте (если слот ссылается на пресет). «Сохранить как пресет…»
// появляется только в dirty-состоянии. Внизу — свёрнутый блок «Исключения» (бывш. специальности).

interface SlotsTabProps {
  isAdmin: boolean;
  data: ProviderData;
  contextUserId: string | null;
  onContextUserId: (id: string | null) => void;
  settings: SpecialtySettingsResponse | null;
  models: ModelOption[];
  tierModels: Record<TierKey, string>;
  ollamaModel?: string;
  savingScope: 'global' | 'owner' | null;
  onSaveLayer: (scope: 'global' | 'owner', next: SpecialtySettingsLayer) => void;
  onGoApply: () => void;
}

export function SlotsTab({ isAdmin, data, contextUserId, onContextUserId, settings, models,
  tierModels, ollamaModel, savingScope, onSaveLayer, onGoApply }: SlotsTabProps) {
  const presets = usePresets();
  const { selectedTiers, globalTiers, globalSettings, setGlobalSettings, setOwnTiers, setUserTiers } = data;

  const [expanded, setExpanded] = useState<TierKey | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [tierBusy, setTierBusy] = useState<TierKey | null>(null);
  const [contextMenuOpen, setContextMenuOpen] = useState(false);

  const tierModel = (t: TierKey): string => selectedTiers?.[t] ?? '';
  const globalTierModel = (t: TierKey): string => globalTiers?.[t] ?? '';

  // Счётчик мест по слоту: реально из каталога действий (api.usage → data.info.actions),
  // не хардкод. Считаем действия, чей маршрут адресован этому слоту.
  const usageByTier = useMemo(() => {
    const actions = data.info?.actions ?? [];
    const out: Record<TierKey, { count: number; titles: string[] }> = {
      strong: { count: 0, titles: [] }, medium: { count: 0, titles: [] }, weak: { count: 0, titles: [] },
    };
    for (const a of actions) {
      const t = routeTier(a.route);
      if (!t) continue;
      out[t].count++;
      if (out[t].titles.length < 3) out[t].titles.push(a.title);
    }
    return out;
  }, [data.info]);

  // Сохранение слота: личный/пользовательский — через свои эндпоинты, общий — через /api/settings.
  function saveTier(t: TierKey, model: string) {
    const prev = selectedTiers;
    const globalPrev = globalSettings;
    setTierBusy(t);
    setError(null);

    if (!isAdmin) setOwnTiers(s => s ? { ...s, [t]: model } : s);
    else if (contextUserId) setUserTiers(s => s ? { ...s, [t]: model } : s);
    else setGlobalSettings(s => s ? { ...s, [TIERS[t].field]: model } : s);

    const rollback = () => {
      if (!isAdmin) setOwnTiers(prev);
      else if (contextUserId) setUserTiers(prev);
      else setGlobalSettings(globalPrev);
    };

    const patch: Partial<ModelTiers> = { [t]: model };
    const ok = () => { void loadModels(); invalidateEffectiveLines(); };
    const fail = (e: unknown) => { rollback(); setError(e instanceof Error ? e.message : 'Не удалось сохранить'); };

    if (!isAdmin) {
      api.meModelTiers.save(patch).then(ok).catch(fail).finally(() => setTierBusy(null));
    } else if (contextUserId) {
      api.adminUserModelTiers.save(contextUserId, patch).then(ok).catch(fail).finally(() => setTierBusy(null));
    } else {
      api.settings.save({ [TIERS[t].field]: model } as Partial<AppSettings>)
        .then(ok).catch(fail).finally(() => setTierBusy(null));
    }
  }

  const ctxLabel = (() => {
    if (!isAdmin) return null;
    if (!contextUserId) return 'Общие · для всех';
    const u = data.users.find(x => x.id === contextUserId);
    return u?.displayName?.trim() || u?.username || 'Пользователь';
  })();

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
      {/* Контекст уровня: общие / конкретный пользователь (админ) */}
      {isAdmin && (
        <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: SP.md, flexWrap: 'wrap' }}>
          <div style={{ position: 'relative' }}>
            <Button variant="ghost" size="sm"
              leftIcon={<ChevronDown size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ color: C.textMuted, flexShrink: 0 }} />}
              onClick={() => setContextMenuOpen(o => !o)}
            >
              {ctxLabel}
            </Button>
            {contextMenuOpen && (
              <CtxMenu
                users={data.users}
                contextUserId={contextUserId}
                onPick={(id) => { setUserTiers(null); onContextUserId(id); setContextMenuOpen(false); setExpanded(null); }}
                onClose={() => setContextMenuOpen(false)}
              />
            )}
          </div>
          <span style={{ fontSize: FS.xs, color: C.textMuted, lineHeight: 1.4, maxWidth: 340 }}>
            Личная цепочка перекрывает общую, пустая — «как у всех». Пресеты остаются в списках выбора модели — и здесь, и в исключениях.
          </span>
        </div>
      )}

      <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
        {TIER_ORDER.map(t => (
          <SlotCard
            key={t}
            tier={t}
            model={tierModel(t)}
            inheritedModel={isAdmin && !contextUserId ? undefined : globalTierModel(t)}
            expanded={expanded === t}
            busy={tierBusy === t}
            usage={data.info ? usageByTier[t] : null}
            presets={presets}
            models={models}
            tierModels={tierModels}
            ollamaModel={ollamaModel}
            settings={settings}
            savingScope={savingScope}
            isAdmin={isAdmin}
            onSaveLayer={onSaveLayer}
            onToggle={() => setExpanded(expanded === t ? null : t)}
            onPickRoute={v => saveTier(t, v)}
            onGoApply={onGoApply}
          />
        ))}
      </div>

      {error && (
        <div style={{ padding: '7px 10px', borderRadius: R.sm, fontSize: FS.sm,
          color: C.dangerText, background: C.dangerBg, border: `1px solid ${C.dangerBorder}` }}>
          {error}
        </div>
      )}

      {/* Свёрнутый блок «Исключения» (бывш. вкладка «Специальности») */}
      <ExceptionsBlock
        settings={settings}
        isAdmin={isAdmin}
        models={models}
        tierModels={tierModels}
        ollamaModel={ollamaModel}
        savingScope={savingScope}
        onSaveLayer={onSaveLayer}
      />
    </div>
  );
}

// Меню выбора контекста уровня (общие / пользователь)
function CtxMenu({ users, contextUserId, onPick, onClose }: {
  users: ProviderData['users'];
  contextUserId: string | null;
  onPick: (id: string | null) => void;
  onClose: () => void;
}) {
  useClickOutside(onClose);
  return (
    <div style={{
      position: 'absolute', top: 34, left: 0, minWidth: 220, zIndex: 20,
      background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.xl,
      boxShadow: 'var(--shadow-dropdown)', padding: 4,
    }}>
      <MenuBtn active={contextUserId === null} label="Общие (все пользователи)" onClick={() => onPick(null)} />
      {users.map(u => (
        <MenuBtn key={u.id} active={contextUserId === u.id}
          label={u.displayName?.trim() || u.username} onClick={() => onPick(u.id)} />
      ))}
    </div>
  );
}

function MenuBtn({ active, label, onClick }: { active: boolean; label: string; onClick: () => void }) {
  return (
    <button type="button" onClick={onClick} style={{
      display: 'block', width: '100%', textAlign: 'left', font: 'inherit', fontSize: FS.sm,
      padding: '7px 10px', borderRadius: R.md, cursor: 'pointer',
      background: active ? C.bgSelected : 'transparent',
      color: active ? C.textHeading : C.textPrimary, border: 'none',
    }}>{label}</button>
  );
}

// Хук: закрыть элемент по клику вне его (без портала — меню живёт в нормальном потоке)
function useClickOutside(onClose: () => void) {
  useEffect(() => {
    const onDown = () => onClose();
    // Отложенный навес: иначе тот же клик, что открыл меню, его и закроет
    const id = setTimeout(() => document.addEventListener('click', onDown, { once: true }), 0);
    return () => { clearTimeout(id); document.removeEventListener('click', onDown); };
  }, [onClose]);
}

// === Карточка слота ===
interface SlotCardProps {
  tier: TierKey;
  model: string;
  inheritedModel?: string | null;
  expanded: boolean;
  busy: boolean;
  usage: { count: number; titles: string[] } | null;
  presets: ReturnType<typeof usePresets>;
  models: ModelOption[];
  tierModels: Record<TierKey, string>;
  ollamaModel?: string;
  settings: SpecialtySettingsResponse | null;
  savingScope: 'global' | 'owner' | null;
  isAdmin: boolean;
  onSaveLayer: (scope: 'global' | 'owner', next: SpecialtySettingsLayer) => void;
  onToggle: () => void;
  onPickRoute: (v: string) => void;
  onGoApply: () => void;
}

function SlotCard({ tier: t, model, inheritedModel, expanded, busy, usage, presets, models,
  tierModels, ollamaModel, settings, savingScope, isAdmin, onSaveLayer, onToggle, onPickRoute, onGoApply }: SlotCardProps) {
  const presetId = presetIdOf(model);
  const scoped = presetId ? presets.find(p => p.id.toLowerCase() === presetId.toLowerCase()) ?? null : null;
  const preset = scoped;
  const broken = presetId !== null && !preset;
  const labelCtx = { tierModels, ollamaModel };

  // Подпись чипа слота: пресет — имя + сводка шагов, битая ссылка — честная пометка,
  // модель — имя + провайдер, пустой — наследование общей / «решает CLI»
  let chipTitle: string;
  let chipSub: string;
  if (broken) {
    chipTitle = 'Пресет удалён';
    chipSub = 'работает настройка по умолчанию';
  } else if (preset) {
    chipTitle = `${preset.name}`;
    chipSub = `${preset.steps.length} шагов: ${chainSummary(preset, labelCtx)}`;
  } else if (model) {
    chipTitle = routeLabel(model, ollamaModel, tierModels);
    const prov = providerLabel(modelProvider(model));
    chipSub = prov || '';
  } else {
    chipTitle = inheritedModel ? `Как у всех · ${modelLabel(inheritedModel)}` : 'не задана — решает CLI';
    chipSub = inheritedModel ? 'личная цепочка не задана' : (TIERS[t].hint);
  }

  const used = usage?.count ?? 0;
  const empty = used === 0;

  return (
    <div style={{
      display: 'flex', flexDirection: 'column', gap: SP.sm,
      background: C.bgWhite, border: `1px solid ${expanded ? C.accent : C.border}`, borderRadius: R.xl,
      padding: `${SP.md}px ${SP.lg}px`,
    }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.md }}>
        {/* Название уровня */}
        <div style={{ width: 110, flexShrink: 0 }}>
          <div style={{ fontSize: FS.base, fontWeight: 700, color: C.textHeading }}>{TIERS[t].title}</div>
          <div style={{ fontSize: FS.xs, color: C.textMuted, marginTop: 2, lineHeight: 1.35 }}>{TIERS[t].hint}</div>
        </div>
        <span style={{ color: C.textMuted, flexShrink: 0 }}>→</span>
        {/* Чип выбранной модели/пресета — клик раскрывает редактор цепочки */}
        <button type="button" onClick={onToggle} disabled={busy}
          title={preset ? `Сейчас пойдёт: ${chainSummary(preset, labelCtx)}` : 'Нажмите, чтобы сменить модель'}
          style={{
            flex: 1, minWidth: 0, display: 'flex', alignItems: 'center', gap: 9, textAlign: 'left',
            padding: '8px 11px', borderRadius: R.lg, cursor: busy ? 'default' : 'pointer',
            opacity: busy ? 0.5 : 1, font: 'inherit',
            background: C.bgCard,
            border: `1px solid ${broken ? C.warning : (model ? C.border : C.dashed)}`,
            borderStyle: broken ? 'solid' : (model ? 'solid' : 'dashed'),
          }}>
          {(preset || broken) && <Link2 size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} style={{ flexShrink: 0, color: broken ? C.warningText : C.textMuted }} />}
          <span style={{ minWidth: 0 }}>
            <span style={{
              display: 'block', fontSize: FS.base, fontWeight: 600,
              color: broken ? C.warningText : (model ? C.textHeading : C.textMuted),
              overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
            }}>{chipTitle}</span>
            <span style={{
              display: 'block', fontSize: FS.xs, color: C.textMuted, marginTop: 1,
              overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
            }}>{chipSub}</span>
          </span>
          <ChevronDown size={ICON_SIZE.sm} strokeWidth={ICON_STROKE}
            style={{ marginLeft: 'auto', flexShrink: 0, color: C.textMuted, transform: expanded ? 'rotate(180deg)' : 'none', transition: 'transform 0.15s' }} />
        </button>
        {/* Счётчик мест — только когда каталог загружен (админ) */}
        {usage && (
          <div style={{ width: 150, flexShrink: 0, fontSize: FS.xs, color: C.textMuted, lineHeight: 1.4 }}>
            {empty ? (
              <><b style={{ color: C.textSecondary }}>0 мест</b><br />только через уровень у персон и задач</>
            ) : (
              <><b style={{ color: C.textSecondary }}>{used} {pluralPlaces(used)}</b><br />{usage.titles.join(', ')}{used > usage.titles.length ? '…' : ''}</>
            )}
          </div>
        )}
      </div>

      {/* Подсказка-переход для пустого слота: не должен читаться как поломка */}
      {usage && empty && (
        <div style={{ margin: '-2px 2px 0' }}>
          <button type="button" onClick={onGoApply} style={linkBtnStyle}>Назначьте её исполнителю задач или сложным местам →</button>
        </div>
      )}

      {/* Раскрытый редактор: цепочка шагов (если пресет) + смена маршрута слота */}
      {expanded && (
        <ChainEditor
          tier={t}
          model={model}
          preset={preset}
          broken={broken}
          presets={presets}
          models={models}
          tierModels={tierModels}
          ollamaModel={ollamaModel}
          settings={settings}
          savingScope={savingScope}
          isAdmin={isAdmin}
          onSaveLayer={onSaveLayer}
          onPickRoute={onPickRoute}
        />
      )}
    </div>
  );
}

// === Редактор цепочки внутри слота ===
function ChainEditor({ tier: t, model, preset, broken, presets, models, tierModels, ollamaModel,
  settings, savingScope, isAdmin, onSaveLayer, onPickRoute }: {
  tier: TierKey;
  model: string;
  preset: ReturnType<typeof usePresets>[number] | null;
  broken: boolean;
  presets: ReturnType<typeof usePresets>;
  models: ModelOption[];
  tierModels: Record<TierKey, string>;
  ollamaModel?: string;
  settings: SpecialtySettingsResponse | null;
  savingScope: 'global' | 'owner' | null;
  isAdmin: boolean;
  onSaveLayer: (scope: 'global' | 'owner', next: SpecialtySettingsLayer) => void;
  onPickRoute: (v: string) => void;
}) {
  const labelCtx = { tierModels, ollamaModel };
  // Черновик шагов: правится локально, пока не «Сохранить». Инициализируем шагами пресета.
  const presetSteps = preset?.steps ?? [];
  const [draft, setDraft] = useState<string[]>(presetSteps);
  const dirty = preset != null && (draft.length !== preset.steps.length ||
    draft.some((s, i) => s !== preset.steps[i]));

  // Самоссылка: шаг «уровень T» внутри пресета, разворачивающегося из этого же слота
  const selfRef = preset ? preset.steps.some(s => routeTier(s) === t) : false;

  const moveStep = (i: number, dir: -1 | 1) =>
    setDraft(steps => { const j = i + dir; if (j < 0 || j >= steps.length) return steps;
      const next = [...steps]; [next[i], next[j]] = [next[j], next[i]]; return next; });
  const removeStep = (i: number) => setDraft(steps => steps.filter((_, j) => j !== i));
  const setStep = (i: number, v: string) => setDraft(steps => steps.map((s, j) => j === i ? v : s));
  const addStep = () => setDraft(steps => [...steps, TIERS.medium.route]);

  // Сохранить: записать обновлённые шаги в пресет (если он свой/админ правит общий)
  const canSavePreset = preset != null && (preset.scope === 'owner' || isAdmin);
  const savePreset = () => {
    if (!preset || !dirty || !canSavePreset || !settings) return;
    if (draft.length === 0) return; // пустую цепочку бэкенд отклонит
    const scope = preset.scope;
    const next = cloneLayer(settings[scope]);
    const p = next.presets.find(x => x.id === preset.id);
    if (p) p.steps = draft;
    onSaveLayer(scope, next);
  };

  // Сохранить как пресет: новый личный пресет из черновика + перепривязка слота на него
  const saveAsPreset = () => {
    if (!settings || draft.length === 0) return;
    const copy = { id: newPresetId(), name: `${preset?.name ?? 'Цепочка'} (копия)`,
      description: preset?.description ?? null, steps: draft };
    const next = cloneLayer(settings.owner);
    next.presets.push(copy);
    onSaveLayer('owner', next);
    onPickRoute(presetRoute(copy.id));
  };

  const showChain = preset != null || broken;

  return (
    <div style={{ borderTop: `1px dashed ${C.border}`, paddingTop: SP.md, display: 'flex', flexDirection: 'column', gap: SP.sm }}>
      {showChain ? (
        <>
          <div style={{ fontSize: FS.xs, color: C.textMuted, lineHeight: 1.45 }}>
            {preset
              ? <>Цепочка из пресета «{preset.name}». Правьте прямо здесь: сам пресет не меняется, пока вы не сохраните цепочку под своим именем.</>
              : 'Пресет удалён — выберите другую цепочку или модель ниже.'}
          </div>

          {preset && preset.steps.length > 0 && (
            <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
              {draft.map((step, i) => (
                <div key={i} style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
                  <span style={{ fontSize: FS.xs, color: C.textMuted, width: 14, textAlign: 'right', flexShrink: 0 }}>{i + 1}</span>
                  <div style={{ flex: 1, minWidth: 0 }}>
                    <RoutePicker
                      route={step}
                      label={chainStepLabel(step, labelCtx)}
                      models={models}
                      tierModels={tierModels}
                      ollamaModel={ollamaModel}
                      allowLocal
                      readOnly={!canSavePreset}
                      busy={savingScope === preset.scope}
                      onChange={v => setStep(i, v)}
                    />
                  </div>
                  {canSavePreset && (
                    <span style={{ display: 'flex', gap: 2, flexShrink: 0 }}>
                      <IconButton size="xs" tone="muted" title="Выше" disabled={savingScope !== null || i === 0} onClick={() => moveStep(i, -1)}>
                        <ArrowUp size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
                      </IconButton>
                      <IconButton size="xs" tone="muted" title="Ниже" disabled={savingScope !== null || i === draft.length - 1} onClick={() => moveStep(i, 1)}>
                        <ArrowDown size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
                      </IconButton>
                      <IconButton size="xs" tone="muted" title={draft.length === 1 ? 'Пустая цепочка не сохранится' : 'Убрать шаг'}
                        disabled={savingScope !== null || draft.length === 1} onClick={() => removeStep(i)}>
                        <X size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
                      </IconButton>
                    </span>
                  )}
                </div>
              ))}
            </div>
          )}

          {canSavePreset && preset && (
            <button type="button" disabled={savingScope !== null || draft.length >= 5}
              onClick={addStep}
              style={addStepBtnStyle}>
              <Plus size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} /> Добавить модель
            </button>
          )}

          {selfRef && (
            <div style={{ fontSize: FS.xs, color: C.warningText, lineHeight: 1.45 }}>
              Шаг «{TIERS[t].title}» внутри пресета указывает обратно на эту же настройку — он будет пропущен.
            </div>
          )}

          {/* Футер правки цепочки: «Сохранить как пресет» видна только в dirty */}
          {canSavePreset && preset && (
            <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap', borderTop: `1px dashed ${C.border}`, paddingTop: SP.md }}>
              {dirty
                ? <span style={{ fontSize: FS.xs, color: C.warningText, fontWeight: 600 }}>Цепочка изменена · не совпадает с пресетом</span>
                : <span style={{ fontSize: FS.xs, color: C.textMuted }}>Цепочка совпадает с пресетом</span>}
              <span style={{ flex: 1 }} />
              {dirty && <Button size="sm" variant="ghost" disabled={savingScope !== null} onClick={() => setDraft(preset.steps)}>Отменить</Button>}
              {dirty && <Button size="sm" variant="primary" disabled={savingScope !== null || draft.length === 0} onClick={savePreset}>Сохранить</Button>}
              {dirty && <Button size="sm" variant="ghost" disabled={savingScope !== null} onClick={saveAsPreset}>Сохранить как пресет…</Button>}
            </div>
          )}
        </>
      ) : null}

      {/* Смена маршрута слота целиком (другая модель / другой пресет) */}
      <div style={{ display: 'flex', flexDirection: 'column', gap: 4, borderTop: showChain ? `1px dashed ${C.border}` : 'none', paddingTop: showChain ? SP.md : 0 }}>
        <span style={{ fontSize: FS.xs, color: C.textMuted }}>Сменить модель или пресет слота:</span>
        <RoutePicker
          route={model}
          label={model ? (isPresetRoute(model) ? presetValueLabel(model, presets) : routeLabel(model, ollamaModel, tierModels)) : ''}
          models={models}
          tierModels={tierModels}
          ollamaModel={ollamaModel}
          showPresets
          busy={savingScope !== null}
          placeholder="не задана — решает CLI"
          onChange={onPickRoute}
        />
      </div>
    </div>
  );
}

const linkBtnStyle = {
  font: 'inherit', fontSize: FS.xs, fontWeight: 600, color: C.accent, background: 'none',
  border: 'none', padding: 0, cursor: 'pointer', textDecoration: 'none',
} as const;

const addStepBtnStyle = {
  display: 'inline-flex', alignItems: 'center', gap: 6, alignSelf: 'flex-start',
  font: 'inherit', fontSize: FS.xs, fontWeight: 600, color: C.accent,
  background: C.accentLight, border: `1px dashed ${C.accentMuted}`, borderRadius: R.md,
  padding: '6px 10px', cursor: 'pointer',
} as const;

function pluralPlaces(n: number): string {
  const m10 = n % 10, m100 = n % 100;
  if (m10 === 1 && m100 !== 11) return 'место';
  if (m10 >= 2 && m10 <= 4 && (m100 < 12 || m100 > 14)) return 'места';
  return 'мест';
}
