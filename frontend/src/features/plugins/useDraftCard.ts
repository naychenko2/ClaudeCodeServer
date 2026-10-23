// Обёртка данных карточки черновика (ADR-031, шаг 4.4): грузит предпросмотр и (по
// требованию) сравнение, подписывается на plugin_draft_changed (агент правит черновик —
// обновляем предпросмотр и подсвечиваем «Черновик обновлён агентом»).
//
// Чистая логика (вклады, блокировка «Принять», срок, diff-подписи) — в draftModel.ts;
// здесь только жизненный цикл: загрузка + подписка. DraftCard «тупой» — данные приходят
// отсюда, поэтому его рендерят в строку в тестах.

import { useCallback, useEffect, useRef, useState } from 'react';
import { onMessage } from '../../lib/signalr';
import { api, type PluginDraftDiff, type PluginDraftPreview, type PluginDraftSummary } from '../../lib/api';

// Сколько держать подсветку «Черновик обновлён агентом» после события, мс.
const JUST_UPDATED_MS = 6000;

export interface DraftCardData {
  // Предпросмотр (вклады/права/секреты/файлы/проверка). null — ещё не загружен
  preview: PluginDraftPreview | null;
  // Сравнение с установленной версией. null — не запрашивали
  diff: PluginDraftDiff | null;
  // Ошибка запроса diff (понятным текстом): состояние «diff не загружен» в окне файлов.
  diffError: string | null;
  // Идёт загрузка предпросмотра
  loading: boolean;
  // Ошибка сервера (403/404/409) — понятным текстом
  error: string | null;
  // «Черновик обновлён агентом»: true, пока действует подсветка после события
  justUpdated: boolean;
  // Запросить сравнение (открытие вкладки «Изменения»); идемпотентно
  loadDiff: () => void;
  // Повторить загрузку предпросмотра (кнопка «Перезагрузить» в шторке)
  refresh: () => void;
}

export function useDraftCard(summary: PluginDraftSummary): DraftCardData {
  const [preview, setPreview] = useState<PluginDraftPreview | null>(null);
  const [diff, setDiff] = useState<PluginDraftDiff | null>(null);
  const [diffError, setDiffError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [justUpdated, setJustUpdated] = useState(false);
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);

  // Загрузка предпросмотра. slug стабилен на время жизни карточки.
  const loadPreview = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const p = await api.plugins.draftPreview(summary.slug);
      setPreview(p);
    } catch (e: unknown) {
      setError(e instanceof Error ? e.message : 'Не удалось загрузить предпросмотр');
    } finally {
      setLoading(false);
    }
  }, [summary.slug]);

  // Первичная загрузка при монтировании.
  useEffect(() => { void loadPreview(); }, [loadPreview]);

  // Агент правит черновик (plugin_draft_changed): обновляем предпросмотр и подсвечиваем.
  // Подсветка гаснет по таймеру; повторное событие продлевает.
  useEffect(() => {
    const off = onMessage(msg => {
      if (msg.type !== 'plugin_draft_changed' || msg.slug !== summary.slug) return;
      setJustUpdated(true);
      if (timer.current) clearTimeout(timer.current);
      timer.current = setTimeout(() => setJustUpdated(false), JUST_UPDATED_MS);
      void loadPreview();
    });
    return () => { off(); if (timer.current) clearTimeout(timer.current); };
  }, [summary.slug, loadPreview]);

  // Сравнение — по требованию (вкладка «Изменения»); идемпотентно. Ошибка не блокирует
  // просмотр файлов, но показывается в окне («diff не загружен» + «Повторить»).
  const loadDiff = useCallback(() => {
    if (diff) return;
    setDiffError(null);
    api.plugins.draftDiff(summary.slug)
      .then(setDiff)
      .catch((e: unknown) => setDiffError(e instanceof Error ? e.message : 'Не удалось сравнить с установленной версией'));
  }, [diff, summary.slug]);

  return { preview, diff, diffError, loading, error, justUpdated, loadDiff, refresh: () => void loadPreview() };
}
