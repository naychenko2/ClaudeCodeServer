// Чистая модель вкладки «Сцена» (ADR-022, макет v7): настройки сцены → префы → умолчание модели,
// причины, по которым снять нельзя, строки «Исполнителя», цена в две строки и сводка полосы.
// Без React и без стора — проверяется юнит-тестами.

import type { ExecutorRow } from 'aihome_shell/kit';
import type {
  FrameRef, VideoCatalog, VideoModelInfo, VideoPrefs, VideoPrice, VideoProvider, VideoQuote, VideoScene, VideoSceneSettings,
} from '../api';

export const TEXT_MAX = 2000;
export const LOCAL_PROVIDER = 'local';
// Один текст отказа: что нельзя, почему и что делать (сервер в LocalVideoEngine говорит то же по смыслу)
export const PERSONAL_LOCAL_REASON = 'Локальные модели работают только в чате проекта: клипу некуда лечь. Выберите другого поставщика или откройте проект';

export interface ResolvedScene {
  provider: VideoProvider | null;
  model: VideoModelInfo | null;
  // Выбор «Авто»: поставщик не закреплён
  auto: boolean;
  durationSec: number;
  aspect: string;
  sound: boolean;
  count: number;
  text: string;
  frameA: FrameRef | null;
  frameB: FrameRef | null;
}

const first = <T,>(...v: (T | undefined | null)[]): T | undefined => v.find(x => x !== undefined && x !== null);

export const findProvider = (catalog: VideoCatalog | null, key: string | undefined | null) =>
  (key && catalog?.providers.find(p => p.key === key)) || null;

export const findModel = (p: VideoProvider | null, id: string | undefined | null) =>
  (id && p?.models.find(m => m.id === id)) || null;

// Подпись модели для человека: «Veo 3.1», а не «veo-3.1». Нет в каталоге — как пришло.
// Один источник и для карточки человека, и для карточки агента
export function modelLabel(catalog: VideoCatalog | null, providerKey: string | null | undefined, id: string | null | undefined): string | null {
  if (!id) return null;
  const own = findModel(findProvider(catalog, providerKey), id);
  if (own) return own.label;
  for (const p of catalog?.providers ?? []) {
    const m = p.models.find(x => x.id === id || x.label === id);
    if (m) return m.label;
  }
  return id;
}

// Ближайшее значение из списка, который понимает модель
export function snapTo(values: readonly number[], want: number | undefined): number | undefined {
  if (!values.length) return want;
  if (want === undefined) return values[0];
  return values.reduce((b, v) => (Math.abs(v - want) < Math.abs(b - want) ? v : b), values[0]);
}

// Настройки, которые видит человек: сцена → несохранённая правка → префы области → умолчание модели
export function resolveScene(
  scene: VideoScene | null, patch: Partial<VideoSceneSettings> | null, prefs: VideoPrefs, catalog: VideoCatalog | null,
): ResolvedScene {
  const s: Partial<VideoSceneSettings> = { ...(scene?.settings ?? {}), ...(patch ?? {}) };
  const providerKey = first(s.provider, prefs.provider);
  const provider = findProvider(catalog, providerKey);
  const modelId = first(s.model, prefs.model);
  const auto = !provider || !modelId || modelId === catalog?.autoModelId;
  const model = auto ? null : findModel(provider, modelId);
  const duration = snapTo(model?.durations ?? [], first(s.durationSec, prefs.durationSec)) ?? first(s.durationSec, prefs.durationSec) ?? 5;
  const aspectWant = first(s.aspect, prefs.aspect);
  const aspect = model && aspectWant && !model.aspects.includes(aspectWant) ? model.aspects[0] ?? aspectWant : aspectWant ?? model?.aspects[0] ?? '16:9';
  const soundWant = first(s.sound, prefs.sound) ?? false;
  return {
    provider: auto ? null : provider, model, auto, durationSec: duration, aspect,
    sound: model ? model.sound && soundWant : soundWant,
    count: Math.min(Math.max(first(s.count, prefs.count) ?? 1, 1), catalog?.maxCount ?? 4),
    text: s.text ?? '',
    frameA: s.frameA ?? null,
    frameB: s.frameB ?? null,
  };
}

// Что уйдёт в PUT настроек сцены: человеческие значения целиком (сервер заменяет настройки)
export function settingsOf(r: ResolvedScene): VideoSceneSettings {
  return {
    ...(r.frameA ? { frameA: r.frameA } : {}),
    ...(r.frameB ? { frameB: r.frameB } : {}),
    text: r.text,
    ...(r.provider && r.model ? { provider: r.provider.key, model: r.model.id } : {}),
    durationSec: r.durationSec,
    aspect: r.aspect,
    sound: r.sound,
    count: r.count,
  };
}

export const frameKey = (f: FrameRef | null | undefined): string =>
  !f ? '' : f.kind === 'image' ? `image:${f.threadId}:${f.versionId}` : `file:${f.path}`;

export const frameName = (f: FrameRef | null | undefined): string => {
  if (!f) return '';
  return f.kind === 'file' ? f.fileName ?? f.path.split('/').pop() ?? f.path : f.versionId === 'origin' ? 'исходник' : 'версия';
};

// Причина, по которой снять нельзя (макет v7, «Тексты»); null — можно
export function runReason(p: {
  sessionId: string | null; r: ResolvedScene; personal: boolean; providerOk: boolean; providerReason?: string | null;
  quoteError?: string | null; running: boolean; needsLastFrame?: boolean;
}): string | null {
  if (!p.sessionId) return 'Сначала начните чат';
  if (p.personal && p.r.provider?.key === LOCAL_PROVIDER) return PERSONAL_LOCAL_REASON;
  // Нечем снимать — это главная причина, кадры тут вторичны
  if (!p.providerOk) return p.providerReason || 'Поставщик недоступен';
  if (!p.r.frameA && !p.r.frameB) return 'Нужны оба кадра: выберите кадр A и кадр B выше';
  if (!p.r.frameA) return 'Нужен кадр A: выберите его выше';
  if (!p.r.frameB) return 'Нужен кадр B: выберите его выше';
  if (!p.r.text.trim()) return 'Напишите текст сцены — что происходит от кадра A к кадру B';
  if (p.quoteError) return p.quoteError;
  return null;
}

const money = (n: number) => (n >= 10 ? n.toFixed(0) : n.toFixed(2));

// Цена в две строки: «≈ $3.20» / «2 × $1.60 за 8 с»; у local — «Бесплатно» / «~31 мин · очередь GPU: 0»
export function priceLines(q: VideoQuote | null, count: number, durationSec: number): [string, string] | undefined {
  if (!q) return undefined;
  const p: VideoPrice = q.price;
  const per = `за ${q.durationSec || durationSec} с`;
  if (p.unit === 'free') {
    const eta = p.eta ? `~${p.eta >= 90 ? `${Math.round(p.eta / 60)} мин` : `${p.eta} с`}` : 'без оплаты';
    return ['Бесплатно', `${eta}${p.queueLength !== undefined ? ` · очередь GPU: ${p.queueLength}` : ''}`];
  }
  if (p.amount === undefined || p.amount === null) return ['Цена станет известна после запуска', per];
  const cur = p.unit === 'usd' ? '$' : '';
  const suffix = p.unit === 'credits' ? ' кр' : '';
  const total = p.unit === 'credits' ? `${money(p.amount)} ${plural(Math.round(p.amount), 'кредит', 'кредита', 'кредитов')}` : `${p.approx ? '≈ ' : ''}${cur}${money(p.amount)}`;
  const one = count > 1 ? `${count} × ${cur}${money(p.amount / count)}${suffix} ${per}` : per;
  return [total, one];
}

export function plural(n: number, one: string, few: string, many: string): string {
  const m10 = n % 10;
  const m100 = n % 100;
  if (m10 === 1 && m100 !== 11) return one;
  if (m10 >= 2 && m10 <= 4 && (m100 < 12 || m100 > 14)) return few;
  return many;
}

const unitMark = (u: string) => (u === 'usd' ? '$' : u === 'credits' ? 'кредиты' : u === 'free' ? 'бесплатно' : u);

// Строки «Исполнителя»: Авто, свои модели, облако. Модель, не снимающая в нужных пропорциях или не
// знающая «кадр → кадр», серая с причиной; у личного чата local закрыт замком
export function executorRows(catalog: VideoCatalog, personal: boolean, aspect: string, autoSub: string): ExecutorRow[] {
  const rows: ExecutorRow[] = [{ id: 'auto', group: 'auto', name: 'Авто', sub: autoSub, price: 'по цене модели' }];
  for (const p of catalog.providers) {
    const local = p.key === LOCAL_PROVIDER;
    for (const m of p.models) {
      let reason: string | undefined;
      if (local && personal) reason = PERSONAL_LOCAL_REASON;
      else if (!p.available) reason = p.reason ?? 'Поставщик недоступен';
      else if (!m.lastFrame) reason = 'Не умеет «кадр A → кадр B»';
      else if (m.aspects.length && !m.aspects.includes(aspect)) reason = `Не снимает в пропорциях ${aspect}`;
      rows.push({
        id: `${p.key}|${m.id}`, group: local ? 'local' : 'cloud', name: m.label,
        sub: [p.label, m.durations.length ? `${m.durations.join(' / ')} с` : '', m.sound ? 'со звуком' : 'без звука'].filter(Boolean).join(' · '),
        price: local ? 'бесплатно' : unitMark(p.priceUnit),
        ...(reason ? { disabled: true, reason } : {}),
        ...(local && personal ? { locked: true } : {}),
      });
    }
  }
  return rows;
}

export const rowId = (r: ResolvedScene) => (r.auto || !r.provider || !r.model ? 'auto' : `${r.provider.key}|${r.model.id}`);

export const pickRow = (id: string): { provider?: string; model?: string } => {
  if (id === 'auto') return {};
  const [provider, model] = id.split('|');
  return { provider, model };
};

export const sceneNumber = (s: VideoScene) => s.name;

// «0:32»
export function clock(sec: number): string {
  const t = Math.max(0, Math.round(sec));
  return `${Math.floor(t / 60)}:${String(t % 60).padStart(2, '0')}`;
}

// «Сцена 5 · Veo 3.1 · 8 с · 2 вар. · ≈ $3.20»
export function sceneSummary(scene: VideoScene | null, r: ResolvedScene, price?: string, short = false): string {
  const parts = [scene?.name ?? 'Новая сцена'];
  if (r.model) parts.push(r.model.label); else parts.push('Авто');
  parts.push(`${r.durationSec} с`);
  if (r.count > 1) parts.push(`${r.count} вар.`);
  if (price) parts.push(price);
  return short ? parts.slice(0, 1).concat(price ? [price] : []).join(' · ') : parts.join(' · ');
}

// Признак «снять нельзя»: нет кадра или текста
export const sceneNotReady = (r: ResolvedScene) => !r.frameA || !r.frameB || !r.text.trim();

export const hasClip = (s: VideoScene | null) => !!s && s.versions.length > 0;

// «Кадр изменён после съёмки — переснимите» и друзья
export function staleNotes(s: VideoScene | null): string[] {
  const st = s?.stale;
  if (!s || !st || !s.versions.length) return [];
  const out: string[] = [];
  if (st.frameA) out.push('Кадр A изменён после съёмки — переснимите');
  if (st.frameB) out.push('Кадр B изменён после съёмки — переснимите');
  if (st.text) out.push('Текст изменён после съёмки — переснимите');
  return out;
}

export const currentVersion = (s: VideoScene | null) =>
  s?.versions.find(v => v.versionId === s.currentVersionId) ?? s?.versions[s.versions.length - 1] ?? null;
