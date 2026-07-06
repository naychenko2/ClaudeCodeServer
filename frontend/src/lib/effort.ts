// Уровни reasoning effort для флага --effort.
// Пустое value → флаг не передаётся (дефолт CLI).
export interface EffortOption {
  value: string;
  label: string;
}

export const EFFORTS: EffortOption[] = [
  { value: '', label: 'По умолчанию' },
  { value: 'low', label: 'Низкий' },
  { value: 'medium', label: 'Средний' },
  { value: 'high', label: 'Высокий' },
  { value: 'xhigh', label: 'Очень высокий' },
  { value: 'max', label: 'Максимум' },
];

// У DeepSeek reasoning_effort только high/max — остальные уровни модель игнорирует.
// Показываем сокращённый список; провайдер определяется по модели.
const DEEPSEEK_EFFORTS: EffortOption[] = [
  { value: '', label: 'По умолчанию' },
  { value: 'high', label: 'Высокий' },
  { value: 'max', label: 'Максимум' },
];

export function effortsForProvider(provider: string): EffortOption[] {
  return provider === 'deepseek' ? DEEPSEEK_EFFORTS : EFFORTS;
}

// Короткая подпись уровня для отображения (value → label).
export function effortLabel(value?: string | null): string {
  if (!value) return 'По умолчанию';
  return EFFORTS.find(e => e.value === value)?.label ?? value;
}
