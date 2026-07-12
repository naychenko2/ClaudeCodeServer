import { useEffect, useState } from 'react';
import type { KnowledgeDocument, KnowledgeDocumentContent } from '../../types';
import { api } from '../../lib/api';
import { C, FONT, R } from '../../lib/design';
import { IconClose, IconBack } from './shared';

// Просмотр содержимого документа базы знаний как встраиваемая правая панель.
//
// Паттерн — общий с «Артефактами сессии» (ArtifactsPanel): на десктопе это push-колонка
// в потоке справа от списка (width задаёт KnowledgeView через стиль контейнера, там же
// сплиттер для ресайза), на мобиле — полноэкранный take-over (list↔item как в NoteView,
// PersonasPage). Панель НЕ рисует оверлей/портал: владелец (KnowledgeView) решает, как
// её разместить — встраивается в обычный flex-поток и заполняет 100% высоты контейнера.
//
// Шапка липкая (имя документа моноширинно + счётчики + закрытие/назад), тело скроллится.
// Сегменты — чанки текста по порядку, raw-контент с pre-wrap (без markdown-рендера).
//
// Контракт (не меняем): api.knowledgeBases.getDocument(kbId, docId) →
// { id, segments: [{ position, content, wordCount }] }.

export function DocumentViewer({ kbId, doc, onClose, isMobile }: {
  kbId: string;
  doc: KnowledgeDocument;
  onClose: () => void;
  isMobile: boolean;
}) {
  const [data, setData] = useState<KnowledgeDocumentContent | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  // Загружаем содержимое документа. Перестарт при смене doc.id.
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

  // Закрытие по Escape — как в любых диалогах/панелях проекта.
  useEffect(() => {
    const handler = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
    document.addEventListener('keydown', handler);
    return () => document.removeEventListener('keydown', handler);
  }, [onClose]);

  const segments = data?.segments ?? [];
  const totalWords = segments.reduce((s, seg) => s + (seg.wordCount ?? 0), 0);

  return (
    <div style={{
      width: '100%', height: '100%', display: 'flex', flexDirection: 'column',
      background: C.bgMain, overflow: 'hidden', minHeight: 0,
    }}>
      <DocHeader doc={doc} onClose={onClose} isMobile={isMobile} />

      <div style={{
        flex: 1, minHeight: 0, overflowY: 'auto',
        // На мобиле читаем с учётом safe-area (жесткий take-over), на десктопе — обычные отступы.
        padding: isMobile
          ? '6px 18px calc(20px + env(safe-area-inset-bottom))'
          : '6px 22px 24px',
        display: 'flex', flexDirection: 'column', gap: isMobile ? 16 : 18,
        WebkitOverflowScrolling: 'touch',
      }}>
        {error ? (
          <StateView text={error} tone="error" />
        ) : loading ? (
          <StateView text="Загрузка документа…" />
        ) : segments.length === 0 ? (
          <StateView text="В документе нет сегментов. Возможно, индексация ещё не завершена." />
        ) : (
          <>
            {/* Метаданные под шапкой — счётчики сегментов и слов */}
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
      </div>
    </div>
  );
}

// Шапка панели: иконка-документ, имя (моноширинно, обрезается многоточием), действие справа.
// Действие зависит от раскладки: мобила — «назад» (стрелка влево, list↔item take-over),
// десктоп — «закрыть» (крестик, колонка в потоке). Слева от имени на мобиле — та же стрелка
// влево, что и в шапках NoteView/Workspace при detail-режиме; на десктопе слева — иконка файла.
function DocHeader({ doc, onClose, isMobile }: { doc: KnowledgeDocument; onClose: () => void; isMobile: boolean }) {
  return (
    <div style={{
      flexShrink: 0, display: 'flex', alignItems: 'center', gap: 10,
      padding: isMobile ? '10px 14px' : '12px 16px',
      borderBottom: `1px solid ${C.borderLight}`,
      background: C.bgMain,
    }}>
      {/* Иконка-документ (квадратный «плиточный» контейнер, как в строках списка) */}
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
      {/* На мобиле — «назад» (take-over), на десктопе — «закрыть» колонку */}
      <button onClick={onClose}
        title={isMobile ? 'Назад к списку' : 'Закрыть просмотр'}
        aria-label={isMobile ? 'Назад к списку' : 'Закрыть просмотр'}
        style={{
          width: 32, height: 32, flexShrink: 0, border: 'none', background: 'transparent',
          cursor: 'pointer', color: C.textMuted, borderRadius: R.md,
          display: 'flex', alignItems: 'center', justifyContent: 'center',
          transition: 'background 0.12s, color 0.12s',
        }}
        onMouseEnter={e => { e.currentTarget.style.background = C.bgSelected; e.currentTarget.style.color = C.textPrimary; }}
        onMouseLeave={e => { e.currentTarget.style.background = 'transparent'; e.currentTarget.style.color = C.textMuted; }}>
        {isMobile ? <IconBack size={18} /> : <IconClose size={18} />}
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
