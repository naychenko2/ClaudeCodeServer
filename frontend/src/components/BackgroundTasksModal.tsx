import { useEffect, useMemo, useState } from 'react';
import { Modal } from './ui';
import { api } from '../lib/api';
import { C, FONT, MODAL_W } from '../lib/design';
import { useModels, modelProvider, providerLabel, type ModelOption } from '../lib/models';
import type { OllamaUsageInfo, OllamaActionInfo } from '../types';

interface Props {
  onClose: () => void;
}

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
          <div style={{ fontSize: 12, color: C.textMuted, lineHeight: 1.5, marginBottom: 4 }}>
            Каждая задача начинается с выбранного исполнителя; если он не ответил — пробуется локальная
            модель, затем Claude. Бесплатные модели OpenRouter доступны в двух вариантах: <b>прямой вызов</b>
            {' '}(быстрее, для фоновых задач) и <b>через провайдера</b> (единообразно с агентским режимом).
          </div>
          {!info.enabled && (
            <div style={{ padding: '9px 11px', margin: '10px 0', borderRadius: 8, fontSize: 12, lineHeight: 1.5,
              color: C.textSecondary, background: C.bgInset, border: `1px solid ${C.border}` }}>
              Локальная модель не настроена (<span style={{ fontFamily: FONT.mono }}>Ollama:Model</span>) —
              шаг локали в цепочке пропускается. Бесплатные и платные модели по-прежнему доступны.
            </div>
          )}

          {error && (
            <div style={{ margin: '8px 0', padding: '7px 10px', borderRadius: 6, fontSize: 12,
              color: C.dangerText, background: C.dangerBg, border: `1px solid ${C.dangerBorder}` }}>
              {error}
            </div>
          )}

          {groups.map(g => (
            <div key={g}>
              <div style={{ fontFamily: FONT.sans, fontSize: 12.5, fontWeight: 600, color: C.textHeading,
                margin: '14px 0 6px' }}>{g}</div>
              {actions.filter(a => a.group === g).map(a => (
                <div key={a.key} style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between',
                  gap: 10, padding: '5px 0' }}>
                  <span style={{ fontSize: 12.5, color: C.textSecondary }}>
                    {a.title}
                    {a.source === 'admin' && (
                      <button
                        onClick={() => reset(a)}
                        disabled={busy === a.key}
                        title="Вернуть значение из конфигурации"
                        style={{ marginLeft: 6, padding: '0 5px', fontSize: 10.5, fontFamily: FONT.sans,
                          color: C.textMuted, background: 'transparent', border: `1px solid ${C.border}`,
                          borderRadius: 20, cursor: busy === a.key ? 'default' : 'pointer' }}>
                        переопределено ✕
                      </button>
                    )}
                  </span>
                  <select
                    value={a.route ?? 'claude'}
                    onChange={e => pick(a, e.target.value)}
                    disabled={busy === a.key}
                    title="С чего начинать действие; дальше — локальная модель, затем Claude"
                    style={{ flexShrink: 0, maxWidth: 250, fontFamily: FONT.sans, fontSize: 11.5,
                      padding: '3px 7px', borderRadius: 6, cursor: busy === a.key ? 'default' : 'pointer',
                      opacity: busy === a.key ? 0.5 : 1,
                      color: a.routedToOllama ? C.accent : C.textSecondary,
                      background: C.bgWhite, border: `1px solid ${a.routedToOllama ? C.accent : C.border}` }}>
                    <option value="local">Локальная{info.model ? ` · ${info.model}` : ''}</option>
                    <option value="claude">Claude (модель по умолчанию)</option>
                    {modelGroups.map(([provider, opts]) => (
                      <optgroup key={provider} label={providerLabel(provider)}>
                        {opts.map(o => <option key={o.value} value={o.value}>{o.label}</option>)}
                      </optgroup>
                    ))}
                  </select>
                </div>
              ))}
            </div>
          ))}

          <div style={{ fontSize: 11, color: C.textMuted, marginTop: 14, lineHeight: 1.5 }}>
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
