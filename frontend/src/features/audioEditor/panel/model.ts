// Модель панели «Звук» (макет docs/mockups/audio-editor-v2-proposal.md, «Вкладка «Настройки»»):
// операции режима, опции поставщиков и моделей с причинами, итог настроек по цепочке нити, цена в
// две строки и причина, по которой запуск невозможен. Чистые функции — под юнит-тестом.

import type {
  AudioCatalog, AudioMode, AudioModelInfo, AudioOp, AudioParamField, AudioParamSchema, AudioPrefs, AudioProvider,
  AudioOpInputs, AudioQuote, AudioThread, AudioThreadSettings,
} from '../api';
import { defaultOp, isNoAi, NO_AI_OPS, OPS, opInfo, type OpInfo } from '../ops';
import { resolveLaunch, type ResolvedLaunch } from '../strip/summary';
import type { AudioSelection } from '../player/selection';
import { LIBRARY_VOICE_OPS, projectInputs, toServerInputs, type PanelInputs } from './inputs';
import { musicReason, type MusicInputs } from './music';

// Правки без ИИ: поставщика и модели у них нет, бегут ffmpeg на сервере
export { NO_AI_OPS, isNoAi };

// Громкость, затухание и нормализация живут внутри «Обрезки и громкости», сведение стемов — в карточке
const HIDDEN_OPS: ReadonlySet<AudioOp> = new Set(['gainFade', 'normalize', 'mixStems']);
// Источник голоса озвучки — переключатель внутри «Озвучить», а не отдельные пилюли
export const SPEAK_SOURCES: readonly AudioOp[] = ['speak', 'designVoice', 'cloneVoice'];
export const SOURCE_LABEL: Partial<Record<AudioOp, string>> = {
  speak: 'Готовый диктор', designVoice: 'По описанию', cloneVoice: 'Образец',
};

export function panelOps(mode: AudioMode): OpInfo[] {
  return OPS.filter(o => o.mode === mode && !HIDDEN_OPS.has(o.op) && (o.op === 'speak' || !SPEAK_SOURCES.includes(o.op)));
}

// Пилюля, которая подсвечена для операции: источники озвучки — под «Озвучить»
export const pillOf = (op: AudioOp): AudioOp => (SPEAK_SOURCES.includes(op) ? 'speak' : op);

// Операции, которым нужен готовый звук на входе (версия нити)
const NEEDS_SOURCE: ReadonlySet<AudioOp> = new Set([
  'convertVoice', 'cover', 'repaint', 'outpaint', 'extract', 'lego', 'complete',
  'separate', 'denoise', 'upsample', 'master', 'transcribe', 'align', 'toMidi', 'trim', 'gainFade', 'normalize', 'mixStems',
]);
export const needsSource = (op: AudioOp) => NEEDS_SOURCE.has(op);

const supports = (p: AudioProvider, op: AudioOp) => p.models.some(m => m.caps.ops.includes(op));

// ── Итог настроек ──

export interface PanelState extends ResolvedLaunch {
  // Выбор человека как есть (null — берётся по цепочке): по нему подсвечиваем «Авто»
  providerKey: string | null;
  modelId: string;
  fields: Record<string, unknown>;
  // Входы операции на сервере: у нити с настройками — её, иначе — префов режима
  inputs: AudioOpInputs | null;
}

// Цепочка «настройки нити → префы режима → умолчание», как AudioPrefsResolver на бэкенде
export function resolvePanel(
  thread: AudioThread | null, prefs: AudioPrefs, catalog: AudioCatalog | null, fallbackMode: AudioMode,
): PanelState {
  const L = resolveLaunch(thread, prefs, catalog, fallbackMode);
  const own = thread?.settings ?? null;
  const p = prefs[L.mode];
  return {
    ...L,
    providerKey: own?.provider ?? p?.provider ?? null,
    modelId: own?.model ?? p?.model ?? catalog?.autoModelId ?? 'auto',
    fields: { ...(p?.fields ?? {}), ...(own?.fields ?? {}) },
    inputs: (own ? own.inputs : p?.inputs) ?? null,
  };
}

export type SettingsPatch = Partial<Pick<AudioThreadSettings, 'mode' | 'operation' | 'provider' | 'model' | 'count'>> & {
  fields?: Record<string, unknown>;
  // undefined — как были (при смене операции — только нужные новой); null — снять
  inputs?: AudioOpInputs | null;
};

// Следующие настройки нити. Смена операции, поставщика или модели сбрасывает то, что от них
// зависит: иначе поля чужой модели ушли бы в params и сервер отказал бы «неизвестный параметр».
// Смена режима поднимает запомненный выбор нового режима из его префов: пустые поля без нити
// ушли бы PUT'ом в те же префы и затёрли бы выбор человека.
// Входы едут всегда (PUT их заменяет), при смене операции — только те, что нужны новой
export function nextSettings(
  cur: PanelState, patch: SettingsPatch, prefs?: AudioPrefs | null, catalog?: AudioCatalog | null,
): AudioThreadSettings {
  const base: AudioThreadSettings = {
    mode: cur.mode, operation: cur.op, provider: cur.providerKey, model: cur.modelId, fields: cur.fields, count: cur.count,
    inputs: cur.inputs,
  };
  const inputsFor = (op: AudioOp) => projectInputs(patch.inputs !== undefined ? patch.inputs : cur.inputs, op);
  if (patch.mode && patch.mode !== cur.mode) {
    const p = prefs?.[patch.mode] ?? null;
    const remembered = p?.operation && opInfo(p.operation)?.mode === patch.mode ? p.operation : defaultOp(patch.mode);
    // Операция вместе с режимом — просьба карточки («Перегенерировать кусок» из режима «Голос»)
    const operation = patch.operation && opInfo(patch.operation)?.mode === patch.mode ? patch.operation : remembered;
    const provider = p?.provider ?? null;
    // Модель и её поля — только если они про ту же операцию и модель у поставщика её умеет
    let model = operation === remembered ? p?.model ?? null : null;
    const known = catalog?.providers.find(x => x.key === provider);
    if (model && model !== (catalog?.autoModelId ?? 'auto') && known
      && !known.models.some(m => m.id === model && m.caps.ops.includes(operation))) model = null;
    return {
      mode: patch.mode, operation, provider, model, fields: model ? { ...(p?.fields ?? {}) } : {},
      count: p?.count ?? cur.count, inputs: inputsFor(operation),
    };
  }
  const next = { ...base, ...patch };
  if (patch.operation && patch.operation !== cur.op) {
    next.model = null;
    next.fields = {};
    next.inputs = inputsFor(patch.operation);
  }
  // Строка списка «Исполнитель» меняет поставщика вместе с моделью — её модель и остаётся
  if (patch.provider !== undefined && patch.provider !== cur.providerKey) {
    next.model = patch.model ?? null;
    next.fields = patch.fields ?? {};
  }
  if (patch.model !== undefined && patch.model !== cur.modelId && !patch.fields) next.fields = {};
  return next;
}

// «Выбрать» на вкладке «Голоса»: голос встаёт в поле текущей операции, а если она голос из
// библиотеки не берёт — панель переходит на «Озвучить по образцу» уже с этим голосом
export function voicePick(
  cur: PanelState, inputs: PanelInputs, value: string, personal: boolean,
): { inputs: Partial<PanelInputs> } | { patch: SettingsPatch } {
  if (LIBRARY_VOICE_OPS.has(cur.op)) return { inputs: { voice: value } };
  return {
    patch: { mode: 'voice', operation: 'cloneVoice', inputs: toServerInputs('cloneVoice', { ...inputs, voice: value }, null, personal) },
  };
}

// ── Поставщики и модели ──

export interface ProviderOption {
  key: string;
  label: string;
  unit: string;
  disabled: boolean;
  reason: string | null;
  // Замок: «Локально» в личном чате
  locked: boolean;
}

export function providerOptions(catalog: AudioCatalog | null, op: AudioOp, personal: boolean): ProviderOption[] {
  const name = opInfo(op)?.label ?? op;
  return (catalog?.providers ?? []).map(p => {
    const locked = personal && p.key === 'local';
    let reason: string | null = null;
    if (!p.available) reason = p.reason ?? 'Поставщик сейчас недоступен';
    else if (!supports(p, op)) reason = `У «${p.label}» нет операции «${name}»`;
    return { key: p.key, label: p.label, unit: providerUnit(p), disabled: reason !== null, reason, locked };
  });
}

export function providerUnit(p: AudioProvider): string {
  switch (p.priceUnit) {
    case 'free': return 'бесплатно';
    case 'usd': return 'в долларах';
    case 'credits': return 'кредиты';
    case 'rub': return 'в рублях';
    default: return p.priceUnit;
  }
}

export interface ModelOption {
  id: string;
  label: string;
  // 'RU' / 'без RU'; null — язык не важен (обработка)
  ru: string | null;
  license: string | null;
  heavy: boolean;
  unit: string | null;
}

export function modelOptions(provider: AudioProvider | null, op: AudioOp, autoModelId: string): ModelOption[] {
  if (!provider) return [];
  const models = provider.models.filter(m => m.caps.ops.includes(op));
  const auto: ModelOption = { id: autoModelId, label: 'Авто', ru: null, license: null, heavy: false, unit: 'подберём под операцию' };
  return [auto, ...models.map(m => ({
    id: m.id,
    label: m.label,
    ru: ruMark(m),
    license: m.caps.license?.label || null,
    heavy: !!m.caps.heavyOps?.includes(op),
    unit: unitLabel(provider, m),
  }))];
}

function ruMark(m: AudioModelInfo): string | null {
  if (m.caps.languageNeutral || !m.caps.languages.length) return null;
  return m.caps.languages.includes('ru') ? 'RU' : 'без RU';
}

const PER_LABEL: Record<string, string> = { chars: 'за 1000 симв.', sec: 'за с', min: 'за мин', run: 'за запуск' };

// «$0.1 за 1000 симв.», «бесплатно»; null — цены каталог не знает
export function unitLabel(provider: AudioProvider | null, model: AudioModelInfo | null): string | null {
  if (provider?.priceUnit === 'free' || model?.caps.priceUnit === 'free') return 'бесплатно';
  const h = model?.priceHint;
  if (!h) return null;
  const amount = h.unit === 'chars' ? h.amount * 1000 : h.amount;
  return `${money(amount, provider?.priceUnit ?? 'usd')} ${PER_LABEL[h.unit] ?? ''}`.trim();
}

export function money(amount: number, currency: string): string {
  const n = String(Math.round(amount * 10_000) / 10_000);
  if (currency === 'usd') return `$${n}`;
  if (currency === 'rub') return `${n.replace('.', ',')} ₽`;
  if (currency === 'credits') return `${n.replace('.', ',')} кред.`;
  return `${n} ${currency}`;
}

// ── Цена в две строки ──

const eta = (sec: number) => (sec < 60 ? `${sec} с` : `${Math.round(sec / 60)} мин`);

export interface PriceInput {
  op: AudioOp;
  provider: AudioProvider | null;
  model: AudioModelInfo | null;
  quote: AudioQuote | null;
  count: number;
  textLength: number;
  pieces?: number;
}

export function priceLines({ op, provider, model, quote, count, textLength, pieces }: PriceInput): [string, string] {
  if (op === 'concat') return ['Бесплатно · без ИИ', `${pieces ?? 0} ${plural(pieces ?? 0, 'кусок', 'куска', 'кусков')} · мгновенно`];
  if (isNoAi(op)) return ['Без ИИ', 'мгновенно · бесплатно'];
  if (provider?.priceUnit === 'free') {
    const p = quote?.price;
    const parts = [p?.eta != null ? `~${eta(p.eta)}` : null, `очередь GPU: ${p?.queueLength ?? 0}`];
    return ['Бесплатно', parts.filter(Boolean).join(' · ')];
  }
  const currency = provider?.priceUnit ?? 'usd';
  const h = model?.priceHint ?? null;
  const times = count > 1 ? ` × ${count}` : '';
  // Цена за символы без текста — ноль, а не цена: показываем ориентир каталога
  const amount = h?.unit === 'chars' && textLength === 0 ? null : quote?.price.amount;
  if (amount != null) {
    const head = `${quote!.price.approx ? '≈ ' : ''}${money(amount, currency)}`;
    if (!h) return [head, `${count} вар.`];
    if (h.unit === 'chars') return [head, `${textLength} симв. по ${money(h.amount * 1000, currency)} за 1000${times}`];
    if (h.unit === 'sec') return [head, `по ${money(h.amount, currency)} за с${times}`];
    if (h.unit === 'min') return [head, `по ${money(h.amount, currency)} за мин${times}`];
    return [head, `${money(h.amount, currency)} за запуск${times}`];
  }
  const unit = unitLabel(provider, model);
  if (unit) return [`≈ ${unit}`, h?.unit === 'chars' ? 'сумма — по длине текста в поле ввода' : 'сумма — после котировки'];
  return ['Цена неизвестна', 'станет известна после запуска'];
}

export function plural(n: number, one: string, few: string, many: string): string {
  const m10 = n % 10, m100 = n % 100;
  if (m10 === 1 && m100 !== 11) return one;
  if (m10 >= 2 && m10 <= 4 && (m100 < 12 || m100 > 14)) return few;
  return many;
}

// ── Причина, по которой запустить нельзя ──

export interface ReasonInput {
  sessionId: string | null;
  thread: AudioThread | null;
  state: PanelState;
  provider: ProviderOption | null;
  text: string;
  hasReference: boolean;
  hasVoiceModel: boolean;
  // Голос из библиотеки в смене голоса: есть ли у него модель RVC; null — голос не выбран или ещё не знаем
  libraryRvc?: boolean | null;
  clips: number;
  replicas: number;
  trimReady: boolean;
  pieces: number;
  quoteError: string | null;
  // Режим «Музыка»: слова, инструментал, длительность и кусок (repaint)
  music?: MusicInputs;
  piece?: AudioSelection | null;
}

export function runReason(r: ReasonInput): string | null {
  const op = r.state.op;
  if (!r.sessionId) return 'Сначала начните чат';
  if (op === 'concat') return r.pieces < 2 ? 'Нужно хотя бы два куска — добавьте ещё один' : null;
  if (needsSource(op) && !r.thread?.currentVersionId) return 'Выберите звук в ленте — операция работает с готовой версией';
  if (op === 'trim') return r.trimReady ? null : 'Задайте кусок, громкость, нарастание, затухание или нормализацию';
  if (isNoAi(op)) return null;
  if (!r.provider) return 'Сейчас нет доступного поставщика для этой операции';
  if (r.provider.disabled) return r.provider.reason;
  if (!r.state.model) return 'У поставщика нет модели для этой операции';
  if (op === 'dialogue') {
    if (r.replicas < 1) return 'Добавьте хотя бы одну реплику';
  } else if (opInfo(op)?.field === 'text' && !r.text.trim()) {
    return 'Напишите текст в поле ввода — он уйдёт модели';
  }
  if ((op === 'cloneVoice' || op === 'master') && !r.hasReference) {
    return op === 'master' ? 'Выберите образец звучания' : 'Загрузите образец голоса';
  }
  if (op === 'convertVoice') {
    const kinds = r.state.model.caps.voiceKinds;
    if (kinds.includes('rvc') && !kinds.includes('clone')) {
      if (r.libraryRvc === false) return 'У этого голоса нет модели RVC — выберите голос с моделью или другую модель смены голоса';
      if (!r.hasVoiceModel) return 'Укажите модель голоса .pth из проекта';
    }
    if (kinds.includes('clone') && !r.hasReference) return 'Выберите, чей голос: загрузите образец';
  }
  if (op === 'trainVoice' && r.clips < 1) return 'Добавьте записи голоса — от 2 до 30 минут';
  if (r.state.mode === 'music' && r.music) {
    const why = musicReason({ op, model: r.state.model, inputs: r.music, piece: r.piece ?? null, fields: r.state.fields });
    if (why) return why;
  }
  return r.quoteError;
}

// ── Поля по схеме ──

// Ключи схемы, которые показываем на виду в «Полях операции» (русская подпись); прочее — в «Дополнительно»
export const OP_FIELD_LABELS: Record<string, string> = {
  speaker: 'Диктор',
  voice: 'Голос',
  reference_text: 'Расшифровка образца',
  expressiveness: 'Выразительность',
  mode: 'Что в записи',
  pitch_shift: 'Сдвиг высоты, полутоны',
  epochs: 'Эпохи',
  format: 'Формат',
  model: 'Что в записи',
  speed: 'Скорость',
  speech_rate: 'Скорость',
  pitch: 'Высота',
  pitch_rate: 'Высота',
  vol: 'Громкость',
  volume: 'Громкость',
  loudness_rate: 'Громкость',
  emotion: 'Эмоция',
  remove_background_noise: 'Убрать фоновый шум',
};

// Ключи музыки — на виду только в режиме «Музыка»: у голосовой модели поле key или strength
// значит другое, и прятать его из «Дополнительно» под чужой подписью нельзя
export const MUSIC_FIELD_LABELS: Record<string, string> = {
  bpm: 'Темп, BPM',
  key: 'Тональность',
  strength: 'Близость к исходнику',
  track: 'Дорожка',
  tracks: 'Инструменты',
  abc: 'Партитура ABC',
};

export function opFieldLabels(mode: AudioMode): Record<string, string> {
  return mode === 'music' ? { ...OP_FIELD_LABELS, ...MUSIC_FIELD_LABELS } : OP_FIELD_LABELS;
}

export function splitSchema(schema: AudioParamSchema | null, mode: AudioMode): { main: AudioParamField[]; extra: AudioParamField[] } {
  const fields = schema?.fields ?? [];
  const labels = opFieldLabels(mode);
  return {
    main: fields.filter(f => f.key in labels && f.passed !== false),
    extra: fields.filter(f => !(f.key in labels) || f.passed === false),
  };
}

// Вариант А (audio-panel-v3-proposal.md): темп и тональность музыки уходят в «Ещё настройки» —
// на виду у песни только слова и длительность; подписи у них остаются русскими
const TUCKED_MUSIC: ReadonlySet<string> = new Set(['bpm', 'key']);

export function tuckSchema(
  mode: AudioMode, split: { main: AudioParamField[]; extra: AudioParamField[] },
): { main: AudioParamField[]; extra: AudioParamField[]; labels: Record<string, string> } {
  if (mode !== 'music') return { ...split, labels: {} };
  const tucked = split.main.filter(f => TUCKED_MUSIC.has(f.key));
  return {
    main: split.main.filter(f => !TUCKED_MUSIC.has(f.key)),
    extra: [...tucked, ...split.extra],
    labels: Object.fromEntries(tucked.map(f => [f.key, MUSIC_FIELD_LABELS[f.key]])),
  };
}

export type Widget = 'slider' | 'number' | 'select' | 'checkbox' | 'text' | 'textarea' | 'json';

export function widgetOf(f: AudioParamField): Widget {
  if (f.enum?.length) return 'select';
  switch (f.type) {
    case 'boolean': return 'checkbox';
    case 'number':
    case 'integer':
      return f.min != null && f.max != null ? 'slider' : 'number';
    case 'string':
      return (f.maxLength ?? 0) > 200 ? 'textarea' : 'text';
    default:
      return 'json';
  }
}

// Только поля схемы, которые доедут до модели: остальное сервер отверг бы «неизвестным параметром»
export function pruneFields(fields: Record<string, unknown>, schema: AudioParamSchema | null): Record<string, unknown> {
  if (!schema) return {};
  const ok = new Set(schema.fields.filter(f => f.passed !== false).map(f => f.key));
  return Object.fromEntries(Object.entries(fields).filter(([k, v]) => ok.has(k) && v !== undefined && v !== ''));
}
