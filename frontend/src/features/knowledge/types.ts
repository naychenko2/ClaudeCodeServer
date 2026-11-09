// Типы домена «Знания» (docs/research/frontend-api-split-2026-11.md): DifyDocument переехал
// из lib/api.ts (S5-P2); типы раздела «Знания» — из types/index.ts (S5-P3).
// lib/api.ts ре-экспортирует их через `export type *` — внешние импорты не меняются.

// Документ Dify-датасета проекта: статус индексации и теги.
export interface DifyDocument {
  id: string;
  name: string;
  indexingStatus: string;
  error?: string | null;   // текст ошибки индексации (только у indexingStatus === 'error')
  tags?: string[];
}

// Типы раздела «Знания» (переехали из types/index.ts, S5-P3).

// ===== Знания (базы знаний Dify — раздел «Знания») =====
// База знаний = Dify-датасет. Список классифицируется бэкендом по имени/permission:
// личные («{user}:…» — заметок/проектов/памяти персон/самостоятельные) и публичные
// (all_team_members). deletable — можно ли удалить из раздела (самостоятельные/публичные).
export type KnowledgeVisibility = 'personal' | 'public';

export interface KnowledgeBaseSummary {
  id: string;
  title: string;
  type: string;                 // Заметки | Проект | Память персоны | Самостоятельная | Публичная
  visibility: KnowledgeVisibility;
  documentCount: number;
  createdAt: string | null;
  deletable: boolean;
  description?: string | null;
}

export interface KnowledgeDocument {
  id: string;
  name: string;
  indexingStatus: string;       // completed | indexing | error и т.п. (строка Dify)
  error?: string | null;        // текст ошибки индексации (только у статуса error)
}

export interface KnowledgeBaseDetail extends KnowledgeBaseSummary {
  documents: KnowledgeDocument[];
}

export interface KnowledgeSearchHit {
  score: number;
  content: string;
  documentName: string;
}

export interface CreateKnowledgeBaseDto {
  title: string;
  description?: string;
  visibility: KnowledgeVisibility;
}

// Ответ GET /api/knowledge: configured=false — Dify не настроен (фронт показывает empty-state)
export interface KnowledgeListResponse {
  configured: boolean;
  items: KnowledgeBaseSummary[];
}

// Сегмент (чанк) документа базы знаний — порция текста для просмотра/поиска
export interface KnowledgeSegment {
  position: number;
  content: string;
  wordCount: number;
}

// Содержимое документа (GET /api/knowledge/{id}/documents/{docId}) — сегменты по порядку
export interface KnowledgeDocumentContent {
  id: string;
  segments: KnowledgeSegment[];
}
