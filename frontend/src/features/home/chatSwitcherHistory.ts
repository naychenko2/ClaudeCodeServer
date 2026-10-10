// История браузера шторки «Недавние» (ChatSwitcherSheet), без React — чтобы проводку
// «назад» можно было проверить тестом на подменном window.
//
// Системный «назад» закрывает шторку, а не уводит со страницы: на открытии кладём
// в историю запись-дубль текущего снимка с флагом, «назад» её снимает. Выбор чата
// сперва снимает эту запись и только потом открывает чат — иначе в истории между
// прошлым и новым чатом остался бы дубль, и «назад» из нового чата тратил бы лишнее
// нажатие.

/** Та часть window, которой пользуется шторка */
export interface SwitcherWindow {
  history: Pick<History, 'state' | 'back' | 'pushState'>;
  location: { href: string };
  addEventListener(type: 'popstate', fn: () => void, opts?: AddEventListenerOptions): void;
  removeEventListener(type: 'popstate', fn: () => void): void;
}

// Флаг записи истории, которую шторка кладёт на открытии
export const HISTORY_FLAG = 'chatSwitcher';

const hasFlagIn = (win: SwitcherWindow) => !!(win.history.state as Record<string, unknown> | null)?.[HISTORY_FLAG];

// Сколько шторок смонтировано сейчас — чтобы снятие записи на размонтировании
// отличало настоящее закрытие от перемонтажа StrictMode (тот монтирует снова сразу)
let liveSheets = 0;

// Снятие записи шторки уже идёт: «назад» асинхронен, и второй back() до его popstate
// увёл бы на запись раньше, мимо чата под шторкой. Флаг общий для всех путей снятия
// (контроллер шторки и afterChatSwitcherClosed): тот, кто пришёл вторым, ждёт тот же popstate
let popping = false;

/** Снять запись шторки одним «назад» на всех; then — после всех слушателей popstate:
 *  они применяют снимок под шторкой (тот же чат), и then его перебивает */
function popSheetRecord(win: SwitcherWindow, then?: () => void) {
  if (then) win.addEventListener('popstate', () => setTimeout(then, 0), { once: true });
  if (popping) return;
  popping = true;
  win.addEventListener('popstate', () => { popping = false; }, { once: true });
  win.history.back();
}

/** Выполнить fn, когда запись шторки снята. Для перезаписи текущей записи истории
 *  (navReplace) при открытой шторке: replaceState затёр бы флаг, а запись под ним
 *  осталась бы дублем. Поэтому сперва «назад» снимает запись шторки (сама шторка
 *  закроется на том же popstate), и только потом fn перезаписывает запись под ней. */
export function afterChatSwitcherClosed(fn: () => void, win: SwitcherWindow = window) {
  if (!hasFlagIn(win)) { fn(); return; }
  popSheetRecord(win, fn);
}

export interface SwitcherController<T> {
  /** Монтирование шторки: кладёт запись и слушает popstate; возвращает размонтирование */
  mount(): () => void;
  /** Закрыть шторку (крестик, фон, Escape, тап по открытому чату) */
  close(): void;
  /** Выбор чата в шторке */
  pick(item: T, isCurrent: boolean): void;
  /** Свежий обработчик закрытия (проп компонента меняется между рендерами) */
  setOnClose(fn: () => void): void;
}

export function createSwitcherController<T>(
  win: SwitcherWindow,
  deps: { onClose: () => void; open: (item: T) => void },
): SwitcherController<T> {
  // Запись шторки уже снята системным «назад» — снимать её при размонтировании не надо
  let closedByPop = false;
  // Шторка уже закрывается: «назад» асинхронен, и второй тап до его popstate позвал бы
  // back() ещё раз — второй popstate увёл бы на запись раньше, мимо выбранного чата
  let closing = false;
  let onClose = deps.onClose;
  const hasFlag = () => hasFlagIn(win);

  const onPop = () => {
    if (hasFlag()) return; // ушли «вперёд» на запись шторки — не наш случай
    closedByPop = true;
    onClose();
  };

  const close = () => {
    if (closing) return;
    closing = true;
    if (hasFlag()) popSheetRecord(win);
    else onClose();
  };

  return {
    mount() {
      liveSheets++;
      // Повторный монтаж (StrictMode) второй записи не кладёт
      if (!hasFlag()) {
        win.history.pushState({ ...(win.history.state ?? {}), [HISTORY_FLAG]: true }, '', win.location.href);
      }
      win.addEventListener('popstate', onPop);
      return () => {
        liveSheets--;
        win.removeEventListener('popstate', onPop);
        // Шторку убрали мимо «назад» (удалён текущий чат, переход по тосту) — снимаем
        // её запись, иначе в истории остаётся дубль и «назад» тратит лишнее нажатие.
        // Проверка отложена: перемонтаж StrictMode успевает поднять счётчик обратно.
        // Если поверх уже легла новая запись, флага наверху нет — трогать нечего.
        // Перезапись записи (navReplace) флаг затирает, поэтому её ведут через
        // afterChatSwitcherClosed — иначе снимать здесь было бы уже нечего
        if (closedByPop) return;
        setTimeout(() => {
          if (liveSheets === 0 && hasFlag()) popSheetRecord(win);
        }, 0);
      };
    },
    close,
    pick(item, isCurrent) {
      if (closing) return;
      if (isCurrent) { close(); return; }
      closing = true;
      if (hasFlag()) {
        // Выбранный чат открывается, когда «назад» снимет запись шторки. Слушатель —
        // свой, а не шторки: popstate страницы под шторкой может её размонтировать раньше,
        // чем очередь дойдёт до её слушателя (popstate — дискретное событие, React
        // рендерит между слушателями), и переход потерялся бы вместе с ней. Сам переход —
        // после всех слушателей, применяющих прежний снимок: иначе они перебили бы его
        popSheetRecord(win, () => deps.open(item));
      }
      else { onClose(); deps.open(item); }
    },
    setOnClose(fn) { onClose = fn; },
  };
}
