import { useEffect, useMemo, useState } from 'react';
import { ChevronDown, RotateCcw } from 'lucide-react';
import { Modal, IconButton } from './ui';
import { ICON_SIZE, ICON_STROKE } from './ui/icons';
import { api } from '../lib/api';
import { C, FONT, FS, R, SHADOW, MODAL_W } from '../lib/design';
import { useModels, modelProvider, providerLabel, type ModelOption } from '../lib/models';
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

// Настройка исполнителя каждого фонового ИИ-действия (теги, заголовки, сводки, память и т.д.):
// локальная модель (Ollama), бесплатная модель OpenRouter (прямой вызов или через провайдера),
// конкретная модель любого провайдера или Claude. Дальше действие идёт по цепочке
// «выбранное → локаль → claude». Настройка серверная и общая для всех — только админ.
export function BackgroundTasksModal({ onClose }: Props) {
  const [info, setInfo] = useState<OllamaUsageInfo | undefined>(undefined);
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const models = useModels();

  useEffect(() => {
    let cancelled = false;
    api.usage.get()
      .then(d => { if (!cancelled) setInfo(d.ollama ?? { enabled: false, actions: [] }); })
      .catch(() => { if (!cancelled) setInfo({ enabled: false, actions: [] }); });
    return () => { cancelled = true; };
  }, []);

  // Модели по провайдерам для селектора; Claude — первой группой (как в ModelPicker),
  // прямой адаптер OpenRouter (openrouter-direct) — сразу за своим провайдером
  const modelGroups = useMemo(() => {
    const by = new Map<string, ModelOption[]>();
    for (const o of models) {
      const p = o.provider ?? modelProvider(o.value);
      (by.get(p) ?? by.set(p, []).get(p)!).push(o);
    }
    return [...by.entries()].sort(([a], [b]) => (a === 'claude' ? -1 : b === 'claude' ? 1 : 0));
  }, [models]);

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
      subtitle="Кто выполняет автоматические ИИ-задачи в фоне. Выбор общий для всех пользователей и применяется сразу."
      width={MODAL_W.form}
      onClose={onClose}
    >
      {info === undefined ? (
        <div style={{ color: C.textMuted, fontSize: 14, padding: '8px 0' }}>Загрузка…</div>
      ) : (
        <>
          <div style={{ fontSize: 12, color: C.textMuted, lineHeight: 1.5 }}>
            Каждая задача начинается с выбранного исполнителя; если он не ответил — пробуется локальная
            модель, затем Claude. Бесплатные модели OpenRouter доступны в двух вариантах: <b>прямой вызов</b>
            {' '}(быстрее, для фоновых задач) и <b>через провайдера</b> (единообразно с агентским режимом).
          </div>
          {!info.enabled && (
            <div style={{ padding: '9px 11px', margin: '10px 0 0', borderRadius: R.md, fontSize: 12, lineHeight: 1.5,
              color: C.textSecondary, background: C.bgInset, border: `1px solid ${C.border}` }}>
              Локальная модель не настроена (<span style={{ fontFamily: FONT.mono }}>Ollama:Model</span>) —
              шаг локали в цепочке пропускается. Бесплатные и платные модели по-прежнему доступны.
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
                    modelGroups={modelGroups}
                    onPick={route => pick(a, route)}
                    onReset={() => reset(a)}
                  />
                ))}
              </div>
            </div>
          ))}

          <div style={{ fontSize: 11, color: C.textMuted, marginTop: 16, lineHeight: 1.5 }}>
            «Лицо продукта» (генерация навыков, утренний бриф, черновик персоны) и сводка «Что нового»
            по умолчанию остаются на Claude — там важны качество и длинный контекст, но их тоже можно
            перевести на бесплатную модель. Дефолты задаются в{' '}
            <span style={{ fontFamily: FONT.mono }}>Ollama:Actions</span> (appsettings.Local.json).
          </div>
        </>
      )}
    </Modal>
  );
}

// Одна строка действия: название (+ кнопка сброса, если переопределено админом) слева,
// стилизованный селектор исполнителя справа.
function ActionRow({ action: a, first, busy, ollamaModel, modelGroups, onPick, onReset }: {
  action: OllamaActionInfo;
  first: boolean;
  busy: boolean;
  ollamaModel?: string;
  modelGroups: [string, ModelOption[]][];
  onPick: (route: string) => void;
  onReset: () => void;
}) {
  const [selectFocused, setSelectFocused] = useState(false);
  const overridden = a.source === 'admin';
  const selectColor = a.routedToOllama ? C.accent : C.textSecondary;

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

      <div style={{ position: 'relative', flexShrink: 0, display: 'flex', alignItems: 'center' }}>
        <select
          value={a.route ?? 'claude'}
          onChange={e => onPick(e.target.value)}
          onFocus={() => setSelectFocused(true)}
          onBlur={() => setSelectFocused(false)}
          disabled={busy}
          title="С чего начинать действие; дальше — локальная модель, затем Claude"
          style={{
            appearance: 'none', WebkitAppearance: 'none', MozAppearance: 'none',
            maxWidth: 230, fontFamily: FONT.sans, fontSize: FS.xs,
            padding: '4px 24px 4px 9px', borderRadius: R.md,
            cursor: busy ? 'default' : 'pointer', opacity: busy ? 0.5 : 1,
            color: selectColor, background: C.bgWhite,
            border: `1px solid ${selectFocused ? C.accent : (a.routedToOllama ? C.accent : C.border)}`,
            outline: 'none', transition: 'border-color 0.15s, box-shadow 0.15s',
            boxShadow: selectFocused ? SHADOW.focus : 'none',
          }}
        >
          <option value="local">Локальная{ollamaModel ? ` · ${ollamaModel}` : ''}</option>
          <option value="claude">Claude (модель по умолчанию)</option>
          {modelGroups.map(([provider, opts]) => (
            <optgroup key={provider} label={providerLabel(provider)}>
              {opts.map(o => <option key={o.value} value={o.value}>{o.label}</option>)}
            </optgroup>
          ))}
        </select>
        <ChevronDown
          size={ICON_SIZE.xs} strokeWidth={ICON_STROKE}
          style={{ position: 'absolute', right: 7, pointerEvents: 'none', color: selectColor }}
        />
      </div>
    </div>
  );
}
