// Типы домена «Заметки» (переехали из types/index.ts, S5-P4). types/index.ts ре-экспортирует их через `export type *` — внешние импорты не меняются.

// ===== Заметки (Obsidian-совместимая база знаний) =====

// Привязка заметки-комментария к MD-документу (флаг doc-annotations)
export interface NoteAnnotationInfo {
  docScope: string;        // 'personal' | projectId (путь от корня проекта)
  docPath: string;
  status: 'open' | 'resolved';
  blockId?: string | null;
  anchorQuote?: string | null;
  anchorHeading?: string | null;
  isReply?: boolean;       // ответ в треде (annotates указывает на заметку-комментарий)
  docMissing?: boolean;    // документ-цель удалён (ghost-узел в дереве)
}

// Ответ в треде комментария
export interface NoteReply {
  noteId: string;
  title: string;
  excerpt: string;
  createdAt: string;
  tags: string[];
}

// Комментарий документа с резолвом привязки — для подсветки при чтении
export interface DocAnnotation {
  noteId: string;
  title: string;
  status: 'open' | 'resolved';
  state: 'exact' | 'changed' | 'orphan';
  start: number;           // офсеты якорного блока в контенте документа; -1 у сироты
  end: number;
  blockId?: string | null;
  anchorHeading?: string | null;
  quote: string;
  excerpt: string;
  tags: string[];
  updatedAt: string;
  replies: number;         // число ответов в треде
}

// Источник заметки: "personal" (личный vault) или id проекта
export interface NoteSummary {
  id: string;
  title: string;
  source: string;
  sourceLabel: string;
  path: string;
  tags: string[];
  createdAt: string;
  updatedAt: string;
  expiresAt?: string;
  sourceSessionId?: string;
  annotation?: NoteAnnotationInfo | null;
  // Привязка к файлу проекта (frontmatter file:): путь от корня проекта-источника,
  // forward slashes; у личных заметок отсутствует. fileMissing — файл не найден на диске.
  file?: string | null;
  fileMissing?: boolean;
}

// Разрешённая исходящая ссылка [[...]]; resolved=false — «призрачная» (цели ещё нет)
export interface NoteLink {
  targetId: string;
  targetTitle: string;
  resolved: boolean;
}

// Обратная ссылка: заметка, ссылающаяся на текущую, + контекст-сниппет
export interface NoteBacklink {
  sourceId: string;
  sourceTitle: string;
  source: string;
  sourceLabel: string;
  snippet: string;
}

export interface NoteDetail {
  id: string;
  title: string;
  source: string;
  sourceLabel: string;
  path: string;
  content: string;      // сырой markdown (для правки)
  tags: string[];
  links: NoteLink[];
  backlinks: NoteBacklink[];
  unlinkedMentions: NoteBacklink[];   // упоминания заголовков без [[…]]
  createdAt: string;
  updatedAt: string;
  expiresAt?: string;
  sourceSessionId?: string;
  annotation?: NoteAnnotationInfo | null;
  file?: string | null;      // привязка к файлу проекта (см. NoteSummary.file)
  fileMissing?: boolean;
}

// Узел графа; ghost=true — «призрачная» заметка (на неё ссылаются, но её нет)
export interface NoteGraphNode {
  id: string;
  title: string;
  source: string;
  sourceLabel: string;
  degree: number;
  ghost: boolean;
  tags?: string[];
  // Комментарии в графе (?annotations=true): 'comment'/'reply' — узлы комментариев,
  // 'doc' — призрачный узел документа-файла; status — open|resolved у корневых
  kind?: 'comment' | 'reply' | 'doc' | null;
  status?: 'open' | 'resolved' | null;
}

// Шаблон заметки (templates/ личного vault)
export interface NoteTemplate {
  id: string;
  title: string;
}

// Результат семантического поиска (Dify RAG)
export interface NoteSemanticHit {
  id: string;
  title: string;
  source: string;
  sourceLabel: string;
  score: number;
  snippet: string;
}

export interface NoteGraphEdge {
  source: string;
  target: string;
}

export interface NoteGraph {
  nodes: NoteGraphNode[];
  edges: NoteGraphEdge[];
}

// Источник для выбора «куда создать» (личный vault + проекты владельца)
export interface NoteSource {
  key: string;
  label: string;
}

// Физическая папка источника (в т.ч. пустая) — для дерева и выбора «куда создать»
export interface NoteFolder {
  source: string;
  path: string;
}

export interface CreateNoteDto {
  title: string;
  content?: string;
  source?: string;
  templateId?: string;
  folder?: string;   // папка внутри источника ("Идеи/Черновики"); пусто = корень
  expiresAfterMinutes?: number | null;
  sourceSessionId?: string;
  // Привязка к файлу проекта (frontmatter file:). Поле используется MCP-сервером;
  // UI кладёт привязку frontmatter-строкой прямо в content — тогда офлайн-создание
  // (notesOffline) проходит тот же путь без изменений outbox.
  file?: string;
}

export interface UpdateNoteDto {
  title?: string;
  content?: string;
}

