// Уровни reasoning effort для флага --effort.
// Пустое value → флаг не передаётся (дефолт CLI).
export interface EffortOption {
  value: string;
  label: string;
  // Короткое пояснение для списков с описаниями (пикер усилия в композере)
  desc?: string;
}

export const EFFORTS: EffortOption[] = [
  { value: '', label: 'По умолчанию', desc: 'Флаг не передаётся — уровень выбирает сам CLI' },
  { value: 'low', label: 'Низкий', desc: 'Меньше рассуждений — быстрее и дешевле' },
  { value: 'medium', label: 'Средний', desc: 'Баланс скорости и глубины' },
  { value: 'high', label: 'Высокий', desc: 'Больше рассуждений на сложных задачах' },
  { value: 'xhigh', label: 'Очень высокий', desc: 'Глубже разбор, ответ заметно дольше' },
  { value: 'max', label: 'Максимум', desc: 'Предельные рассуждения — самый долгий и дорогой ход' },
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
