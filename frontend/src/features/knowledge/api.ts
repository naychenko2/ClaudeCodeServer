// Домен «Знания» (вынесено из lib/api.ts, docs/research/frontend-api-split-2026-11.md):
// проекльный индекс Dify (knowledge) и менеджер баз знаний (knowledgeBases). Тонкая
// обёртка над живым REST; DTO-типы — types/index.ts и ./types.ts (остальные доедут
// в ./types.ts в S5-P3).
import { readStoredToken, request } from '../../lib/offline';
import type { DifyDocument } from './types';
import type {
  CreateKnowledgeBaseDto,
  KnowledgeBaseDetail,
  KnowledgeDocumentContent,
  KnowledgeListResponse,
  KnowledgeSearchHit,
} from '../../types';

// Методы домена; lib/api.ts подмешивает их в объект api spread'ом
// (адреса api.knowledge.* и api.knowledgeBases.* остаются прежними).
export const knowledgeApi = {
  knowledge: {
    getStatus: (projectId: string) =>
      request<{ datasetId: string | null; documents: DifyDocument[]; total: number }>(`/projects/${projectId}/knowledge`),
    indexFile: (projectId: string, relativePath: string) =>
      request<{ datasetId: string; document: DifyDocument }>(
        `/projects/${projectId}/knowledge/index`,
        { method: 'POST', body: JSON.stringify({ relativePath }) }
      ),
    indexFolder: (projectId: string, relativePath: string) =>
      request<{ indexed: number; skipped: number; documents: DifyDocument[] }>(
        `/projects/${projectId}/knowledge/index-folder`,
        { method: 'POST', body: JSON.stringify({ relativePath }) }
      ),
    deleteDocument: (projectId: string, documentId: string) =>
      request<void>(`/projects/${projectId}/knowledge/documents/${documentId}`, { method: 'DELETE' }),
    deleteDataset: (projectId: string) =>
      request<void>(`/projects/${projectId}/knowledge`, { method: 'DELETE' }),
    setDocumentTags: (projectId: string, documentName: string, documentId: string, tags: string[]) =>
      request<void>(`/projects/${projectId}/knowledge/tags`, {
        method: 'PUT',
        body: JSON.stringify({ documentName, documentId, tags }),
      }),
  },

  // Раздел «Знания»: менеджер баз знаний Dify (личные + публичные), не путать с
  // проектным knowledge выше. Dify — источник истины; configured=false — не настроен.
  knowledgeBases: {
    list: () => request<KnowledgeListResponse>('/knowledge'),
    get: (id: string) => request<KnowledgeBaseDetail>(`/knowledge/${encodeURIComponent(id)}`),
    // Сгенерировать описание базы по составу документов (локальная модель / claude) и сохранить
    describe: (id: string) =>
      request<{ description: string }>(`/knowledge/${encodeURIComponent(id)}/ai/describe`, { method: 'POST' }),
    create: (dto: CreateKnowledgeBaseDto) =>
      request<{ id: string; title: string; visibility: string }>('/knowledge', {
        method: 'POST', body: JSON.stringify(dto),
      }),
    remove: (id: string) => request<void>(`/knowledge/${encodeURIComponent(id)}`, { method: 'DELETE' }),
    // Добавить документ текстом
    addDocumentText: (id: string, name: string, text: string) =>
      request<{ id: string; name: string; indexingStatus: string }>(
        `/knowledge/${encodeURIComponent(id)}/documents`,
        { method: 'POST', body: JSON.stringify({ name, text }) },
      ),
    // Загрузить документ файлом (multipart — request() не ставит Content-Type для FormData)
    addDocumentFile: async (id: string, file: File, name?: string): Promise<{ id: string; name: string; indexingStatus: string }> => {
      const token = readStoredToken();
      const form = new FormData();
      form.append('file', file);
      if (name) form.append('name', name);
      const res = await fetch(`/api/knowledge/${encodeURIComponent(id)}/documents/file`,
        { method: 'POST', headers: token ? { Authorization: `Bearer ${token}` } : {}, body: form });
      if (res.status === 401) {
        if (token && typeof window !== 'undefined') window.dispatchEvent(new Event('cc-unauthorized'));
        throw new Error('Нет доступа');
      }
      if (!res.ok) {
        const err = await res.json().catch(() => ({ error: res.statusText }));
        throw new Error(err.error ?? res.statusText);
      }
      return res.json();
    },
    removeDocument: (id: string, docId: string) =>
      request<void>(`/knowledge/${encodeURIComponent(id)}/documents/${encodeURIComponent(docId)}`, { method: 'DELETE' }),
    // Содержимое документа — сегменты (чанки) по порядку
    getDocument: (id: string, docId: string) =>
      request<KnowledgeDocumentContent>(`/knowledge/${encodeURIComponent(id)}/documents/${encodeURIComponent(docId)}`),
    // method: semantic (по смыслу) | fulltext (точный полнотекстовый)
    search: (id: string, q: string, method: 'semantic' | 'fulltext', topK = 8) =>
      request<{ items: KnowledgeSearchHit[] }>(
        `/knowledge/${encodeURIComponent(id)}/search?q=${encodeURIComponent(q)}&topK=${topK}&method=${method}`),
  },
};
