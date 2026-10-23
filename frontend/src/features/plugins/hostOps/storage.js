/**
 * Операции storage.* для плагинов — чистая логика, общая для боевого хоста
 * (обычный import) и эмулятора панели (<script type="module">). Логика пишется
 * ОДИН раз: ошибка в одной копии — дыра (решение Александра, шаг F1).
 *
 * Прямого доступа к localStorage у модуля нет: хост передаёт store в форме
 * Storage или функции-провайдера () => Storage (F-д6: одна точка получения
 * хранилища и разбора его ошибок — и здесь же, а не в хосте и не в эмуляторе).
 * Префикс ccs.plugin.<ownerId>.<pluginId>. собирает только хост —
 * модуль берёт ownerId и pluginId сверху, из args их не достаёт. Поля
 * сообщения панели (свои granted/pluginId/ownerId/prefix) на проверку права
 * и префикс не влияют.
 */

// Префикс всего хранилища плагинов в origin.
export const STORAGE_PREFIX = 'ccs.plugin.';

// Пределы (в code units UTF-16 — так считает квота браузера).
export const KEY_MAX_LEN = 64;
export const VALUE_MAX_BYTES = 8 * 1024; // 8 КиБ сериализованного JSON на значение
export const KEYS_MAX_PER_PLUGIN = 100; // ключей на плагин
export const PLUGIN_BYTES_MAX = 128 * 1024; // 128 КиБ на плагин
export const ORIGIN_BYTES_MAX = 1024 * 1024; // 1 МиБ на весь ccs.plugin.* в origin

// Ключ: 1–64 символов из [A-Za-z0-9._-]. Разделитель префикса — «.», поэтому
// подряд идущие точки («..») запрещены: ключ не должен выглядеть границей
// префикса (Александр, чек-лист инвариантов, п.3).
const KEY_RE = /^[A-Za-z0-9._-]{1,64}$/;
// pluginId — грамматика publisher/slug, обе части [a-z0-9-].
const PLUGIN_ID_RE = /^[a-z0-9-]+\/[a-z0-9-]+$/;

function fail(code, message) {
  return { ok: false, error: { code, message } };
}

function prefixOf(ownerId, pluginId) {
  return STORAGE_PREFIX + ownerId + '.' + pluginId + '.';
}

function validKey(key) {
  return typeof key === 'string' && key.length >= 1 && key.length <= KEY_MAX_LEN && KEY_RE.test(key) && !key.includes('..');
}

// Все ключи store через индексацию Storage (key(i)/length).
function allKeys(store) {
  const out = [];
  for (let i = 0; i < store.length; i++) {
    const k = store.key(i);
    if (typeof k === 'string') out.push(k);
  }
  return out;
}

// Байты ключа: длина ключа + длина сохранённого значения, в code units.
function keyBytes(store, key) {
  const raw = store.getItem(key);
  return key.length + (raw === null ? 0 : raw.length);
}

// Значение, которое нельзя выразить в JSON: undefined/NaN/±Infinity. Молча
// превращать их в null нельзя — это silent-потеря данных.
function jsonUnrepresentable(value) {
  return value === undefined || Number.isNaN(value) || value === Infinity || value === -Infinity;
}

/**
 * Один вызов storage.* от панели.
 * op: get|set|remove|keys; args: {key} / {key, value} / {}.
 * store — объект в форме Storage или провайдер () => Storage. Геттер
 * window.localStorage бросает SecurityError при отключённом хранилище
 * (Firefox dom.storage.enabled=false, приватный режим), поэтому провайдер
 * разрешается внутри этого try: ошибка разбирается здесь, а не в хосте (F-д6).
 * Функция НИКОГДА не бросает: панель всегда получает result.
 * Возвращает {ok:true, data} или {ok:false, error:{code, message}}.
 */
export function handleStorageCall(input) {
  try {
    // Одна точка получения хранилища: провайдер здесь же, в том же try.
    const store = typeof input.store === 'function' ? input.store() : input.store;
    // SecurityError (хранилище отключено/приватный режим) бросает любое обращение к
    // store — get/remove/keys/length/key(i), не только setItem. Для панели это
    // unavailable на ЛЮБОЙ операции: иначе исключение уходит в хост, а промис
    // ccsPlugin.call висит вечно (ревью Глеба R2, инвариант 7).
    return handleStorageCallImpl({ ...input, store });
  } catch (e) {
    // F-д2 (M1), F-д6: два разных случая, и оба видны — здесь, одной точкой.
    // SecurityError — штатный тихий unavailable для панели, без лога. Всё прочее —
    // настоящая ошибка: логируем с меткой, чтобы разработчик её видел, и не прячем
    // под человеческий текст «Хранилище недоступно».
    if (e && e.name === 'SecurityError') return fail('unavailable', 'Хранилище недоступно');
    console.error('[plugin storage] внутренняя ошибка хранилища:', e);
    return fail('unavailable', 'Внутренняя ошибка хранилища');
  }
}

function handleStorageCallImpl({ op, args, granted, ownerId, pluginId, store }) {
  // 1. Право — только по granted состояния хоста (п.2).
  if (!Array.isArray(granted) || !granted.includes('storage')) {
    return fail('denied', 'Нет права: storage');
  }
  // 2. Нет владельца — fail-closed: unavailable, ни одной записи (п.5).
  if (!ownerId) {
    return fail('unavailable', 'Нет владельца: хранилище недоступно');
  }
  // 3. Префикс собирается только из host-параметров (п.3).
  if (typeof pluginId !== 'string' || !PLUGIN_ID_RE.test(pluginId)) {
    return fail('invalid', 'Неверный идентификатор плагина');
  }

  const prefix = prefixOf(ownerId, pluginId);

  switch (op) {
    case 'get': {
      const key = args.key;
      if (!validKey(key)) return fail('invalid', 'Неверный ключ');
      const raw = store.getItem(prefix + key);
      if (raw === null) return { ok: true, data: { exists: false } };
      // Отсутствие ключа — НЕ ошибка; значение отдаём разобранным JSON.
      // Контракт строгий: записанное модулем значение всегда JSON. Чужое или
      // испорченное — отказ invalid, а не смена типа значения на лету
      // (ревью Глеба, F1-д минор 3).
      try {
        return { ok: true, data: { exists: true, value: JSON.parse(raw) } };
      } catch {
        return fail('invalid', 'Значение повреждено: не JSON');
      }
    }

    case 'set': {
      const key = args.key;
      const value = args.value;
      if (!validKey(key)) return fail('invalid', 'Неверный ключ');
      if (jsonUnrepresentable(value)) return fail('invalid', 'Значение не выражается в JSON');
      // JSON.stringify бросает на циклах, BigInt и Symbol. Без try/catch исключение
      // выходит из handleStorageCall: промис панели висит, обработчик хоста падает
      // (ревью Глеба, F1-д). Function даёт undefined — тоже отказ, не падение.
      let raw;
      try {
        raw = JSON.stringify(value);
      } catch {
        return fail('invalid', 'Значение не выражается в JSON');
      }
      if (typeof raw !== 'string') return fail('invalid', 'Значение не выражается в JSON');
      if (raw.length > VALUE_MAX_BYTES) return fail('limit', 'Значение больше 8 КиБ');

      const fullKey = prefix + key;
      const ownKeys = allKeys(store).filter(k => k.startsWith(prefix));
      const oldRaw = store.getItem(fullKey);

      // Пределы проверяются ДО setItem — «всё или ничего»: после отказа store
      // не меняется (п.6).
      if (oldRaw === null && ownKeys.length >= KEYS_MAX_PER_PLUGIN) {
        return fail('limit', 'Больше 100 ключей на плагин');
      }
      const ownBytes = ownKeys.reduce((sum, k) => sum + keyBytes(store, k), 0);
      // Объём — по той же мере, что keyBytes/storage.keys: длина ключа + длина значения.
      const oldBytes = oldRaw === null ? 0 : fullKey.length + oldRaw.length;
      const newBytes = fullKey.length + raw.length;
      const newOwnBytes = ownBytes - oldBytes + newBytes;
      if (newOwnBytes > PLUGIN_BYTES_MAX) return fail('limit', 'Больше 128 КиБ на плагин');

      const originKeys = allKeys(store).filter(k => k.startsWith(STORAGE_PREFIX));
      const originBytes = originKeys.reduce((sum, k) => sum + keyBytes(store, k), 0);
      const newOriginBytes = originBytes - oldBytes + newBytes;
      if (newOriginBytes > ORIGIN_BYTES_MAX) return fail('limit', 'Хранилище плагинов больше 1 МиБ');

      try {
        store.setItem(fullKey, raw);
      } catch (e) {
        // Квота браузера — тот же код, что наши пределы (п.7).
        if (e && e.name === 'QuotaExceededError') return fail('limit', 'Квота хранилища исчерпана');
        if (e && e.name === 'SecurityError') return fail('unavailable', 'Хранилище недоступно');
        throw e;
      }
      return { ok: true, data: {} };
    }

    case 'remove': {
      const key = args.key;
      if (!validKey(key)) return fail('invalid', 'Неверный ключ');
      const fullKey = prefix + key;
      const existed = store.getItem(fullKey) !== null;
      store.removeItem(fullKey);
      return { ok: true, data: { removed: existed } };
    }

    case 'keys': {
      // Только свой префикс; ключи отдаём БЕЗ префикса — панели он не нужен (п.4).
      const ownKeys = allKeys(store).filter(k => k.startsWith(prefix));
      const bytes = ownKeys.reduce((sum, k) => sum + keyBytes(store, k), 0);
      return { ok: true, data: { keys: ownKeys.map(k => k.slice(prefix.length)), bytes } };
    }

    default:
      return fail('invalid', 'Неизвестная операция: ' + String(op));
  }
}

// Частичное стирание (хранилище отключилось в середине цикла) не молчит.
function logPartial(what, removed) {
  console.warn('[plugin storage] ' + what + ' прервано SecurityError: удалено ключей ' + removed + ', остальные остались');
}

/**
 * Стереть ровно свой префикс. clear() не вызывается никогда: он гасит токен
 * и шлёт событие storage с key === null во всех вкладках (offline.ts:75-77).
 * Возвращает число удалённых ключей.
 */
export function removePluginStorage({ ownerId, pluginId, store }) {
  if (!ownerId || typeof pluginId !== 'string' || !PLUGIN_ID_RE.test(pluginId)) return 0;
  const prefix = prefixOf(ownerId, pluginId);
  let removed = 0;
  for (const k of allKeys(store)) {
    if (k.startsWith(prefix)) {
      try {
        store.removeItem(k);
        removed++;
      } catch (e) {
        // F-д3 (минор 5): хранилище отключилось в середине цикла — часть ключей
        // уже удалена, счётчик возвращаем честный (фактически удалённое), цикл
        // останавливаем. Настоящая ошибка — вверх, как раньше.
        if (e && e.name === 'SecurityError') {
          logPartial('стирание плагина', removed);
          break;
        }
        throw e;
      }
    }
  }
  return removed;
}

/**
 * Ленивая сборка мусора: удалить ключи текущего владельца, чей pluginId не в
 * keepPluginIds. Чужой ownerId не трогается никогда (п.10).
 * Возвращает число удалённых ключей.
 */
export function collectStorageGarbage({ ownerId, keepPluginIds, store }) {
  if (!ownerId) return 0;
  const ownerPrefix = STORAGE_PREFIX + ownerId + '.';
  const keep = new Set(keepPluginIds);
  let removed = 0;
  for (const k of allKeys(store)) {
    if (!k.startsWith(ownerPrefix)) continue;
    // После ownerPrefix идёт "<pluginId>.<key>"; pluginId не содержит точек
    // (грамматика publisher/slug), первая точка — граница.
    const pluginId = k.slice(ownerPrefix.length).split('.')[0];
    if (!keep.has(pluginId)) {
      try {
        store.removeItem(k);
        removed++;
      } catch (e) {
        // F-д3 (минор 5): частичный сбой — счётчик по фактически удалённому.
        if (e && e.name === 'SecurityError') {
          logPartial('сборка мусора', removed);
          break;
        }
        throw e;
      }
    }
  }
  return removed;
}
