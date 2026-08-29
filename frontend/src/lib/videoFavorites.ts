import { useEffect, useSyncExternalStore } from 'react';
import type { VideoChannel } from '../types';
import { api } from './api';

// Избранные каналы «Видео»: то, что показывает полоса в шапке панели, центрального
// острова и плавающего окна.
//
// Хранение СЕРВЕРНОЕ (per-user, users.json), а не в localStorage: продукт открывают
// и с десктопа, и с планшета, и набор каналов из тех настроек, что ждёшь одинаковыми
// везде. Плата — асинхронный старт, поэтому у стора есть состояние «ещё не знаем»
// (keys === null): пока оно держится, полоса не рисуется вовсе — мигнуть дефолтом
// и тут же сменить его на чужой набор хуже, чем показаться на четверть секунды позже.
//
// Ключ канала — «провайдер:id»: у источников свои пространства id, и «1» у СМОТРИМ
// столкнулось бы с «1» у YouTube.
//
// Три места показа читают ОДИН стор и ОДИН список каналов (см. useLiveChannels ниже):
// иначе каждое тянуло бы каталог само, и на переезде кадра панель↔центр↔окно шли бы
// три одинаковых запроса подряд.

/** Ключ канала в наборе избранного. */
export function channelKey(c: Pick<VideoChannel, 'id' | 'provider'>): string {
  return `${c.provider}:${c.id}`;
}

let keys: string[] | null = null;
// Настраивал ли человек набор сам. Пустой набор при false — это дефолт с сервера,
// при true — осознанно снятые звёздочки: разные экраны, и свести их в одно нельзя.
let configured = false;
let loading = false;
const listeners = new Set<() => void>();

function emit() { for (const cb of listeners) cb(); }

function subscribe(cb: () => void): () => void {
  listeners.add(cb);
  return () => { listeners.delete(cb); };
}

// Снапшот для useSyncExternalStore: тот сравнивает ССЫЛКУ, поэтому массив пересобираем
// только при настоящем изменении, а не на каждый рендер — иначе бесконечный цикл.
let snapshot: { keys: string[] | null; configured: boolean } = { keys: null, configured: false };

function write(next: string[] | null, isConfigured: boolean): void {
  keys = next;
  configured = isConfigured;
  snapshot = { keys: next, configured: isConfigured };
  emit();
}

function getSnapshot() { return snapshot; }

/** Один запрос на всё приложение: повторные вызовы во время полёта ничего не делают. */
export function loadFavorites(): void {
  if (keys !== null || loading) return;
  loading = true;
  void (async () => {
    try {
      const res = await api.video.favorites();
      write(res.keys, res.configured);
    } catch {
      // Сервер не ответил — набор остаётся неизвестным, полоса не рисуется. Показать
      // здесь дефолт значило бы соврать: человек мог как раз снять все звёздочки.
    } finally {
      loading = false;
    }
  })();
}

/** Набор избранного. null — ещё не загружен. */
export function useFavoriteKeys(): { keys: string[] | null; configured: boolean } {
  const value = useSyncExternalStore(subscribe, getSnapshot, getSnapshot);
  useEffect(loadFavorites, []);
  return value;
}

/**
 * Поставить или снять звёздочку. Изменение применяется СРАЗУ, до ответа сервера:
 * полоса — это переключатель, и ждать сети на каждое нажатие нельзя. Отказ сервера
 * откатывает набор к прежнему — молча тянуть звёздочку, которой на сервере нет, хуже.
 */
export async function toggleFavorite(c: Pick<VideoChannel, 'id' | 'provider'>): Promise<void> {
  const key = channelKey(c);
  const before = keys;
  if (before === null) return; // набор ещё не загружен — переключать нечего

  const next = before.includes(key) ? before.filter(k => k !== key) : [...before, key];
  write(next, true);
  try {
    const res = await api.video.setFavorites(next);
    // Сервер нормализует набор (дедуп, потолок) — берём его ответ как истину
    write(res.keys, res.configured);
  } catch {
    write(before, configured);
  }
}

// ── Каналы источников ────────────────────────────────────────────────────────────

// Каталог играбельных каналов, общий на три места показа. Держим в модуле, а не в
// состоянии компонентов: полоса живёт одновременно в панели и в шапке окна, и каждый
// собственный запрос был бы вторым обходом каталога (тот на сервере ходит по 41 карточке).
let channels: VideoChannel[] | null = null;
let channelsLoading = false;
// Снапшот отдельным объектом: useSyncExternalStore сравнивает ССЫЛКУ, поэтому собирать
// его на каждый вызов нельзя — получился бы бесконечный рендер.
let channelsSnapshot: { channels: VideoChannel[] | null; failed: boolean } =
  { channels: null, failed: false };
const channelListeners = new Set<() => void>();

function emitChannels() { for (const cb of channelListeners) cb(); }

function subscribeChannels(cb: () => void): () => void {
  channelListeners.add(cb);
  return () => { channelListeners.delete(cb); };
}

function getChannelsSnapshot() { return channelsSnapshot; }

/**
 * Играбельные каналы эфира. Неиграбельные сюда не попадают намеренно: полоса
 * переключает то, что можно СМОТРЕТЬ, а карточка-ссылка на чужой сайт переключателем
 * быть не может.
 */
export function loadLiveChannels(force = false): void {
  if ((channels !== null && !force) || channelsLoading) return;
  channelsLoading = true;
  void (async () => {
    try {
      const res = await api.video.channels('smotrim');
      channels = res.channels.filter(c => c.embeddable && c.embedUrl);
      // Пустой ответ — тоже отказ: играбельные каналы в каталоге есть всегда, а вот
      // сервис мог ответить огрызком. Панель обязана сказать об этом, а не молчать
      channelsSnapshot = { channels, failed: channels.length === 0 };
      emitChannels();
    } catch {
      channelsSnapshot = { channels: null, failed: true };
      emitChannels();
    } finally {
      channelsLoading = false;
    }
  })();
}

/** Каналы эфира и признак отказа. channels === null — ещё не загружены. */
export function useLiveChannelsState(): { channels: VideoChannel[] | null; failed: boolean } {
  const value = useSyncExternalStore(subscribeChannels, getChannelsSnapshot, getChannelsSnapshot);
  useEffect(() => loadLiveChannels(), []);
  return value;
}

/** Все играбельные каналы эфира. null — ещё не загружены. */
export function useLiveChannels(): VideoChannel[] | null {
  return useLiveChannelsState().channels;
}

/**
 * Что показывает полоса: избранные каналы в порядке КАТАЛОГА, а не в порядке добавления
 * в избранное. Порядок каталога — это кнопки телевизора, он привычен и не меняется от
 * того, в каком настроении отмечали звёздочки.
 *
 * null — данных ещё нет (каталог или набор не загружены), и полосу рисовать рано.
 */
export function useStripChannels(): VideoChannel[] | null {
  const all = useLiveChannels();
  const { keys: favorites } = useFavoriteKeys();
  if (!all || !favorites) return null;
  const wanted = new Set(favorites);
  return all.filter(c => wanted.has(channelKey(c)));
}
