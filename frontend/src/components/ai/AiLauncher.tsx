import { useEffect, useMemo, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { C, FONT, R, Z, SHADOW } from '../../lib/design';
import { getNav } from '../../lib/nav';
import { getFlag } from '../../lib/featureFlags';
import { api } from '../../lib/api';
import { useOnline } from '../../hooks/useOnline';
import { rankedActions, runActionById, type AiAction, type AiActionCtx } from '../../lib/ai/actions';
import {
  computeSuggestion, canShow, markShown, markDismissed,
  isProactiveEnabled, setProactiveEnabled, type Suggestion,
} from '../../lib/ai/proactive';

// AI-хаб (pull-слой): плавающая кнопка + командная палитра. Открывается кликом,
// хоткеем ⌘/Ctrl+K или событием 'cc-open-ai'. Палитра через getNav() знает текущий
// раздел и поднимает наверх релевантные действия. Всё гейтится флагом ai-hub в App.
export const OPEN_AI_EVENT = 'cc-open-ai';

export function AiLauncher() {
  const online = useOnline();
  const [open, setOpen] = useState(false);
  const [q, setQ] = useState('');
  const [idx, setIdx] = useState(0);
  const inputRef = useRef<HTMLInputElement>(null);
  // Push-слой: активная проактивная подсказка + состояние тумблера
  const [suggestion, setSuggestion] = useState<Suggestion | null>(null);
  const [proactiveOn, setProactiveOn] = useState(isProactiveEnabled());
  // Доступность семантики (Dify) — для действия «Поиск по смыслу»
  const [semanticCaps, setSemanticCaps] = useState(false);
  // Мобильный вид — палитра становится нижней шторкой
  const [isMobile, setIsMobile] = useState(() => typeof window !== 'undefined' && window.matchMedia('(max-width: 767px)').matches);
  useEffect(() => { api.notes.caps().then(c => setSemanticCaps(c.semantic)).catch(() => {}); }, []);
  useEffect(() => {
    const mq = window.matchMedia('(max-width: 767px)');
    const h = (e: MediaQueryListEvent) => setIsMobile(e.matches);
    mq.addEventListener('change', h);
    return () => mq.removeEventListener('change', h);
  }, []);

  // Контекст собираем на момент открытия (getNav синхронен вне React)
  const buildCtx = (): AiActionCtx => ({ nav: getNav(), online, flag: getFlag, caps: { semantic: semanticCaps } });

  // Список действий пересчитывается на каждый ввод, пока палитра открыта
  const items = useMemo(
    () => (open ? rankedActions(buildCtx(), q) : []),
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [open, q, online, semanticCaps],
  );

  useEffect(() => { setIdx(0); }, [q, open]);
  useEffect(() => { if (open) setTimeout(() => inputRef.current?.focus(), 40); }, [open]);

  // Глобальный хоткей ⌘/Ctrl+K и внешнее открытие
  useEffect(() => {
    // Capture-фаза: перехватываем ⌘/Ctrl+K раньше редакторов (CodeMirror и т.п.),
    // иначе «глобальный» хоткей не срабатывал бы при фокусе внутри заметки.
    const onKey = (e: KeyboardEvent) => {
      if ((e.metaKey || e.ctrlKey) && (e.key === 'k' || e.key === 'K')) {
        e.preventDefault();
        e.stopPropagation();
        setOpen(o => !o);
      }
    };
    const onOpen = () => { setQ(''); setOpen(true); };
    window.addEventListener('keydown', onKey, true);
    window.addEventListener(OPEN_AI_EVENT, onOpen);
    return () => { window.removeEventListener('keydown', onKey, true); window.removeEventListener(OPEN_AI_EVENT, onOpen); };
  }, []);

  const close = () => setOpen(false);
  const fire = (a: AiAction) => { close(); a.run(buildCtx()); };

  // Проактивный движок: тихо опрашиваем контекст, после короткого «дожития» на
  // одном месте предлагаем релевантное действие (с дозировкой из proactive.ts).
  useEffect(() => {
    let lastSig = '';
    let stableSince = Date.now();
    let firedForSig = '';
    const DWELL = 4000;
    const sigOf = (n: ReturnType<typeof getNav>) => n ? `${n.screen}|${n.note ?? ''}|${n.task ?? ''}` : '';
    const tick = () => {
      if (open || !isProactiveEnabled()) return;
      const ctx = buildCtx();
      const sig = sigOf(ctx.nav);
      if (sig !== lastSig) { lastSig = sig; stableSince = Date.now(); firedForSig = ''; setSuggestion(null); return; }
      if (firedForSig === sig || Date.now() - stableSince < DWELL) return;
      firedForSig = sig; // помечаем сразу, чтобы не дёргать данные каждый тик
      void computeSuggestion(ctx).then(sug => {
        if (!sug || !canShow(sug.key)) return;
        // За время запроса контекст мог смениться — не показываем устаревшую подсказку
        if (sigOf(getNav()) !== sig || open) return;
        markShown();
        setSuggestion(sug);
      });
    };
    const h = setInterval(tick, 1500);
    return () => clearInterval(h);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, online]);

  const acceptSuggestion = () => {
    if (!suggestion) return;
    const s = suggestion;
    markDismissed(s.key);
    setSuggestion(null);
    runActionById(s.actionId, buildCtx());
  };
  const dismissSuggestion = () => {
    if (suggestion) markDismissed(suggestion.key);
    setSuggestion(null);
  };
  const toggleProactive = () => {
    const next = !proactiveOn;
    setProactiveEnabled(next);
    setProactiveOn(next);
    if (!next) setSuggestion(null);
  };

  const onInputKey = (e: React.KeyboardEvent) => {
    if (e.key === 'ArrowDown') { e.preventDefault(); if (items.length) setIdx(i => (i + 1) % items.length); }
    else if (e.key === 'ArrowUp') { e.preventDefault(); if (items.length) setIdx(i => (i - 1 + items.length) % items.length); }
    else if (e.key === 'Enter') { e.preventDefault(); if (items[idx]) fire(items[idx].action); }
    else if (e.key === 'Escape') { e.preventDefault(); close(); }
  };

  const nav = getNav();
  const ctxLabel = sectionLabelForScreen(nav?.screen);

  return (
    <>
      {/* Проактивная подсказка (push) — тихий балун у кнопки */}
      {!open && suggestion && (
        <div style={balloonStyle} role="status">
          <div style={balloonHead}>
            <span style={{ color: C.accent, display: 'flex' }}><SparkleIcon size={15} /></span>
            <b style={{ fontSize: 12.5, color: C.textHeading }}>AI может помочь</b>
            <button onClick={dismissSuggestion} aria-label="Скрыть" style={balloonClose}>×</button>
          </div>
          <p style={{ margin: '0 0 11px', fontSize: 13, color: C.textPrimary, lineHeight: 1.4 }}>{suggestion.text}</p>
          <div style={{ display: 'flex', gap: 8 }}>
            <button onClick={acceptSuggestion} style={balloonPrimary}>Предложить</button>
            <button onClick={dismissSuggestion} style={balloonGhost}>Позже</button>
          </div>
        </div>
      )}

      {/* Плавающая кнопка */}
      {!open && (
        <button
          onClick={() => { setQ(''); setOpen(true); }}
          aria-label="AI-действия (Ctrl/⌘ + K)"
          title="AI-действия · ⌘K"
          style={fabStyle}
          onMouseEnter={e => { e.currentTarget.style.transform = 'translateY(-2px)'; }}
          onMouseLeave={e => { e.currentTarget.style.transform = 'translateY(0)'; }}
        >
          {suggestion && <span style={pulseDot} />}
          <SparkleIcon />
        </button>
      )}

      {open && createPortal(
        <div style={{ ...overlayStyle, ...(isMobile ? { alignItems: 'flex-end', paddingTop: 0 } : {}) }} onMouseDown={close}>
          <div
            style={{ ...paletteStyle, ...(isMobile ? { width: '100%', maxWidth: '100%', borderRadius: `${R.sheet}px ${R.sheet}px 0 0`, maxHeight: '82vh' } : {}) }}
            onMouseDown={e => e.stopPropagation()} role="dialog" aria-label="AI-палитра">
            {/* Поиск + бейдж контекста */}
            <div style={searchRow}>
              <span style={{ color: C.accent, display: 'flex', flex: 'none' }}><SparkleIcon size={18} /></span>
              <input
                ref={inputRef} value={q} onChange={e => setQ(e.target.value)} onKeyDown={onInputKey}
                placeholder="Что сделать с помощью AI…" autoComplete="off" style={inputStyle}
              />
              {ctxLabel && <span style={ctxBadge}>{ctxLabel}</span>}
            </div>

            {/* Список */}
            <div style={{ overflowY: 'auto', padding: 8, maxHeight: 'min(52vh, 400px)' }}>
              {items.length === 0 && (
                <div style={emptyStyle}>{q ? 'Ничего не найдено' : 'Нет доступных действий'}</div>
              )}
              {items.map((it, i) => {
                const prev = items[i - 1];
                const showCtxHeader = it.contextual && i === 0;
                const showSecHeader = !it.contextual && (!prev || prev.contextual || prev.action.section !== it.action.section);
                return (
                  <div key={it.action.id}>
                    {showCtxHeader && <div style={{ ...groupHeader, color: C.accent }}>Здесь и сейчас{ctxLabel ? ` · ${ctxLabel}` : ''}</div>}
                    {showSecHeader && <div style={groupHeader}>{it.action.sectionLabel}</div>}
                    <button
                      onClick={() => fire(it.action)}
                      onMouseEnter={() => setIdx(i)}
                      style={{ ...itemStyle, background: i === idx ? C.accentLight : 'transparent' }}
                    >
                      <span style={{ ...itemIco, background: i === idx ? C.bgWhite : C.bgSelected }}>{it.action.icon}</span>
                      <span style={{ flex: 1, minWidth: 0 }}>
                        <span style={itemTitle}>{it.action.title}</span>
                        <span style={itemHint}>{it.action.hint}</span>
                      </span>
                      <span style={itemSec}>{it.action.sectionLabel}</span>
                    </button>
                  </div>
                );
              })}
            </div>

            {/* Футер: хоткеи + тумблер проактивных подсказок */}
            <div style={footStyle}>
              <span><Kbd>↑↓</Kbd> навигация</span>
              <span><Kbd>↵</Kbd> выполнить</span>
              <span><Kbd>esc</Kbd> закрыть</span>
              <button onClick={toggleProactive} style={toggleBtn}
                title={proactiveOn ? 'Проактивные подсказки включены' : 'Проактивные подсказки выключены'}>
                <span style={{ ...toggleTrack, background: proactiveOn ? C.accent : C.track }}>
                  <span style={{ ...toggleThumb, transform: proactiveOn ? 'translateX(12px)' : 'translateX(0)' }} />
                </span>
                Подсказки
              </button>
            </div>
          </div>
        </div>,
        document.body,
      )}
    </>
  );
}

function sectionLabelForScreen(screen?: string): string | null {
  switch (screen) {
    case 'notes': return 'Заметки';
    case 'calendar': return 'Задачи';
    case 'chats': return 'Чат';
    case 'project': return 'Проект';
    default: return null;
  }
}

function SparkleIcon({ size = 24 }: { size?: number }) {
  return (
    <svg viewBox="0 0 24 24" width={size} height={size} fill="currentColor" aria-hidden="true">
      <path d="M12 1.6l1.9 6.2 6.2 1.9-6.2 1.9L12 17.8l-1.9-6.2L3.9 9.7l6.2-1.9z" />
      <path d="M18.6 14.4l.8 2.5 2.5.8-2.5.8-.8 2.5-.8-2.5-2.5-.8 2.5-.8z" opacity={0.7} />
    </svg>
  );
}

function Kbd({ children }: { children: React.ReactNode }) {
  return (
    <span style={{
      fontFamily: FONT.mono, fontSize: 11, color: C.textMuted, background: C.bgInset,
      border: `1px solid ${C.border}`, borderRadius: 5, padding: '1px 6px',
    }}>{children}</span>
  );
}

const fabStyle: React.CSSProperties = {
  position: 'fixed', right: 20, bottom: 'calc(env(safe-area-inset-bottom, 0px) + 20px)',
  width: 54, height: 54, borderRadius: '50%', border: 'none', cursor: 'pointer',
  background: C.accent, color: C.onAccent, boxShadow: SHADOW.fab,
  display: 'grid', placeItems: 'center', zIndex: Z.modal - 1, transition: 'transform .16s',
};

const overlayStyle: React.CSSProperties = {
  position: 'fixed', inset: 0, background: C.overlay, zIndex: Z.modal,
  display: 'flex', alignItems: 'flex-start', justifyContent: 'center', paddingTop: '10vh',
};
const paletteStyle: React.CSSProperties = {
  width: 'min(560px, 92vw)', background: C.bgCard, border: `1px solid ${C.border}`,
  borderRadius: R.modal, boxShadow: SHADOW.modal, overflow: 'hidden',
  display: 'flex', flexDirection: 'column',
};
const searchRow: React.CSSProperties = {
  display: 'flex', alignItems: 'center', gap: 10, padding: '13px 16px',
  borderBottom: `1px solid ${C.border}`,
};
const inputStyle: React.CSSProperties = {
  flex: 1, minWidth: 0, border: 'none', background: 'transparent', outline: 'none',
  fontFamily: FONT.sans, fontSize: 15.5, color: C.textHeading,
};
const ctxBadge: React.CSSProperties = {
  flex: 'none', fontFamily: FONT.mono, fontSize: 10.5, textTransform: 'uppercase', letterSpacing: 0.4,
  color: C.textMuted, background: C.bgInset, border: `1px solid ${C.border}`, borderRadius: R.sm, padding: '3px 8px',
};
const groupHeader: React.CSSProperties = {
  fontFamily: FONT.mono, fontSize: 10.5, textTransform: 'uppercase', letterSpacing: 0.6,
  color: C.textMuted, padding: '10px 10px 4px',
};
const itemStyle: React.CSSProperties = {
  display: 'flex', alignItems: 'center', gap: 12, width: '100%', textAlign: 'left',
  border: 'none', cursor: 'pointer', borderRadius: R.lg, padding: '9px 10px', fontFamily: FONT.sans,
};
const itemIco: React.CSSProperties = {
  width: 30, height: 30, borderRadius: R.md, display: 'grid', placeItems: 'center',
  color: C.accent, flex: 'none',
};
const itemTitle: React.CSSProperties = { display: 'block', fontSize: 14, fontWeight: 600, color: C.textHeading };
const itemHint: React.CSSProperties = {
  display: 'block', fontSize: 12, color: C.textMuted, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
};
const itemSec: React.CSSProperties = {
  flex: 'none', fontFamily: FONT.mono, fontSize: 10, textTransform: 'uppercase', letterSpacing: 0.4, color: C.textMuted,
};
const emptyStyle: React.CSSProperties = { padding: 26, textAlign: 'center', fontSize: 13.5, color: C.textMuted, fontFamily: FONT.sans };
const footStyle: React.CSSProperties = {
  display: 'flex', alignItems: 'center', gap: 16, padding: '8px 14px', borderTop: `1px solid ${C.border}`,
  fontSize: 11.5, color: C.textMuted, fontFamily: FONT.sans,
};
const toggleBtn: React.CSSProperties = {
  marginLeft: 'auto', display: 'inline-flex', alignItems: 'center', gap: 6,
  border: 'none', background: 'transparent', cursor: 'pointer',
  fontFamily: FONT.sans, fontSize: 11.5, color: C.textMuted,
};
const toggleTrack: React.CSSProperties = {
  width: 26, height: 15, borderRadius: 999, position: 'relative', flex: 'none', transition: 'background .16s',
};
const toggleThumb: React.CSSProperties = {
  position: 'absolute', top: 2, left: 2, width: 11, height: 11, borderRadius: '50%',
  background: C.bgWhite, boxShadow: SHADOW.thumb, transition: 'transform .16s',
};

// --- Push-слой: пульс на кнопке + балун подсказки ---
const pulseDot: React.CSSProperties = {
  position: 'absolute', top: 3, right: 4, width: 12, height: 12, borderRadius: '50%',
  background: C.success, border: `2px solid ${C.accent}`,
};
const balloonStyle: React.CSSProperties = {
  position: 'fixed', right: 20, bottom: 'calc(env(safe-area-inset-bottom, 0px) + 86px)',
  width: 280, background: C.bgCard, border: `1px solid ${C.accentMuted}`, borderRadius: R.xl,
  boxShadow: SHADOW.modal, padding: '13px 14px 12px', zIndex: Z.modal - 1, fontFamily: FONT.sans,
};
const balloonHead: React.CSSProperties = { display: 'flex', alignItems: 'center', gap: 7, marginBottom: 7 };
const balloonClose: React.CSSProperties = {
  marginLeft: 'auto', border: 'none', background: 'none', cursor: 'pointer', color: C.textMuted, fontSize: 16, lineHeight: 1, padding: 2,
};
const balloonPrimary: React.CSSProperties = {
  border: 'none', background: C.accent, color: C.onAccent, borderRadius: R.md, padding: '7px 12px',
  fontFamily: FONT.sans, fontSize: 12.5, fontWeight: 600, cursor: 'pointer',
};
const balloonGhost: React.CSSProperties = {
  border: `1px solid ${C.border}`, background: 'transparent', color: C.textMuted, borderRadius: R.md, padding: '7px 12px',
  fontFamily: FONT.sans, fontSize: 12.5, fontWeight: 600, cursor: 'pointer',
};
