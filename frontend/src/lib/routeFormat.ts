// Клиентская проверка формата маршрута локального действия (ADR-009).
//
// Тот же перечень из восьми форм значения, что разбирает бэкенд, — чтобы UI
// не пускал в `PUT /api/admin/local-actions/{key}` то, что контракт всё равно
// отвергнет (или, что хуже, молча проглотит как «id модели» и тихо уйдёт в
// фолбэк). Это дублирующая проверка: контракт держит запись на бэкенде, UI
// лишь не даёт человеку сохранить кривое значение.
//
// Источник текстов — docs/features/model-route-format-validation.md (дословно).
// Правило языка раздела соблюдено: нигде нет слов «маршрут», «префикс»,
// `direct:`, «каталог», «валидация». Главные сценарии (модель без поставщика,
// неизвестная модель) используют утверждённые тексты; редкие технические случаи
// (пустой id пресета, пробелы в названии) сформулированы в том же тоне.

import type { ModelOption } from './models';

// Дословные тексты из docs/features (ключи route.*). Не строковые шаблоны —
// фиксированные значения, чтобы случайно не разъехались с спекой.
export const RATE_PICKER_HINT =
  'Модель берётся из списка — вместе с названием запоминается поставщик, через которого она вызывается.';

export const RATE_NO_PROVIDER =
  'У этой модели не указан поставщик — непонятно, через кого её вызывать. Выберите модель в списке: поставщик подставится сам.';

// route.unknownModel — с подстановкой названия модели в ёлочках (как в спеке).
export function rateUnknownModel(model: string): string {
  return `Модель «${model}» не найдена среди доступных. Выберите её в списке раздела «Модели и расход».`;
}

// Доп. тексты для редких случаев, не охваченных docs/features. Без стоп-слов
// раздела («маршрут», «префикс», `direct:`, «каталог», «валидация»).
export const RATE_PRESET_EMPTY =
  'Не указана цепочка — выберите её в списке, и ссылка подставится сама.';
export function ratePresetMissing(id: string): string {
  return `Цепочки «${id}» нет среди доступных — выберите цепочку в списке.`;
}
export const RATE_MODEL_SPACES =
  'В названии модели не может быть пробелов.';

export interface RouteCheckOptions {
  // Каталог /api/models: нужен для проверки формы «{id модели}» и «direct:{id}».
  // Без каталога эти формы считаются валидными по синтаксису (семантику проверить нечем).
  models?: readonly ModelOption[];
  // id доступных пресетов — для проверки «preset:{id}». Без списка ссылка считается валидной.
  presetIds?: readonly string[];
}

export type RouteCheckResult = { ok: true } | { ok: false; message: string };

const TIER_LEVELS = ['strong', 'medium', 'weak'];
const KNOWN_LITERALS = new Set(['local', 'claude', 'default']);

// Канонические формы литералов/префиксов — в нижнем регистре (ADR-009 §2).
// `local`/`claude`/`default` — регистрозависимы (switch по константам), без trim.
// tier:/preset: — префикс регистронезависим; direct: — регистрозависим.
export function validateLocalActionRoute(
  value: string | null | undefined,
  opts: RouteCheckOptions = {},
): RouteCheckResult {
  const trimmed = (value ?? '').trim();
  if (!trimmed) return { ok: false, message: '' }; // пусто — просто блокируем Save, без текста

  // Формы 1–3: литералы. Регистрозависимое сравнение (ADR-009 §2).
  if (KNOWN_LITERALS.has(trimmed)) return { ok: true };

  // Форма 4–6: tier:{level}. Префикс и уровень — нижний регистр; внутренний пробел
  // уже снят внешним trim, но «tier: strong» остаётся пробелом внутри — ParseTierRoute
  // такое не узнаёт, и мы честно считаем уровень невалидным.
  if (trimmed.toLowerCase().startsWith('tier:')) {
    const level = trimmed.slice('tier:'.length).toLowerCase();
    if (TIER_LEVELS.includes(level)) return { ok: true };
    return { ok: false, message: rateUnknownModel(trimmed) };
  }

  // Форма 7: preset:{id}. Префикс регистронезависим, id — в исходном регистре.
  if (trimmed.toLowerCase().startsWith('preset:')) {
    const id = trimmed.slice('preset:'.length).trim();
    if (!id) return { ok: false, message: RATE_PRESET_EMPTY };
    if (opts.presetIds && !opts.presetIds.some(p => p.toLowerCase() === id.toLowerCase())) {
      return { ok: false, message: ratePresetMissing(id) };
    }
    return { ok: true };
  }

  // Форма 8 (с подвидом direct:): {id модели}. Пробелы в id недопустимы.
  if (/\s/.test(trimmed)) return { ok: false, message: RATE_MODEL_SPACES };

  if (opts.models && opts.models.length > 0) {
    const inCatalog = opts.models.some(m => m.value === trimmed);
    if (inCatalog) return { ok: true };

    // Случай прод-дефекта: модель прямого вызова записана голым именем — без
    // указания поставщика. В каталоге она есть как «direct:{id}», а сохранено
    // «{id}»: вызов уходил обычным путём, не находил модель и молча брал дефолт.
    const asDirect = `direct:${trimmed}`;
    if (opts.models.some(m => m.value === asDirect)) {
      return { ok: false, message: RATE_NO_PROVIDER };
    }
    return { ok: false, message: rateUnknownModel(trimmed) };
  }

  // Каталога нет — синтаксис допустим, семантику проверить нечем.
  return { ok: true };
}

// Пригодность значения для сохранения: ok=true ИЛИ ok=false с непустым сообщением.
// Пустое сообщение — поле пустое, Save блокируем, но ошибку не показываем.
export function routeCanSave(value: string | null | undefined, opts: RouteCheckOptions = {}): boolean {
  const r = validateLocalActionRoute(value, opts);
  return r.ok;
}
