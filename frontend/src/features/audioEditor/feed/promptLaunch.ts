// Что запустит «Сгенерировать» в карточке «Текст для звука» (audio_suggest_prompt). Агент мог
// указать режим и модель — тогда запуск по ним, а не по полосе: кнопка под озвучкой не должна
// запускать «Обработка · Склеить», выбранную в панели (дизайн-ревью «Звука», M7). Без указаний —
// по полосе. Режим чужой нити сервер не наследует (AudioPrefsResolver: настройки нити — только при
// том же режиме), поэтому и здесь при смене режима нить не участвует — цена на кнопке совпадёт с
// котировкой.

import type { AudioCatalog, AudioMode, AudioModelInfo, AudioOp, AudioPrefs, AudioProvider, AudioThread } from '../api';
import { MODE_LABEL, OPS, opInfo } from '../ops';
import { priceLabel, resolveLaunch, type ResolvedLaunch } from '../strip/summary';

export interface PromptLaunch {
  launch: ResolvedLaunch;
  // Запуск не совпадает с полосой: кнопка называет, с чем пойдёт
  differs: boolean;
  // Явное в запрос котировки: null — как в полосе
  override: { mode: AudioMode; operation: AudioOp; provider: string | null; model: string | null } | null;
}

const norm = (s: string) => s.toLowerCase().replace(/[^a-z0-9а-яё]/g, '');

// Модель по слову агента: точное совпадение id или подписи, иначе id/подпись, содержащие слово.
// Только модели, умеющие операцию режима, и сперва у доступных поставщиков
export function findModel(catalog: AudioCatalog | null, query: string, mode: AudioMode):
  { provider: AudioProvider; model: AudioModelInfo; op: AudioOp } | null {
  const q = norm(query);
  if (!q || !catalog) return null;
  const modeOps = OPS.filter(o => o.mode === mode && o.field !== 'none').map(o => o.op);
  const all = [...catalog.providers].sort((a, b) => Number(b.available) - Number(a.available))
    .flatMap(p => p.models.map(m => ({ provider: p, model: m, op: modeOps.find(op => m.caps.ops.includes(op)) })))
    .filter((x): x is { provider: AudioProvider; model: AudioModelInfo; op: AudioOp } => !!x.op);
  return all.find(x => norm(x.model.id) === q || norm(x.model.label) === q)
    ?? all.find(x => norm(x.model.id).includes(q) || norm(x.model.label).includes(q))
    ?? null;
}

export function promptLaunch(
  thread: AudioThread | null, prefs: AudioPrefs, catalog: AudioCatalog | null, stripMode: AudioMode,
  card: { mode: AudioMode | null; model: string | null },
): PromptLaunch {
  const strip = resolveLaunch(thread, prefs, catalog, stripMode);
  const mode = card.mode ?? strip.mode;
  let launch = mode === strip.mode ? strip : resolveLaunch(null, prefs, catalog, mode);
  // Текст карточки некуда отдать операции без поля (склейка, шумодав) — берём первую с полем
  if (opInfo(launch.op)?.field === 'none' && card.mode) {
    const op = OPS.find(o => o.mode === mode && o.field !== 'none')!.op;
    launch = resolveLaunch(null, { ...prefs, [mode]: { ...(prefs[mode] ?? {}), operation: op } } as AudioPrefs, catalog, mode);
  }
  const hit = card.model ? findModel(catalog, card.model, mode) : null;
  if (hit && hit.model.id !== launch.model?.id) {
    const op = hit.model.caps.ops.includes(launch.op) ? launch.op : hit.op;
    launch = {
      ...launch, op, provider: hit.provider, model: hit.model, auto: false,
      price: priceLabel(hit.provider, hit.model), heavy: !!hit.model.caps.heavyOps?.includes(op),
    };
  }
  const differs = launch.mode !== strip.mode || launch.op !== strip.op || launch.model?.id !== strip.model?.id;
  return {
    launch, differs,
    override: differs
      ? { mode: launch.mode, operation: launch.op, provider: launch.provider?.key ?? null, model: launch.auto ? null : launch.model?.id ?? null }
      : null,
  };
}

// «Озвучить · Qwen3-TTS 1.7B · бесплатно» — когда запуск не как в полосе; иначе «Сгенерировать · цена»
export function promptRunLabel(p: PromptLaunch): string {
  const L = p.launch;
  if (!p.differs) return L.price ? `Сгенерировать · ${L.price}` : 'Сгенерировать';
  return [opInfo(L.op)?.run ?? MODE_LABEL[L.mode], L.model?.label, L.price].filter(Boolean).join(' · ');
}
