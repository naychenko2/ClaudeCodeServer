// Контекст чата (фича chat-context): материалы, приложенные к чату явной кнопкой.
//
// Кеш per-session (sessionId → состав) в модульном сторе — образец useExternalPreviewLinks.
// Почему стор, а не состояние WorkspacePage: состав меняется из любого окна (PUT +
// broadcast context_updated), а читать его будет полоса контекста сплита (B3) и кнопки
// «в контекст» (B4, индикация «уже в контексте») — им нужен один источник без
// перезагрузки. Записи хранятся как отдаёт GET — с признаком missing; событие
// context_updated приходит без missing и обновляет состав по-быстрому, следующий GET
// доведёт признаки до истины.
import { useSyncExternalStore } from 'react';
import type { ServerMessage, SessionContextEntry } from '../types';
import { api } from './api';

// Составы по sessionId. Внутри — только ссылки из GET/события: кладём всегда новый
// массив, useSyncExternalStore различает обновления по ссылке
let entries = new Map<string, SessionContextEntry[]>();
const listeners = new Set<() => void>();

function emit(): void {
  listeners.forEach(l => l());
}

function subscribe(l: () => void): () => void {
  listeners.add(l);
  return () => { listeners.delete(l); };
}

// Первичная загрузка состава (GET с live: true внутри api). Идемпотентна по «кеша нет»:
// актуальность после загрузки держат событие context_updated и saveChatContext
export async function loadChatContext(projectId: string, sessionId: string): Promise<void> {
  if (entries.has(sessionId)) return;
  try {
    entries.set(sessionId, await api.sessions.getContext(projectId, sessionId));
    emit();
  } catch {
    // офлайн/ошибка — кеша нет, полоса решает, что показывать; повторная попытка —
    // следующим вызовом (кеша-то нет)
  }
}

// Замена состава: PUT + обязательный ре-GET за признаками missing (ответ PUT их не
// несёт). Свежий GET и есть новое содержимое кеша
export async function saveChatContext(projectId: string, sessionId: string,
  list: Pick<SessionContextEntry, 'type' | 'id' | 'title'>[]): Promise<SessionContextEntry[]> {
  const fresh = await api.sessions.putContext(projectId, sessionId, list);
  entries.set(sessionId, fresh);
  emit();
  return fresh;
}

// Применить серверное событие context_updated (полный состав после PUT в любом окне).
// Записи события без missing — прошлая оценка недействительна, сервер её пересчитает
// в ближайшем GET
export function applyContextUpdated(msg: ServerMessage): void {
  if (msg.type !== 'context_updated') return;
  entries.set(msg.sessionId, msg.entries);
  emit();
  // Признаки missing считает только сервер, и в событии их нет — иначе материал,
  // добавленный из ДРУГОГО окна, так и остался бы без пометки «не найден» до
  // переключения чата. Догоняем их отдельным GET (проект знаем из открытого чата)
  if (activeChat?.sessionId !== msg.sessionId) return;
  const { projectId, sessionId } = activeChat;
  void api.sessions.getContext(projectId, sessionId)
    .then(fresh => { entries.set(sessionId, fresh); emit(); })
    .catch(() => { /* офлайн — состав из события остаётся, признаки догонит следующий GET */ });
}

// Разовое чтение (кнопкам B4 для индикации «уже в контексте»); undefined — не грузили
export function getChatContext(sessionId: string): SessionContextEntry[] | undefined {
  return entries.get(sessionId);
}

// Подписка на состав конкретного чата (полоса B3); undefined — состав ещё не грузили
export function useChatContext(sessionId: string | null): SessionContextEntry[] | undefined {
  return useSyncExternalStore(
    subscribe,
    () => entries.get(sessionId ?? ''),
    () => undefined,
  );
}

// Непустой ли контекст чата: полосу и её обёртку (фон, высота, граница) рисуют
// РАЗНЫЕ компоненты, и решать «показывать ли ряд» они обязаны по одному признаку
export function useHasChatContext(sessionId: string | null): boolean {
  const list = useChatContext(sessionId);
  return !!list && list.length > 0;
}

// === Открытый чат экрана ===
// Кнопки «в контекст чата» (B4) живут далеко от владельца сессии: строка дерева
// файлов, шапка просмотрщика, карточка задачи. Тащить туда проп через пять
// уровней ради «есть ли открытый чат» — дороже, чем одна ячейка в том же сторе,
// который эти кнопки и так читают. Выставляет владелец экрана (WorkspacePage),
// снимает — он же при уходе.
let activeChat: ActiveChat | null = null;

export interface ActiveChat { projectId: string; sessionId: string }

export function setActiveChatForContext(v: ActiveChat | null): void {
  if (v?.projectId === activeChat?.projectId && v?.sessionId === activeChat?.sessionId) return;
  activeChat = v;
  emit();
}

// Открытый чат для кнопок «в контекст»; null — чата нет, кнопки не показываются
export function useActiveChatForContext(): ActiveChat | null {
  return useSyncExternalStore(subscribe, () => activeChat, () => null);
}

// === Правка состава ===
// Записи сравниваются по паре тип+адрес; у файлов разделители приводятся к «/»
// (дерево отдаёт posix-путь, а из ленты чата тот же файл приходит с обратными)
export function contextKey(type: SessionContextEntry['type'], id: string): string {
  return `${type}:${type === 'file' ? id.replace(/\\/g, '/') : id}`;
}

// Есть ли материал в контексте чата (индикация кнопок). undefined-состав = «ещё не
// грузили» — считаем, что нет: кнопка предложит добавить, PUT идемпотентен
export function inChatContext(list: SessionContextEntry[] | undefined, type: SessionContextEntry['type'], id: string): boolean {
  const key = contextKey(type, id);
  return !!list?.some(e => contextKey(e.type, e.id) === key);
}

// Состав без служебного признака missing — то, что уходит в PUT
function toPayload(list: SessionContextEntry[]): Pick<SessionContextEntry, 'type' | 'id' | 'title'>[] {
  return list.map(e => ({ type: e.type, id: e.id, title: e.title }));
}

// Добавить материал (идемпотентно: повторное добавление ничего не меняет).
// Возвращает true, если состав изменился — вызывающая сторона показывает тост
export async function addToChatContext(projectId: string, sessionId: string,
  entry: Pick<SessionContextEntry, 'type' | 'id' | 'title'>): Promise<boolean> {
  await loadChatContext(projectId, sessionId);
  const list = entries.get(sessionId) ?? [];
  if (inChatContext(list, entry.type, entry.id)) return false;
  await saveChatContext(projectId, sessionId, [...toPayload(list), entry]);
  return true;
}

// Убрать материал из контекста
export async function removeFromChatContext(projectId: string, sessionId: string,
  type: SessionContextEntry['type'], id: string): Promise<void> {
  const list = entries.get(sessionId) ?? await api.sessions.getContext(projectId, sessionId);
  const key = contextKey(type, id);
  await saveChatContext(projectId, sessionId, toPayload(list).filter(e => contextKey(e.type, e.id) !== key));
}

// Заменить запись по месту («Указать заново…» у ненайденного материала): порядок
// в полосе сохраняется — иначе переуказанный материал уезжал бы в конец ряда
export async function replaceChatContextEntry(projectId: string, sessionId: string,
  type: SessionContextEntry['type'], id: string,
  next: Pick<SessionContextEntry, 'type' | 'id' | 'title'>): Promise<void> {
  const list = entries.get(sessionId) ?? await api.sessions.getContext(projectId, sessionId);
  const key = contextKey(type, id);
  await saveChatContext(projectId, sessionId, toPayload(list).map(e => contextKey(e.type, e.id) === key ? next : e));
}
