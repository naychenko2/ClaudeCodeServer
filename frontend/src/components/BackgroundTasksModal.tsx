import { useEffect, useLayoutEffect, useRef, useState } from 'react';
import { ChevronDown, RotateCcw, Sparkles, Gift, Cpu, Zap, Scale } from 'lucide-react';
import { Modal, IconButton } from './ui';
import { ModelPicker } from './ModelPicker';
import { ICON_SIZE, ICON_STROKE } from './ui/icons';
import { api } from '../lib/api';
import { C, FONT, FS, R, SHADOW, Z, MODAL_W } from '../lib/design';
import { useModels, modelLabel, type ModelOption } from '../lib/models';
import type { OllamaUsageInfo, OllamaActionInfo } from '../types';

interface Props {
  onClose: () => void;
}

// Ненавязчивая hover-подсветка строки действия — через инжектимый класс (как в IconButton),
// без per-row состояния (строк в списке много, группами по разделам).
const ROW_CLASS = 'cc-bgtask-row';
if (typeof document !== 'undefined' && !document.getElementById('cc-bgtask-row-style')) {
  const el = document.createElement('style');
  el.id = 'cc-bgtask-row-style';
  el.textContent = `.${ROW_CLASS}:hover{background:${C.bgSelected};}`;
  document.head.appendChild(el);
}

const groupHeaderStyle: React.CSSProperties = {
  fontFamily: FONT.sans, fontSize: FS.xs, fontWeight: 700, color: C.textMuted,
  textTransform: 'uppercase', letterSpacing: '0.06em', margin: '18px 2px 6px',
};

// Пресеты автоподбора: сервер проставляет исполнителя всем действиям по единому правилу.
type PresetKey = 'recommended' | 'balanced' | 'free' | 'local';
const PRESETS: { key: PresetKey; icon: typeof Sparkles; title: string; desc: string }[] = [
  { key: 'recommended', icon: Sparkles, title: 'Рекомендованное',
    desc: 'Лучшее качество: локаль и Claude под сложность задачи (могут быть платные)' },
  { key: 'balanced', icon: Scale, title: 'Сбалансированный',
    desc: 'По сложности: простое — на локальной модели, среднее — бесплатные облачные, тяжёлое — Claude' },
  { key: 'free', icon: Gift, title: 'Только бесплатные',
    desc: 'Бесплатные облачные модели OpenRouter — без затрат' },
  { key: 'local', icon: Cpu, title: 'Локальные',
    desc: 'Локальная модель, где подходит; для сложных задач — бесплатная облачная' },
];

// Настройка исполнителя каждого фонового ИИ-действия (теги, заголовки, сводки, память и т.д.):
// локальная модель (Ollama), бесплатная модель OpenRouter (прямой вызов или через провайдера),
// конкретная модель любого провайдера или Claude. Дальше действие идёт по цепочке
// «выбранное → локаль → claude». Настройка серверная и общая для всех — только админ.
export function BackgroundTasksModal({ onClose }: Props) {
  const [info, setInfo] = useState<OllamaUsageInfo | undefined>(undefined);
  const [busy, setBusy] = useState<string | null>(null);
  const [preset, setPreset] = useState<PresetKey | null>(null);
  const [error, setError] = useState<string | null>(null);
  const models = useModels();

  useEffect(() => {
    let cancelled = false;
    api.usage.get()
      .then(d => { if (!cancelled) setInfo(d.ollama ?? { enabled: false, actions: [] }); })
      .catch(() => { if (!cancelled) setInfo({ enabled: false, actions: [] }); });
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

  return (
    <Modal
      title="Фоновые задачи"
      subtitle="Кто выполняет автоматические фоновые ИИ-задачи. Настройка общая и применяется сразу."
      width={MODAL_W.form}
      onClose={onClose}
    >
      {info === undefined ? (
        <div style={{ color: C.textMuted, fontSize: 14, padding: '8px 0' }}>Загрузка…</div>
      ) : (
        <>
          {/* Автоподбор: один клик проставляет исполнителя всем действиям */}
          <div style={{ display: 'grid', gap: 7 }}>
            {PRESETS.map(p => {
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
                    display: 'flex', alignItems: 'center', gap: 11, textAlign: 'left',
                    padding: '10px 12px', borderRadius: R.lg, background: C.bgWhite,
                    border: `1px solid ${C.border}`,
                    cursor: disabled || preset ? 'default' : 'pointer',
                    opacity: disabled ? 0.5 : 1, transition: 'border-color 0.15s, background 0.15s',
                  }}
                >
                  <Icon size={18} strokeWidth={ICON_STROKE} style={{ color: C.accent, flexShrink: 0 }} />
                  <div style={{ minWidth: 0, flex: 1 }}>
                    <div style={{ fontSize: FS.sm, fontWeight: 600, color: C.textPrimary }}>{p.title}</div>
                    <div style={{ fontSize: 11, color: C.textMuted, lineHeight: 1.4, marginTop: 1 }}>
                      {hint ?? p.desc}
                    </div>
                  </div>
                  {preset === p.key && (
                    <span style={{ fontSize: 11, color: C.textMuted, flexShrink: 0 }}>Применяю…</span>
                  )}
                </button>
              );
            })}
          </div>

          <div style={{ display: 'flex', alignItems: 'center', gap: 6, margin: '14px 2px 2px' }}>
            <Zap size={12} strokeWidth={ICON_STROKE} style={{ color: C.accent, flexShrink: 0 }} />
            <span style={{ fontSize: 11, color: C.textMuted, lineHeight: 1.4 }}>
              — задаче нужна сильная модель, локальная не подойдёт: для неё подбирается Claude или облачная.
            </span>
          </div>

          {!info.enabled && (
            <div style={{ padding: '9px 11px', margin: '10px 0 0', borderRadius: R.md, fontSize: 12, lineHeight: 1.5,
              color: C.textSecondary, background: C.bgInset, border: `1px solid ${C.border}` }}>
              Локальная модель не настроена — шаг локали в цепочке пропускается.
            </div>
          )}

          {error && (
            <div style={{ margin: '10px 0 0', padding: '7px 10px', borderRadius: R.sm, fontSize: 12,
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
                    models={models}
                    onPick={route => pick(a, route)}
                    onReset={() => reset(a)}
                  />
                ))}
              </div>
            </div>
          ))}
        </>
      )}
    </Modal>
  );
}

// Компактная карточка-опция «Локальная модель» / «Claude» наверху панели — тот же стиль
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
      <span style={{ fontSize: 13, fontWeight: 600, color: active ? C.textHeading : C.textPrimary, fontFamily: FONT.sans }}>
        {title}
      </span>
      <span style={{ fontSize: 11.5, color: C.textMuted, lineHeight: 1.35 }}>
        {subtitle}
      </span>
    </button>
  );
}

// Человекочитаемая подпись текущего выбора триггера
function routeLabel(route: string | null | undefined, ollamaModel?: string): string {
  const r = route ?? 'claude';
  if (r === 'local') return `Локальная${ollamaModel ? ` · ${ollamaModel}` : ''}`;
  if (r === 'claude') return 'Claude';
  return modelLabel(r);
}

const PANEL_W = 320;
const PANEL_MAX_H = 340;

// Одна строка действия: название (+ кнопка сброса, если переопределено админом) слева,
// кастомный дропдаун-исполнитель справа — триггер-кнопка + всплывающая панель с карточками
// «Локальная»/«Claude» и полным ModelPicker (карточки моделей с описаниями, как в чате).
function ActionRow({ action: a, first, busy, ollamaModel, models, onPick, onReset }: {
  action: OllamaActionInfo;
  first: boolean;
  busy: boolean;
  ollamaModel?: string;
  models: ModelOption[];
  onPick: (route: string) => void;
  onReset: () => void;
}) {
  const [open, setOpen] = useState(false);
  const [pos, setPos] = useState<{ top: number; left: number; maxHeight: number } | null>(null);
  const rootRef = useRef<HTMLDivElement>(null);
  const triggerRef = useRef<HTMLButtonElement>(null);
  const overridden = a.source === 'admin';
  const selectColor = a.routedToOllama ? C.accent : C.textSecondary;
  const route = a.route ?? 'claude';
  // «Сильному» действию выбрана локаль — по факту пойдёт фолбэк на Claude (локаль пропускается)
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

  const pick = (v: string) => { onPick(v); setOpen(false); };

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
              ? 'Нужна сильная модель — локальная будет пропущена, пойдёт Claude'
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

      <div ref={rootRef} style={{ position: 'relative', flexShrink: 0, display: 'flex', alignItems: 'center' }}>
        <button
          ref={triggerRef}
          type="button"
          onClick={() => setOpen(o => !o)}
          disabled={busy}
          title="С чего начинать действие; дальше — локальная модель, затем Claude"
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
            {routeLabel(a.route, ollamaModel)}
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
              title="Claude"
              subtitle="модель по умолчанию"
              active={route === 'claude'}
              onClick={() => pick('claude')}
            />
            <div style={{ borderTop: `1px solid ${C.borderLight}`, margin: '2px 0' }} />
            <ModelPicker
              value={a.route ?? ''}
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
