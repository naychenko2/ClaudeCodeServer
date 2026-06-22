// Доступные модели для выбора в чате.
// Пустое value → флаг --model не передаётся (дефолтная модель CLI).
export interface ModelOption {
  value: string;
  label: string;
}

export const MODELS: ModelOption[] = [
  { value: '', label: 'По умолчанию' },
  { value: 'claude-opus-4-8', label: 'Opus 4.8' },
  { value: 'claude-sonnet-4-6', label: 'Sonnet 4.6' },
  { value: 'claude-haiku-4-5-20251001', label: 'Haiku 4.5' },
];

// Короткая подпись модели для отображения (id → label).
// Неизвестный id показываем как есть — например, фактическую модель из session_started.
export function modelLabel(value?: string | null): string {
  if (!value) return 'По умолчанию';
  return MODELS.find(m => m.value === value)?.label ?? value;
}
