import type { DifyConfig } from './config.js';

// ---- Типы ответов ----

export interface Dataset {
  id: string;
  name: string;
  description?: string;
  document_count: number;
  word_count: number;
  created_at: number;
}

export interface DatasetsListResponse {
  data: Dataset[];
  has_more: boolean;
  limit: number;
  total: number;
  page: number;
}

export interface Document {
  id: string;
  name: string;
  word_count: number;
  tokens: number;
  indexing_status: string;
  created_at: number;
}

export interface DocumentCreateResponse {
  document: Document;
  batch: string;
}

export interface DocumentsListResponse {
  data: Document[];
  has_more: boolean;
  limit: number;
  total: number;
  page: number;
}

export interface Segment {
  id: string;
  position: number;
  content: string;
  word_count: number;
  tokens: number;
  keywords: string[];
  enabled: boolean;
}

export interface SegmentsListResponse {
  data: Segment[];
  doc_form: string;
}

export interface RetrieveRecord {
  segment: {
    id: string;
    content: string;
    document: { id: string; name: string; metadata?: Record<string, unknown> };
    keywords: string[];
  };
  score: number;
}

export interface RetrieveResponse {
  query: { content: string };
  records: RetrieveRecord[];
}

// ---- Клиент ----

export class DifyClient {
  private readonly baseUrl: string;
  private readonly apiKey: string;

  constructor(config: DifyConfig) {
    this.baseUrl = config.apiUrl;
    this.apiKey = config.apiKey;
  }

  private authHeader(): Record<string, string> {
    return { Authorization: `Bearer ${this.apiKey}` };
  }

  private async request<T>(method: string, path: string, body?: unknown): Promise<T> {
    const res = await fetch(`${this.baseUrl}${path}`, {
      method,
      headers: { ...this.authHeader(), 'Content-Type': 'application/json' },
      body: body !== undefined ? JSON.stringify(body) : undefined,
    });
    if (!res.ok) {
      const text = await res.text();
      throw new Error(`Dify ${method} ${path} → ${res.status}: ${text}`);
    }
    if (res.status === 204) return undefined as T;
    return res.json() as Promise<T>;
  }

  private async requestMultipart<T>(
    method: string,
    path: string,
    form: FormData
  ): Promise<T> {
    const res = await fetch(`${this.baseUrl}${path}`, {
      method,
      headers: this.authHeader(),
      body: form,
    });
    if (!res.ok) {
      const text = await res.text();
      throw new Error(`Dify ${method} ${path} → ${res.status}: ${text}`);
    }
    return res.json() as Promise<T>;
  }

  // ---- Датасеты ----

  async createDataset(params: {
    name: string;
    description?: string;
    indexing_technique?: string;
    permission?: string;
  }): Promise<Dataset> {
    return this.request<Dataset>('POST', '/datasets', params);
  }

  async listDatasets(page = 1, limit = 20): Promise<DatasetsListResponse> {
    return this.request<DatasetsListResponse>('GET', `/datasets?page=${page}&limit=${limit}`);
  }

  async deleteDataset(datasetId: string): Promise<void> {
    await this.request<void>('DELETE', `/datasets/${datasetId}`);
  }

  // ---- Документы ----

  async createDocumentByText(
    datasetId: string,
    params: {
      name: string;
      text: string;
      indexing_technique?: string;
      process_rule?: { mode: string };
    }
  ): Promise<DocumentCreateResponse> {
    return this.request<DocumentCreateResponse>(
      'POST',
      `/datasets/${datasetId}/document/create_by_text`,
      params
    );
  }

  async createDocumentByFile(
    datasetId: string,
    fileBuffer: Buffer,
    fileName: string,
    data: Record<string, unknown>
  ): Promise<DocumentCreateResponse> {
    const form = new FormData();
    form.append('file', new Blob([fileBuffer]), fileName);
    form.append('data', JSON.stringify(data));
    return this.requestMultipart<DocumentCreateResponse>(
      'POST',
      `/datasets/${datasetId}/document/create_by_file`,
      form
    );
  }

  async listDocuments(
    datasetId: string,
    page = 1,
    limit = 20,
    keyword?: string
  ): Promise<DocumentsListResponse> {
    const qs = new URLSearchParams({ page: String(page), limit: String(limit) });
    if (keyword) qs.set('keyword', keyword);
    return this.request<DocumentsListResponse>('GET', `/datasets/${datasetId}/documents?${qs}`);
  }

  async deleteDocument(datasetId: string, documentId: string): Promise<void> {
    await this.request<void>('DELETE', `/datasets/${datasetId}/documents/${documentId}`);
  }

  async updateDocumentByText(
    datasetId: string,
    documentId: string,
    params: {
      name?: string;
      text?: string;
      process_rule?: { mode: string };
    }
  ): Promise<DocumentCreateResponse> {
    return this.request<DocumentCreateResponse>(
      'POST',
      `/datasets/${datasetId}/documents/${documentId}/update_by_text`,
      params
    );
  }

  async updateDocumentByFile(
    datasetId: string,
    documentId: string,
    fileBuffer: Buffer,
    fileName: string,
    data: Record<string, unknown>
  ): Promise<DocumentCreateResponse> {
    const form = new FormData();
    form.append('file', new Blob([fileBuffer]), fileName);
    form.append('data', JSON.stringify(data));
    return this.requestMultipart<DocumentCreateResponse>(
      'POST',
      `/datasets/${datasetId}/documents/${documentId}/update_by_file`,
      form
    );
  }

  // ---- Сегменты ----

  async listSegments(
    datasetId: string,
    documentId: string,
    keyword?: string,
    status?: string
  ): Promise<SegmentsListResponse> {
    const qs = new URLSearchParams();
    if (keyword) qs.set('keyword', keyword);
    if (status) qs.set('status', status);
    const suffix = qs.toString() ? `?${qs}` : '';
    return this.request<SegmentsListResponse>(
      'GET',
      `/datasets/${datasetId}/documents/${documentId}/segments${suffix}`
    );
  }

  async addSegments(
    datasetId: string,
    documentId: string,
    segments: Array<{ content: string; answer?: string; keywords?: string[] }>
  ): Promise<{ data: Segment[] }> {
    return this.request<{ data: Segment[] }>(
      'POST',
      `/datasets/${datasetId}/documents/${documentId}/segments`,
      { segments }
    );
  }

  // ---- Поиск ----

  async retrieve(
    datasetId: string,
    query: string,
    options?: {
      search_method?: string;
      top_k?: number;
      score_threshold?: number;
      score_threshold_enabled?: boolean;
    }
  ): Promise<RetrieveResponse> {
    return this.request<RetrieveResponse>('POST', `/datasets/${datasetId}/retrieve`, {
      query,
      retrieval_model: {
        search_method: options?.search_method ?? 'semantic_search',
        top_k: options?.top_k ?? 5,
        score_threshold_enabled: options?.score_threshold_enabled ?? false,
        score_threshold: options?.score_threshold ?? 0.5,
        reranking_enable: false,
      },
    });
  }
}
