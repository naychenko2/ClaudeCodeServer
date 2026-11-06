// Типы домена «Продуктовая история» (переехали из types/index.ts, S5-P3). types/index.ts ре-экспортирует их через `export type *` — внешние импорты не меняются.

// ===== Продуктовая история (AI-сводка по всем проектам) =====

// Пункт продуктовой сводки за день: что нового и чем полезно
export interface ChangelogItem {
  type: 'feature' | 'improvement' | 'fix' | 'other';
  area: string;      // раздел продукта — для группировки внутри дня
  emoji: string;
  title: string;
  benefit: string;
  score: number;        // значимость 1-5 (5 — хит, 1-2 — по мелочи)
  scoreReason: string;  // обоснование оценки (в тултипе бейджа)
  authors: string[];
  projects: string[];
}

// Во сколько обошлась сборка сводки дня — показывается внизу дня
export interface ChangelogGeneration {
  durationMs: number;           // сколько ждали ответа, вместе со стартом CLI
  inputTokens: number;          // вход без кеша
  cacheCreationTokens: number;  // вход, записанный в кеш промптов
  cacheReadTokens: number;      // вход из кеша (дешевле обычного)
  outputTokens: number;
  costUsd: number | null;       // для подписки — оценка по тарифам API, деньги не списываются
  model: string | null;
  generatedAt: string;          // ISO-дата сборки
}

// Сводка изменений за один день (по всем проектам)
export interface ChangelogDay {
  date: string; // yyyy-MM-dd
  items: ChangelogItem[];
  degraded?: boolean;       // сводку собрать не удалось — пункты сырые (subject'ы коммитов)
  degradedReason?: string;  // что сломалось и как это починить
  generation?: ChangelogGeneration | null;  // расход на сборку (нет у старых записей кеша)
}

// Заглушка дня для мгновенного списка (без LLM)
export interface DaySummaryStub {
  date: string;
  commitCount: number;
  cached: boolean;
}

// Статус настройки источника changelog — «настроено» vs «донастрой инстанс»
export interface ChangelogStatus {
  configured: boolean;
  mode: string;         // 'repo' | 'projects'
  detail: string | null;
}
