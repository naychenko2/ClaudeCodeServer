// Локальный движок моделей: раньше это была только Ollama, после переезда бэкенд поднимает
// либо её, либо llama-server (выбор конфигом LocalLlm:Provider) и сообщает выбранный движок
// полем provider в ответе /api/usage. Ключи и человеческие подписи держим здесь одной точкой:
// иначе проверка «это локальный движок?» расползается по вкладкам раздела «Модели и расход».

export const LOCAL_ENGINE_KEYS: readonly string[] = ['ollama', 'llama-server'];

// Ключ провайдера принадлежит локальному движку (бесплатный, без лимитов и баланса).
export function isLocalEngineKey(key: string): boolean {
  return LOCAL_ENGINE_KEYS.includes(key);
}

// Подписи движков как их пишут люди: Ollama — с заглавной, llama-server — строчными
// (так он зовётся в собственной документации).
const LOCAL_ENGINE_LABELS: Record<string, string> = {
  ollama: 'Ollama',
  'llama-server': 'llama-server',
};

// Подпись движка для интерфейса. Поле provider опциональное: старый бэкенд его не шлёт —
// тогда подпись прежняя, «Ollama». Незнакомый движок показываем ключом как есть: соврать
// про Ollama хуже, чем показать машинное имя.
export function localEngineLabel(provider?: string | null): string {
  const key = provider?.trim();
  if (!key) return 'Ollama';
  return LOCAL_ENGINE_LABELS[key] ?? key;
}
