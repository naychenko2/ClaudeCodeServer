// Сравнение черновика с установленной версией — по требованию (окно «Файлы черновика»).
// Общий хук карточки в настройках (useDraftCard) и карточки в ленте (DraftFeedCard).
//
// version — отметка состояния черновика (ревизия или счётчик plugin_draft_changed): смена
// обнуляет diff/diffError, а если сравнение уже запрашивали (окно открывали) — запрашивает
// заново. Ответ, ушедший до смены version, отбрасывается; повторный loadDiff при запросе
// в полёте второго запроса не шлёт.

import { useCallback, useEffect, useRef, useState } from 'react';
import { api, type PluginDraftDiff } from '../../lib/api';

export interface DraftDiffData {
  diff: PluginDraftDiff | null;
  // Ошибка запроса (понятным текстом): состояние «diff не загружен» + «Повторить»
  diffError: string | null;
  // Запросить сравнение; идемпотентно (загружено или уже в полёте — ничего не делает)
  loadDiff: () => void;
}

export function useDraftDiff(slug: string, version: string | number): DraftDiffData {
  const [diff, setDiff] = useState<PluginDraftDiff | null>(null);
  const [diffError, setDiffError] = useState<string | null>(null);
  // Номер поколения: растёт при смене slug/version, устаревшие ответы его не совпадут
  const generation = useRef(0);
  const inFlight = useRef(false);
  const wanted = useRef(false);
  const loaded = useRef(false);

  const fetchDiff = useCallback(() => {
    if (inFlight.current || loaded.current) return;
    inFlight.current = true;
    setDiffError(null);
    const gen = generation.current;
    api.plugins.draftDiff(slug)
      .then(d => {
        if (gen !== generation.current) return;
        loaded.current = true;
        setDiff(d);
      })
      .catch((e: unknown) => {
        if (gen !== generation.current) return;
        setDiffError(e instanceof Error ? e.message : 'Не удалось сравнить с установленной версией');
      })
      .finally(() => { if (gen === generation.current) inFlight.current = false; });
  }, [slug]);

  // Смена версии/slug: сброс и, если сравнение уже просили, повторный запрос
  const first = useRef(true);
  useEffect(() => {
    if (first.current) { first.current = false; return; }
    generation.current++;
    inFlight.current = false;
    loaded.current = false;
    setDiff(null);
    setDiffError(null);
    if (wanted.current) fetchDiff();
  }, [slug, version, fetchDiff]);

  const loadDiff = useCallback(() => { wanted.current = true; fetchDiff(); }, [fetchDiff]);

  return { diff, diffError, loadDiff };
}
