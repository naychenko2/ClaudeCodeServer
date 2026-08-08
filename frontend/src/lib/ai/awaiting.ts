// Глобальный сигнал «Claude ждёт ответа человека» для плавающей кнопки AI-хаба.
// Источник истины — поток статусов сессий (SignalR status_changed по всем чатам), а не
// смонтированный ChatPanel: смысл состояния ровно в том, чтобы узнать про ожидание,
// находясь в ДРУГОМ разделе. Пока чат открыт, его покрывает тот же глобальный поток.
// Образец подписки — lib/agentBoard.ts. Имена чатов status_changed не несёт, поэтому
// подпись берём из кэша метаданных, который наполняет сводка главной (api.home.summary);
// она же — начальное состояние (чат мог ждать ещё до загрузки страницы) и страховка после
// обрыва связи (onReconnected). «Адрес важнее подписи»: чат кликабелен и без имени.

import { useSyncExternalStore } from 'react';
import type { HomeSessionInfo } from '../../types';
import { api } from '../api';
import { onMessage, onReconnected, joinUser } from '../signalr';

export interface AiAwaitingRec {
  chatId: string;
  // Подпись строки в балуне FAB. Пустая строка — имя неизвестно: строка покажет
  // нейтральную подпись, но чат остаётся кликабельным (адрес важнее подписи).
  name: string;
  // Проектный чат — открыть воркспейс проекта; null/undefined у чата вне проекта.
  projectId?: string | null;
}

// Ждущие чаты: chatId → запись. Составом управляет поток статусов.
const POLL_MS = 30_000;
const awaiting = new Map<string, AiAwaitingRec>();
// Кэш метаданных (имя, проект) из сводки: status_changed имени не несёт, поэтому при
// включении waitingберём подпись отсюда. Наполняется при каждой загрузке сводки.
const meta = new Map<string, { name: string; projectId?: string | null }>();

const listeners = new Set<() => void>();
const emit = () => listeners.forEach(l => l());

// Членство в user_{userId} — туда бэк шлёт status_changed по всем чатам владельца.
// Повторный JoinUser для того же соединения безопасен (образец — lib/tasks.ts).
function joinUserGroup() {
  const uid = localStorage.getItem('cc_user_id') || sessionStorage.getItem('cc_user_id');
  if (uid) joinUser(uid).catch(() => {});
}

// Поколение состояния: инкрементируем на каждом realtime-событии и на старте загрузки
// сводки. Если за время сетевого запроса пришёл status_changed, поколение меняется —
// и устаревший снимок сводки отбрасывается, не успев затереть свежее realtime-состояние
// (гонка опроса и потока статусов: иначе старый снимок «воскрешал» уже отвеченный чат
// или гасил только что ушедший в waiting).
let _gen = 0;

// Стабильный снимок для useSyncExternalStore: пересобираем только при смене состава,
// иначе каждое чтение getSnapshot давало бы новый массив → бесконечный ререндер.
let snapshot: AiAwaitingRec[] = [];
const EMPTY: AiAwaitingRec[] = [];
// Равны ли два снимка по составу. Порядок Map.values() стабилен для одного набора
// ключей (Map сохраняет позицию существующего ключа при set), поэтому сравниваем
// поэлементно — этого достаточно, чтобы опросный тик без смены состава не плодил
// новый массив и лишний ререндер.
function sameSnapshot(a: AiAwaitingRec[], b: AiAwaitingRec[]): boolean {
  if (a === b) return true;
  if (a.length !== b.length) return false;
  for (let i = 0; i < a.length; i++) {
    const x = a[i], y = b[i];
    if (x.chatId !== y.chatId || x.name !== y.name || (x.projectId ?? null) !== (y.projectId ?? null)) return false;
  }
  return true;
}
function recompute() {
  const next = awaiting.size === 0 ? EMPTY : Array.from(awaiting.values());
  if (sameSnapshot(snapshot, next)) return;
  snapshot = next;
  emit();
}

function putAwaiting(chatId: string): void {
  const m = meta.get(chatId);
  awaiting.set(chatId, { chatId, name: m?.name ?? '', projectId: m?.projectId ?? null });
}

// Перестроить ждущих из снимка сводки (начальная загрузка / реконнект). Сводка —
// единственный источник по проектным чатам: их статусы не доезжают в user-группу SignalR
// (только в группы session/project), поэтому пуллинг summary их и ловит.
function applySummary(active: HomeSessionInfo[]): void {
  for (const s of active) {
    meta.set(s.id, { name: s.name ?? s.projectName ?? '', projectId: s.projectId ?? null });
  }
  awaiting.clear();
  for (const s of active) {
    if (s.status === 'waiting') putAwaiting(s.id);
  }
  recompute();
}

async function loadFromSummary(): Promise<void> {
  // Старт поколения: если за время запроса придёт realtime-событие, _gen вырастет
  // и устаревший снимок будет отброшен в проверке ниже.
  const gen = ++_gen;
  try {
    // recent=1: параметр режет только ленту недавних, active не ограничен
    // (HomeController.GetSummary) — большее значение тянуло лишние DTO каждые 30 с.
    const res = await api.home.summary(1);
    if (gen !== _gen) return; // realtime уже изменил состояние — снимок устарел
    applySummary(res.active);
  } catch {
    // Офлайн/ошибка — текущее состояние не сбрасываем
  }
}

let _wired = false;
function wireRealtime() {
  if (_wired) return;
  _wired = true;
  onMessage(msg => {
    if (msg.type === 'status_changed') {
      // Realtime тронул состояние — летящий снимок сводки должен быть отброшен
      _gen++;
      if (msg.status === 'waiting') {
        if (!awaiting.has(msg.sessionId)) { putAwaiting(msg.sessionId); recompute(); }
        return;
      }
      // Любой иной статус (working/starting/finished/active/error/…) — чат больше не ждёт
      if (awaiting.delete(msg.sessionId)) recompute();
      return;
    }
    if (msg.type === 'chat_deleted') {
      _gen++;
      meta.delete(msg.sessionId);
      if (awaiting.delete(msg.sessionId)) recompute();
    }
  });
  // После обрыва связи обновляем членство в группе (как соседние сторы) и переснимаем
  // состояние: за простой мы могли пропустить status_changed.
  onReconnected(() => { joinUserGroup(); void loadFromSummary(); });
  // Статусы ПРОЕКТНЫХ чатов не доезжают в user-группу SignalR (только в session/project),
  // а status_changed не несёт ни имени, ни projectId. Поэтому периодически пересчитываем
  // состав и meta-кэш из сводки — она единственно достоверно видит все проекты и чаты.
  // Тот же эндпоинт, что у дашборда «Домой» (прецедент — lib/agentBoard).
  setInterval(() => { void loadFromSummary(); }, POLL_MS);
}

// Первичная загрузка + запуск реалтайм-подписки. Вызывается из AiLauncher при монтировании
// (он рендерится глобально и живёт во всех разделах).
export function ensureAiAwaitingLoaded(): Promise<void> {
  wireRealtime();
  joinUserGroup();
  return loadFromSummary();
}

function subscribe(cb: () => void): () => void {
  listeners.add(cb);
  return () => { listeners.delete(cb); };
}
const getSnapshot = () => snapshot;

export function useAiAwaiting(): AiAwaitingRec[] {
  return useSyncExternalStore(subscribe, getSnapshot, getSnapshot);
}

// Очистка данных прежнего пользователя при разлогине: имена чатов не должны жить в
// памяти вкладки после смены аккаунта. Слушатели НЕ трогаем — стор глобальный (как
// agentBoard), а listeners.clear() навсегда оглушил бы все useSyncExternalStore-подписки.
export function resetAiAwaiting() {
  awaiting.clear();
  meta.clear();
  snapshot = EMPTY;
  _gen++;
  emit();
}
