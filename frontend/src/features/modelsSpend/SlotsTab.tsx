import { useEffect, useMemo, useRef, useState } from 'react';
import { ChevronDown, Link2, RotateCcw } from 'lucide-react';
import { Button } from '../../components/ui';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import {
  TIERS, TIER_ORDER, routeTier, routeLabel,
  type ProviderData, type TierKey,
} from '../../lib/modelProvidersShared';
import { RoutePicker } from '../../components/RoutePicker';
import { ChainStepsEditor } from '../../components/ChainStepsEditor';
import {
  chainSummary, isPresetRoute, presetIdOf, presetRoute,
  presetValueLabel, usePresets, invalidateEffectiveLines,
} from '../../lib/presets';
import { api, type ModelTiers } from '../../lib/api';
import { cloneLayer, newPresetId } from '../../lib/specialties';
import { loadModels, modelLabel, providerLabel, modelProvider, type ModelOption } from '../../lib/models';
import { C, FS, R, SP } from '../../lib/design';
import { showToast } from '../../lib/toast';
import type { AppSettings, SpecialtySettingsLayer, SpecialtySettingsResponse } from '../../types';
import { EffectiveLine } from '../../components/EffectiveLine';
import { ResetConfirmDialog } from './ResetConfirmDialog';

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
  savingScope: 'global' | 'owner' | 'user' | null;
  onSaveLayer: (scope: 'global' | 'owner' | 'user', next: SpecialtySettingsLayer) => Promise<void>;
  meUserId: string | null;
  // A2: запрос на запуск черновика новой цепочки (от requestNewPreset() из RoutePicker).
  // Раскрываем первую карточку, чтобы человек сразу увидел редактор.
  pendingDraft?: boolean;
  onPendingDraftConsumed?: () => void;
}

export function SlotsTab({ isAdmin, data, contextUserId, onContextUserId, settings, models,
  tierModels, ollamaModel, savingScope, onSaveLayer, meUserId,
  pendingDraft, onPendingDraftConsumed }: SlotsTabProps) {
  const presets = usePresets();
  const { selectedTiers, globalTiers, globalSettings, setGlobalSettings, setOwnTiers, setUserTiers } = data;

  const [expanded, setExpanded] = useState<TierKey | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [tierBusy, setTierBusy] = useState<TierKey | null>(null);
  const [contextMenuOpen, setContextMenuOpen] = useState(false);
  // resetBusy — отдельно от tierBusy: reset пишет патч на три поля разом,
  // а tierBusy типизирован как TierKey | null и жест на три слота не выражает
  const [resetBusy, setResetBusy] = useState(false);
  const [resetConfirmOpen, setResetConfirmOpen] = useState(false);
  // A4: ref вместо state — нам не нужно ререндерить SlotCard на каждый чих черновика,
  // нужно только прочитать значение в requestToggle. SlotCard держит свой dirty локально
  // и публикует через onDirtyChange.
  const editorDirtyRef = useRef<Record<TierKey, boolean>>({ strong: false, medium: false, weak: false });
  const [confirmCollapseOf, setConfirmCollapseOf] = useState<TierKey | null>(null);
  const requestToggle = (t: TierKey) => {
    if (expanded === t && editorDirtyRef.current[t]) {
      // Свернуть пытаемся, пока редактор грязный — спросить подтверждение вместо тихой потери
      setConfirmCollapseOf(t);
      return;
    }
    setExpanded(expanded === t ? null : t);
  };

  const tierModel = (t: TierKey): string => selectedTiers?.[t] ?? '';
  const globalTierModel = (t: TierKey): string => globalTiers?.[t] ?? '';

  // Счётчик мест по слоту: реально из каталога действий (api.usage → data.info.actions),
  // не хардкод. Считаем действия, чей маршрут адресован этому слоту (tier:*). Действия без
  // явного route или с route ≠ tier:* НЕ приписываем к слоту на фронте — дефолтный уровень
  // места знает только бэкенд (LocalActionCatalog.EffectiveDefaultTier). totalActions —
  // размер всего каталога, чтобы подпись была честной «X из M».
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

  const totalActions = data.info?.actions.length ?? 0;

  // A2: запрос на черновик цепочки — открываем первую (Сильную) карточку, чтобы человек
  // сразу попал в редактор. Разовая реакция: после первого маунта флаг сбрасываем.
  useEffect(() => {
    if (pendingDraft) {
      setExpanded(TIER_ORDER[0]);
      onPendingDraftConsumed?.();
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [pendingDraft]);

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

  // Имя пользователя текущего контекста (без префикса «Общие · для всех» из ctxLabel) —
  // нужно отдельно для текста диалога подтверждения чужого сброса
  const contextUserName = (() => {
    if (!contextUserId) return null;
    const u = data.users.find(x => x.id === contextUserId);
    return u?.displayName?.trim() || u?.username || 'Пользователь';
  })();

  // Сброс всей тройки контекста: пустой патч на три поля разом, тем же ветвлением каналов,
  // что и saveTier. Инвариант — сброс ВСЕГДА шлёт "", никогда не вписывает имя модели.
  const resetTiers = () => {
    const prev = selectedTiers;
    const globalPrev = globalSettings;
    setResetBusy(true);
    setError(null);

    const emptyTiers: ModelTiers = { strong: '', medium: '', weak: '' };
    const emptyGlobalPatch = Object.fromEntries(TIER_ORDER.map(t => [TIERS[t].field, ''])) as Partial<AppSettings>;

    if (!isAdmin) setOwnTiers(emptyTiers);
    else if (contextUserId) setUserTiers(emptyTiers);
    else setGlobalSettings(s => s ? { ...s, ...emptyGlobalPatch } : s);

    const rollback = () => {
      if (!isAdmin) setOwnTiers(prev);
      else if (contextUserId) setUserTiers(prev);
      else setGlobalSettings(globalPrev);
    };

    const ok = () => {
      void loadModels();
      invalidateEffectiveLines();
      showToast('Модели', 'Вернули к настройке по умолчанию');
    };
    const fail = (e: unknown) => { rollback(); setError(e instanceof Error ? e.message : 'Не удалось сбросить'); };
    const done = () => { setResetBusy(false); setResetConfirmOpen(false); };

    if (!isAdmin) {
      api.meModelTiers.save(emptyTiers).then(ok).catch(fail).finally(done);
    } else if (contextUserId) {
      api.adminUserModelTiers.save(contextUserId, emptyTiers).then(ok).catch(fail).finally(done);
    } else {
      api.settings.save(emptyGlobalPatch).then(ok).catch(fail).finally(done);
    }
  };

  // Диалог — только когда сбрасывают не своё: у рядового пользователя contextUserId
  // всегда null и это его личные слоты, поэтому isAdmin обязателен в условии
  const resetIsForeign = isAdmin && (contextUserId === null || contextUserId !== meUserId);
  const resetAllEmpty = !selectedTiers || (!selectedTiers.strong && !selectedTiers.medium && !selectedTiers.weak);

  const handleResetClick = () => {
    if (resetIsForeign) setResetConfirmOpen(true);
    else resetTiers();
  };

  const resetDialogTitle = !contextUserId
    ? 'Сбросить общие модели по умолчанию?'
    : `Сбросить модели по умолчанию у ${contextUserName}?`;
  const resetDialogBody = !contextUserId
    ? 'Три поля опустеют, и все, у кого нет своих настроек, вернутся к тому, что выбирает место применения.'
    : 'Это чужие настройки — человек увидит изменение у себя.';

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
      {/* Контекст уровня (общие / конкретный пользователь — только у админа) + сброс тройки слотов */}
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.md, flexWrap: 'wrap' }}>
        {isAdmin && (
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
        )}
        <span style={{ flex: 1 }} />
        {isAdmin && (
          <span style={{ fontSize: FS.xs, color: C.textMuted, lineHeight: 1.4, maxWidth: 340 }}>
            Личная цепочка перекрывает общую, пустая — «как у всех». Цепочки остаются в списках выбора модели — и здесь, и в особых правилах.
          </span>
        )}
        <Button variant="ghost" size="sm" disabled={resetAllEmpty || resetBusy}
          loading={resetBusy}
          leftIcon={<RotateCcw size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
          title="Вернуть поля к настройке по умолчанию"
          onClick={handleResetClick}
        >
          Сбросить
        </Button>
      </div>

      <ResetConfirmDialog
        open={resetConfirmOpen}
        title={resetDialogTitle}
        body={resetDialogBody}
        confirmLabel="Сбросить"
        busy={resetBusy}
        onCancel={() => setResetConfirmOpen(false)}
        onConfirm={resetTiers}
      />

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
            totalActions={totalActions}
            presets={presets}
            models={models}
            tierModels={tierModels}
            ollamaModel={ollamaModel}
            settings={settings}
            savingScope={savingScope}
            isAdmin={isAdmin}
            onSaveLayer={onSaveLayer}
            onToggle={() => requestToggle(t)}
            onPickRoute={v => saveTier(t, v)}
            onDirtyChange={d => { editorDirtyRef.current[t] = d; }}
          />
        ))}
      </div>

      {error && (
        <div style={{ padding: '7px 10px', borderRadius: R.sm, fontSize: FS.sm,
          color: C.dangerText, background: C.dangerBg, border: `1px solid ${C.dangerBorder}` }}>
          {error}
        </div>
      )}

      {/* A4: при сворачивании карточки с несохранённой правкой черновика цепочки —
          подтверждаем потерю. Иначе один клик по заголовку уносит несохранённые шаги. */}
      <ResetConfirmDialog
        open={confirmCollapseOf !== null}
        title="В черновике остались шаги"
        body="Если свернуть карточку, изменения пропадут. Сохраните или отмените правки, прежде чем закрывать."
        confirmLabel="Свернуть без сохранения"
        variant="danger"
        onCancel={() => setConfirmCollapseOf(null)}
        onConfirm={() => {
          const t = confirmCollapseOf;
          setConfirmCollapseOf(null);
          if (t !== null) setExpanded(null);
        }}
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
      <MenuBtn active={contextUserId === null} label="Для всех" onClick={() => onPick(null)} />
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
  totalActions: number;
  presets: ReturnType<typeof usePresets>;
  models: ModelOption[];
  tierModels: Record<TierKey, string>;
  ollamaModel?: string;
  settings: SpecialtySettingsResponse | null;
  savingScope: 'global' | 'owner' | 'user' | null;
  isAdmin: boolean;
  onSaveLayer: (scope: 'global' | 'owner' | 'user', next: SpecialtySettingsLayer) => Promise<void>;
  onToggle: () => void;
  onPickRoute: (v: string) => void;
  // A4: редактор цепочки публикует наружу «грязный» флаг, чтобы родитель мог
  // спросить подтверждение перед сворачиванием
  onDirtyChange?: (dirty: boolean) => void;
}

function SlotCard({ tier: t, model, inheritedModel, expanded, busy, usage, totalActions,
  presets, models, tierModels, ollamaModel, settings, savingScope, isAdmin, onSaveLayer,
  onToggle, onPickRoute, onDirtyChange }: SlotCardProps) {
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
    chipTitle = 'Цепочка удалена';
    chipSub = 'работает настройка по умолчанию';
  } else if (preset) {
    chipTitle = `${preset.name}`;
    chipSub = `${preset.steps.length} шагов: ${chainSummary(preset, labelCtx)}`;
  } else if (model) {
    chipTitle = routeLabel(model, ollamaModel, tierModels);
    const prov = providerLabel(modelProvider(model));
    chipSub = prov || '';
  } else {
    chipTitle = inheritedModel ? `Как у всех · ${modelLabel(inheritedModel)}` : 'не задана — выберет Claude Code сам';
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
        {/* Чип выбранной модели/цепочки — клик раскрывает редактор уровня: цепочка + смена.
            Тултип соответствует содержимому, которое человек увидит после клика. */}
        <button type="button" onClick={onToggle} disabled={busy}
          title={
            broken ? 'Цепочка удалена · нажмите, чтобы выбрать другую или задать модель'
            : preset ? `Откроется: цепочка «${preset.name}» и смена уровня`
            : model ? `Откроется: «${modelLabel(model)}» и смена уровня`
            : 'Нажмите, чтобы выбрать модель или собрать цепочку'
          }
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
        {/* Счётчик мест — только когда каталог загружен (админ). «X из M» — где M весь каталог,
            показывает, сколько мест вообще ездит через этот уровень и сколько идёт мимо (другие
            маршруты / дефолт). */}
        {usage && (
          <div style={{ width: 150, flexShrink: 0, fontSize: FS.xs, color: C.textMuted, lineHeight: 1.4 }}>
            {empty ? (
              <><b style={{ color: C.textSecondary }}>0 из {totalActions}</b><br />через уровень не поедет никто
              {totalActions > 0 && ', только через персон и задачи'}</>
            ) : (
              <><b style={{ color: C.textSecondary }}>{used} из {totalActions}</b><br />{usage.titles.join(', ')}{used > usage.titles.length ? '…' : ''}</>
            )}
          </div>
        )}
      </div>

      {/* B7: «Сейчас пойдёт» — что реально поедет на этом уровне, если специальность не
          переопределяет. Эндпоинт GET /api/models/preview?kind=specialty&tier=...&specialtyKey=any
          считается той же дорогой, что и запуск хода — второй точки истины нет. */}
      <EffectiveLine ctx={{ kind: 'specialty', tier: t, specialtyKey: 'any' }} />

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
          onDirtyChange={onDirtyChange}
        />
      )}
    </div>
  );
}

// === Редактор цепочки внутри слота ===
function ChainEditor({ tier: t, model, preset, broken, presets, models, tierModels, ollamaModel,
  settings, savingScope, isAdmin, onSaveLayer, onPickRoute, onDirtyChange }: {
  tier: TierKey;
  model: string;
  preset: ReturnType<typeof usePresets>[number] | null;
  broken: boolean;
  presets: ReturnType<typeof usePresets>;
  models: ModelOption[];
  tierModels: Record<TierKey, string>;
  ollamaModel?: string;
  settings: SpecialtySettingsResponse | null;
  savingScope: 'global' | 'owner' | 'user' | null;
  isAdmin: boolean;
  onSaveLayer: (scope: 'global' | 'owner' | 'user', next: SpecialtySettingsLayer) => Promise<void>;
  onPickRoute: (v: string) => void;
  // A4: редактор шлёт «грязный» флаг наверх, чтобы SlotCard не дал свернуть молча
  onDirtyChange?: (dirty: boolean) => void;
}) {
  // Черновик шагов: правится локально, пока не «Сохранить». Инициализируем шагами пресета.
  const presetSteps = preset?.steps ?? [];
  const [draft, setDraft] = useState<string[]>(presetSteps);
  const dirty = preset != null && (draft.length !== preset.steps.length ||
    draft.some((s, i) => s !== preset.steps[i]));
  // A4: публикуем dirty наверх, чтобы SlotCard не схлопнул карточку молча
  useEffect(() => { onDirtyChange?.(dirty); }, [dirty, onDirtyChange]);

  // Самоссылка: шаг «уровень T» внутри пресета, разворачивающегося из этого же слота
  const selfRef = preset ? preset.steps.some(s => routeTier(s) === t) : false;

  // Сохранить: записать обновлённые шаги в пресет (если он свой/админ правит общий)
  const canSavePreset = preset != null && (preset.scope === 'owner' || isAdmin);
  // Правка общей цепочки админом — с подтверждением: изменение увидят все пользователи
  const [confirmSharedEdit, setConfirmSharedEdit] = useState(false);
  // Собственно запись шагов в слой — вынесено, чтобы диалог подтверждения мог его вызвать
  const commitSavePreset = () => {
    if (!preset || !settings) return;
    if (draft.length === 0) return; // пустую цепочку бэкенд отклонит
    const scope = preset.scope;
    const baseLayer = settings[scope];
    if (!baseLayer) return;
    const next = cloneLayer(baseLayer);
    const p = next.presets.find(x => x.id === preset.id);
    if (p) p.steps = draft;
    // catch пустой намеренно: отказ уже показан баннером в ModelsSpendModal, здесь он нужен
    // только чтобы отклонённый промис не всплыл как «Uncaught (in promise)»
    void onSaveLayer(scope, next).catch(() => {});
  };
  const savePreset = () => {
    if (!preset || !dirty || !canSavePreset || !settings) return;
    if (draft.length === 0) return;
    // Админ и общая цепочка — сначала диалог «Сохранить для всех», иначе сразу запись
    if (isAdmin && preset.scope === 'global') {
      setConfirmSharedEdit(true);
      return;
    }
    commitSavePreset();
  };

  // Сохранить как пресет: новый пресет из черновика + перепривязка слота на него. Слой —
  // owner у обычного пользователя (личный слот), но у админа ВСЕГДА global — даже когда
  // контекст слота "чужой" (contextUserId): личный пресет админа не резолвится ни в его
  // общем слоте, ни тем более в слоте другого пользователя (MAJOR 2, ревью d23231bd)
  const saveAsPreset = () => {
    if (!settings || draft.length === 0) return;
    const targetScope: 'global' | 'owner' | 'user' = isAdmin ? 'global' : 'owner';
    const copy = { id: newPresetId(), name: `${preset?.name ?? 'Цепочка'} (копия)`,
      description: preset?.description ?? null, steps: draft };
    const next = cloneLayer(settings[targetScope]);
    next.presets.push(copy);
    // Перепривязка слота — только после записи слоя. Иначе PUT тира уходит параллельно и,
    // в отличие от мест, ссылку на пресет не валидирует вовсе: упавшая запись слоя оставила
    // бы слот указывающим на несуществующий пресет — молча, у всех (MAJOR 1, ревью 03607845)
    void onSaveLayer(targetScope, next)
      .then(() => onPickRoute(presetRoute(copy.id)))
      .catch(() => {});
  };

  const showChain = preset != null || broken;

  return (
    <div style={{ borderTop: `1px dashed ${C.border}`, paddingTop: SP.md, display: 'flex', flexDirection: 'column', gap: SP.sm }}>
      {showChain ? (
        <>
          <div style={{ fontSize: FS.xs, color: C.textMuted, lineHeight: 1.45 }}>
            {preset
              ? <>Цепочка «{preset.name}». Правьте прямо здесь: цепочка не сохранится, пока вы не запишете её отдельной цепочкой.</>
              : 'Цепочка удалена — выберите другую цепочку или модель ниже.'}
          </div>

          {preset && preset.steps.length > 0 && (
            <ChainStepsEditor
              steps={draft}
              onChange={setDraft}
              models={models}
              tierModels={tierModels}
              ollamaModel={ollamaModel}
              readOnly={!canSavePreset}
              busy={savingScope === preset.scope}
            />
          )}

          {selfRef && (
            <div style={{ fontSize: FS.xs, color: C.warningText, lineHeight: 1.45 }}>
              Шаг «{TIERS[t].title}» внутри цепочки указывает обратно на эту же настройку — он будет пропущен.
            </div>
          )}

          {/* Футер правки цепочки: «Сохранить как пресет» видна только в dirty */}
          {canSavePreset && preset && (
            <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap', borderTop: `1px dashed ${C.border}`, paddingTop: SP.md }}>
              {dirty
                ? <span style={{ fontSize: FS.xs, color: C.warningText, fontWeight: 600 }}>Цепочка изменена · не совпадает с цепочкой</span>
                : <span style={{ fontSize: FS.xs, color: C.textMuted }}>Цепочка совпадает с цепочкой</span>}
              <span style={{ flex: 1 }} />
              {dirty && <Button size="sm" variant="ghost" disabled={savingScope !== null} onClick={() => setDraft(preset.steps)}>Отменить</Button>}
              {dirty && <Button size="sm" variant="primary" disabled={savingScope !== null || draft.length === 0} onClick={savePreset}>Сохранить</Button>}
              {dirty && <Button size="sm" variant="ghost" disabled={savingScope !== null} onClick={saveAsPreset}>Сохранить как цепочку…</Button>}
            </div>
          )}
        </>
      ) : null}

      {/* Смена маршрута слота целиком (другая модель / другой пресет) */}
      <div style={{ display: 'flex', flexDirection: 'column', gap: 4, borderTop: showChain ? `1px dashed ${C.border}` : 'none', paddingTop: showChain ? SP.md : 0 }}>
        <span style={{ fontSize: FS.xs, color: C.textMuted }}>Сменить модель или цепочку уровня:</span>
        <RoutePicker
          route={model}
          label={model ? (isPresetRoute(model) ? presetValueLabel(model, presets) : routeLabel(model, ollamaModel, tierModels)) : ''}
          models={models}
          tierModels={tierModels}
          ollamaModel={ollamaModel}
          showPresets
          // 'global' у админа (и для общего слота, и для чужого контекста пользователя) —
          // личный пресет админа не годится ни туда: свой слот у остальных пользователей
          // читает Global.Presets, а чужой личный слот резолвится в ЕГО owner-пресетах,
          // где созданного админом пресета тоже нет (MAJOR 2, ревью d23231bd)
          presetScope={isAdmin ? 'global' : undefined}
          presetCreation={{ settings, savingScope, onSaveLayer }}
          busy={savingScope !== null}
          placeholder="не задана — выберет Claude Code сам"
          onChange={onPickRoute}
        />
      </div>

      <ResetConfirmDialog
        open={confirmSharedEdit}
        title="Сохранить цепочку для всех?"
        body="Цепочка общая. Изменение увидят все пользователи."
        confirmLabel="Сохранить для всех"
        variant="primary"
        busy={savingScope !== null}
        onCancel={() => setConfirmSharedEdit(false)}
        onConfirm={() => { setConfirmSharedEdit(false); commitSavePreset(); }}
      />
    </div>
  );
}
