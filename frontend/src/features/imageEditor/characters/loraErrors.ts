// Коды отказов ручек обучения и версий LoRA (ADR-026 §7) → типизированные причины для интерфейса.

import { OfflineError, RequestTimeoutError } from 'aihome_shell/kit';

export type LoraFailReason =
  | 'disabled' | 'no_seam' | 'unavailable' | 'confirm_required' | 'bad_preset' | 'bad_trigger'
  | 'too_few_photos' | 'character_not_found' | 'owner_busy' | 'gpu_busy' | 'version_limit' | 'queue_full'
  | 'version_not_found' | 'version_not_ready' | 'version_training' | 'lora_not_visible' | 'lora_missing'
  | 'bad_title' | 'not_training' | 'invalid_request' | 'photo_not_found' | 'lost' | 'network' | 'unknown';

export interface LoraFailure {
  reason: LoraFailReason;
  // gpu_busy: когда карта освободится
  busyUntil?: string;
}

const KNOWN = new Set<string>([
  'disabled', 'no_seam', 'unavailable', 'confirm_required', 'bad_preset', 'bad_trigger', 'too_few_photos',
  'character_not_found', 'owner_busy', 'gpu_busy', 'version_limit', 'queue_full', 'version_not_found',
  'version_not_ready', 'version_training', 'lora_not_visible', 'lora_missing', 'bad_title', 'not_training',
  'invalid_request', 'photo_not_found', 'lost',
]);

// network — обрыв связи или таймаут; всё остальное без известного code (401/403, 500, чужой код) — unknown
export function loraFailure(e: unknown): LoraFailure {
  const err = e as { body?: { code?: unknown; busyUntil?: unknown } } | null;
  const code = err?.body?.code;
  if (typeof code === 'string' && KNOWN.has(code)) {
    const busyUntil = typeof err?.body?.busyUntil === 'string' ? err.body.busyUntil : undefined;
    return { reason: code as LoraFailReason, ...(busyUntil ? { busyUntil } : {}) };
  }
  if (e instanceof OfflineError || e instanceof RequestTimeoutError || e instanceof TypeError) return { reason: 'network' };
  return { reason: 'unknown' };
}
