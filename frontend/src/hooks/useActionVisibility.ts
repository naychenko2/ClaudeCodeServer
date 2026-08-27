import { useCallback, useEffect, useRef, useState } from 'react';

// Видимость элементов в рядах действий (шапка чата, губа композера, плитка чата).
// Кнопка «⋯» стоит в ряду ВСЕГДА, а пользователь сам решает, что показывать
// рядом с ней, а что убрать внутрь меню: тумблеры живут в самом «⋯».
//
// Хранится набор СКРЫТЫХ ключей, а не показанных: пустой набор = «всё на виду»,
// то есть дефолт не требует записи и переживает появление новых кнопок (новая
// кнопка сразу видна, а не прячется задним числом).
//
// Хранение — localStorage per-surface: настройка чисто локальная (у другого
// устройства свои привычки и другая ширина экрана), та же природа, что у
// cc_chat_view / cc_proj_board_*. Формат общий — JSON-массив строк.
//
// Поверхность в одном экране живёт во МНОГИХ экземплярах (каждая плитка списка
// держит свой хук), а localStorage — один на всех. Нативный storage-евент
// стреляет только в чужих вкладках, поэтому свои экземпляры оповещаем сами:
// после записи диспатчим window-событие, и каждый подписчик перечитывает стор.
// Без этого глазик в одной плитке менял ряд только в ней — соседние узнавали
// о настройке после перезагрузки страницы.

export type ActionSurface = 'chat-header' | 'chat-wall' | 'composer' | 'chat-card';

const KEYS: Record<ActionSurface, string> = {
  'chat-header': 'cc_chat_header_hidden',
  // Стена — своя настройка на все её колонки сразу (см. chatActions)
  'chat-wall': 'cc_chat_wall_hidden',
  'composer': 'cc_composer_hidden',
  'chat-card': 'cc_chat_card_hidden',
};

const CHANGE_EVENT = 'cc-action-visibility-change';

// null — настройки нет вовсе (в т.ч. когда localStorage недоступен): вызывающий
// возьмёт дефолт. Массив — сохранённый набор, пустой в нём тоже значим («показать всё»)
function readHidden(surface: ActionSurface): string[] | null {
  try {
    const raw = localStorage.getItem(KEYS[surface]);
    if (raw === null) return null;
    const parsed = JSON.parse(raw);
    // Битое значение (не массив / не строки) — тихо считаем «ничего не скрыто»:
    // настройка косметическая, ради неё интерфейс падать не должен
    if (!Array.isArray(parsed)) return null;
    return parsed.filter((v): v is string => typeof v === 'string');
  } catch {
    return null;
  }
}

// defaultHidden — что скрыто, пока пользователь ничего не настраивал. Нужен, потому
// что набор действий чата шире, чем разумно держать в ряду: без дефолта первый же
// показ выкатывал бы все восемь кнопок сразу. Сохранённая настройка (даже пустой
// массив «показать всё») дефолт перебивает — он работает ровно один раз, до первого
// касания глазика
export function useActionVisibility(surface: ActionSurface, defaultHidden: string[] = []) {
  const [hidden, setHidden] = useState<string[]>(() => readHidden(surface) ?? defaultHidden);
  // Дефолт в ref: callback'ам ниже он нужен живым, а в зависимостях он бы
  // пересоздавал их каждый рендер
  const defaultRef = useRef(defaultHidden);

  // Переключить видимость элемента: показать (убрать из скрытых) или скрыть.
  // Читаем и пишем синхронно, БЕЗ setState-updater'а: стор — единственный источник
  // истины (два быстрых клика в одном тике иначе делают read-modify-write по
  // устаревшему prev), а сайд-эффекты записи внутри updater'а запрещены — React
  // в StrictMode прогоняет updater дважды, и второй прогон откатывал первый.
  // Рендер трогаем последним простым setValue — соседи узнают о смене по событию
  const toggle = useCallback((key: string) => {
    const cur = readHidden(surface) ?? defaultRef.current;
    const next = cur.includes(key) ? cur.filter(k => k !== key) : [...cur, key];
    try {
      localStorage.setItem(KEYS[surface], JSON.stringify(next));
      window.dispatchEvent(new CustomEvent(CHANGE_EVENT, { detail: { surface } }));
    } catch { /* приватный режим — живём без сохранения */ }
    setHidden(next);
  }, [surface]);

  // Скрыть ключи разом (не переключая): нормализация сверхлимитного набора,
  // считанного из старого localStorage. Идемпотентна; стор — истина (см. toggle)
  const hide = useCallback((keys: string[]) => {
    if (keys.length === 0) return;
    const cur = readHidden(surface) ?? defaultRef.current;
    const next = [...cur, ...keys.filter(k => !cur.includes(k))];
    if (next.length === cur.length) return;
    try {
      localStorage.setItem(KEYS[surface], JSON.stringify(next));
      window.dispatchEvent(new CustomEvent(CHANGE_EVENT, { detail: { surface } }));
    } catch { /* приватный режим — живём без сохранения */ }
    setHidden(next);
  }, [surface]);

  // Чужая запись в стор (глазик в другой плитке) — перечитать и подхватить.
  // Событие приходит после записи, так что readHidden вернёт свежий набор;
  // null не бывает (запись только что сделали), на всякий случай — дефолт
  useEffect(() => {
    const onChange = (e: Event) => {
      const detail = (e as CustomEvent<{ surface: ActionSurface }>).detail;
      if (!detail || detail.surface !== surface) return;
      setHidden(readHidden(surface) ?? defaultRef.current);
    };
    window.addEventListener(CHANGE_EVENT, onChange);
    return () => window.removeEventListener(CHANGE_EVENT, onChange);
  }, [surface]);

  const isHidden = useCallback((key: string) => hidden.includes(key), [hidden]);
  const isVisible = useCallback((key: string) => !hidden.includes(key), [hidden]);

  return { hidden, toggle, hide, isHidden, isVisible };
}
