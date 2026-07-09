import { useEffect, useRef, useState } from 'react';

// Настройки графа в духе Obsidian Graph View: фильтры, группы-раскраска,
// отображение, силы симуляции. Персист в localStorage per-ключ
// (глобальный и локальный графы хранят настройки раздельно).

export interface GraphGroup {
  query: string;   // запрос в синтаксисе graphQuery (tag:/source:/слово)
  color: string;   // hex-цвет узлов группы
}

export interface GraphSettings {
  filters: {
    search: string;
    existingOnly: boolean;     // скрыть призрачные узлы (ссылки без заметок)
    showOrphans: boolean;      // показывать узлы без связей
    hiddenSources: string[];
    selectedTags: string[];
    depth: number;             // 1..3 — глубина соседства (только локальный граф)
  };
  groups: GraphGroup[];
  display: {
    arrows: boolean;     // стрелки направления ссылок
    textFade: number;    // порог затухания подписей от зума (0.1..2.5)
    nodeSize: number;    // множитель размера узлов (0.5..2)
    lineWidth: number;   // толщина связей (0.3..5)
  };
  forces: {
    center: number;        // 0..1 — притяжение к центру
    repel: number;         // 0..20 — отталкивание узлов
    link: number;          // 0..1 — сила связей
    linkDistance: number;  // 30..500 — целевая длина связи
  };
}

export const GRAPH_DEFAULTS: GraphSettings = {
  filters: { search: '', existingOnly: false, showOrphans: true, hiddenSources: [], selectedTags: [], depth: 1 },
  groups: [],
  display: { arrows: false, textFade: 1, nodeSize: 1, lineWidth: 1 },
  forces: { center: 0.5, repel: 10, link: 1, linkDistance: 120 },
};

export function loadGraphSettings(key: string): GraphSettings {
  let saved: Partial<GraphSettings> | null = null;
  try {
    const raw = localStorage.getItem(key);
    if (raw) saved = JSON.parse(raw);
  } catch { /* битый JSON — начинаем с дефолтов */ }
  const s: GraphSettings = {
    filters: { ...GRAPH_DEFAULTS.filters, ...saved?.filters },
    groups: saved?.groups ?? [],
    display: { ...GRAPH_DEFAULTS.display, ...saved?.display },
    forces: { ...GRAPH_DEFAULTS.forces, ...saved?.forces },
  };
  // Миграция старого фильтра источников из сайдбара NotesPage (один раз, пока нет своих настроек)
  if (!saved && key === 'cc_graph_global') {
    try {
      const legacy = localStorage.getItem('cc_notes_hidden_sources');
      if (legacy) s.filters.hiddenSources = JSON.parse(legacy);
    } catch { /* игнорируем */ }
  }
  return s;
}

// key === null отключает чтение/запись localStorage — для случая, когда настройки
// на самом деле управляются извне (владелец состояния держит их сам), а внутренний
// экземпляр нужен лишь как заглушка (условный вызов хука недопустим).
export function useGraphSettings(key: string | null) {
  const [settings, setSettings] = useState(() => key ? loadGraphSettings(key) : GRAPH_DEFAULTS);
  const keyRef = useRef(key);
  useEffect(() => {
    if (keyRef.current !== key) { keyRef.current = key; if (key) setSettings(loadGraphSettings(key)); }
  }, [key]);
  // Дебаунс записи: слайдеры шлют изменения на каждый пиксель
  useEffect(() => {
    if (!key) return;
    const t = setTimeout(() => {
      try { localStorage.setItem(key, JSON.stringify(settings)); } catch { /* квота/private mode */ }
    }, 300);
    return () => clearTimeout(t);
  }, [settings, key]);
  return [settings, setSettings] as const;
}
