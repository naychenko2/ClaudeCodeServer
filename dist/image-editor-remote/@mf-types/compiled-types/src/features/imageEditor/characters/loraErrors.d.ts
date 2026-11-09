export type LoraFailReason = 'disabled' | 'no_seam' | 'unavailable' | 'confirm_required' | 'bad_preset' | 'bad_trigger' | 'too_few_photos' | 'character_not_found' | 'owner_busy' | 'gpu_busy' | 'version_limit' | 'queue_full' | 'version_not_found' | 'version_not_ready' | 'version_training' | 'lora_not_visible' | 'lora_missing' | 'bad_title' | 'not_training' | 'invalid_request' | 'photo_not_found' | 'lost' | 'network' | 'unknown';
export interface LoraFailure {
    reason: LoraFailReason;
    busyUntil?: string;
}
export declare function loraFailure(e: unknown): LoraFailure;
