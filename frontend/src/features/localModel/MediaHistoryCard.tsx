// Карточка записи истории (макет §4): Modal MODAL_W.wide на десктопе (просмотр
// слева, сведения справа 260 px), шторка на мобиле (Modal сам). ←/→ листают
// записи ленты, Esc закрывает (фокус возвращается на строку), на мобиле —
// свайп вниз от верха закрывает.
//
// Просмотр: картинка — <img> по size=full + ImageLightbox (лупа); видео —
// <video controls>; звук — <audio controls>. Файл тянем fetch'ем: 410 несёт
// reason («результат больше не доступен»), 404 — «результата нет».
// Ошибка/отмена/прерывание до результата — файл не запрашиваем вовсе (макет:
// «плитка вида»).

import { useCallback, useEffect, useRef, useState } from 'react';
import { AudioLines, Check, ChevronLeft, ChevronRight, Clapperboard, Copy, Image as ImageIcon, ImageOff, Maximize2, User, X } from 'lucide-react';
import { C, FONT, FS, ICON_SIZE, ICON_STROKE, MODAL_W, R, SP } from 'aihome_shell/kit';
import { IconButton, ImageLightbox, Modal, WaitingIndicator } from '../../components/ui';
import type { MediaHistoryRecord } from './api';
import {
  formatDayTimeFromIso, goneReasonText, historyParamsRows,
  historyRowTitle, historyTimeRows, humanErrorPhrase, historyVariantCount,
} from './mediaFormat';

// URL файла результата: n — индекс варианта, size — thumb (webp) | full (оригинал)
export function historyFileUrl(id: string, n: number, size: 'thumb' | 'full'): string {
  return `/api/local-model/history/${encodeURIComponent(id)}/file?n=${n}&size=${size}`;
}

// Состояние просмотра результата
type Preview =
  | { phase: 'loading' }
  | { phase: 'ready'; url: string }
  | { phase: 'gone'; reason: string | null }   // 410: reason — одна из причин
  | { phase: 'noresult' }                       // 404 или исход без результата
  | { phase: 'error' };

// Завершённые исходы, у которых результата по построению нет
const NO_RESULT: Record<string, string> = {
  failed: 'Генерация завершилась ошибкой.',
  cancelled: 'Генерацию отменили.',
  interrupted: 'Генерацию прервали.',
};

// Заголовок карточки: вид · операция; без операции — только вид
function cardTitle(record: MediaHistoryRecord): string {
  return historyRowTitle(record.kind, record.op);
}

// Плитка вида в шапке: иконка по kind
function KindTile({ kind }: { kind?: string | null }) {
  const icon = kind === 'audio'
    ? <AudioLines size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
    : kind === 'video'
      ? <Clapperboard size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
      : <ImageIcon size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />;
  return (
    <span style={{
      width: 28, height: 28, flexShrink: 0, borderRadius: R.md,
      background: C.bgInset, display: 'flex', alignItems: 'center', justifyContent: 'center',
      color: C.textSecondary,
    }}>
      {icon}
    </span>
  );
}

// Бейдж исхода (та же семантика, что у строки)
function OutcomeBadge({ status }: { status?: string | null }) {
  const map: Record<string, { label: string; bg: string; fg: string }> = {
    completed: { label: 'готово', bg: C.successBg, fg: C.successText },
    cancelled: { label: 'отменено', bg: C.bgInset, fg: C.textSecondary },
    failed: { label: 'ошибка', bg: C.dangerBg, fg: C.dangerText },
    interrupted: { label: 'прервано', bg: C.warningBg, fg: C.warningText },
  };
  const b = status ? map[status] : null;
  if (!b) return null;
  return (
    <span style={{
      flexShrink: 0, display: 'inline-flex', alignItems: 'center',
      borderRadius: R.max, padding: '2px 8px', fontSize: FS.xs, fontWeight: 700,
      whiteSpace: 'nowrap', background: b.bg, color: b.fg,
    }}>
      {b.label}
    </span>
  );
}

// Секция сведений: заголовок + опциональная кнопка-иконка справа
function InfoSection({ title, action, children }: {
  title: string;
  action?: React.ReactNode;
  children: React.ReactNode;
}) {
  return (
    <div>
      <div style={{
        display: 'flex', alignItems: 'center', margin: '0 0 4px',
        fontSize: FS.xs, fontWeight: 700, textTransform: 'uppercase', letterSpacing: '0.06em',
        color: C.textMuted,
      }}>
        <span>{title}</span>
        {action}
      </div>
      {children}
    </div>
  );
}

// Таблица «ключ — значение»: mono для значений, na — приглушённый курсив
function KeyValueTable({ rows }: { rows: { key: string; value: string; mono?: boolean; na?: boolean }[] }) {
  return (
    <dl style={{
      display: 'grid', gridTemplateColumns: 'auto 1fr', gap: '4px 12px',
      fontSize: FS.sm, lineHeight: 1.45, margin: 0,
    }}>
      {rows.map(r => (
        <div key={r.key} style={{ display: 'contents' }}>
          <dt style={{ color: C.textMuted }}>{r.key}</dt>
          <dd style={{
            margin: 0, color: r.na ? C.textMuted : C.textPrimary,
            fontFamily: r.na ? FONT.sans : (r.mono ? FONT.mono : FONT.sans),
            fontStyle: r.na ? 'italic' : 'normal',
            overflowWrap: 'anywhere',
          }}>
            {r.value}
          </dd>
        </div>
      ))}
    </dl>
  );
}

// Просмотр результата: загрузка / готово / «результат больше не доступен» / «результата нет»
function Preview({ record, preview }: { record: MediaHistoryRecord; preview: Preview }) {
  const [lightbox, setLightbox] = useState(false);
  const isImage = record.kind === 'image';

  if (preview.phase === 'loading') {
    return (
      <div style={{
        borderRadius: R.xl, background: C.bgInset, minHeight: 200,
        display: 'flex', alignItems: 'center', justifyContent: 'center',
      }}>
        <WaitingIndicator />
      </div>
    );
  }

  if (preview.phase === 'gone' || preview.phase === 'noresult') {
    const gone = preview.phase === 'gone';
    return (
      <div style={{
        borderRadius: R.xl, background: C.bgInset, padding: '24px 16px',
        display: 'flex', flexDirection: 'column', alignItems: 'center', gap: 6,
        textAlign: 'center', color: C.textSecondary, fontSize: FS.sm, lineHeight: 1.45,
      }}>
        <ImageOff size={ICON_SIZE.lg} strokeWidth={ICON_STROKE} style={{ color: C.textMuted }} />
        <b style={{ color: C.textHeading, fontSize: FS.md, fontWeight: 700 }}>
          {gone ? 'Результат больше не доступен' : 'Результата нет'}
        </b>
        {gone ? goneReasonText(preview.reason) : (NO_RESULT[record.status] ?? 'Результат не сохранён.')}
      </div>
    );
  }

  if (preview.phase === 'error') {
    return (
      <div style={{
        borderRadius: R.xl, background: C.dangerBg, border: `1px solid ${C.dangerBorder}`,
        padding: '8px 12px', fontSize: FS.sm, color: C.dangerText,
      }}>
        Не удалось загрузить результат.
      </div>
    );
  }

  // ready
  if (isImage) {
    return (
      <div>
        <div
          onClick={() => setLightbox(true)}
          style={{
            position: 'relative', borderRadius: R.xl, overflow: 'hidden',
            background: C.bgInset, display: 'flex', alignItems: 'center', justifyContent: 'center',
            cursor: 'zoom-in',
          }}
        >
          <img
            src={preview.url}
            alt={cardTitle(record)}
            style={{ maxWidth: '100%', maxHeight: '60vh', objectFit: 'contain', display: 'block' }}
          />
          <span
            title="Открыть крупно"
            style={{
              position: 'absolute', right: 8, top: 8, width: 28, height: 28, borderRadius: R.md,
              background: C.overlay, color: C.onDark, display: 'flex', alignItems: 'center', justifyContent: 'center',
            }}
          >
            <Maximize2 size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
          </span>
        </div>
        {lightbox && (
          <ImageLightbox src={preview.url} alt={cardTitle(record)} onClose={() => setLightbox(false)} />
        )}
      </div>
    );
  }

  if (record.kind === 'video') {
    return (
      <div style={{ borderRadius: R.xl, overflow: 'hidden', background: C.bgInset }}>
        <video
          src={preview.url}
          controls
          preload="metadata"
          style={{ width: '100%', aspectRatio: '16 / 9', display: 'block', background: C.bgInset }}
        />
      </div>
    );
  }

  // audio и other
  return (
    <div style={{
      borderRadius: R.xl, background: C.bgInset, padding: SP.md,
      display: 'flex', alignItems: 'center', gap: SP.md,
    }}>
      <AudioLines size={ICON_SIZE.md} strokeWidth={ICON_STROKE} style={{ color: C.textSecondary, flexShrink: 0 }} />
      <audio src={preview.url} controls style={{ flex: 1, minWidth: 0, height: 40 }} />
    </div>
  );
}

// Ряд вариантов (batch): 40 px, выбранный — акцентная обводка
function Variants({ record, selected, onPick }: {
  record: MediaHistoryRecord;
  selected: number;
  onPick: (n: number) => void;
}) {
  const count = historyVariantCount(record.params);
  if (record.kind !== 'image' || count < 2) return null;
  return (
    <div style={{ display: 'flex', gap: 4, marginTop: 8 }}>
      {Array.from({ length: count }, (_, i) => (
        <button
          key={i}
          onClick={() => onPick(i)}
          aria-label={`Вариант ${i + 1}`}
          style={{
            width: 40, height: 40, borderRadius: R.sm, overflow: 'hidden', padding: 0,
            border: 'none', cursor: 'pointer', flexShrink: 0,
            boxShadow: i === selected ? `0 0 0 2px ${C.accent}` : 'none',
            background: C.bgInset,
          }}
        >
          <img
            src={historyFileUrl(record.id, i, 'thumb')}
            alt=""
            loading="lazy"
            style={{ width: '100%', height: '100%', objectFit: 'cover', display: 'block' }}
          />
        </button>
      ))}
    </div>
  );
}

// Карточка. record — текущая запись; onPrev/onNext — навигация по ленте
// (null — на первом/последнем); onClose — закрыть (фокус вернёт вызывающий).
// meUsername — логин текущего пользователя: совпал с owner — «Ваша генерация».
export function MediaHistoryCard({ record, isMobile, meUsername, onPrev, onNext, onClose }: {
  record: MediaHistoryRecord;
  isMobile: boolean;
  meUsername: string | null;
  onPrev: (() => void) | null;
  onNext: (() => void) | null;
  onClose: () => void;
}) {
  const [variant, setVariant] = useState(0);
  const [preview, setPreview] = useState<Preview>({ phase: 'loading' });
  const [copied, setCopied] = useState(false);
  const closeWrapRef = useRef<HTMLSpanElement>(null);
  const swipe = useRef<{ y: number; dy: number } | null>(null);

  // Файл результата: fetch (410 несёт reason, 404 — «результата нет»).
  // Ошибка/отмена/прерывание — файл не запрашиваем: результата по построению нет.
  useEffect(() => {
    let cancelled = false;
    let objectUrl: string | null = null;
    const done = record.status === 'completed';
    if (!done) {
      setPreview({ phase: 'noresult' });
      return;
    }
    setPreview({ phase: 'loading' });
    fetch(historyFileUrl(record.id, variant, 'full'))
      .then(async res => {
        if (cancelled) return;
        if (res.status === 410) {
          const body = await res.json().catch(() => null) as { reason?: string } | null;
          setPreview({ phase: 'gone', reason: body?.reason ?? null });
        } else if (res.status === 404) {
          setPreview({ phase: 'noresult' });
        } else if (res.ok) {
          const blob = await res.blob();
          objectUrl = URL.createObjectURL(blob);
          setPreview({ phase: 'ready', url: objectUrl });
        } else {
          setPreview({ phase: 'error' });
        }
      })
      .catch(() => { if (!cancelled) setPreview({ phase: 'error' }); });
    return () => {
      cancelled = true;
      if (objectUrl) URL.revokeObjectURL(objectUrl);
    };
  }, [record.id, variant, record.status]);

  // Фокус на крестик при открытии
  useEffect(() => {
    const btn = closeWrapRef.current?.querySelector('button');
    btn?.focus();
  }, []);

  // ←/→ — навигация по ленте
  useEffect(() => {
    const handler = (e: KeyboardEvent) => {
      if (e.key === 'ArrowLeft' && onPrev) { e.preventDefault(); onPrev(); }
      else if (e.key === 'ArrowRight' && onNext) { e.preventDefault(); onNext(); }
    };
    document.addEventListener('keydown', handler);
    return () => document.removeEventListener('keydown', handler);
  }, [onPrev, onNext]);

  const copyPrompt = useCallback(() => {
    if (!record.prompt) return;
    void navigator.clipboard?.writeText(record.prompt).then(() => {
      setCopied(true);
      window.setTimeout(() => setCopied(false), 1500);
    }).catch(() => { /* clipboard API недоступен — молча */ });
  }, [record.prompt]);

  // Свайп вниз (мобила): старт в верхней части шторки, ход > 60 px — закрыть
  const onPointerDown = (e: React.PointerEvent) => {
    if (!isMobile) return;
    const top = e.currentTarget.getBoundingClientRect().top;
    if (e.clientY - top > 60) return;
    swipe.current = { y: e.clientY, dy: 0 };
  };
  const onPointerMove = (e: React.PointerEvent) => {
    if (swipe.current) swipe.current.dy = e.clientY - swipe.current.y;
  };
  const onPointerUp = () => {
    if (swipe.current && swipe.current.dy > 60) onClose();
    swipe.current = null;
  };

  const title = cardTitle(record);
  const when = formatDayTimeFromIso(record.finishedAt ?? record.startedAt ?? record.enqueuedAt);
  // «чужая» — owner есть и это не текущий пользователь
  const foreign = record.owner != null && record.owner !== meUsername;
  const paramsRows = historyParamsRows(record);
  const timeRows = historyTimeRows(record);

  return (
    <Modal width={MODAL_W.wide} hideCloseButton onClose={onClose}>
      <div
        onPointerDown={onPointerDown}
        onPointerMove={onPointerMove}
        onPointerUp={onPointerUp}
        style={{ display: 'flex', flexDirection: 'column', gap: SP.md }}
      >
        {/* Шапка: плитка вида + заголовок + бейдж + навигация + крестик */}
        <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm }}>
          <KindTile kind={record.kind} />
          <h4 style={{
            margin: 0, flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis',
            whiteSpace: 'nowrap', fontSize: FS.lg, fontWeight: 700, color: C.textHeading,
            fontFamily: FONT.sans,
          }}>
            {title}
          </h4>
          <OutcomeBadge status={record.status} />
          <div style={{ display: 'flex', gap: 4, flexShrink: 0 }}>
            <IconButton size="sm" variant="ghost" ariaLabel="Предыдущая (←)" disabled={!onPrev} onClick={() => onPrev?.()}>
              <ChevronLeft size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
            </IconButton>
            <IconButton size="sm" variant="ghost" ariaLabel="Следующая (→)" disabled={!onNext} onClick={() => onNext?.()}>
              <ChevronRight size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
            </IconButton>
            <span ref={closeWrapRef} style={{ display: 'inline-flex' }}>
              <IconButton size="sm" variant="ghost" ariaLabel="Закрыть (Esc)" onClick={onClose}>
                <X size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
              </IconButton>
            </span>
          </div>
        </div>

        {/* Подзаголовок: чья генерация + дата */}
        <div style={{ fontSize: FS.sm, color: C.textSecondary }}>
          {foreign
            ? (
                <span style={{
                  display: 'inline-flex', alignItems: 'center', gap: 4,
                  background: C.infoBg, color: C.textHeading, borderRadius: R.max,
                  padding: '1px 8px', fontSize: FS.xs, marginRight: 4,
                }}>
                  <User size={11} strokeWidth={ICON_STROKE} />
                  Генерация пользователя <b>{record.owner}</b>
                </span>
              )
            : 'Ваша генерация · '}
          {when}
        </div>

        {/* Тело: просмотр + сведения (десктоп — сетка, мобила — колонка) */}
        <div style={{
          display: 'grid',
          gridTemplateColumns: isMobile ? '1fr' : '1fr 260px',
          gap: SP.lg,
          alignItems: 'start',
        }}>
          <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm, minWidth: 0 }}>
            <Preview record={record} preview={preview} />
            <Variants record={record} selected={variant} onPick={setVariant} />
          </div>

          <div style={{ display: 'flex', flexDirection: 'column', gap: SP.lg, minWidth: 0 }}>
            {record.status === 'failed' && record.error != null && (
              <div style={{
                background: C.dangerBg, border: `1px solid ${C.dangerBorder}`,
                borderRadius: R.md, padding: '8px 12px', fontSize: FS.sm, lineHeight: 1.45,
                color: C.dangerText,
              }}>
                <b>{humanErrorPhrase(record.error)}</b>
                <code style={{
                  display: 'block', fontFamily: FONT.mono, fontSize: 11,
                  color: C.textSecondary, marginTop: 4, overflowWrap: 'anywhere',
                }}>
                  {record.error}
                </code>
              </div>
            )}
            {record.status === 'interrupted' && (
              <div style={{
                background: C.warningBg, borderRadius: R.md, padding: '8px 12px',
                fontSize: FS.sm, lineHeight: 1.45, color: C.warningText,
              }}>
                <b>Прервано рестартом.</b> Сервер AI Home перезапустился, пока задача
                ждала очереди. Результата нет — запустите генерацию заново.
              </div>
            )}

            {record.prompt != null && (
              <InfoSection
                title="Промпт"
                action={
                  <IconButton
                    size="xs"
                    variant="ghost"
                    ariaLabel={copied ? 'Скопировано' : 'Скопировать промпт'}
                    onClick={copyPrompt}
                    style={{ marginLeft: 'auto' }}
                  >
                    {copied
                      ? <Check size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ color: C.successText }} />
                      : <Copy size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
                  </IconButton>
                }
              >
                <div style={{
                  fontSize: FS.md, lineHeight: 1.5, color: C.textPrimary,
                  background: C.bgInset, borderRadius: R.md, padding: '8px 12px',
                  maxHeight: 120, overflow: 'auto', whiteSpace: 'pre-wrap',
                }}>
                  {record.prompt}
                </div>
              </InfoSection>
            )}

            {paramsRows.length > 0 && (
              <InfoSection title="Параметры">
                <KeyValueTable rows={paramsRows} />
              </InfoSection>
            )}

            <InfoSection title="Время и карта">
              <KeyValueTable rows={timeRows} />
            </InfoSection>
          </div>
        </div>
      </div>
    </Modal>
  );
}
