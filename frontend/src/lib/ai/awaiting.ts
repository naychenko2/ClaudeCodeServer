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
import { onMessage, onReconnected } from '../signalr';

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
// включении waiting берём подпись отсюда. Наполняется при каждой загрузке сводки.
const meta = new Map<string, { name: string; projectId?: string | null }>();

const listeners = new Set<() => void>();
const emit = () => listeners.forEach(l => l());

// Стабильный снимок для useSyncExternalStore: пересобираем только при смене состава,
// иначе каждое чтение getSnapshot давало бы новый массив → бесконечный ререндер.
let snapshot: AiAwaitingRec[] = [];
const EMPTY: AiAwaitingRec[] = [];
function recompute() {
  snapshot = awaiting.size === 0 ? EMPTY : Array.from(awaiting.values());
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
  try {
    const res = await api.home.summary(40);
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
      if (msg.status === 'waiting') {
        if (!awaiting.has(msg.sessionId)) { putAwaiting(msg.sessionId); recompute(); }
        return;
      }
      // Любой иной статус (working/starting/finished/active/error/…) — чат больше не ждёт
      if (awaiting.delete(msg.sessionId)) recompute();
      return;
    }
    if (msg.type === 'chat_deleted') {
      meta.delete(msg.sessionId);
      if (awaiting.delete(msg.sessionId)) recompute();
    }
  });
  onReconnected(() => { void loadFromSummary(); });
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

// Очистка данных (для тестов). Реалтайм-подписка живёт вечно (как в agentBoard).
export function resetAiAwaiting() {
  awaiting.clear();
  meta.clear();
  snapshot = EMPTY;
  listeners.clear();
}
