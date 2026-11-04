// W12 · Счётчики рельсы: число на «Входящих» (пакеты, ждущие решения) и число на «Здоровье»
// (находки последнего отчёта выбранного раздела). Читает их рельса (подача в railBadges — W3,
// вне этого шага) через хук; перечитывать приказывают экраны, меняющие данные: «Входящие»
// (closePackage) и «Здоровье» (после успешной проверки).
//
// Состояние — модульное (образец: lib/tasks.ts, featureFlags): обычная функция
// refreshWikiRailCounts() должна вызываться из экрана, который сам числа не читает, а значение
// обязано доходить до хука рельсы. Компонент своего setState из ответов не зовёт вовсе: ответ
// обновляет состояние и извещает подписчиков; размонтированный подписчик уже не подписан —
// «ответ после размонтирования не зовёт setState» держится конструкцией.
//
// Порядок ответов — тот же инвариант, что везде в вики (урок S5b/S5e, образец loadSeq в
// WikiInbox.tsx): на каждый поток свой счётчик поколения, устаревший ответ (запрос начался
// раньше, пришёл позже) отбрасывается. Счётчики inbox и health отдельные — потоки не затирают
// друг друга.
//
// Отказ (409 «вики выключена», сеть, отчёт с «## Отказ») — значение null: числа на рельсе нет.
// Без необработанного исключения и без console.error наружу: .catch на каждом запросе.

import { useEffect, useSyncExternalStore } from 'react';
import { getWikiHealth, listWikiPackages } from 'aihome_shell/kit';
import { parseHealth } from './WikiHealth';

export interface WikiRailCounts {
  /** Пакетов со статусом proposed по всем разделам; null — отказа чтения */
  inbox: number | null;
  /** Находок в последнем отчёте раздела healthKey; null — ключа нет или отчёт отказан */
  health: number | null;
}

let counts: WikiRailCounts = { inbox: null, health: null };
const listeners = new Set<() => void>();

// Поколение по потокам (образец loadSeq, WikiInbox.tsx:147): только последний запрос пишет значение
let inboxSeq = 0;
let healthSeq = 0;

// Ключ чтения здоровья регистрирует хук (раздел открытого «Здоровья»): refreshWikiRailCounts()
// без аргументов — зовущий экран ключа рельсы не знает. Ключ один на модуль: при нескольких
// одновременно живых хуках с разными ключами действует последний зарегистрированный.
let healthKey: string | null = null;

function emit(): void {
  for (const l of [...listeners]) l();
}

function loadInbox(): void {
  const mine = ++inboxSeq;
  listWikiPackages()
    .then(d => { if (inboxSeq === mine) { counts = { ...counts, inbox: d.packages.length }; emit(); } })
    .catch(() => { if (inboxSeq === mine) { counts = { ...counts, inbox: null }; emit(); } });
}

function loadHealth(): void {
  const mine = ++healthSeq;
  if (healthKey === null) {
    // Раздела нет — числа нет: счёт находок относится к разделу, не к вики целиком
    counts = { ...counts, health: null };
    emit();
    return;
  }
  getWikiHealth(healthKey)
    .then(d => {
      if (healthSeq !== mine) return;
      const r = parseHealth(d.content);
      // «## Отказ» — прогон раздела не завершился: число проблем неизвестно, числа на рельсе нет.
      // Пустой отчёт — честный ноль, не отказ.
      const value = r.failure !== null ? null : r.findings.length;
      counts = { ...counts, health: value };
      emit();
    })
    .catch(() => { if (healthSeq === mine) { counts = { ...counts, health: null }; emit(); } });
}

// Перечитать оба числа. Зовётся из экранов, изменивших данные («Входящие» — закрытие пакета,
// «Здоровье» — успешная проверка), и по visibilitychange → visible.
export function refreshWikiRailCounts(): void {
  loadInbox();
  loadHealth();
}

// Перечитка при возвращении вкладки в фон живого экрана (образец useSession.ts:519): за время
// в фоне счёт мог устареть (ночной прогон, решение в другой вкладке). Слушатель живёт только
// пока есть читающий: ставится на первой подписке, снимается на последней отписке.
let visibilityHandler: (() => void) | null = null;
function attachVisibility(): void {
  if (visibilityHandler !== null) return;
  visibilityHandler = () => {
    if (document.visibilityState !== 'visible') return;
    refreshWikiRailCounts();
  };
  document.addEventListener('visibilitychange', visibilityHandler);
}
function detachVisibility(): void {
  if (visibilityHandler === null) return;
  document.removeEventListener('visibilitychange', visibilityHandler);
  visibilityHandler = null;
}

function subscribe(fn: () => void): () => void {
  listeners.add(fn);
  if (listeners.size === 1) attachVisibility();
  return () => {
    listeners.delete(fn);
    if (listeners.size === 0) detachVisibility();
  };
}

export function useWikiRailCounts(healthSectionKey: string | null): WikiRailCounts {
  const snapshot = useSyncExternalStore(subscribe, () => counts, () => counts);
  useEffect(() => {
    // Перечитка при монтировании и при смене ключа: ключ регистрируется в состоянии модуля —
    // с него health читается для текущего раздела, ключа нет — health=null без запроса
    healthKey = healthSectionKey;
    refreshWikiRailCounts();
  }, [healthSectionKey]);
  return snapshot;
}
