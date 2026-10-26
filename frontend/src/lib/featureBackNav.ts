// «← назад» в плашке ключевого чата (Ф4, «Обзор фич»): откуда пришли, в памяти вкладки.
//
// Записывается, когда из плашки чата фичи (кнопка «Ключевой чат →») перешли в ключевой
// чат. Плашка ключевого чата читает: показывает «← <роль исходного чата>» и возвращает
// в исходный чат одним кликом.
//
// Живёт в памяти вкладки (не localStorage) — как режим/выбор фичи в featureRuns.
// Сбрасывается: (1) по «← назад» (возврат в исходный чат); (2) при выборе ДРУГОГО
// чата (handleSelectSession: записанный указатель ведёт в другой чат — протухший
// чип гасится). Повторный клик по ТОМУ ЖЕ чату чип не гасит — это сознательно:
// чип ведёт туда, откуда пришли (решение Александра, шов 2д-1). При монтировании
// другого воркспейса гасятся только указатели fromKind='chat' — «чат → чат»
// локальны для проекта (шов 2д-2).
//
// Один указатель (не per-session): «← назад» — про ТОТ переход, что только что сделали.
// Фронт ничего не сортирует и не считает: порядок и роль исходного чата даёт индекс фич.

import { useSyncExternalStore } from 'react';
import { getFeaturePageTab, type FeaturePageTab } from './featurePage';

// Куда перешли: в чат (id чата), на страницу фичи (id фичи) или в задачу (id задачи).
export interface BackNavTarget {
  kind: 'chat' | 'feature' | 'task';
  id: string;
}

export interface BackNav {
  // Исходный id (чат или фича), из которого перешли.
  from: string;
  // Дискриминатор: 'chat' — from = id чата (recordBackNav); 'feature' — from = id фичи
  // (recordFeatureReturn). Ф9b читает этот указатель: без дискриминатора id фичи
  // неотличим от id чата (ревью фронта 1, minor 6).
  fromKind: 'chat' | 'feature';
  // Куда перешли (чат или страница фичи).
  to: BackNavTarget;
  // Название исходной фичи (fromKind='feature'): для «← к фиче {название}» в шапке чата
  // (Ф-pl-b). null — вызывающий не передал (шапка покажет «← к фиче» без имени).
  fromTitle?: string | null;
  // Вкладка страницы фичи, с которой ушли (fromKind='feature'): чип возврата показывает её
  // название («← к фиче · Чаты»), а возврат открывает ту же вкладку (Ф9b).
  fromTab?: FeaturePageTab;
  // Показывать ли вкладку в чипе возврата (Ф9b, major 1): false — переход из попапа/
  // панели/шторки (не из вкладки страницы): чип «← к фиче», вкладки нет, но возврат
  // ведёт на последнюю использованную вкладку (fromTab). true (дефолт) — из вкладки.
  showTab?: boolean;
  // Снимок вида страницы на момент перехода (GC-F11, fromKind='feature'): его берёт
  // recordFeatureReturn у зарегистрированного поставщика (вкладки вида), а не у
  // вызывающих. null — поставщика нет (ушли не с «Хронологии») или он не дал снимка.
  view?: FeatureViewSnapshot | null;
}

// === Снимок вида «Хронологии» (GC-F11) ===
// Возврат по чипу обязан вернуть не только на вкладку, но и на состояние вида:
// раскрытый этап, раскрытая карточка ленты «Производство», позиция прокрутки. Снимок
// не собирают вызывающие recordFeatureReturn (их много, вида они не знают) — его
// даёт поставщик, зарегистрированный самим видом (FeatureTimelineTab). Нет поставщика
// (ушли с другой вкладки) — нет и снимка.
export interface FeatureViewSnapshot {
  // Раскрытый этап «Хронологии» (StageKey); null — все схлопнуты.
  stageKey: string | null;
  // Раскрытая карточка ленты «Производство» (stepRef или card-N); null — нет.
  expandedKey: string | null;
  // Прокрутка ТОГО контейнера, который реально скроллит страницу фичи: внутренней
  // панели с overflow:auto (FeaturePage.tsx:366). Не window.scrollY: на странице он
  // всегда 0 — прокрутка терялась (дефект 7d399549). Контейнера нет — window.scrollY
  // (запасной вариант).
  scrollTop: number;
}
export type FeatureViewProvider = () => FeatureViewSnapshot | null;

// Поставщик снимка — один (последний зарегистрированный). Ключ — featureId:
// recordFeatureReturn зовёт его только для той же фичи, что зарегистрировала.
let _viewProvider: { featureId: string; fn: FeatureViewProvider } | null = null;

// Зарегистрировать поставщика снимка (на время монтирования вкладки). Возвращает
// снятие: по идентичности записи — снятие старого монтирования не должно стереть
// регистрацию нового (перемонтирование вкладки). Снятие — НЕ сигнал ухода (GC-F11R4,
// мажор GC-R-P6): размонтирование вкладки (remount 'loading'→'ok', мнимый unmount
// StrictMode) окно не гасит и его расход не отменяет — окно принадлежит открытию
// страницы, а не инстансу вкладки (см. registerFeaturePage).
export function registerFeatureViewProvider(featureId: string, fn: FeatureViewProvider): () => void {
  const entry = { featureId, fn };
  _viewProvider = entry;
  return () => {
    if (_viewProvider === entry) _viewProvider = null;
  };
}

// Старая строка в хранилище (id чата) читается как переход в чат; объект — как есть.
export function normalizeBackTarget(raw: string | BackNavTarget): BackNavTarget {
  if (typeof raw === 'string') return { kind: 'chat', id: raw };
  return raw;
}

let _back: BackNav | null = null;
// Однократный снимок «возврат по чипу» (GC-F11): живёт отдельно от указателя —
// clearBackNav его не трогает (см. consumeFeatureViewReturn ниже).
let _pendingView: PendingFeatureView | null = null;
// Подтверждённый применением снимок (GC-F11R2, дефект 7d399549): remount вкладки
// читает его, когда pending уже подтверждён (см. commitFeatureViewApplied ниже).
let _lastApplied: PendingFeatureView | null = null;
let _pendingSeq = 0;
const _listeners = new Set<() => void>();
function emit() { _listeners.forEach(fn => fn()); }

function subscribe(fn: () => void): () => void {
  _listeners.add(fn);
  return () => { _listeners.delete(fn); };
}

// Записать переход: из чата `from` в `to` (чат или страницу фичи).
// `to` — строка (id чата, старый формат) или объект { kind, id }.
// Переход «чат → тот же чат» (to.kind==='chat' && to.id===from) — не переход.
export function recordBackNav(from: string, to: string | BackNavTarget): void {
  const target = normalizeBackTarget(to);
  if (!from || !target.id || (target.kind === 'chat' && target.id === from)) return;
  _back = { from, fromKind: 'chat', to: target };
  emit();
}

// Сброс (возврат «← назад» или обычное открытие чата).
export function clearBackNav(): void {
  if (_back) { _back = null; emit(); }
}

// Точка возврата «страница фичи → чат/задача» (Ф9a, «Навигация в один клик»):
// записывает, что из страницы фичи `featureId` открыли чат/задачу `to`. Чип
// «вернуться на страницу фичи» (Ф9b) читает это и предлагает возврат. Использует
// тот же указатель _back, что и «← назад» (chat → chat/feature) — один переход
// за раз; `from` здесь — id фичи (не чата), `fromKind` = 'feature', `to.kind` — 'chat' | 'task'.
// Вкладка берётся из стора страницы в момент перехода (tab — только для тестов/явного
// указания): вызывающим вкладкам её передавать не нужно.
//
// ОСОЗНАННОЕ ПОВЕДЕНИЕ (minor 5, ревью Ф9b): указатель _back — ЕДИНСТВЕННЫЙ (один
// переход за раз). Повторный переход из ДРУГОЙ вкладки/попапа/шторки ПЕРЕЗАПИСЫВАЕТ
// его: у НОВОГО чата чип возврата (с новой вкладкой/без), у ПРЕЖНЕГО чата чипа больше
// нет (to.id сменился). Это не баг, а «один возврат за раз» — как и у «← назад».
//
// `showTab` (major 1): false — переход из попапа/панели/шторки (не из вкладки): чип
// «← к фиче {название}» БЕЗ вкладки, но возврат ведёт на последнюю использованную
// вкладку (fromTab из стора). true (дефолт) — из вкладки: чип с названием вкладки.
export function recordFeatureReturn(featureId: string, to: { kind: 'chat' | 'task'; id: string }, title?: string | null, tab?: FeaturePageTab, showTab?: boolean): void {
  if (!featureId || !to.id) return;
  // Новый переход невалидирует незабранный возврат (GC-F11): старый pending не должен
  // примениться к следующей открытой странице. То же для подтверждённого снимка
  // (lastApplied, GC-F11R2): окно remount закрыла не новая страница, а новый переход.
  _pendingView = null;
  _lastApplied = null;
  // Снимок вида — у поставщика этой фичи (registerFeatureViewProvider), если он есть;
  // вызывающие его не передают и не знают (GC-F11).
  const view = _viewProvider && _viewProvider.featureId === featureId ? _viewProvider.fn() : null;
  _back = {
    from: featureId, fromKind: 'feature', to: { kind: to.kind, id: to.id },
    fromTitle: title ?? null, fromTab: tab ?? getFeaturePageTab(),
    showTab: showTab ?? true,
    view: view ?? null,
  };
  emit();
}

// === Однократный канал «возврат по чипу» (GC-F11, GC-F11R2) ===
// Клик чипа переносит снимок из указателя в этот канал ПЕРЕД тем, как обработчик
// cc-open-feature погасит указатель (clearBackNav) — восстановление не зависит от
// жизни указателя (дефект 4c4645e2: указатель не должен стираться раньше
// восстановления). «Хронология» читает канал и ПРИМЕНЯЕТ снимок к своему состоянию;
// чтение само по себе ничего не гасит (GC-F11R2, дефект 7d399549): гасится только
// подтверждённое применение, и не самим подтверждением, а стабильным кадром после
// него — см. commitFeatureViewApplied и scheduleConsume ниже.
export interface PendingFeatureView { featureId: string; view: FeatureViewSnapshot; seq: number }

// Забрать снимок из ЖИВОГО указателя в pending (зовёт чип — только его клик значит
// «возврат»; открытие страницы из списка или из плашки чипа не вызывает).
// Возвращает, был ли снимок. Указатель при этом не гасится — это делает обработчик
// cc-open-feature, как и раньше.
export function consumeFeatureViewReturn(featureId: string): boolean {
  if (!_back || _back.fromKind !== 'feature' || _back.from !== featureId || !_back.view) return false;
  _pendingSeq += 1;
  _pendingView = { featureId, view: _back.view, seq: _pendingSeq };
  emit();
  return true;
}

// Прочитать pending без подписки (для эффектов и тестов); стабильная ссылка —
// тот же объект, пока не поставлен новый или не погашен.
export function pendingFeatureView(featureId: string): PendingFeatureView | null {
  return _pendingView && _pendingView.featureId === featureId ? _pendingView : null;
}

// Хук: однократный снимок возврата для этой фичи (null, пока не забран чипом).
export function usePendingFeatureView(featureId: string): PendingFeatureView | null {
  return useSyncExternalStore(subscribe, () => pendingFeatureView(featureId), () => pendingFeatureView(featureId));
}

// === Подтверждение применения (GC-F11R2, дефект 7d399549; GC-F11R3 — идемпотентность и расход) ===
// Старый механизм гасил снимок в момент применения (clearPendingFeatureView в эффекте
// после монтажа) — и remount вкладки в том же кадре терял вид: после возврата по
// чипу страница сначала рисуется из КЭША 'ok' (стор featurePage — уровень модуля,
// unmount его не сбрасывает), затем первичный запрос сбрасывает её в 'loading'
// (featurePage.ts:100) → вкладка unmount→mount, а снимок уже погашен. Теперь:
// применение подтверждается — pending ПЕРЕНОСИТСЯ в lastApplied, и remount вкладки
// читает тот же снимок из него. Подтверждение идемпотентно по seq (GC-F11R3):
// повторный вызов с тем же seq — no-op. Это обязано быть так: StrictMode (main.tsx:14)
// в dev дважды зовёт эффекты монтирования, и «закрытие окна повторным подтверждением»
// (бывший механизм) закрывал окно ещё до реального remount — фикс не работал в dev-сборке.
// Окно расходует не подтверждение, а стабильный кадр после применения (scheduleConsume);
// новый переход гасит оба канала (recordFeatureReturn), а уход со страницы — отложенной
// проверкой (registerFeaturePage, GC-F11R4).
export function commitFeatureViewApplied(featureId: string, seq: number): void {
  if (_pendingView && _pendingView.featureId === featureId && _pendingView.seq === seq) {
    _lastApplied = { featureId, seq, view: _pendingView.view };
    _pendingView = null;
    emit();
  } else if (!(_lastApplied && _lastApplied.featureId === featureId && _lastApplied.seq === seq)) {
    return; // чужой featureId/seq: ничего не гасит, ничего не откладывает
  }
  scheduleConsume(featureId, seq);
}

// === Расход окна (GC-F11R3, GC-F11R4) ===
// Конец окна привязан к жизни открытия, а не к числу подтверждений: через CONSUME_MS
// после применения, если вкладка этой фичи смонтирована, применение стабильно —
// снимок расходован (следующее открытие фичи без чипа его не найдёт). Размонтирование
// вкладки — не сигнал ухода (GC-F11R4): пока вкладка не смонтирована, расход ОТКЛАДАЕТСЯ
// — окно достаётся remount-инстансу при любой скорости ответа сервера (прежний механизм
// отменял расход при размонтировании и, если инстанс не возвращался, окно жило до
// нового перехода — мажор GC-R-P6). Настоящий уход — размонтирование страницы:
// оно гасит окно (см. registerFeaturePage).
const CONSUME_MS = 1000;
// Предел отложений расхода (И5): у таймера есть конец — окно ждёт remount вкладки не
// дольше CONSUME_MAX_DEFERS секунд, потом гасится, а не перезапускается бесконечно.
const CONSUME_MAX_DEFERS = 10;
let _consumeTimer: ReturnType<typeof setTimeout> | null = null;
function scheduleConsume(featureId: string, seq: number, defers = 0): void {
  if (_consumeTimer) clearTimeout(_consumeTimer);
  _consumeTimer = setTimeout(() => {
    _consumeTimer = null;
    if (!(_lastApplied && _lastApplied.featureId === featureId && _lastApplied.seq === seq)) return;
    if (_viewProvider && _viewProvider.featureId === featureId) {
      _lastApplied = null;
      emit();
      return;
    }
    // Вкладка не смонтирована (remount 'loading'→'ok', медленный ответ): расход отложен,
    // окно достаётся remount. Страница ушла (нет регистрации) или попытки кончились —
    // окно возврата живёт ради одного открытия, таймер гаснет вместе с ним.
    if (!(_pageReg && _pageReg.featureId === featureId) || defers + 1 >= CONSUME_MAX_DEFERS) {
      _lastApplied = null;
      emit();
      return;
    }
    scheduleConsume(featureId, seq, defers + 1);
  }, CONSUME_MS);
}

// Прочитать подтверждённый снимок без подписки (для инициализации состояния вкладки):
// remount вкладки (страницы 'loading'→'ok') берёт тот же снимок отсюда.
export function lastAppliedFeatureView(featureId: string): PendingFeatureView | null {
  return _lastApplied && _lastApplied.featureId === featureId ? _lastApplied : null;
}

// === Регистрация страницы фичи (GC-F11R4, мажор GC-R-P6) ===
// Окно возврата живёт только ради того открытия фичи, ради которого снято. Размонтирование
// ВКЛАДКИ (remount 'loading'→'ok', мнимый unmount StrictMode) его не убивает; уход со
// СТРАНИЦЫ — убивает. Гашение отложенное: cleanup страницы не гасит сразу, а ставит
// проверку (setTimeout(0)), которую отменяет повторная регистрация страницы с тем же
// featureId (мнимый unmount→mount под StrictMode, main.tsx:14); реальный уход (страница
// не вернулась) гасит pending и lastApplied этой фичи и её таймер расхода. Зовёт
// FeaturePage при монтировании — сигнал жизни страницы; само окно живёт в сторе.
let _pageReg: { featureId: string } | null = null;
let _pageKill: { featureId: string; timer: ReturnType<typeof setTimeout> } | null = null;
export function registerFeaturePage(featureId: string): () => void {
  // Mount страницы: снять отложенную проверку прошлого размонтирования (путь StrictMode).
  if (_pageKill && _pageKill.featureId === featureId) {
    clearTimeout(_pageKill.timer);
    _pageKill = null;
  }
  const entry = { featureId };
  _pageReg = entry;
  return () => {
    if (_pageReg === entry) _pageReg = null;
    if (_pageKill) clearTimeout(_pageKill.timer);
    _pageKill = {
      featureId,
      timer: setTimeout(() => {
        _pageKill = null;
        if (_pageReg && _pageReg.featureId === featureId) return; // страница вернулась — окно живо
        let changed = false;
        if (_pendingView && _pendingView.featureId === featureId) { _pendingView = null; changed = true; }
        if (_lastApplied && _lastApplied.featureId === featureId) {
          _lastApplied = null;
          if (_consumeTimer) { clearTimeout(_consumeTimer); _consumeTimer = null; }
          changed = true;
        }
        if (changed) emit();
      }, 0),
    };
  };
}

// Сырой указатель (для эффекта «сброс при обычном открытии» в FeatureBanner).
export function getBackNav(): BackNav | null {
  return _back;
}

// «← назад» для чата `sessionId`: id исходного чата, если мы сейчас в `to`-ЧАТЕ;
// иначе null (перехода не было, открыли чат не из плашки, или `to` — страница фичи).
export function backNavFrom(sessionId: string | null): string | null {
  if (!sessionId || !_back || _back.to.kind !== 'chat' || _back.to.id !== sessionId) return null;
  return _back.from;
}

// Хук: id исходного чата для «← назад» (null, пока перехода не было).
export function useBackNavFrom(sessionId: string | null): string | null {
  return useSyncExternalStore(subscribe, () => backNavFrom(sessionId), () => backNavFrom(sessionId));
}

// «← к фиче {название}» (Ф-pl-b): чат открыт переходом ИЗ страницы фичи (recordFeatureReturn
// с fromKind='feature') и текущий чат — цель перехода (to.kind='chat', to.id=sessionId).
// Возвращает id и название исходной фичи; null — перехода из фичи не было. Отличается от
// backNavFrom (он — chat→chat): здесь дискриминатор fromKind='feature' обязателен.
export interface FeatureBackNavInfo { featureId: string; title: string | null; tab: FeaturePageTab; showTab: boolean }
// Снимок кэшируем по ссылке на запись _back: useSyncExternalStore требует стабильного
// значения между вызовами, иначе новый объект на каждый getSnapshot зациклит рендер.
let _infoSrc: BackNav | null = null;
let _info: FeatureBackNavInfo | null = null;
export function featureBackNavFrom(sessionId: string | null): FeatureBackNavInfo | null {
  if (!sessionId || !_back || _back.fromKind !== 'feature' || _back.to.kind !== 'chat' || _back.to.id !== sessionId) return null;
  if (_infoSrc !== _back) {
    _infoSrc = _back;
    _info = { featureId: _back.from, title: _back.fromTitle ?? null, tab: _back.fromTab ?? 'timeline', showTab: _back.showTab ?? true };
  }
  return _info;
}

// Хук: «← к фиче {название}» для шапки чата (null, пока перехода из фичи не было).
export function useFeatureBackNav(sessionId: string | null): FeatureBackNavInfo | null {
  return useSyncExternalStore(subscribe, () => featureBackNavFrom(sessionId), () => featureBackNavFrom(sessionId));
}

// «← к фиче {название}» для экрана открытой задачи (дефект 4c4645e2): задача открыта
// переходом ИЗ страницы фичи (recordFeatureReturn с to.kind='task') и текущая задача —
// цель перехода. Тот же снимок FeatureBackNavInfo и тот же кэш по ссылке на _back, что
// у чата: чип на задаче — тот же компонент, без нового стиля.
export function featureBackNavFromTask(taskId: string | null): FeatureBackNavInfo | null {
  if (!taskId || !_back || _back.fromKind !== 'feature' || _back.to.kind !== 'task' || _back.to.id !== taskId) return null;
  if (_infoSrc !== _back) {
    _infoSrc = _back;
    _info = { featureId: _back.from, title: _back.fromTitle ?? null, tab: _back.fromTab ?? 'timeline', showTab: _back.showTab ?? true };
  }
  return _info;
}

// Хук: «← к фиче {название}» для панели деталей задачи (null, пока перехода из фичи не было).
export function useFeatureBackNavForTask(taskId: string | null): FeatureBackNavInfo | null {
  return useSyncExternalStore(subscribe, () => featureBackNavFromTask(taskId), () => featureBackNavFromTask(taskId));
}

// Тестовые швы (не публичное API).
export const __test = {
  reset() {
    if (_consumeTimer) { clearTimeout(_consumeTimer); _consumeTimer = null; }
    if (_pageKill) { clearTimeout(_pageKill.timer); _pageKill = null; }
    _pageReg = null;
    _back = null;
    _pendingView = null;
    _lastApplied = null;
    _pendingSeq = 0;
    _viewProvider = null;
    _listeners.clear();
  },
  recordBackNav,
  recordFeatureReturn,
  clearBackNav,
  getBackNav,
  backNavFrom,
  featureBackNavFrom,
  featureBackNavFromTask,
  normalizeBackTarget,
  registerFeatureViewProvider,
  registerFeaturePage,
  consumeFeatureViewReturn,
  pendingFeatureView,
  commitFeatureViewApplied,
  lastAppliedFeatureView,
  CONSUME_MS,
};
