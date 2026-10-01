// Чистая логика вкладки «Голоса» (записка v2, раздел «Вкладка «Голоса»»): вид экрана, подпись
// карточки, таблица «Где работает» по состояниям поставщиков, проверки формы и образцов.
// Компонент только рисует то, что отдают эти функции, — так поведение проверяется без DOM.

import {
  MAX_VOICE_SAMPLE_MB, MAX_VOICE_SAMPLES, MINIMAX_RECREATE_READY,
  type AudioVoice, type VoiceProviderState, type VoicesList,
} from './api';

// Значение поля «Голос» панели «Звук» для голоса из библиотеки
export const VOICE_PICK_PREFIX = 'voice:';
export const voicePickValue = (slug: string) => `${VOICE_PICK_PREFIX}${slug}`;
export const pickedSlug = (value: string | null | undefined) =>
  value?.startsWith(VOICE_PICK_PREFIX) ? value.slice(VOICE_PICK_PREFIX.length) : null;

export const PERSONAL_TITLE = '«Голоса» живут в проекте';
export const PERSONAL_TEXT =
  'Библиотека хранится в папке voices/ проекта. В личном чате можно озвучивать готовыми дикторами, по описанию и по образцу из файла.';
export const EMPTY_TITLE = 'Голосов пока нет';
export const EMPTY_TEXT =
  'Добавьте запись 5–15 секунд чистой речи. Голос — это записи человека и их расшифровка: его смогут взять все поставщики, которые умеют клонировать.';
export const MINIMAX_TTL_TEXT = 'MiniMax удаляет клон через 7 дней без использования';
export const RECREATE_PENDING_HINT = 'Пересоздание клона появится в следующем обновлении';

export type VoicesView =
  | { kind: 'loading' }
  | { kind: 'error'; message: string }
  | { kind: 'personal' }
  | { kind: 'empty' }
  | { kind: 'list'; voices: AudioVoice[] };

// Личный чат определяется и по области (запроса нет), и по ответу сервера available:false
export function voicesView(personal: boolean, list: VoicesList | null, error: string | null): VoicesView {
  if (personal) return { kind: 'personal' };
  if (error) return { kind: 'error', message: error };
  if (!list) return { kind: 'loading' };
  if (!list.available) return { kind: 'personal' };
  if (list.voices.length === 0) return { kind: 'empty' };
  return { kind: 'list', voices: [...list.voices].sort((a, b) => a.name.localeCompare(b.name, 'ru')) };
}

const plural = (n: number, one: string, few: string, many: string) => {
  const m10 = n % 10, m100 = n % 100;
  if (m10 === 1 && m100 !== 11) return one;
  if (m10 >= 2 && m10 <= 4 && (m100 < 12 || m100 > 14)) return few;
  return many;
};

export const samplesCountText = (n: number) => `${n} ${plural(n, 'образец', 'образца', 'образцов')}`;

export function voiceSubtitle(v: AudioVoice): string {
  return v.kind === 'rvc' ? 'Модель RVC · voice.pth · voice.index' : `По записям · ${samplesCountText(v.samples.length)}`;
}

// ok — работает, stale — клон удалён, none — не создан, no — не умеет
export type WhereStatus = 'ok' | 'stale' | 'none' | 'no';

export interface WhereRow {
  key: string;
  label: string;
  status: WhereStatus;
  note: string;
}

const fmtDate = (iso: string | null) => {
  if (!iso) return null;
  const d = new Date(iso);
  return Number.isNaN(d.getTime()) ? null : d.toLocaleDateString('ru-RU', { day: '2-digit', month: '2-digit' });
};

const daysSince = (iso: string | null, now: Date) => {
  if (!iso) return null;
  const t = new Date(iso).getTime();
  return Number.isNaN(t) ? null : Math.floor((now.getTime() - t) / 86_400_000);
};

const stateOf = (v: AudioVoice, key: VoiceProviderState['provider']) => v.providers.find(p => p.provider === key);

// Таблица «Где работает» по кешу поставщиков. Клон у fal и Higgsfield создаётся при первом
// запуске — до него строка «не создан», а не «не умеет»
export function whereWorks(v: AudioVoice, now = new Date()): WhereRow[] {
  if (v.kind === 'rvc') {
    const rvc = stateOf(v, 'rvc');
    const trained = fmtDate(rvc?.createdAt ?? null);
    return [
      { key: 'local-rvc', label: 'Локально · RVC (смена голоса)', status: rvc?.state === 'ok' ? 'ok' : 'none',
        note: rvc?.state === 'ok' ? (trained ? `обучена ${trained}` : 'модель на месте') : 'нет пары .pth и .index' },
      { key: 'others', label: 'Остальные', status: 'no', note: 'модель RVC понимают только локальные' },
    ];
  }

  const hf = stateOf(v, 'higgsfield');
  const mm = stateOf(v, 'minimax');
  const qwen = stateOf(v, 'falQwen');
  const mmDays = daysSince(mm?.lastUsedAt ?? mm?.createdAt ?? null, now);
  return [
    { key: 'local-tts', label: 'Локально · Qwen3-TTS, MOSS, Chatterbox', status: 'ok', note: 'по образцу на лету' },
    { key: 'local-vc', label: 'Локально · Seed-VC (смена голоса)', status: 'ok', note: 'образец 1–30 с' },
    hf?.state === 'ok'
      ? { key: 'higgsfield', label: 'Higgsfield · Seed Audio', status: 'ok', note: 'элемент воркспейса' }
      : { key: 'higgsfield', label: 'Higgsfield · Seed Audio', status: 'none', note: 'элемент создастся при первом запуске' },
    mm?.state === 'stale'
      ? { key: 'minimax', label: 'fal · MiniMax клон', status: 'stale',
          note: mmDays !== null ? `MiniMax удалил клон: не использовался ${mmDays} ${plural(mmDays, 'день', 'дня', 'дней')}` : 'MiniMax мог удалить клон' }
      : mm?.state === 'ok'
        ? { key: 'minimax', label: 'fal · MiniMax клон', status: 'ok', note: MINIMAX_TTL_TEXT }
        : { key: 'minimax', label: 'fal · MiniMax клон', status: 'none', note: 'клон создастся при первом запуске, цену покажем заранее' },
    qwen?.state === 'ok'
      ? { key: 'falQwen', label: 'fal · Qwen3 клон', status: 'ok', note: 'эмбеддинг сохранён' }
      : { key: 'falQwen', label: 'fal · Qwen3 клон', status: 'none', note: 'эмбеддинг создастся при первом запуске' },
    { key: 'yandex', label: 'Яндекс', status: 'no', note: 'Яндекс не клонирует голоса' },
  ];
}

export const isStale = (v: AudioVoice) => v.needsAttention || v.providers.some(p => p.state === 'stale');

// Кнопка «Пересоздать · цена»: активна, только когда ручка есть и цена известна
export function recreateAction(price: string | null, ready = MINIMAX_RECREATE_READY): { label: string; disabled: boolean; hint: string | null } {
  const label = price ? `Пересоздать · ${price}` : 'Пересоздать';
  if (!ready) return { label, disabled: true, hint: RECREATE_PENDING_HINT };
  if (!price) return { label, disabled: true, hint: 'Считаем цену…' };
  return { label, disabled: false, hint: null };
}

// Последний образец убрать нельзя — голос без записей не клонируется; удаляется голос целиком
export function sampleRemoval(v: AudioVoice): { allowed: boolean; reason: string | null } {
  if (v.kind !== 'samples') return { allowed: false, reason: 'У модели RVC нет образцов' };
  if (v.samples.length <= 1) return { allowed: false, reason: 'Последнюю запись убрать нельзя — удалите голос целиком' };
  return { allowed: true, reason: null };
}

export const canAddSamples = (v: AudioVoice) => v.kind === 'samples' && v.samples.length < MAX_VOICE_SAMPLES;

export interface SampleDraft { files: { name: string; size: number }[]; projectFiles: string[] }

export const draftCount = (d: SampleDraft) => d.files.length + d.projectFiles.length;

// Проверка записей до отправки; existing — сколько образцов у голоса уже есть
export function samplesProblem(d: SampleDraft, existing = 0): string | null {
  const n = draftCount(d);
  if (n === 0) return 'Добавьте хотя бы одну запись';
  if (existing + n > MAX_VOICE_SAMPLES) {
    return existing ? `У голоса не больше ${MAX_VOICE_SAMPLES} записей — можно добавить ещё ${MAX_VOICE_SAMPLES - existing}` : `Не больше ${MAX_VOICE_SAMPLES} записей`;
  }
  const big = d.files.find(f => f.size > MAX_VOICE_SAMPLE_MB * 1024 * 1024);
  if (big) return `${big.name} больше ${MAX_VOICE_SAMPLE_MB} МБ`;
  return null;
}

// Чего не хватает форме нового голоса; null — можно сохранять
export function newVoiceProblem(name: string, d: SampleDraft): string | null {
  if (!name.trim()) return 'Укажите имя голоса';
  return samplesProblem(d);
}
