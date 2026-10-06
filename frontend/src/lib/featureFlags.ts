// Глобальный стор фич-флагов: эффективные значения per-user приходят с бэка
// (через /api/auth/me и /api/feature-flags) и раздаются компонентам хуком useFeature.
// Паттерн — как у offline.ts: модульное состояние + подписки + useSyncExternalStore.

import { useEffect, useSyncExternalStore } from 'react';
import { api } from './api';

// Тонкий реестр ключей для type-safe вызовов useFeature. Дублирует ключи из
// бэкового FeatureFlagCatalog (одна строка на флаг). Описания/дефолты/стадии
// приходят с сервера — здесь только ключи.
export const FLAGS = {
  workspaceDestructive: 'workspace-destructive',
  changeDossiersRecall: 'change-dossiers-recall',
  specialtyPromptSections: 'specialty-prompt-sections',
  // Автоправило архивации чатов (план «Архив чатов» v4, шаг 6): фоновый сервис
  // раз в час убирает в архив чаты без активности дольше порога. Флаг закрывает
  // ТОЛЬКО автоправило и его настройки; ручной архив, раздел «Архив» и сводка
  // карточки работают без флага.
  chatAutoArchive: 'chat-auto-archive',
  // Визуальный разворот плана: контекстные замечания к разделам (часть A).
  // Слой маркеров у заголовков и отправка обратной связи существующим
  // контрактом onRespond; сама разметка картой живёт под этим же флагом.
  visualPlan: 'visual-plan',
  // Каталог MCP-серверов (волна 1, задача 9fa075ec): поиск по реестру и предзаполнение
  // формы из черновика записи. Флаг — на вход в раздел; ручной путь «Добавить» работает
  // всегда.
  mcpCatalog: 'mcp-catalog',
  chatContext: 'chat-context',
  // Ветвление чата (docs/research/chat-branching-2026-09.md, §7): новый чат с копией
  // истории оригинала до выбранного шага.
  chatBranch: 'chat-branch',
  // Уборка карты проекта (CLAUDE.md), фронтовая часть: аккордеон в настройках проекта
  // и модалка с фактами сканера (мёртвые ссылки, длинные секции, вложенные карты).
  // Тумблер скрывает и секцию, и модалку; review/apply под ним закрыты на сервере 404.
  projectMapHygiene: 'project-map-hygiene',
  // Редактор картинок в проекте (ADR-017): вход «Редактировать» / «Нарисовать картинку»
  // и сам экран. Серверные ручки image-editor/* под этим же флагом отвечают 404.
  imageEditor: 'image-editor',
  // Локальные проекты (ADR-016): проект на устройстве владельца, ход идёт через агента.
  // Что доступно у такого проекта, решает матрица capabilities из DTO проекта, а не флаг.
  localProjects: 'local-projects',
  // Локальная модель по умолчанию для картинок и видео (MCP local-media).
  localMediaDefault: 'local-media-default',
  // Модуль «Звук» (ADR-021): озвучка, музыка и правка звука проекта.
  audioEditor: 'audio-editor',
  // Модуль «Видео»: сцены и фильм (ADR-022) — съёмка по кадрам A и B, сборка фильма без ИИ.
  videoEditor: 'video-editor',
// MIDI-просмотрщик: ноты из .mid в файлах и в редакторе звука.
  midiEditor: 'midi-editor',
  // Сферы: группы проектов с общей командой персон и хартией; зона персоны «Сфера».
  spheres: 'spheres',
  // Панель «Картинки» v5 «Создать / Править»: режим на чат и выбор по режиму. Выключен —
  // картинки ведут себя побайтно как раньше.
  imagePanelV5: 'image-panel-v5',
} as const;

export type FlagKey = (typeof FLAGS)[keyof typeof FLAGS];

let _flags: Record<string, boolean> = {};
const _listeners = new Set<() => void>();

function emit() {
  _listeners.forEach(fn => fn());
}

// Заменить весь набор флагов (на старте/после логина из ответа me)
export function setAllFlags(flags: Record<string, boolean>) {
  _flags = { ...flags };
  emit();
}

// Влить пришедшие значения поверх известных: пустой или частичный ответ не затирает
// ключи, которых в нём нет (в отличие от setAllFlags, которая заменяет набор целиком)
export function mergeFlags(flags: Record<string, boolean> | null | undefined) {
  if (!flags) return;
  _flags = { ..._flags, ...flags };
  emit();
}

// Оптимистичное локальное обновление одного флага (для мгновенной реакции тумблера)
export function setFlagLocal(key: string, value: boolean) {
  _flags = { ..._flags, [key]: value };
  emit();
}

export function getFlag(key: string): boolean {
  return _flags[key] ?? false;
}

export function getAllFlags(): Record<string, boolean> {
  return _flags;
}

export function subscribeFlags(fn: () => void): () => void {
  _listeners.add(fn);
  return () => _listeners.delete(fn);
}

// Подписка компонента на конкретный флаг. Возвращает true, если фича включена.
export function useFeature(key: FlagKey): boolean {
  return useSyncExternalStore(
    subscribeFlags,
    () => getFlag(key),
    () => getFlag(key),
  );
}

// Освежение стора: флаги читаются при старте SPA, а тумблер мог сменить другой клиент
// или прямой PUT. Дросселирование — чтобы alt-tab не дёргал сервер на каждый возврат.
export const FLAGS_REFRESH_MIN_INTERVAL_MS = 45_000;

export function createFlagsRefresher(
  fetchFlags: () => Promise<Record<string, boolean> | null | undefined>,
  now: () => number = Date.now,
  minIntervalMs: number = FLAGS_REFRESH_MIN_INTERVAL_MS,
) {
  let last = Number.NEGATIVE_INFINITY;
  return async function refresh(force = false): Promise<boolean> {
    const t = now();
    if (!force && t - last < minIntervalMs) return false;
    last = t;
    try {
      mergeFlags(await fetchFlags());
    } catch {
      // сбой освежения не критичен: остаются прежние значения
    }
    return true;
  };
}

// Один раз на всё приложение (после авторизации): при монтировании и при возврате
// окну видимости/фокуса.
export function useFeatureFlagsRefresh(enabled: boolean) {
  useEffect(() => {
    if (!enabled) return;
    const refresh = createFlagsRefresher(() => api.auth.me().then(me => me?.featureFlags));
    void refresh(true);
    const onVisible = () => { if (document.visibilityState === 'visible') void refresh(); };
    const onFocus = () => { void refresh(); };
    document.addEventListener('visibilitychange', onVisible);
    window.addEventListener('focus', onFocus);
    return () => {
      document.removeEventListener('visibilitychange', onVisible);
      window.removeEventListener('focus', onFocus);
    };
  }, [enabled]);
}
