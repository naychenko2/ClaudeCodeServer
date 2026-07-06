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

// Все провайдеры работают через claude CLI — набор уровней --effort единый
export function effortsForProvider(_provider: string): EffortOption[] {
  return EFFORTS;
}

// Короткая подпись уровня для отображения (value → label).
export function effortLabel(value?: string | null): string {
  if (!value) return 'По умолчанию';
  return EFFORTS.find(e => e.value === value)?.label ?? value;
}
