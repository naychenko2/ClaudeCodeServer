import { useEffect, useState } from 'react';
import { createPortal } from 'react-dom';
import type { KnowledgeDocument, KnowledgeDocumentContent } from '../../types';
import { api } from '../../lib/api';
import { C, FONT, R } from '../../lib/design';
import { MOBILE_MAX } from '../../lib/breakpoints';
import { IconClose } from './shared';

// Просмотр содержимого документа базы знаний: сегменты (чанки) по порядку.
// Живёт в общем Modal (центр. карточка на десктопе / bottom-sheet на мобиле) —
// чтобы не плодить новые паттерны и сохранить единый визуальный язык с другими
// диалогами раздела. Заголовок-имя документа и счётчики липнут к верху, тело
// скроллится. Сегменты — обычный текст с переносами (без markdown-рендера).
//
// Контракт (не меняем): api.knowledgeBases.getDocument(kbId, docId) →
// { id, segments: [{ position, content, wordCount }] }.

export function DocumentViewer({ kbId, doc, onClose }: {
  kbId: string;
  doc: KnowledgeDocument;
  onClose: () => void;
}) {
  const [data, setData] = useState<KnowledgeDocumentContent | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  // Загружаем содержимое документа. Перестарт при смене doc.id (как в NoteView).
  useEffect(() => {
    let alive = true;
    setLoading(true);
    setError(null);
    setData(null);
    api.knowledgeBases.getDocument(kbId, doc.id)
      .then(d => { if (alive) setData(d); })
      .catch(e => { if (alive) setError(e instanceof Error ? e.message : 'Не удалось загрузить документ'); })
      .finally(() => { if (alive) setLoading(false); });
    return () => { alive = false; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [kbId, doc.id]);

  const segments = data?.segments ?? [];
  const totalWords = segments.reduce((s, seg) => s + (seg.wordCount ?? 0), 0);

  return (
    <ModalShell doc={doc} onClose={onClose}>
      {error ? (
        <StateView text={error} tone="error" />
      ) : loading ? (
        <StateView text="Загрузка документа…" />
      ) : segments.length === 0 ? (
        <StateView text="В документе нет сегментов. Возможно, индексация ещё не завершена." />
      ) : (
        <>
          {/* Метаданные под заголовком — счётчики сегментов и слов */}
          <div style={{
            display: 'flex', alignItems: 'center', gap: 10, flexWrap: 'wrap',
            fontSize: 11.5, color: C.textSecondary, fontFamily: FONT.sans, flexShrink: 0,
          }}>
            <CounterChip>{pluralSegments(segments.length)}</CounterChip>
            <CounterChip>≈ {totalWords.toLocaleString('ru-RU')} {pluralWords(totalWords)}</CounterChip>
          </div>

          {/* Сегменты по порядку */}
          <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
            {segments.map(seg => (
              <SegmentCard key={seg.position} position={seg.position} content={seg.content} wordCount={seg.wordCount} />
            ))}
          </div>
        </>
      )}
    </ModalShell>
  );
}

// Каркас модалки: липкая шапка с именем документа и кнопкой закрытия + тело.
// Делаем свой layout вместо Modal-title/footer, т.к. шапка должна оставаться
// на месте при скролле длинного содержимого. Используем createPortal напрямую,
// сохраняя адаптив Modal (bottom-sheet на мобиле, центрированная карточка на десктопе).
function ModalShell({ doc, onClose, children }: {
  doc: KnowledgeDocument;
  onClose: () => void;
  children: React.ReactNode;
}) {
  const isMobile = useIsMobileViewport();

  // Закрытие по Escape
  useEffect(() => {
    const handler = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
    document.addEventListener('keydown', handler);
    return () => document.removeEventListener('keydown', handler);
  }, [onClose]);

  if (isMobile) {
    return createPortal(
      <div
        className="cc-overlay"
        style={{
          position: 'fixed', inset: 0, background: C.overlay, zIndex: 1000,
          display: 'flex', alignItems: 'flex-end',
        }}
        onPointerDown={e => { if (e.target === e.currentTarget) onClose(); }}
      >
        <div style={{
          background: C.bgMain, width: '100%', maxHeight: '92vh',
          borderTopLeftRadius: R.sheet, borderTopRightRadius: R.sheet,
          display: 'flex', flexDirection: 'column', boxSizing: 'border-box',
        }}>
          {/* Drag-handle — как в Modal */}
          <div style={{ display: 'flex', justifyContent: 'center', padding: '10px 0 4px', flexShrink: 0 }}>
            <div style={{ width: 38, height: 4, borderRadius: 2, background: C.track }} />
          </div>
          <DocHeader doc={doc} onClose={onClose} />
          <div style={{
            flex: 1, minHeight: 0, overflowY: 'auto',
            padding: '4px 18px calc(20px + env(safe-area-inset-bottom))',
            display: 'flex', flexDirection: 'column', gap: 16,
            WebkitOverflowScrolling: 'touch',
          }}>
            {children}
          </div>
        </div>
      </div>,
      document.body,
    );
  }

  // Десктоп/планшет — центрированная карточка с увеличенной шириной (читать длинный текст).
  return createPortal(
    <div
      className="cc-overlay"
      style={{
        position: 'fixed', inset: 0, background: C.overlay, zIndex: 1000,
        display: 'flex', justifyContent: 'center', alignItems: 'center', padding: 24,
      }}
      onPointerDown={e => { if (e.target === e.currentTarget) onClose(); }}
    >
      <div style={{
        background: C.bgMain, borderRadius: R.modal,
        width: '100%', maxWidth: 680, maxHeight: 'calc(100vh - 48px)',
        display: 'flex', flexDirection: 'column', overflow: 'hidden',
        boxShadow: 'var(--shadow-modal)',
      }}>
        <DocHeader doc={doc} onClose={onClose} />
        <div style={{
          flex: 1, minHeight: 0, overflowY: 'auto',
          padding: '6px 28px 28px', display: 'flex', flexDirection: 'column', gap: 18,
        }}>
          {children}
        </div>
      </div>
    </div>,
    document.body,
  );
}

// Шапка модалки: иконка-документ, имя документа (моноширинно) и кнопка закрытия.
// Имя может быть длинным — обрезаем многоточием. На мобиле отступы компактнее.
function DocHeader({ doc, onClose }: { doc: KnowledgeDocument; onClose: () => void }) {
  return (
    <div style={{
      flexShrink: 0, display: 'flex', alignItems: 'center', gap: 10,
      padding: '12px 18px', borderBottom: `1px solid ${C.borderLight}`,
    }}>
      <span style={{
        width: 28, height: 28, borderRadius: R.sm, background: C.bgPanel, flex: 'none',
        display: 'flex', alignItems: 'center', justifyContent: 'center', color: C.textSecondary,
      }}>
        <DocIcon />
      </span>
      <div style={{ flex: 1, minWidth: 0 }}>
        <div style={{
          fontFamily: FONT.mono, fontSize: 13, fontWeight: 500, color: C.textHeading,
          whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
        }} title={doc.name}>{doc.name}</div>
      </div>
      <button onClick={onClose} title="Закрыть" aria-label="Закрыть"
        style={{
          width: 32, height: 32, flexShrink: 0, border: 'none', background: 'transparent',
          cursor: 'pointer', color: C.textMuted, borderRadius: R.md,
          display: 'flex', alignItems: 'center', justifyContent: 'center',
          transition: 'background 0.12s, color 0.12s',
        }}
        onMouseEnter={e => { e.currentTarget.style.background = C.bgSelected; e.currentTarget.style.color = C.textPrimary; }}
        onMouseLeave={e => { e.currentTarget.style.background = 'transparent'; e.currentTarget.style.color = C.textMuted; }}>
        <IconClose size={18} />
      </button>
    </div>
  );
}

// Карточка одного сегмента: бейдж номера + текст (с переносами, читаемо).
// Текст — сырой контент чанка, не markdown; показываем с pre-wrap для сохранения
// структуры (абзацы, отступы) и нормального переноса длинных строк.
function SegmentCard({ position, content, wordCount }: {
  position: number;
  content: string;
  wordCount: number;
}) {
  return (
    <div style={{
      padding: '14px 16px', borderRadius: R.lg, background: C.bgCard,
      border: `1px solid ${C.borderLight}`,
    }}>
      <div style={{
        display: 'flex', alignItems: 'center', gap: 8, marginBottom: 8,
        fontSize: 10.5, fontWeight: 700, letterSpacing: '0.06em', textTransform: 'uppercase',
        color: C.textMuted, fontFamily: FONT.sans,
      }}>
        <span style={{
          display: 'inline-flex', alignItems: 'center', justifyContent: 'center',
          height: 18, minWidth: 20, padding: '0 6px', borderRadius: R.sm,
          background: C.bgSelected, color: C.textSecondary,
          fontSize: 10.5, fontWeight: 600, letterSpacing: 0, textTransform: 'none',
          fontFamily: FONT.mono,
        }}>#{position}</span>
        {wordCount > 0 && <span style={{ fontWeight: 500 }}>{wordCount.toLocaleString('ru-RU')} сл.</span>}
      </div>
      <div style={{
        fontSize: 13.5, lineHeight: 1.6, color: C.textPrimary, fontFamily: FONT.sans,
        whiteSpace: 'pre-wrap', wordBreak: 'break-word',
      }}>
        {content || <span style={{ color: C.textMuted, fontStyle: 'italic' }}>пустой сегмент</span>}
      </div>
    </div>
  );
}

function CounterChip({ children }: { children: React.ReactNode }) {
  return (
    <span style={{
      display: 'inline-flex', alignItems: 'center', height: 22, padding: '0 9px',
      borderRadius: R.sm, background: C.bgSelected, color: C.textSecondary,
      fontSize: 11.5, fontWeight: 500, whiteSpace: 'nowrap',
    }}>{children}</span>
  );
}

function StateView({ text, tone }: { text: string; tone?: 'error' }) {
  return (
    <div style={{
      padding: '32px 16px', textAlign: 'center', fontFamily: FONT.sans, fontSize: 13,
      color: tone === 'error' ? C.dangerText : C.textMuted, lineHeight: 1.5,
    }}>
      {text}
    </div>
  );
}

// Иконка документа для шапки (Feather file-text, общий стиль раздела)
function DocIcon() {
  return (
    <svg width={14} height={14} viewBox="0 0 24 24" fill="none" stroke="currentColor"
      strokeWidth={2} strokeLinecap="round" strokeLinejoin="round">
      <path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z" />
      <path d="M14 2v6h6M16 13H8M16 17H8M10 9H8" />
    </svg>
  );
}

// --- Утилиты ---

function pluralSegments(n: number): string {
  const last = n % 10, last2 = n % 100;
  if (last === 1 && last2 !== 11) return `${n} сегмент`;
  if (last >= 2 && last <= 4 && (last2 < 10 || last2 >= 20)) return `${n} сегмента`;
  return `${n} сегментов`;
}

function pluralWords(n: number): string {
  const last = n % 10, last2 = n % 100;
  if (last === 1 && last2 !== 11) return 'слово';
  if (last >= 2 && last <= 4 && (last2 < 10 || last2 >= 20)) return 'слов';
  return 'слов';
}

// Локальная копия определения мобилки (как в Modal.tsx) — чтобы не зависеть от
// внутреннего хука Modal и не тянуть лишний импорт. Порог — единый с раскладкой.
function useIsMobileViewport() {
  const [mobile, setMobile] = useState(() =>
    typeof window !== 'undefined'
      ? window.matchMedia(`(max-width: ${MOBILE_MAX}px)`).matches
      : false
  );
  useEffect(() => {
    const mq = window.matchMedia(`(max-width: ${MOBILE_MAX}px)`);
    const handler = (e: MediaQueryListEvent) => setMobile(e.matches);
    setMobile(mq.matches);
    if (mq.addEventListener) mq.addEventListener('change', handler);
    else mq.addListener(handler);
    return () => {
      if (mq.removeEventListener) mq.removeEventListener('change', handler);
      else mq.removeListener(handler);
    };
  }, []);
  return mobile;
}
