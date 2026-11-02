// Поповер сноски страницы вики (F3a, ADR-038 п.3, макет notes-wiki-unified-v1 §3): на 1280 —
// плавающая карточка у сноски, на 360 — шторка снизу с тем же содержимым. Сноска ИИ: номер,
// дата, цитата, источник и «Открыть в чате»; сноска человека: «Написал …» и пояснение про
// замену. Источник ИИ-сноски подгружается отдельным чтением карточки (мимо слота страницы
// стора); ответ, пришедший после смены сноски или страницы, отбрасывается по ключу.
// Доступность: фокус внутрь при открытии и возврат на сноску при закрытии, Esc и клик по
// подложке закрывают, aria-label у диалога.

import { useEffect, useRef, useState } from 'react';
import { C, FONT, FS, R, SHADOW, SP, Z, openChatById, useIsMobile, loadWikiSourceCard, type WikiSourceCardInfo } from 'aihome_shell/kit';
import { Button } from '../../../components/ui/Button';

// Тексты — дословно из ADR-038 п.3 / записки макета; тест сверяет их с файлом ADR
export const FOOTNOTE_TEXT = {
  humanBody: 'Ваша правка. ИИ может заменить её, если найдёт источник новее вашей правки. '
    + 'Прежняя формулировка тогда уйдёт в «Историю», а здесь появится «Вернуть мою».',
  openChat: 'Открыть в чате',
  aiTitle: (num: string, date: string) => `Сноска ${num} · записал ИИ${date ? `, ${date}` : ''}`,
  humanTitle: (name: string | null, date: string) =>
    `${name ? `Написал ${name}` : 'Написали вы'}${date ? `, ${date}` : ''}`,
  source: (name: string) => `Источник: ${name}`,
  sourceChat: (name: string) => `Источник: чат «${name}»`,
  sourceLoading: 'Читаю источник…',
  sourceFailed: 'Источник сейчас не прочитать.',
} as const;

// ISO 8601 → «дд.мм» (как плашка «✎ Андрей, 02.11»); непонятное — как есть, пустое — пусто
export function fmtDayMonth(iso: string | null | undefined): string {
  if (!iso) return '';
  const m = /^\d{4}-(\d{2})-(\d{2})/.exec(iso);
  return m ? `${m[2]}.${m[1]}` : iso;
}

export interface FootnoteView {
  label: string;
  card: string;
  cardPage: string | null;
  human: boolean;
  author: string | null;
  captured: string | null;
  quote: string;
}

const CARD_W = 340;
const EDGE = 8;

type SourceState = { key: string; status: 'loading' | 'ok' | 'failed'; info?: WikiSourceCardInfo };

export function WikiFootnotePopover({ sectionKey, pageKey, fn, anchor, onClose }: {
  sectionKey: string;
  pageKey: string;
  fn: FootnoteView;
  anchor: DOMRect | null;
  onClose: () => void;
}) {
  const isMobile = useIsMobile();
  const boxRef = useRef<HTMLDivElement>(null);
  const closeRef = useRef(onClose);
  closeRef.current = onClose;
  // Ключ того, что открыто сейчас: ответ чтения источника применяется, только если ключ тот же
  const key = `${sectionKey}\n${pageKey}\n${fn.label}`;
  const activeKey = useRef<string | null>(null);
  const [src, setSrc] = useState<SourceState | null>(null);

  useEffect(() => {
    const prev = document.activeElement as HTMLElement | null;
    boxRef.current?.focus();
    const onKey = (e: KeyboardEvent) => {
      if (e.key !== 'Escape' || e.defaultPrevented) return;
      e.preventDefault();
      closeRef.current();
    };
    document.addEventListener('keydown', onKey);
    return () => {
      document.removeEventListener('keydown', onKey);
      prev?.focus?.();
    };
  }, []);

  useEffect(() => {
    activeKey.current = key;
    if (fn.human) { setSrc(null); return () => { activeKey.current = null; }; }
    // Нет ключа страницы карточки — читать нечего, запрос не уходит
    if (!fn.cardPage) { setSrc({ key, status: 'failed' }); return () => { activeKey.current = null; }; }
    setSrc({ key, status: 'loading' });
    loadWikiSourceCard(sectionKey, fn.cardPage).then(
      info => { if (activeKey.current === key) setSrc({ key, status: 'ok', info }); },
      () => { if (activeKey.current === key) setSrc({ key, status: 'failed' }); },
    );
    return () => { activeKey.current = null; };
  }, [key, sectionKey, fn.cardPage, fn.human]);

  const num = /^s(\d+)$/.exec(fn.label)?.[1] ?? fn.label;
  const date = fmtDayMonth(fn.captured);
  const title = fn.human ? FOOTNOTE_TEXT.humanTitle(fn.author, date) : FOOTNOTE_TEXT.aiTitle(num, date);
  const state = src && src.key === key ? src : null;
  const chatId = state?.info?.ref?.startsWith('session:')
    ? state.info.ref.slice('session:'.length).split('#')[0] : '';

  const vw = (typeof window !== 'undefined' && window.innerWidth) || 1280;
  const vh = (typeof window !== 'undefined' && window.innerHeight) || 800;
  const floating = !isMobile && anchor;
  const pos = floating
    ? {
        left: Math.max(EDGE, Math.min(anchor.left, vw - CARD_W - EDGE)),
        ...(anchor.bottom + 220 > vh && anchor.top > 220
          ? { bottom: vh - anchor.top + EDGE } : { top: anchor.bottom + EDGE }),
        width: CARD_W,
        borderRadius: R.xl,
      }
    : { left: 0, right: 0, bottom: 0, maxHeight: '80vh', borderRadius: `${R.sheet}px ${R.sheet}px 0 0` };

  return (
    <>
      <div
        data-testid="wiki-footnote-overlay"
        onClick={onClose}
        style={{ position: 'fixed', inset: 0, zIndex: Z.modal, background: isMobile ? C.overlay : 'transparent' }}
      />
      <div
        ref={boxRef}
        tabIndex={-1}
        role="dialog"
        aria-modal={isMobile ? 'true' : undefined}
        aria-label={title}
        data-testid="wiki-footnote-popover"
        data-mode={floating ? 'popover' : 'sheet'}
        style={{
          position: 'fixed', zIndex: Z.modal + 1, background: C.bgWhite, boxShadow: SHADOW.dropdown,
          border: `1px solid ${C.border}`, outline: 'none', overflowY: 'auto',
          padding: SP.md, display: 'flex', flexDirection: 'column', gap: SP.sm, ...pos,
        }}
      >
        <div style={{ fontSize: FS.sm, fontWeight: 600, color: C.textHeading }}>{title}</div>
        {fn.human ? (
          <div style={{ fontSize: FS.sm, color: C.textSecondary, lineHeight: 1.5 }}>{FOOTNOTE_TEXT.humanBody}</div>
        ) : (
          <>
            {fn.quote && (
              <div style={{ borderLeft: `3px solid ${C.info}`, paddingLeft: SP.sm, fontFamily: FONT.serif, fontSize: FS.md, color: C.textPrimary, lineHeight: 1.5, overflowWrap: 'anywhere' }}>
                «{fn.quote}»
              </div>
            )}
            <div data-testid="wiki-footnote-source" style={{ fontSize: FS.sm, color: C.textSecondary, overflowWrap: 'anywhere' }}>
              {!state || state.status === 'loading' ? FOOTNOTE_TEXT.sourceLoading
                : state.status === 'failed' ? FOOTNOTE_TEXT.sourceFailed
                : state.info?.name
                  ? (chatId ? FOOTNOTE_TEXT.sourceChat(state.info.name) : FOOTNOTE_TEXT.source(state.info.name))
                  : FOOTNOTE_TEXT.source(fn.card)}
            </div>
            {chatId && (
              <div>
                <Button
                  variant="secondary"
                  size={isMobile ? 'md' : 'sm'}
                  dataTestId="wiki-footnote-open-chat"
                  onClick={() => { void openChatById(chatId); onClose(); }}
                >
                  {FOOTNOTE_TEXT.openChat}
                </Button>
              </div>
            )}
          </>
        )}
      </div>
    </>
  );
}
