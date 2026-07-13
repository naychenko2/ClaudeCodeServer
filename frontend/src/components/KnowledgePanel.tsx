import { useState, useEffect, useCallback, useRef, type ReactNode } from 'react';
import { BookOpen, Database, Folder, Info, RotateCcw, Search, Tag, Trash2, X } from 'lucide-react';
import type { Project } from '../types';
import type { DifyDocument } from '../lib/api';
import { api } from '../lib/api';
import { C, R, SHADOW, FONT, TB } from '../lib/design';
import { ICON_SIZE, ICON_STROKE } from './ui/icons';

interface Props {
  project: Project;
  isMobile?: boolean;
  alwaysShowIcons?: boolean;
  onDocumentsChanged?: (fileNames: Set<string>) => void;
  onBack?: () => void;
}

interface KnowledgeStatus {
  datasetId: string | null;
  documents: DifyDocument[];
  total: number;
}

const TERMINAL_STATUSES = ['completed', 'available', 'error'];

function statusColor(indexingStatus: string): string {
  if (indexingStatus === 'completed' || indexingStatus === 'available') return C.success;
  if (indexingStatus === 'error') return C.accent;
  return C.info;
}

// --- Диалог редактирования тегов ---

const MAX_VISIBLE_SUGGESTIONS = 9;

interface TagsDialogProps {
  doc: DifyDocument;
  existingTags: string[];
  onClose: () => void;
  onSave: (tags: string[]) => Promise<void>;
}

function TagsDialog({ doc, existingTags, onClose, onSave }: TagsDialogProps) {
  const displayName = doc.name.includes('/') ? doc.name.split('/').pop()! : doc.name;
  const [tags, setTags] = useState<string[]>(doc.tags ?? []);
  const [input, setInput] = useState('');
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [suggestionsExpanded, setSuggestionsExpanded] = useState(false);
  const inputRef = useRef<HTMLInputElement>(null);

  useEffect(() => { inputRef.current?.focus(); }, []);

  const query = input.trim().toLowerCase();

  // Теги из других документов, не добавленные в текущий
  const available = existingTags.filter(t => !tags.includes(t));
  const filtered = query ? available.filter(t => t.includes(query)) : available;
  const visibleSuggestions = (query || suggestionsExpanded)
    ? filtered
    : filtered.slice(0, MAX_VISIBLE_SUGGESTIONS);
  const hiddenCount = (!query && !suggestionsExpanded) ? Math.max(0, filtered.length - MAX_VISIBLE_SUGGESTIONS) : 0;

  const addTag = (tag?: string) => {
    const value = (tag ?? (filtered.length === 1 ? filtered[0] : input)).trim().toLowerCase();
    if (!value || tags.includes(value)) { if (!tag) setInput(''); return; }
    setTags(prev => [...prev, value]);
    setInput('');
  };

  const removeTag = (tag: string) => setTags(prev => prev.filter(t => t !== tag));

  const handleKeyDown = (e: React.KeyboardEvent<HTMLInputElement>) => {
    if (e.key === 'Enter') { e.preventDefault(); addTag(); }
    if (e.key === 'Escape') onClose();
  };

  const handleSave = async () => {
    setSaving(true);
    setError(null);
    // Фиксируем тег из поля ввода если не был добавлен через Enter
    const pending = input.trim().toLowerCase();
    const finalTags = pending && !tags.includes(pending) ? [...tags, pending] : tags;
    try {
      await onSave(finalTags);
      onClose();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Ошибка сохранения');
      setSaving(false);
    }
  };

  return (
    <div
      onClick={onClose}
      style={{
        position: 'fixed', inset: 0, zIndex: 1000,
        background: C.overlay,
        display: 'flex', alignItems: 'center', justifyContent: 'center',
      }}
    >
      <div
        onClick={e => e.stopPropagation()}
        style={{
          background: C.bgCard,
          borderRadius: R.modal,
          boxShadow: SHADOW.modal,
          width: 360,
          maxWidth: 'calc(100vw - 32px)',
          display: 'flex', flexDirection: 'column',
          overflow: 'hidden',
        }}
      >
        {/* Шапка */}
        <div style={{
          padding: '14px 16px 12px',
          borderBottom: `1px solid ${C.border}`,
          display: 'flex', alignItems: 'center', gap: 8,
        }}>
          <div style={{ flex: 1, minWidth: 0 }}>
            <div style={{ fontSize: 11, color: C.textMuted, marginBottom: 2 }}>Теги документа</div>
            <div title={doc.name} style={{
              fontSize: 13, fontWeight: 600, color: C.textHeading,
              overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
            }}>
              {displayName}
            </div>
          </div>
          <button
            onClick={onClose}
            style={{ background: 'none', border: 'none', cursor: 'pointer', color: C.textMuted, padding: 4, borderRadius: R.sm }}
          >
            <X size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
          </button>
        </div>

        {/* Тело */}
        <div style={{ padding: '14px 16px', flex: 1 }}>
          {/* Активные теги документа */}
          {tags.length > 0 && (
            <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6, marginBottom: 12 }}>
              {tags.map(tag => (
                <span key={tag} style={{
                  display: 'inline-flex', alignItems: 'center', gap: 4,
                  background: C.accentLight, color: C.accent,
                  borderRadius: R.sm, padding: '3px 8px',
                  fontSize: 11, fontWeight: 500,
                }}>
                  {tag}
                  <button
                    onClick={() => removeTag(tag)}
                    style={{ background: 'none', border: 'none', cursor: 'pointer', color: C.accent, padding: 0, display: 'flex', lineHeight: 1 }}
                  >
                    <X size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
                  </button>
                </span>
              ))}
            </div>
          )}

          {/* Ввод нового тега */}
          <input
            ref={inputRef}
            value={input}
            onChange={e => { setInput(e.target.value); setSuggestionsExpanded(false); }}
            onKeyDown={handleKeyDown}
            placeholder="Новый тег… (Enter)"
            style={{
              width: '100%', boxSizing: 'border-box',
              padding: '7px 10px',
              border: `1px solid ${C.border}`,
              borderRadius: R.lg,
              fontSize: 12, color: C.textPrimary,
              background: C.bgMain,
              outline: 'none',
              fontFamily: FONT.sans,
            }}
          />

          {/* Теги из других документов */}
          {existingTags.length > 0 && (
            <div style={{ marginTop: 12 }}>
              <div style={{ fontSize: 10, color: C.textMuted, marginBottom: 6, textTransform: 'uppercase', letterSpacing: '0.05em' }}>
                {query ? `Из других документов (${filtered.length})` : 'Из других документов'}
              </div>
              {filtered.length === 0 && query ? (
                <div style={{ fontSize: 11, color: C.textMuted }}>Нет совпадений — нажмите Enter чтобы создать «{query}»</div>
              ) : (
                <div style={{ display: 'flex', flexWrap: 'wrap', gap: 5 }}>
                  {visibleSuggestions.map(tag => (
                    <button
                      key={tag}
                      onClick={() => addTag(tag)}
                      style={{
                        display: 'inline-flex', alignItems: 'center',
                        background: C.bgInset, color: C.textSecondary,
                        border: `1px solid ${C.border}`,
                        borderRadius: R.sm, padding: '3px 8px',
                        fontSize: 11, fontWeight: 400,
                        cursor: 'pointer', fontFamily: FONT.sans,
                        transition: 'background 0.1s, color 0.1s',
                      }}
                      onMouseEnter={e => {
                        (e.currentTarget as HTMLButtonElement).style.background = C.accentLight;
                        (e.currentTarget as HTMLButtonElement).style.color = C.accent;
                        (e.currentTarget as HTMLButtonElement).style.borderColor = C.accentMuted;
                      }}
                      onMouseLeave={e => {
                        (e.currentTarget as HTMLButtonElement).style.background = C.bgInset;
                        (e.currentTarget as HTMLButtonElement).style.color = C.textSecondary;
                        (e.currentTarget as HTMLButtonElement).style.borderColor = C.border;
                      }}
                    >
                      + {tag}
                    </button>
                  ))}
                  {hiddenCount > 0 && (
                    <button
                      onClick={() => setSuggestionsExpanded(true)}
                      style={{
                        background: 'none', border: 'none', cursor: 'pointer',
                        fontSize: 11, color: C.textMuted, padding: '3px 4px',
                        fontFamily: FONT.sans,
                      }}
                    >
                      ещё {hiddenCount} ↓
                    </button>
                  )}
                </div>
              )}
            </div>
          )}

          {error && (
            <div style={{ fontSize: 11, color: C.accent, marginTop: 8 }}>{error}</div>
          )}
        </div>

        {/* Футер */}
        <div style={{
          padding: '10px 16px 14px',
          borderTop: `1px solid ${C.border}`,
          display: 'flex', gap: 8, justifyContent: 'flex-end',
        }}>
          <button
            onClick={onClose}
            style={{
              padding: '6px 14px', borderRadius: R.lg, fontSize: 12,
              border: `1px solid ${C.border}`, background: 'transparent',
              color: C.textSecondary, cursor: 'pointer', fontFamily: FONT.sans,
            }}
          >
            Отмена
          </button>
          <button
            onClick={handleSave}
            disabled={saving}
            style={{
              padding: '6px 14px', borderRadius: R.lg, fontSize: 12, fontWeight: 500,
              border: 'none', background: saving ? C.accentSoft : C.accent,
              color: C.onAccent, cursor: saving ? 'default' : 'pointer',
              fontFamily: FONT.sans,
            }}
          >
            {saving ? 'Сохранение…' : 'Сохранить'}
          </button>
        </div>
      </div>
    </div>
  );
}

// --- Строка документа ---

function DocumentRow({ doc, deleting, retrying, isMobile, alwaysShowIcons, onDelete, onRetry, onEditTags }: {
  doc: DifyDocument;
  deleting: boolean;
  retrying: boolean;
  isMobile: boolean;
  alwaysShowIcons?: boolean;
  onDelete: () => void;
  onRetry?: () => void;
  onEditTags: () => void;
}) {
  const [hovered, setHovered] = useState(false);
  const showActions = isMobile || alwaysShowIcons || hovered;
  const displayName = doc.name.includes('/') ? doc.name.split('/').pop()! : doc.name;
  const isError = doc.indexingStatus === 'error';
  const hasTags = doc.tags && doc.tags.length > 0;

  return (
    <div
      onMouseEnter={() => setHovered(true)}
      onMouseLeave={() => setHovered(false)}
      style={{
        padding: '6px 14px',
        background: hovered ? C.bgSelected : 'transparent',
        transition: 'background 0.1s',
      }}
    >
      <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
        <div style={{ width: 7, height: 7, borderRadius: '50%', background: statusColor(doc.indexingStatus), flexShrink: 0 }} />
        <div style={{ flex: 1, minWidth: 0 }}>
          <div title={doc.name} style={{ fontSize: 13, color: C.textPrimary, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
            {displayName}
          </div>
          {isError ? (
            <div style={{ fontSize: 11, color: C.accent, marginTop: 1 }}>Ошибка индексирования</div>
          ) : !TERMINAL_STATUSES.includes(doc.indexingStatus) && (
            <div style={{ fontSize: 11, color: C.textMuted, marginTop: 1 }}>Индексируется…</div>
          )}
        </div>

        {/* Кнопка тегов */}
        <button
          onClick={onEditTags}
          title="Редактировать теги"
          style={{
            opacity: showActions || hasTags ? 1 : 0,
            background: 'none', border: 'none', cursor: 'pointer',
            padding: isMobile ? 8 : 3,
            minWidth: isMobile ? 36 : undefined, minHeight: isMobile ? 36 : undefined,
            color: hasTags ? C.accent : C.textMuted,
            display: 'flex', alignItems: 'center', justifyContent: 'center',
            transition: 'opacity 0.15s', borderRadius: R.sm, flexShrink: 0,
          }}
        >
          <Tag size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
        </button>

        {/* Кнопка повтора */}
        {isError && onRetry && (
          <button
            onClick={onRetry}
            disabled={retrying || deleting}
            title="Повторить индексирование"
            style={{
              opacity: showActions || retrying ? 1 : 0,
              background: 'none', border: 'none', cursor: retrying ? 'default' : 'pointer',
              padding: isMobile ? 8 : 3,
              minWidth: isMobile ? 36 : undefined, minHeight: isMobile ? 36 : undefined,
              color: C.success, display: 'flex', alignItems: 'center', justifyContent: 'center',
              transition: 'opacity 0.15s', borderRadius: R.sm, flexShrink: 0,
            }}
          >
            <RotateCcw size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
          </button>
        )}

        <button
          onClick={onDelete}
          disabled={deleting || retrying}
          title="Удалить из БЗ"
          style={{
            opacity: showActions || deleting ? 1 : 0,
            background: 'none', border: 'none', cursor: 'pointer',
            padding: isMobile ? 8 : 3,
            minWidth: isMobile ? 36 : undefined, minHeight: isMobile ? 36 : undefined,
            color: C.accent, display: 'flex', alignItems: 'center', justifyContent: 'center',
            transition: 'opacity 0.15s', borderRadius: R.sm, flexShrink: 0,
          }}
        >
          <Trash2 size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
        </button>
      </div>

      {/* Чипсы тегов под именем файла */}
      {hasTags && (
        <div style={{ display: 'flex', flexWrap: 'wrap', gap: 4, marginTop: 5, paddingLeft: 15 }}>
          {doc.tags!.map(tag => (
            <span key={tag} style={{
              background: C.accentLight, color: C.accent,
              borderRadius: R.sm, padding: '2px 6px',
              fontSize: 10, fontWeight: 500,
            }}>
              {tag}
            </span>
          ))}
        </div>
      )}
    </div>
  );
}

// --- Подсказка в empty state ---

function KnowledgeTip({ icon, title, text }: { icon: ReactNode; title: string; text: string }) {
  return (
    <div style={{ display: 'flex', gap: 10, alignItems: 'flex-start' }}>
      <div style={{
        width: 28, height: 28, borderRadius: R.md, background: C.bgInset,
        display: 'flex', alignItems: 'center', justifyContent: 'center',
        flexShrink: 0, color: C.textSecondary,
      }}>
        {icon}
      </div>
      <div>
        <div style={{ fontSize: 12, fontWeight: 600, color: C.textSecondary, marginBottom: 2 }}>{title}</div>
        <div style={{ fontSize: 11.5, color: C.textMuted, lineHeight: 1.55 }}>{text}</div>
      </div>
    </div>
  );
}

// --- Главный компонент ---

export function KnowledgePanel({ project, isMobile = false, alwaysShowIcons = false, onDocumentsChanged, onBack }: Props) {
  const [status, setStatus] = useState<KnowledgeStatus | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [deletingId, setDeletingId] = useState<string | null>(null);
  const [retryingId, setRetryingId] = useState<string | null>(null);
  const [tagEditDoc, setTagEditDoc] = useState<DifyDocument | null>(null);
  const [searchQuery, setSearchQuery] = useState('');
  const [notification, setNotification] = useState<{ message: string; type: 'error' | 'success' } | null>(null);
  const notifTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);

  const loadStatus = useCallback(async (silent = false) => {
    if (!silent) setLoading(true);
    if (!silent) setError(null);
    try {
      const s = await api.knowledge.getStatus(project.id);
      setStatus(s);
    } catch (e) {
      if (!silent) setError(e instanceof Error ? e.message : 'Ошибка загрузки');
    } finally {
      if (!silent) setLoading(false);
    }
  }, [project.id]);

  useEffect(() => { loadStatus(); }, [loadStatus]);

  useEffect(() => {
    if (!onDocumentsChanged || !status) return;
    const names = new Set(status.documents.map(d => {
      const parts = d.name.split('/');
      return parts[parts.length - 1];
    }));
    onDocumentsChanged(names);
  }, [status, onDocumentsChanged]);

  const hasIndexing = status?.documents.some(d => !TERMINAL_STATUSES.includes(d.indexingStatus)) ?? false;
  useEffect(() => {
    if (!hasIndexing) return;
    const id = setInterval(() => loadStatus(true), 3000);
    return () => clearInterval(id);
  }, [hasIndexing, loadStatus]);

  const handleDeleteDocument = async (docId: string) => {
    setDeletingId(docId);
    try {
      await api.knowledge.deleteDocument(project.id, docId);
      setStatus(prev => prev ? {
        ...prev,
        documents: prev.documents.filter(d => d.id !== docId),
        total: prev.total - 1,
      } : prev);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Ошибка удаления');
    } finally {
      setDeletingId(null);
    }
  };

  const handleRetryDocument = async (doc: DifyDocument) => {
    if (!doc.name.includes('/')) return;
    setRetryingId(doc.id);
    try {
      await api.knowledge.deleteDocument(project.id, doc.id);
      const result = await api.knowledge.indexFile(project.id, doc.name);
      setStatus(prev => prev ? {
        ...prev,
        documents: prev.documents.map(d => d.id === doc.id ? { ...result.document, tags: doc.tags } : d),
      } : prev);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Ошибка повтора индексирования');
    } finally {
      setRetryingId(null);
    }
  };


  const showNotification = (message: string, type: 'error' | 'success') => {
    if (notifTimerRef.current) clearTimeout(notifTimerRef.current);
    setNotification({ message, type });
    notifTimerRef.current = setTimeout(() => setNotification(null), 5000);
  };

  const handleSaveTags = async (tags: string[]) => {
    if (!tagEditDoc) return;
    try {
      await api.knowledge.setDocumentTags(project.id, tagEditDoc.name, tagEditDoc.id, tags);
      setStatus(prev => prev ? {
        ...prev,
        documents: prev.documents.map(d => d.id === tagEditDoc.id ? { ...d, tags } : d),
      } : prev);
    } catch (e) {
      const msg = e instanceof Error ? e.message : 'Ошибка сохранения тегов';
      console.error('[KnowledgePanel] setDocumentTags failed:', e);
      showNotification(msg, 'error');
      throw e; // пробрасываем — диалог тоже покажет ошибку
    }
  };

  if (loading) {
    return (
      <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'center', height: '100%' }}>
        <div style={{ fontSize: 13, color: C.textMuted }}>Загрузка…</div>
      </div>
    );
  }

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%', overflow: 'hidden' }}>
      {/* Шапка — icon-toggle (зеркало строки поиска FileExplorer) */}
      <div style={{ padding: '4px 12px 10px', flexShrink: 0 }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
          {/* Поиск по знаниям */}
          <div style={{ flex: 1, display: 'flex', alignItems: 'center', background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.lg, padding: '0 11px', height: 36 }}>
            <span style={{ color: C.textMuted, marginRight: 8, display: 'flex', flexShrink: 0 }}>
              <Search size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
            </span>
            <input
              value={searchQuery}
              onChange={e => setSearchQuery(e.target.value)}
              placeholder="Поиск по знаниям…"
              style={{
                flex: 1, border: 'none', outline: 'none', background: 'transparent',
                fontSize: 13, color: C.textPrimary, fontFamily: FONT.sans,
              }}
            />
            {searchQuery && (
              <button
                onClick={() => setSearchQuery('')}
                style={{ background: 'none', border: 'none', cursor: 'pointer', color: C.textMuted, padding: 0, display: 'flex', marginLeft: 4 }}
              >
                <X size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
              </button>
            )}
          </div>
          {/* icon-toggle: Знания активна */}
          <div style={{ display: 'flex', alignItems: 'center', gap: 2, background: TB.pillTrack, borderRadius: 8, padding: 2, flexShrink: 0 }}>
            {/* Файлы — неактивна, возврат */}
            <button onClick={onBack} title="Файлы" style={{ width: 28, height: 28, border: 'none', borderRadius: 6, cursor: onBack ? 'pointer' : 'default', display: 'flex', alignItems: 'center', justifyContent: 'center', background: 'transparent', color: C.textMuted }}>
              <Folder size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
            </button>
            {/* Знания — активна */}
            <button title="Знания" style={{ width: 28, height: 28, border: 'none', borderRadius: 6, cursor: 'default', display: 'flex', alignItems: 'center', justifyContent: 'center', background: C.bgMain, color: C.successText, boxShadow: TB.pillThumbShadow }}>
              <BookOpen size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
            </button>
          </div>
        </div>
      </div>

      {/* Ошибка */}
      {error && (
        <div style={{ padding: '10px 14px', flexShrink: 0 }}>
          <div style={{ fontSize: 12, color: C.accent, marginBottom: 6 }}>{error}</div>
          <button onClick={() => loadStatus()} style={{ fontSize: 11, color: C.accent, background: 'none', border: 'none', cursor: 'pointer', padding: 0, textDecoration: 'underline' }}>
            Повторить
          </button>
        </div>
      )}

      {/* Уведомление об ошибке/успехе операции с тегами */}
      {notification && (
        <div style={{
          margin: '0 12px 8px',
          padding: '8px 12px',
          borderRadius: R.lg,
          fontSize: 12,
          background: notification.type === 'error' ? C.dangerBg : C.successBg,
          color: notification.type === 'error' ? C.dangerText : C.successText,
          border: `1px solid ${notification.type === 'error' ? C.dangerBorder : C.success}`,
          display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 8,
          flexShrink: 0,
        }}>
          <span>{notification.message}</span>
          <button
            onClick={() => setNotification(null)}
            style={{ background: 'none', border: 'none', cursor: 'pointer', color: 'inherit', padding: 0, display: 'flex', flexShrink: 0 }}
          >
            <X size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
          </button>
        </div>
      )}

      {/* Список документов */}
      <div style={{ flex: 1, overflowY: 'auto' }}>
        {/* Шапка раздела — видна когда есть документы или идёт поиск */}
        {!error && status?.datasetId && (
          <div style={{
            display: 'flex', alignItems: 'center', gap: 8,
            padding: '8px 14px 6px',
            borderBottom: `1px solid ${C.border}`,
            marginBottom: 2,
          }}>
            <div style={{ color: C.successText, display: 'flex', flexShrink: 0 }}>
              <BookOpen size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
            </div>
            <span style={{ fontSize: 12, fontWeight: 600, color: C.successText, flex: 1 }}>
              База знаний
            </span>
            <span style={{ fontSize: 11, color: C.textMuted, fontFamily: FONT.mono }}>
              {searchQuery.trim()
                ? `${status.documents.filter(d => (d.name.includes('/') ? d.name.split('/').pop()! : d.name).toLowerCase().includes(searchQuery.toLowerCase())).length} из ${status.total}`
                : `${status.total} ${status.total === 1 ? 'документ' : status.total < 5 ? 'документа' : 'документов'}`
              }
            </span>
          </div>
        )}

        <div style={{ padding: '4px 0' }}>
          {!error && (!status?.datasetId || status.documents.length === 0) ? (
            <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', padding: '32px 16px 20px', gap: 16 }}>
              <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', gap: 10 }}>
                <div style={{ width: 48, height: 48, borderRadius: 14, background: C.bgInset, color: C.accent, display: 'flex', alignItems: 'center', justifyContent: 'center' }}>
                  <Database size={ICON_SIZE.xl} strokeWidth={ICON_STROKE} color={C.textMuted} />
                </div>
                <div style={{ textAlign: 'center' }}>
                  <div style={{ fontFamily: FONT.serif, fontWeight: 500, fontSize: 18, color: C.textPrimary, letterSpacing: '-0.01em', marginBottom: 4 }}>Нет документов</div>
                  <div style={{ fontSize: 12.5, color: C.textSecondary, lineHeight: 1.5 }}>
                    Добавьте файл через<br />файловый менеджер
                  </div>
                </div>
              </div>

              {/* Подсказки о базе знаний */}
              <div style={{ width: '100%', display: 'flex', flexDirection: 'column', gap: 10, borderTop: `1px solid ${C.border}`, paddingTop: 16 }}>
                <KnowledgeTip
                  icon={<Info size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
                  title="Что такое база знаний"
                  text="Большие документы — книги, статьи — ассистент обрабатывает медленно: ему нужно прочитать их целиком. В базе знаний документы индексируются заранее, поэтому ассистент ищет по ним быстро, не читая каждый раз с начала."
                />
                <KnowledgeTip
                  icon={
                    <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                      <ellipse cx="12" cy="5" rx="9" ry="3"/><path d="M21 12c0 1.66-4 3-9 3s-9-1.34-9-3"/><path d="M3 5v14c0 1.66 4 3 9 3s9-1.34 9-3V5"/>
                      <line x1="9" y1="5" x2="9" y2="8" strokeWidth="2.5"/><line x1="6.5" y1="6.5" x2="11.5" y2="6.5" strokeWidth="2.5"/>
                    </svg>
                  }
                  title="Как добавить документ"
                  text="В файловом менеджере нажмите иконку базы данных рядом с нужным файлом — он появится здесь и будет проиндексирован."
                />
                <KnowledgeTip
                  icon={<Tag size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
                  title="Зачем нужны теги"
                  text="Теги позволяют обращаться к базе знаний выборочно: когда ассистент ищет информацию, можно ограничить поиск только документами с нужными тегами — это сокращает объём обработки и повышает точность ответа."
                />
              </div>
            </div>
          ) : (() => {
            const q = searchQuery.trim().toLowerCase();
            const filtered = q
              ? (status?.documents ?? []).filter(doc => {
                  const name = doc.name.includes('/') ? doc.name.split('/').pop()! : doc.name;
                  return name.toLowerCase().includes(q);
                })
              : (status?.documents ?? []);

            if (filtered.length === 0 && q) {
              return (
                <div style={{ padding: '24px 16px', textAlign: 'center' }}>
                  <div style={{ fontSize: 13, color: C.textMuted }}>Ничего не найдено по запросу «{searchQuery}»</div>
                </div>
              );
            }

            return filtered.map(doc => (
              <DocumentRow
                key={doc.id}
                doc={doc}
                deleting={deletingId === doc.id}
                retrying={retryingId === doc.id}
                isMobile={isMobile}
                alwaysShowIcons={alwaysShowIcons}
                onDelete={() => handleDeleteDocument(doc.id)}
                onRetry={doc.name.includes('/') ? () => handleRetryDocument(doc) : undefined}
                onEditTags={() => setTagEditDoc(doc)}
              />
            ));
          })()}
        </div>
      </div>


      {/* Диалог тегов */}
      {tagEditDoc && (
        <TagsDialog
          doc={tagEditDoc}
          existingTags={
            [...new Set(
              status?.documents
                .filter(d => d.id !== tagEditDoc.id)
                .flatMap(d => d.tags ?? [])
                .sort() ?? []
            )]
          }
          onClose={() => setTagEditDoc(null)}
          onSave={handleSaveTags}
        />
      )}
    </div>
  );
}
