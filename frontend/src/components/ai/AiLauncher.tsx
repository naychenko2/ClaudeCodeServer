import { useEffect, useMemo, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { C, FONT, R, Z, SHADOW } from '../../lib/design';
import { getNav, NAV_CHANGE_EVENT } from '../../lib/nav';
import { useAiBusy } from '../../lib/ai/busy';
import { getFlag } from '../../lib/featureFlags';
import { api } from '../../lib/api';
import { useOnline } from '../../hooks/useOnline';
import { rankedActions, runActionById, AI_ACTIONS, type AiAction, type AiActionCtx } from '../../lib/ai/actions';
import { getChatContext, AI_RECOMPUTE_EVENT } from '../../lib/ai/chatContext';
import { useIsMobile } from '../../lib/breakpoints';
import { FLAGS } from '../../lib/featureFlags';
import { shouldSurface, levelLabel, type SuggestionLevel } from '../../lib/ai/levels';
import { rankContext } from '../../lib/ai/suggest';
import { aiOllamaAvailable } from '../../lib/ai/ollama';
import {
  computeContextState, canShow, markShown, markDismissed,
  isProactiveEnabled, setProactiveEnabled, type Suggestion, type ActionRec,
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
  const hoverTimer = useRef<number | null>(null);
  // Push-слой: активная проактивная подсказка + состояние тумблера
  const [suggestion, setSuggestion] = useState<Suggestion | null>(null);
  const [proactiveOn, setProactiveOn] = useState(isProactiveEnabled());
  // Агрегатный уровень контекста — определяет вид FAB (бледный/яркий/анимация), даже
  // когда балун подавлен дозировкой. Активна градация только при флаге ai-local-suggest.
  const [fabLevel, setFabLevel] = useState<SuggestionLevel>('none');
  const gradedFab = getFlag(FLAGS.aiLocalSuggest);
  // Доступность семантики (Dify) — для действия «Поиск по смыслу»
  const [semanticCaps, setSemanticCaps] = useState(false);
  // Мобильный вид — палитра становится нижней шторкой
  const isMobile = useIsMobile();
  const aiBusy = useAiBusy();
  useEffect(() => { api.notes.caps().then(c => setSemanticCaps(c.semantic)).catch(() => {}); }, []);

  // Немедленный сброс статуса FAB при смене раздела — не ждём опросного тика (иначе старая
  // подсказка/уровень «залипают» до 1.5 с и кажется, что статус не сбрасывается).
  useEffect(() => {
    const onNav = () => { setSuggestion(null); setFabLevel('none'); setRecs([]); };
    window.addEventListener(NAV_CHANGE_EVENT, onNav);
    return () => window.removeEventListener(NAV_CHANGE_EVENT, onNav);
  }, []);

  // Контекст собираем на момент открытия (getNav синхронен вне React)
  const buildCtx = (): AiActionCtx => ({ nav: getNav(), online, flag: getFlag, caps: { semantic: semanticCaps }, chat: getChatContext() });

  // Рекомендации модели (id + уровень) — единый источник для: выделения в палитре,
  // hover-балуна FAB и переупорядочивания. Обновляются проактивным тиком и при открытии
  // палитры/наведении. Уровень минорнее medium в палитре тоже помечаем (пользователь видит,
  // что именно советует AI). Пусто — рекомендаций нет.
  const [recs, setRecs] = useState<ActionRec[]>([]);
  const [fabHover, setFabHover] = useState(false);
  // Свайп-закрытие проактивного балуна на тач-экране (смещение вправо за экран)
  const [dragX, setDragX] = useState(0);
  const swipeStart = useRef<number | null>(null);

  // Список действий пересчитывается на каждый ввод, пока палитра открыта. Рекомендованные
  // (recs) получают уровень (recLevel) для бейджа/подсветки и поднимаются в начало группы.
  const items = useMemo(() => {
    if (!open) return [];
    const ranked = rankedActions(buildCtx(), q).map(r => ({ ...r, recLevel: recs.find(x => x.id === r.action.id)?.level }));
    if (recs.length === 0) return ranked;
    const pos = (id: string) => { const i = recs.findIndex(x => x.id === id); return i < 0 ? Number.MAX_SAFE_INTEGER : i; };
    const ctxItems = ranked.filter(r => r.contextual).sort((a, b) => pos(a.action.id) - pos(b.action.id));
    const rest = ranked.filter(r => !r.contextual);
    return [...ctxItems, ...rest];
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, q, online, semanticCaps, recs]);

  // При открытии палитры — обновить рекомендации под текущее содержание (при флаге+Ollama)
  useEffect(() => {
    if (!open || !gradedFab) return;
    let alive = true;
    void aiOllamaAvailable().then(ok => {
      if (!ok || !alive) return;
      return rankContext(buildCtx()).then(r => {
        if (alive && r?.available && r.ranked.length) setRecs(r.ranked);
      });
    });
    return () => { alive = false; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, gradedFab]);

  useEffect(() => { setIdx(0); }, [q, open]);
  // На мобиле НЕ автофокусим поле — иначе сразу выскакивает клавиатура и перекрывает
  // список действий. Фокус (и клавиатура) — только по явному тапу пользователя.
  useEffect(() => { if (open && !isMobile) setTimeout(() => inputRef.current?.focus(), 40); }, [open, isMobile]);

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
    const DWELL = 4000;          // обзорные экраны — полное «дожитие»
    const ENTITY_DWELL = 900;    // открыта конкретная сущность — быстрый пересчёт (триггер «смена сущности»)
    const sigOf = (n: ReturnType<typeof getNav>) => n ? `${n.screen}|${n.note ?? ''}|${n.task ?? ''}|${n.persona ?? ''}|${n.knowledge ?? ''}|${n.file ?? ''}|${n.project?.id ?? ''}` : '';
    const hasEntity = (n: ReturnType<typeof getNav>) => !!(n && (n.note || n.task || n.file || n.persona || n.knowledge));
    // Форс-пересчёт (завершение хода Claude) — сбрасываем отметку, чтобы tick пересчитал
    const onRecompute = () => { firedForSig = ''; };
    window.addEventListener(AI_RECOMPUTE_EVENT, onRecompute);
    const tick = () => {
      if (open || !isProactiveEnabled()) return;
      const ctx = buildCtx();
      const sig = sigOf(ctx.nav);
      if (sig !== lastSig) { lastSig = sig; stableSince = Date.now(); firedForSig = ''; setSuggestion(null); setFabLevel('none'); setRecs([]); return; }
      const dwell = hasEntity(ctx.nav) ? ENTITY_DWELL : DWELL;
      if (firedForSig === sig || Date.now() - stableSince < dwell) return;
      firedForSig = sig; // помечаем сразу, чтобы не дёргать данные каждый тик
      void computeContextState(ctx).then(({ suggestion: sug, level, recommendations }) => {
        // За время запроса контекст мог смениться — игнорируем устаревший результат
        if (sigOf(getNav()) !== sig || open) return;
        setFabLevel(level); // FAB отражает уровень всегда (даже без всплытия балуна)
        setRecs(recommendations); // полный список — для hover-балуна и палитры
        if (!sug) return;
        // Балун всплывает сам только на сильной рекомендации (shouldSurface = strong)
        const surface = shouldSurface(sug.level);
        if (!surface || !canShow(sug.key)) return;
        markShown();
        setSuggestion(sug);
      });
    };
    const h = setInterval(tick, 1500);
    return () => { clearInterval(h); window.removeEventListener(AI_RECOMPUTE_EVENT, onRecompute); };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, online, gradedFab]);

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
    setDragX(0);
  };
  // Свайп проактивного балуна: тянем только вправо, за порогом — закрыть
  const onSwipeStart = (e: React.TouchEvent) => { swipeStart.current = e.touches[0].clientX; };
  const onSwipeMove = (e: React.TouchEvent) => {
    if (swipeStart.current == null) return;
    setDragX(Math.max(0, e.touches[0].clientX - swipeStart.current));
  };
  const onSwipeEnd = () => {
    if (dragX > 70) dismissSuggestion();
    else setDragX(0);
    swipeStart.current = null;
  };
  const toggleProactive = () => {
    const next = !proactiveOn;
    setProactiveEnabled(next);
    setProactiveOn(next);
    if (!next) setSuggestion(null);
  };

  // Наведение на FAB → балун со списком рекомендаций. Таймер на уход, чтобы курсор
  // успел перейти с кнопки на балун через зазор (иначе балун схлопывается).
  // На тач-экране hover нет: тап эмулирует mouseenter и балун «AI рекомендует» залипал бы
  // без возможности закрыть. Поэтому на мобилке hover-балун не поднимаем вовсе.
  const enterFab = () => { if (isMobile) return; if (hoverTimer.current) { clearTimeout(hoverTimer.current); hoverTimer.current = null; } setFabHover(true); };
  const leaveFab = () => { hoverTimer.current = window.setTimeout(() => setFabHover(false), 140); };
  // Клик по пункту hover-балуна — сразу запустить действие
  const runRec = (id: string) => { setFabHover(false); setSuggestion(null); runActionById(id, buildCtx()); };
  // Рекомендации с их действиями (для hover-балуна), в порядке уровня от модели
  const recActions = recs
    .map(r => ({ action: AI_ACTIONS.find(a => a.id === r.id), level: r.level }))
    .filter((r): r is { action: AiAction; level: SuggestionLevel } => !!r.action);

  const onInputKey = (e: React.KeyboardEvent) => {
    if (e.key === 'ArrowDown') { e.preventDefault(); if (items.length) setIdx(i => (i + 1) % items.length); }
    else if (e.key === 'ArrowUp') { e.preventDefault(); if (items.length) setIdx(i => (i - 1 + items.length) % items.length); }
    else if (e.key === 'Enter') { e.preventDefault(); if (items[idx]) fire(items[idx].action); }
    else if (e.key === 'Escape') { e.preventDefault(); close(); }
  };

  const nav = getNav();
  const ctxLabel = sectionLabelForScreen(nav?.screen);

  // Градация FAB по уровню (только при флаге): none — бледный, medium+ — яркий + пульс,
  // strong — добавляется анимация привлечения. Без флага — старое поведение (пульс = есть подсказка).
  // Кнопка «просыпается» (пульс + акцент + анимация) ТОЛЬКО при сильной рекомендации.
  // medium/minor её не будят — они лишь подсвечиваются в открытой палитре. Пока реальной
  // идеи нет — кнопка приглушена, чтобы не создавать ложного ощущения «есть что предложить».
  const fabDim = fabLevel !== 'strong';
  const fabStrong = fabLevel === 'strong';

  return (
    <>
      {/* Проактивная подсказка (push) — тихий балун у кнопки. При наведении на FAB
          уступает место hover-балуну со списком рекомендаций. */}
      {!open && suggestion && !fabHover && (
        <div
          style={{
            ...balloonStyle,
            transform: dragX ? `translateX(${dragX}px)` : undefined,
            opacity: dragX ? Math.max(0, 1 - dragX / 200) : 1,
            transition: dragX ? 'none' : 'transform .16s, opacity .16s',
            touchAction: 'pan-y',
          }}
          role="status"
          onTouchStart={onSwipeStart}
          onTouchMove={onSwipeMove}
          onTouchEnd={onSwipeEnd}
        >
          <div style={balloonHead}>
            <span style={{ color: C.accent, display: 'flex' }}><SparkleIcon size={15} /></span>
            <b style={{ fontSize: 12.5, color: C.textHeading }}>AI может помочь</b>
            {gradedFab && (
              <span style={{
                fontFamily: FONT.mono, fontSize: 9.5, textTransform: 'uppercase', letterSpacing: 0.4,
                color: suggestion.level === 'strong' ? C.onAccent : C.accent,
                background: suggestion.level === 'strong' ? C.accent : C.accentLight,
                borderRadius: R.sm, padding: '2px 6px',
              }}>{levelLabel(suggestion.level)}</span>
            )}
            <button onClick={dismissSuggestion} aria-label="Скрыть" style={balloonClose}>×</button>
          </div>
          <p style={{ margin: '0 0 11px', fontSize: 13, color: C.textPrimary, lineHeight: 1.4 }}>{suggestion.text}</p>
          <div style={{ display: 'flex', gap: 8 }}>
            <button onClick={acceptSuggestion} style={balloonPrimary}>Предложить</button>
            <button onClick={dismissSuggestion} style={balloonGhost}>Позже</button>
          </div>
        </div>
      )}

      {/* Hover-балун: наведение на FAB → список рекомендованных действий, клик запускает.
          Показываем при наведении, если есть рекомендации и активна градация (флаг). */}
      {!open && fabHover && gradedFab && recActions.length > 0 && !isMobile && (
        <div style={hoverBalloonStyle} role="menu" onMouseEnter={enterFab} onMouseLeave={leaveFab}>
          <div style={{ ...balloonHead, marginBottom: 8 }}>
            <span style={{ color: C.accent, display: 'flex' }}><SparkleIcon size={15} /></span>
            <b style={{ fontSize: 12.5, color: C.textHeading }}>AI рекомендует</b>
          </div>
          <div style={{ display: 'flex', flexDirection: 'column', gap: 2 }}>
            {recActions.map(({ action, level }) => (
              <button key={action.id} role="menuitem" onClick={() => runRec(action.id)} style={hoverItemStyle}
                onMouseEnter={e => { e.currentTarget.style.background = C.accentLight; }}
                onMouseLeave={e => { e.currentTarget.style.background = 'transparent'; }}>
                <span style={{ ...itemIco, width: 26, height: 26, background: level === 'strong' ? C.accent : C.bgSelected, color: level === 'strong' ? C.onAccent : C.accent }}>{action.icon}</span>
                <span style={{ flex: 1, minWidth: 0 }}>
                  <span style={{ ...itemTitle, fontSize: 13 }}>{action.title}</span>
                  <span style={itemHint}>{action.hint}</span>
                </span>
                <span style={recBadge(level)}>{levelLabel(level)}</span>
              </button>
            ))}
          </div>
        </div>
      )}

      {/* Плавающая кнопка */}
      {!open && (
        <button
          onClick={() => { setQ(''); setOpen(true); }}
          aria-label="AI-действия (Ctrl/⌘ + K)"
          title="AI-действия · ⌘K"
          className={fabStrong && !aiBusy ? 'cc-fab-hop' : undefined}
          style={{
            ...fabStyle,
            background: 'none', padding: 0, overflow: 'visible',
            ...(isMobile ? { right: 16, width: FAB_MOBILE, height: FAB_MOBILE } : {}),
          }}
          onMouseEnter={e => { e.currentTarget.style.transform = 'translateY(-2px)'; enterFab(); }}
          onMouseLeave={e => { e.currentTarget.style.transform = 'translateY(0)'; leaveFab(); }}
        >
          {/* Логотип «AI Home» (домик с трубой) на весь круг. Покой — серый; идея — подскок
              (cc-fab-hop на кнопке); работа — «пыхтит» (cc-fab-huff) + дым из трубы. */}
          <img
            src="/pwa-64x64.png" alt="" aria-hidden
            className={aiBusy ? 'cc-fab-huff' : undefined}
            style={{
              position: 'absolute', inset: 0, width: '100%', height: '100%',
              objectFit: 'cover', borderRadius: '50%', display: 'block',
              ...(fabDim && !aiBusy ? { opacity: 0.55, filter: 'grayscale(0.9)' } : {}),
            }}
          />
          {aiBusy && <span className="cc-smoke cc-fab-smoke" aria-hidden><i /><i /><i /><i /></span>}
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
                      style={{
                        ...itemStyle,
                        background: i === idx ? C.accentLight : (it.recLevel ? C.bgSelected : 'transparent'),
                        // Рекомендованные — акцентная левая полоса, чтобы явно выделялись в общем списке
                        boxShadow: it.recLevel ? `inset 3px 0 0 ${C.accent}` : undefined,
                      }}
                    >
                      <span style={{ ...itemIco, background: it.recLevel ? C.accent : (i === idx ? C.bgWhite : C.bgSelected), color: it.recLevel ? C.onAccent : C.accent }}>{it.action.icon}</span>
                      <span style={{ flex: 1, minWidth: 0 }}>
                        <span style={itemTitle}>{it.action.title}</span>
                        <span style={itemHint}>{it.action.hint}</span>
                      </span>
                      {it.recLevel
                        ? <span style={recBadge(it.recLevel)}>{levelLabel(it.recLevel)}</span>
                        : <span style={itemSec}>{it.action.sectionLabel}</span>}
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
    case 'personas': return 'Персоны';
    case 'knowledge': return 'Знания';
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

// На мобиле кнопка мельче в полтора раза (54 → 36): на телефоне она ближе к контенту
// и в полный размер заметно его перекрывала
const FAB_MOBILE = 36;

const fabStyle: React.CSSProperties = {
  // --cc-fab-bottom задаёт страница снизу (в чате — высота композера + зазор), чтобы
  // FAB вставал НАД композером и не сталкивался с кнопкой «вниз». Дефолт — угол 20px.
  position: 'fixed', right: 20, bottom: 'calc(env(safe-area-inset-bottom, 0px) + var(--cc-fab-bottom, 20px))',
  width: 54, height: 54, borderRadius: '50%', border: 'none', cursor: 'pointer',
  background: C.accent, color: C.onAccent, boxShadow: SHADOW.fab,
  // bottom едет плавно за счёт анимируемой @property --cc-fab-bottom (см. index.css)
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

const balloonStyle: React.CSSProperties = {
  position: 'fixed', right: 20, bottom: 'calc(env(safe-area-inset-bottom, 0px) + var(--cc-fab-bottom, 20px) + 66px)',
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

// Hover-балун со списком рекомендаций (шире проактивного, с прокруткой при длинном списке)
const hoverBalloonStyle: React.CSSProperties = {
  position: 'fixed', right: 20, bottom: 'calc(env(safe-area-inset-bottom, 0px) + var(--cc-fab-bottom, 20px) + 66px)',
  width: 300, maxHeight: '60vh', overflowY: 'auto', background: C.bgCard, border: `1px solid ${C.accentMuted}`,
  borderRadius: R.xl, boxShadow: SHADOW.modal, padding: '12px 12px 10px', zIndex: Z.modal - 1, fontFamily: FONT.sans,
};
const hoverItemStyle: React.CSSProperties = {
  display: 'flex', alignItems: 'center', gap: 10, width: '100%', textAlign: 'left',
  border: 'none', background: 'transparent', cursor: 'pointer', borderRadius: R.md, padding: '7px 8px',
};
// Бейдж уровня рекомендации (палитра + hover-балун)
function recBadge(level: SuggestionLevel): React.CSSProperties {
  const strong = level === 'strong';
  return {
    flex: 'none', fontFamily: FONT.mono, fontSize: 9.5, textTransform: 'uppercase', letterSpacing: 0.4,
    color: strong ? C.onAccent : C.accent, background: strong ? C.accent : C.accentLight,
    borderRadius: R.sm, padding: '2px 6px',
  };
}
