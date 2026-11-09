// Словарь прав плагина (ADR-031 §4, решение В7 макетов): человекочитаемое название
// для каждого ключа права. Ключи — крупные, называют поверхности хоста:
//   notes:read · tasks:read · net:{exact-host} · secret:{name}
// secret:{name} в списке прав НЕ показывается — у секретов свой блок (макет, экран 2),
// поэтому для них labelPermission возвращает null.
//
// Словарь держит фронт (не бэк): бэк отдаёт только ключи, а тексты интерфейса —
// забота морды. Если бэк позже начнёт отдавать title — словарь здесь станет фолбэком.

// Право «сеть»: есть ли у ключа префикс net: (для иконки-глобуса и предупреждения)
export function isNetPermission(key: string): boolean {
  return key.startsWith('net:');
}

// Хост из net:{host} (без префикса); для non-net — null
export function netHostOf(key: string): string | null {
  if (!isNetPermission(key)) return null;
  return key.slice('net:'.length);
}

// Имя секрета из secret:{name} (без префикса); для non-secret — null
export function secretNameOf(key: string): string | null {
  if (!key.startsWith('secret:')) return null;
  return key.slice('secret:'.length);
}

// Человекочитаемое название права. null — право не показываем в списке (secret:*)
export function permissionLabel(key: string): string | null {
  if (key === 'notes:read') return 'Читать заметки проекта';
  if (key === 'tasks:read') return 'Читать задачи проекта';
  if (key === 'storage') return 'Хранить данные в этом браузере';
  if (isNetPermission(key)) {
    const host = netHostOf(key);
    return host ? `Ходить в сеть: только ${host}` : 'Ходить в сеть';
  }
  if (secretNameOf(key) !== null) return null; // секреты — в своём блоке
  // Неизвестное право — показываем ключ как есть, чтобы не терять информацию
  return key;
}

// Предупреждение для прав net:* (макет, экран 2): данные уйдут за пределы AI Home
export const NET_PERMISSION_WARNING = 'Данные уйдут за пределы AI Home. Выдавайте, если доверяете автору.';

// Пояснение под пунктом права (null — пояснения нет). Одно право storage, без деления на чтение и запись.
export const STORAGE_PERMISSION_HINT =
  'До 128 КБ. На другом устройстве их не будет. Их может прочитать любой, кто работает в этом браузере';

export function permissionHint(key: string): string | null {
  return key === 'storage' ? STORAGE_PERMISSION_HINT : null;
}
