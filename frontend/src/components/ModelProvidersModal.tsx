import { useEffect, useLayoutEffect, useRef, useState } from 'react';
import { ChevronDown, RotateCcw, Sparkles, Gift, Cpu, Zap, Scale, Boxes } from 'lucide-react';
import { Modal, IconButton, Toggle } from './ui';
import { ModelPicker } from './ModelPicker';
import { ICON_SIZE, ICON_STROKE } from './ui/icons';
import { api } from '../lib/api';
import { C, FONT, FS, R, SHADOW, Z, MODAL_W } from '../lib/design';
import { useModels, useProviders, modelLabel, providerLabel, modelProvider, type ModelOption } from '../lib/models';
import type { OllamaUsageInfo, OllamaActionInfo, AppSettings } from '../types';

interface Props {
  onClose: () => void;
}

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

// Пресеты автоподбора: сервер проставляет исполнителя всем действиям по единому правилу.
type PresetKey = 'recommended' | 'balanced' | 'free' | 'local';
const PRESETS: { key: PresetKey; icon: typeof Sparkles; title: string; desc: string }[] = [
  { key: 'recommended', icon: Sparkles, title: 'Рекомендованное',
    desc: 'Лучшее качество: локаль и AI под сложность задачи (могут быть платные)' },
  { key: 'balanced', icon: Scale, title: 'Сбалансированный',
    desc: 'По сложности: простое — на локальной модели, среднее — бесплатные облачные, тяжёлое — AI' },
  { key: 'free', icon: Gift, title: 'Только бесплатные',
    desc: 'Бесплатные облачные модели OpenRouter — без затрат' },
  { key: 'local', icon: Cpu, title: 'Локальные',
    desc: 'Локальная модель, где подходит; для сложных задач — бесплатная облачная' },
];

// Статус адаптера → цвет точки (только дизайн-токены)
const DOT_COLOR: Record<'active' | 'inactive' | 'offline', string> = {
  active: C.success,
  inactive: C.textMuted,
  offline: C.warning,
};

// Диалог «Поставщики моделей»: три уровня сверху вниз.
//  1. Подключённые модели — сворачиваемая плитка адаптеров со статусами (read-only, из /api/models).
//  2. Модель по умолчанию — глобальная модель для новых чатов (GET/PUT /api/settings, DefaultChatModel).
//  3. Для отдельных задач — исполнитель каждого фонового ИИ-действия (как бывшие «Фоновые задачи»):
//     локальная модель / модель по умолчанию / конкретная модель любого провайдера.
// Настройка серверная и общая для всех — только админ.
export function ModelProvidersModal({ onClose }: Props) {
  const [info, setInfo] = useState<OllamaUsageInfo | undefined>(undefined);
  const [busy, setBusy] = useState<string | null>(null);
  const [preset, setPreset] = useState<PresetKey | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [autopickOpen, setAutopickOpen] = useState(false);
  const models = useModels();
  const providers = useProviders();

  // Настройки инстанса (AppSettings): DefaultChatModel (уровень 2) + тумблер утреннего брифа.
  const [settings, setSettings] = useState<AppSettings | null>(null);
  const [briefingBusy, setBriefingBusy] = useState(false);
  const [defaultBusy, setDefaultBusy] = useState(false);
  const [editingDefault, setEditingDefault] = useState(false);
  const briefingOn = settings?.dailyBriefingEnabled ?? true;
  const defaultModel = settings?.defaultChatModel ?? '';

  // Уровень 1 по умолчанию свёрнут (краткий однострочник), разворачивается по клику
  const [providersExpanded, setProvidersExpanded] = useState(false);

  // Оптимистично: применяем сразу, при ошибке возвращаем прежнее значение. Шлём ТОЛЬКО своё
  // поле — PUT /api/settings патчит присланное, поэтому наш (возможно устаревший) снимок
  // не откатывает соседние настройки, изменённые тем временем с другого экрана.
  function toggleBriefing(v: boolean) {
    const prev = settings;
    setBriefingBusy(true);
    setError(null);
    setSettings(s => s ? { ...s, dailyBriefingEnabled: v } : s);
    api.settings.save({ dailyBriefingEnabled: v })
      .then(saved => setSettings(saved))
      .catch(e => {
        setSettings(prev);
        setError(e instanceof Error ? e.message : 'Не удалось сохранить');
      })
      .finally(() => setBriefingBusy(false));
  }

  // Сохранение модели по умолчанию: model='' → сознательный сброс к дефолту CLI (бэкенд: "" = сброс).
  function saveDefault(model: string) {
    const prev = settings;
    setDefaultBusy(true);
    setError(null);
    setSettings(s => s ? { ...s, defaultChatModel: model } : s);
    api.settings.save({ defaultChatModel: model })
      .then(saved => { setSettings(saved); setEditingDefault(false); })
      .catch(e => {
        setSettings(prev);
        setError(e instanceof Error ? e.message : 'Не удалось сохранить');
      })
      .finally(() => setDefaultBusy(false));
  }

  useEffect(() => {
    let cancelled = false;
    api.usage.get()
      .then(d => { if (!cancelled) setInfo(d.ollama ?? { enabled: false, actions: [] }); })
      .catch(() => { if (!cancelled) setInfo({ enabled: false, actions: [] }); });
    // Настройки инстанса грузим отдельно: их отсутствие не должно прятать список действий
    api.settings.get()
      .then(s => { if (!cancelled) setSettings(s); })
      .catch(() => { /* модель по умолчанию останется пустой, тумблер — включённым */ });
    return () => { cancelled = true; };
  }, []);

  // Доступность пресетов: бесплатные облачные модели есть в каталоге? локаль настроена?
  const hasFree = models.some(m => m.provider === 'openrouter-direct');
  const ollamaOn = info?.enabled ?? false;

  async function applyPreset(key: PresetKey) {
    setPreset(key);
    setError(null);
    try {
      await api.localActions.applyPreset(key);
      const d = await api.usage.get().catch(() => undefined);
      setInfo(d?.ollama ?? { enabled: false, actions: [] });
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Не удалось применить пресет');
    } finally {
      setPreset(null);
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

  // === Уровень 1: плитка адаптеров ===
  // CLI-провайдеры (включая ненастроенные) + Ollama из блока usage. OpenRouter-direct — это
  // виртуальный провайдер каталога моделей (не отдельный адаптер), отдельной плитки не имеет.
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

  const defaultLabel = defaultModel ? modelLabel(defaultModel) : 'По умолчанию (CLI)';
  const defaultProvider = defaultModel ? providerLabel(modelProvider(defaultModel)) : null;

  return (
    <Modal
      title="Поставщики моделей"
      subtitle="Кто выполняет чаты и фоновые задачи. Настройка общая и применяется сразу."
      width={MODAL_W.form}
      onClose={onClose}
    >
      {/* === Уровень 1: Подключённые модели (свёрнут по умолчанию) === */}
      <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
        <button
          type="button"
          onClick={() => setProvidersExpanded(o => !o)}
          style={{
            display: 'flex', alignItems: 'center', gap: 9, width: '100%', textAlign: 'left',
            padding: '10px 12px', borderRadius: R.lg, background: C.bgWhite,
            border: `1px solid ${C.border}`, cursor: 'pointer', transition: 'border-color 0.15s',
          }}
        >
          <Boxes size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} style={{ color: C.accent, flexShrink: 0 }} />
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
                    width: 6, height: 6, borderRadius: '50%', flexShrink: 0,
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
                    style={{ fontSize: FS.xs, color: C.accent, alignSelf: 'flex-start', marginTop: 1 }}>
                    Настроить →
                  </span>
                )}
              </div>
            ))}
          </div>
        )}
      </div>

      {/* === Уровень 2: Модель по умолчанию === */}
      <div style={{ display: 'flex', flexDirection: 'column' }}>
        <div style={levelTitleStyle()}>Модель по умолчанию</div>
        {!editingDefault ? (
          <div style={{
            display: 'flex', alignItems: 'center', gap: 10, padding: '11px 14px',
            background: C.bgCard, border: `1px solid ${C.border}`, borderRadius: R.xl,
          }}>
            <div style={{ flex: 1, minWidth: 0 }}>
              <div style={{ fontSize: FS.xs, color: C.textMuted, marginBottom: 2 }}>Для новых чатов</div>
              <div style={{ fontSize: FS.md, fontWeight: 600, color: C.textHeading,
                overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                {defaultLabel}{defaultProvider ? ` · ${defaultProvider}` : ''}
              </div>
            </div>
            <button
              type="button"
              onClick={() => setEditingDefault(true)}
              disabled={defaultBusy}
              style={{
                flexShrink: 0, padding: '6px 12px', borderRadius: R.md, cursor: defaultBusy ? 'default' : 'pointer',
                border: `1px solid ${C.border}`, background: C.bgWhite,
                fontFamily: FONT.sans, fontSize: FS.sm, fontWeight: 600, color: C.accent,
                opacity: defaultBusy ? 0.5 : 1,
              }}>
              Сменить
            </button>
          </div>
        ) : (
          <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
            <ModelPicker
              value={defaultModel}
              options={models}
              onChange={saveDefault}
              collapsible={false}
            />
            <button
              type="button"
              onClick={() => setEditingDefault(false)}
              style={{
                alignSelf: 'flex-start', border: 'none', background: 'none', padding: '2px 2px',
                cursor: 'pointer', fontFamily: FONT.sans, fontSize: FS.sm, fontWeight: 600, color: C.textMuted,
              }}>
              Отмена
            </button>
          </div>
        )}
      </div>

      {/* === Уровень 3: Для отдельных задач === */}
      {info === undefined ? (
        <div style={{ color: C.textMuted, fontSize: FS.md, padding: '8px 0' }}>Загрузка…</div>
      ) : (
        <>
          <div style={{ display: 'flex', flexDirection: 'column' }}>
            <div style={levelTitleStyle()}>Для отдельных задач</div>

            {/* Автоподбор — пресеты схлопнуты в выпадающее меню (вместо 4 больших кнопок) */}
            <div>
              <button
                type="button"
                onClick={() => setAutopickOpen(o => !o)}
                style={{
                  display: 'flex', alignItems: 'center', gap: 8, width: '100%', textAlign: 'left',
                  padding: '10px 12px', borderRadius: R.lg, background: C.bgCard,
                  border: `1px solid ${autopickOpen ? C.accent : C.border}`, cursor: 'pointer',
                  transition: 'border-color 0.15s',
                }}>
                <Sparkles size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} style={{ color: C.accent, flexShrink: 0 }} />
                <span style={{ flex: 1, fontSize: FS.base, fontWeight: 500, color: C.textHeading }}>
                  Автоподбор
                </span>
                <ChevronDown size={ICON_SIZE.xs} strokeWidth={ICON_STROKE}
                  style={{ flexShrink: 0, color: C.textMuted, transform: autopickOpen ? 'rotate(180deg)' : 'none',
                    transition: 'transform 0.15s' }} />
              </button>

              {autopickOpen && (
                <div style={{
                  marginTop: 6, background: C.bgWhite, border: `1px solid ${C.border}`,
                  borderRadius: R.lg, overflow: 'hidden',
                }}>
                  {PRESETS.map((p, i) => {
                    const disabled = (p.key === 'free' && !hasFree) || (p.key === 'local' && !ollamaOn);
                    const hint = p.key === 'free' && !hasFree ? 'Бесплатные облачные модели не настроены'
                      : p.key === 'local' && !ollamaOn ? 'Локальная модель (Ollama) не настроена'
                      : undefined;
                    const Icon = p.icon;
                    return (
                      <button
                        key={p.key}
                        type="button"
                        onClick={() => applyPreset(p.key)}
                        disabled={disabled || preset !== null}
                        title={hint}
                        style={{
                          display: 'flex', alignItems: 'flex-start', gap: 10, width: '100%', textAlign: 'left',
                          padding: '9px 12px', cursor: disabled || preset ? 'default' : 'pointer',
                          background: 'none', border: 'none',
                          borderTop: i === 0 ? 'none' : `1px solid ${C.borderLight}`,
                          opacity: disabled ? 0.5 : 1,
                        }}>
                        <Icon size={16} strokeWidth={ICON_STROKE} style={{ color: C.accent, flexShrink: 0, marginTop: 1 }} />
                        <div style={{ minWidth: 0, flex: 1 }}>
                          <div style={{ fontSize: FS.base, fontWeight: 600, color: C.textPrimary }}>{p.title}</div>
                          <div style={{ fontSize: FS.xs, color: C.textMuted, lineHeight: 1.4, marginTop: 1 }}>
                            {hint ?? p.desc}
                          </div>
                        </div>
                        {preset === p.key && (
                          <span style={{ fontSize: FS.xs, color: C.textMuted, flexShrink: 0 }}>Применяю…</span>
                        )}
                      </button>
                    );
                  })}
                </div>
              )}
            </div>

            <div style={{ display: 'flex', alignItems: 'center', gap: 6, margin: '10px 2px 2px' }}>
              <Zap size={12} strokeWidth={ICON_STROKE} style={{ color: C.accent, flexShrink: 0 }} />
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
                      defaultModel={defaultModel}
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
      )}
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
      <span style={{ fontSize: 11.5, color: C.textMuted, lineHeight: 1.35 }}>
        {subtitle}
      </span>
    </button>
  );
}

// Человекочитаемая подпись текущего выбора триггера.
// 'default' и legacy 'claude' — оба «По умолчанию» (модель по умолчанию для чатов).
function routeLabel(route: string | null | undefined, ollamaModel?: string, defaultModel?: string): string {
  const r = route ?? 'default';
  if (r === 'local') return `Локальная${ollamaModel ? ` · ${ollamaModel}` : ''}`;
  if (r === 'default' || r === 'claude') {
    return defaultModel ? `По умолчанию · ${modelLabel(defaultModel)}` : 'По умолчанию';
  }
  return modelLabel(r);
}

const PANEL_W = 320;
const PANEL_MAX_H = 340;

// Одна строка действия: название (+ кнопка сброса, если переопределено админом) слева,
// кастомный дропдаун-исполнитель справа — триггер-кнопка + всплывающая панель с карточками
// «Локальная»/«По умолчанию» и полным ModelPicker (карточки моделей с описаниями, как в чате).
// У отдельных действий (утренний бриф) перед дропдауном есть тумблер «выполнять ли вообще» —
// он приходит пропсами enabled/onToggleEnabled; остальные строки его не показывают.
function ActionRow({ action: a, first, busy, ollamaModel, defaultModel, models, onPick, onReset,
  enabled, onToggleEnabled, toggleBusy, toggleTitle }: {
  action: OllamaActionInfo;
  first: boolean;
  busy: boolean;
  ollamaModel?: string;
  defaultModel?: string;
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
  const route = a.route ?? 'default';
  // «По умолчанию» и legacy 'claude' — одинаковый выбор
  const isDefault = route === 'default' || route === 'claude';
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

  // ModelPicker value='' означает «По умолчанию» → маппим в route 'default'
  const pick = (v: string) => { onPick(v === '' ? 'default' : v); setOpen(false); };
  // Для подсветки в ModelPicker: конкретная модель — её id, иначе '' (карточка «По умолчанию»)
  const pickerValue = isDefault || route === 'local' ? '' : route;

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
            {routeLabel(a.route, ollamaModel, defaultModel)}
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
            <QuickOptionCard
              title="Локальная модель"
              subtitle={ollamaModel ? `Ollama · ${ollamaModel}` : 'не настроена'}
              active={route === 'local'}
              onClick={() => pick('local')}
            />
            <QuickOptionCard
              title="По умолчанию"
              subtitle={defaultModel ? modelLabel(defaultModel) : 'модель по умолчанию для чатов'}
              active={isDefault}
              onClick={() => pick('default')}
            />
            <div style={{ borderTop: `1px solid ${C.borderLight}`, margin: '2px 0' }} />
            <ModelPicker
              value={pickerValue}
              options={models}
              onChange={pick}
              collapsible={false}
              includeDirect
            />
          </div>
        )}
      </div>
    </div>
  );
}
