// Разбор вызовов инструментов агента audio_* (AudioEditorToolset.cs) для карточек ленты.
// Чистые функции — под юнит-тестом: вход — input и result вызова, выход — то, что видит человек.
// Отказы сервер пишет агенту («верни результат тому, кто тебя позвал») — человеку их
// пересказываем своими словами.

import type { AudioMode, AudioOp } from '../api';
import { MODE_LABEL, opInfo } from '../ops';

export const AUDIO_TOOL = (name: string) => `mcp__audio-editor__${name}`;

export const str = (v: unknown) => (typeof v === 'string' && v.trim() ? v.trim() : null);
export const num = (v: unknown) => (typeof v === 'number' && Number.isFinite(v) ? v : null);

export function parseJson(text: string | undefined): Record<string, unknown> | null {
  if (!text) return null;
  try {
    const v = JSON.parse(text) as unknown;
    return v && typeof v === 'object' && !Array.isArray(v) ? v as Record<string, unknown> : null;
  } catch { return null; }
}

const obj = (v: unknown): Record<string, unknown> | null =>
  v && typeof v === 'object' && !Array.isArray(v) ? v as Record<string, unknown> : null;

export const asMode = (v: unknown): AudioMode | null => (v === 'voice' || v === 'music' || v === 'process' ? v : null);

// ── Отказы ──

export type DenialKind = 'turnLimit' | 'delegated' | 'report' | 'unchecked' | 'error';
export interface Denial { kind: DenialKind; text: string }

// Отказ вызова: JSON { error, code } исполнителя или текст сторожа хода
export function parseDenial(result: string | undefined): Denial {
  const raw = (result ?? '').trim();
  const err = str(parseJson(raw)?.error);
  if (err) return { kind: 'error', text: err };
  const limit = /не больше (\d+) операц/.exec(raw);
  if (limit) {
    return {
      kind: 'turnLimit',
      text: `За один ход можно запустить не больше ${limit[1]} операций со звуком. Ответьте в чате — и работа продолжится.`,
    };
  }
  if (raw.includes('на делегированном ходу')) {
    return {
      kind: 'delegated',
      text: 'Этот ход начат другим чатом, а тратить деньги может только ход, который видите вы. Запустите сами или попросите об этом здесь.',
    };
  }
  if (raw.includes('отвечаешь на доклад')) {
    return {
      kind: 'report',
      text: 'Это был ответ на доклад исполнителя — из такого хода запуск закрыт. Попросите запустить отдельным сообщением.',
    };
  }
  if (raw.includes('отказ по построению')) {
    return { kind: 'unchecked', text: 'Сервер не смог проверить ход, поэтому запуск закрыт. Попробуйте ещё раз.' };
  }
  return { kind: 'error', text: raw || 'Сервер не ответил.' };
}

// ── Цена ──

export interface PriceView { amount: number | null; unit: string; approx: boolean }

export function parsePrice(v: unknown): PriceView | null {
  const p = obj(v);
  const unit = str(p?.unit);
  if (!p || !unit) return null;
  return { amount: num(p.amount), unit, approx: p.approx === true };
}

// «бесплатно», «≈ $0.12», «12 кред.», «3,5 ₽»; сумма неизвестна — null
export function priceText(p: PriceView | null): string | null {
  if (!p) return null;
  if (p.unit === 'free') return 'бесплатно';
  if (p.amount === null) return null;
  const n = String(Math.round(p.amount * 10_000) / 10_000);
  const sum = p.unit === 'rub' ? `${n.replace('.', ',')} ₽`
    : p.unit === 'credits' ? `${n.replace('.', ',')} кред.`
    : p.unit === 'usd' ? `$${n}` : `${n} ${p.unit}`;
  return p.approx ? `≈ ${sum}` : sum;
}

// ── audio_generate ──

export interface LaunchView {
  jobId: string;
  threadId: string | null;
  baseLabel: string | null;
  provider: string | null;
  model: string | null;
  op: AudioOp | null;
  count: number | null;
  price: PriceView | null;
}

export function parseLaunch(result: string | undefined): LaunchView | null {
  const r = parseJson(result);
  const jobId = str(r?.jobId);
  if (!r || !jobId) return null;
  const q = obj(r.quote) ?? {};
  return {
    jobId,
    threadId: str(r.threadId),
    baseLabel: str(obj(r.baseVersion)?.label),
    provider: str(q.provider),
    model: str(q.model),
    op: (opInfo(str(q.op) as AudioOp)?.op) ?? null,
    count: num(q.count),
    price: parsePrice(q.price),
  };
}

// Монтаж без ИИ (op trim, gainFade…) и склейка отвечают сразу готовой версией, без задачи
export interface ReadyView { threadId: string; versionId: string; name: string | null }

export function parseReady(result: string | undefined): ReadyView | null {
  const r = parseJson(result);
  const threadId = str(r?.threadId);
  const versionId = str(r?.versionId);
  return r && threadId && versionId && !str(r.jobId) ? { threadId, versionId, name: str(r.name) } : null;
}

// Подпись операции: «Озвучить», «Песня»; без op — режим или «Операция со звуком»
export function opTitle(op: AudioOp | null, mode: AudioMode | null): string {
  return opInfo(op)?.label ?? (mode ? MODE_LABEL[mode] : 'Операция со звуком');
}

// ── audio_focus и audio_new ──

export interface FocusView { threadId: string | null; name: string | null; version: string | null }

const baseName = (path: string) => path.split('/').pop() || path;

// Ответ audio_focus / audio_new: focus и thread { threadId, file, name, currentVersionId, versions[{versionId,label}] }.
// null-поля сервер не пишет вовсе: снятый выбор — ответ без focus
export function parseFocus(result: string | undefined): FocusView | null {
  const r = parseJson(result);
  if (!r) return null;
  const t = obj(r.thread);
  const threadId = str(r.focus) ?? str(t?.threadId);
  if (!threadId) return { threadId: null, name: null, version: null };
  const file = str(t?.file);
  const name = file ? baseName(file) : str(t?.name) ?? 'Новый звук';
  const current = str(t?.currentVersionId);
  const versions = Array.isArray(t?.versions) ? t!.versions as unknown[] : [];
  const v = versions.map(obj).find(x => x && str(x.versionId) === current);
  const label = str(v?.label);
  return { threadId, name, version: label };
}

// ── Служебные: audio_state, audio_voices, audio_cancel ──

export function stateLine(result: string | undefined): string {
  const r = parseJson(result);
  const threads = Array.isArray(r?.threads) ? (r!.threads as unknown[]).length : null;
  if (threads === null) return 'Просмотр звуков чата';
  if (threads === 0) return 'Звуки чата: пока ни одного';
  return `Звуки чата: ${threads} ${plural(threads, 'звук', 'звука', 'звуков')}`;
}

export function voicesLine(input: Record<string, unknown>, result: string | undefined): string {
  const r = parseJson(result);
  const providers = Array.isArray(r?.providers) ? r!.providers as unknown[] : null;
  const lang = str(input.language);
  const head = `Дикторы${lang ? ` (${lang})` : ''}`;
  if (!providers) return head;
  const voices = providers.reduce<number>((s, p) => s + (Array.isArray(obj(p)?.voices) ? (obj(p)!.voices as unknown[]).length : 0), 0);
  const library = Array.isArray(r?.library) ? (r!.library as unknown[]).length : 0;
  const parts = [`${voices} ${plural(voices, 'диктор', 'диктора', 'дикторов')}`];
  if (library) parts.push(`голосов проекта: ${library}`);
  return `${head}: ${parts.join(', ')}`;
}

const CANCEL_STATUS: Record<string, string> = {
  cancelled: 'отменена', completed: 'уже была готова', failed: 'уже завершилась ошибкой',
};

export function cancelLine(result: string | undefined): string {
  const r = parseJson(result);
  const status = str(r?.status);
  const variants = num(r?.variants) ?? 0;
  const head = status && CANCEL_STATUS[status] ? `Операция со звуком ${CANCEL_STATUS[status]}` : 'Отмена операции со звуком';
  const tail = [
    variants ? `готовых вариантов: ${variants}` : null,
    r?.charged === false ? 'деньги не списаны' : r?.charged === true ? 'поставщик уже списал оплату' : null,
  ].filter(Boolean);
  return tail.length ? `${head} · ${tail.join(' · ')}` : head;
}

export function plural(n: number, one: string, few: string, many: string): string {
  const m10 = n % 10, m100 = n % 100;
  if (m10 === 1 && m100 !== 11) return one;
  if (m10 >= 2 && m10 <= 4 && (m100 < 12 || m100 > 14)) return few;
  return many;
}
