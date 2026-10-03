// Пасхалка правой рельсы — утка-кнопка, которая ничего не делает. Идея подсмотрена у
// Viaduct (NavDuck): там утка в шапке крякает и изредка уплывает за край экрана.
// Код и рисунок свои: Viaduct под BUSL, его исходники в репу не попадают.
//
// Механика: клик — пузырь с репликой под случайным наклоном. Частые клики злят утку:
// RAGE_CLICKS кликов подряд (каждый не позже CLICK_WINDOW_MS после предыдущего) — она
// ругается, спрыгивает с рельсы вниз и уплывает по низу окна за ЛЕВЫЙ край (рельса
// прижата к правому, и уход вправо за ~76px не успевал читаться), потом выглядывает
// оттуда, ворчит и плывёт обратно на место.
// Пока утки нет на месте, кнопка не реагирует. При prefers-reduced-motion утка
// только ругается, никуда не плавая.
//
// Финал: FINALE_ESCAPES побегов за FINALE_WINDOW_MS — утка не выглядывает, а зовёт
// подмогу: ролик (frontend/public/duck/finale.mp4) на весь экран поверх интерфейса.
// Видит его только тот, кто кликал, — никакой рассылки по чужим экранам. Ролик
// доиграл (или его закрыли раньше) — слой уходит, утка обиженно приплывает на место.
import { useCallback, useEffect, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { X } from 'lucide-react';
import { C, FONT, FS, R, SHADOW, SP, Z } from '../../lib/design';
import { Button, IconButton, RailIconButton } from '../../components/ui';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { quack } from './quack';

const QUIPS = [
  'кря',
  'кря-кря, это фейковая кнопка',
  'кря, а тесты прогнал?',
  'кря, сборка зелёная?',
  'кря, в пятницу не выкатываем',
  'кря, попей водички',
  'кря, сделай перерыв',
  'кря, закоммить уже',
  'кря, переименуй что-нибудь',
  'кря, виноват кэш',
] as const;
const RAGE_LINE = 'Да хватит тыкать! Ухожу.';
const PEEK_LINE = 'Ну шо, натыкался?';
const RETURN_LINE = 'Ладно, вернулась. Но я всё помню.';
const FINALE_RETURN_LINE = 'Я обиделась. Кря.';

const RAGE_CLICKS = 4;
const CLICK_WINDOW_MS = 3000;
const FINALE_ESCAPES = 3;
const FINALE_WINDOW_MS = 5 * 60_000;
const FINALE_VIDEO = '/duck/finale.mp4';
const BUBBLE_MS = 2200;

const DUCK = 17;            // как иконки панелей в рельсе (PanelRail: 17px)
const OFFSCREEN = 40;       // насколько за край окна уплывает
const PEEK_VISIBLE = 12;    // сколько утки видно, когда она выглядывает
const FLOOR_GAP = 28;       // «вода» — на столько выше нижней кромки окна она плывёт
const PADDLE = 'cubic-bezier(0.45, 0.05, 0.55, 0.95)';

type Phase = 'rage' | 'drop' | 'out' | 'gone' | 'peek' | 'retreat' | 'back' | 'rise';
// Побег по шагам: сколько длится движение и сколько утка стоит после него
const STEPS: readonly { phase: Phase; move: number; hold: number }[] = [
  { phase: 'rage', move: 0, hold: 1300 },
  { phase: 'drop', move: 600, hold: 100 },     // спрыгнула с рельсы вниз, к «воде»
  { phase: 'out', move: 2500, hold: 0 },       // плывёт по низу окна за левый край
  { phase: 'gone', move: 0, hold: 1400 },
  { phase: 'peek', move: 700, hold: 2800 },    // выглядывает из-за левого края
  { phase: 'retreat', move: 800, hold: 900 },
  { phase: 'back', move: 2500, hold: 100 },    // плывёт обратно под рельсу
  { phase: 'rise', move: 600, hold: 0 },       // запрыгивает на место
];

interface Quip { id: number; text: string; tilt: number }
interface Anchor { top: number; left: number; width: number; height: number }

const rollTilt = () => Math.round((Math.random() * 10 - 5) * 10) / 10;
const reducedMotion = () => window.matchMedia?.('(prefers-reduced-motion: reduce)').matches ?? false;

// Резиновая утка в профиль клювом вправо. Силуэт — по мотивам утки Viaduct (большая
// круглая голова, плоский клюв, «плывущее» тело с плоским дном, вздёрнутый хвостик),
// но пути свои: логотип Quiet Grid Labs в репу не берём. Контур в сетке lucide
// (24×24, круглые концы), чтобы в рельсе не отличаться от соседей. Цвет — currentColor
function DuckIcon() {
  return (
    <svg width={DUCK} height={DUCK} viewBox="0 0 24 24" fill="none" stroke="currentColor"
      strokeWidth={ICON_STROKE} strokeLinecap="round" strokeLinejoin="round" aria-hidden style={{ display: 'block' }}>
      <circle cx="14.5" cy="8" r="4.5" />
      <path d="M18.6 7.6c1.9-.3 3.9 0 3.9 1s-2 1.6-3.9 1.6" />
      <path d="M17.3 11.6c1.7.9 2.7 2 2.7 3.4 0 2.6-2.9 4.5-8.6 4.5-4.6 0-7.5-1.6-8.3-4.5L1.8 10.8c1.2.7 2.5 1.1 3.7 1.3 1.6-1.2 3.4-1.6 5.2-1.4" />
      <path d="M7.2 15c1.6-1.4 4.3-1.4 5.6.2-1.3 1.7-4.2 1.9-5.6-.2Z" />
      <circle cx="15.8" cy="6.8" r="0.7" fill="currentColor" />
    </svg>
  );
}

// Пузырь реплики. Хвостик смотрит на утку. side — с какой стороны от утки пузырь:
// у рельсы (правый край окна) — слева, а когда утка выглядывает из-за ЛЕВОГО края —
// справа, иначе пузырь уехал бы за кромку
function Bubble({ quip, side }: { quip: Quip; side: 'left' | 'right' }) {
  const toRight = side === 'right';
  const edge = `1.5px solid ${C.textSecondary}`;
  return (
    <span
      key={quip.id}
      className="cc-duck-bubble"
      style={{
        '--cc-duck-tilt': `${quip.tilt}deg`,
        // Пузырь «растёт» из хвостика: точка опоры — со стороны утки
        transformOrigin: toRight ? '-6px 50%' : 'calc(100% + 6px) 50%',
        position: 'relative', display: 'inline-flex', alignItems: 'center',
        padding: '7px 10px', borderRadius: R.xl,
        border: edge, background: C.bgCard, color: C.textPrimary,
        boxShadow: SHADOW.dropdown, fontFamily: FONT.sans, fontSize: FS.sm, fontWeight: 700,
        whiteSpace: 'nowrap',
      } as React.CSSProperties}
    >
      {quip.text}
      <span aria-hidden style={{
        position: 'absolute', top: 'calc(50% - 5px)', width: 9, height: 9, background: C.bgCard,
        ...(toRight
          ? { left: -6, borderBottom: edge, borderLeft: edge }
          : { right: -6, borderTop: edge, borderRight: edge }),
        transform: 'rotate(45deg)',
      }} />
    </span>
  );
}

export function RailDuck() {
  const [quip, setQuip] = useState<Quip | null>(null);
  const [phase, setPhase] = useState<Phase | null>(null);
  const [anchor, setAnchor] = useState<Anchor | null>(null);
  const boxRef = useRef<HTMLSpanElement>(null);
  const bubbleTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const steps = useRef<ReturnType<typeof setTimeout>[]>([]);
  const clicks = useRef<{ n: number; at: number }>({ n: 0, at: 0 });
  const seq = useRef(0);
  // Побеги в окне FINALE_WINDOW_MS: на FINALE_ESCAPES-м — финал с роликом
  const escapes = useRef<{ n: number; at: number }>({ n: 0, at: 0 });
  const [finaleOpen, setFinaleOpen] = useState(false);

  useEffect(() => () => {
    if (bubbleTimer.current) clearTimeout(bubbleTimer.current);
    steps.current.forEach(clearTimeout);
  }, []);

  const measure = (): Anchor | null => {
    const r = boxRef.current?.getBoundingClientRect();
    return r ? { top: r.top, left: r.left, width: r.width, height: r.height } : null;
  };

  const say = useCallback((text: string, ms = BUBBLE_MS) => {
    seq.current += 1;
    setQuip({ id: seq.current, text, tilt: rollTilt() });
    if (bubbleTimer.current) clearTimeout(bubbleTimer.current);
    bubbleTimer.current = setTimeout(() => setQuip(null), ms);
  }, []);

  // Прогнать шаги побега по порядку; done — когда последний доиграл
  const runSteps = useCallback((list: readonly typeof STEPS[number][], done: () => void) => {
    let t = 0;
    list.forEach(step => {
      steps.current.push(setTimeout(() => {
        setPhase(step.phase);
        if (step.phase === 'rage') { say(RAGE_LINE, step.hold); quack('angry'); }
        if (step.phase === 'drop') setQuip(null);
        if (step.phase === 'peek') { say(PEEK_LINE, step.move + step.hold); quack(); }
      }, t));
      t += step.move + step.hold;
    });
    steps.current.push(setTimeout(() => { steps.current = []; done(); }, t));
  }, [say]);

  // Шаги обратного пути (от «плывёт обратно» до места) — общие для побега и финала
  const homeward = STEPS.slice(STEPS.findIndex(s => s.phase === 'back'));

  const escape = useCallback((at: Anchor, finale: boolean) => {
    setAnchor(at);
    // В финале утка не выглядывает: уплыла — и зовёт подмогу. Возврат — по закрытию ролика
    if (finale) {
      runSteps(STEPS.slice(0, STEPS.findIndex(s => s.phase === 'gone') + 1), () => setFinaleOpen(true));
      return;
    }
    // Доплыла домой — копия уходит, на место встаёт настоящая кнопка
    runSteps(STEPS, () => { setPhase(null); say(RETURN_LINE); quack(); });
  }, [runSteps, say]);

  // Ролик закрыли — утка приплывает обратно, счётчик побегов начинается заново
  const closeFinale = () => {
    setFinaleOpen(false);
    escapes.current = { n: 0, at: 0 };
    runSteps(homeward, () => { setPhase(null); say(FINALE_RETURN_LINE); quack('sad'); });
  };

  const onClick = () => {
    if (phase) return;
    const now = Date.now();
    const c = clicks.current;
    c.n = now - c.at <= CLICK_WINDOW_MS ? c.n + 1 : 1;
    c.at = now;
    const at = measure();
    setAnchor(at);
    if (c.n < RAGE_CLICKS) {
      // Подряд одна и та же реплика не выпадает
      const pool = QUIPS.filter(q => q !== quip?.text);
      say(pool[Math.floor(Math.random() * pool.length)]);
      quack();
      return;
    }
    c.n = 0;
    if (!at || reducedMotion()) { say(RAGE_LINE); quack('angry'); return; }
    const e = escapes.current;
    e.n = now - e.at <= FINALE_WINDOW_MS ? e.n + 1 : 1;
    e.at = now;
    escape(at, e.n >= FINALE_ESCAPES);
  };

  // Где копия утки сейчас (смещение от её места в рельсе). rage — подпрыгнула от злости,
  // floorY — уровень «воды» у нижней кромки окна, offX — за левым краем окна
  const floorY = anchor ? window.innerHeight - anchor.top - anchor.height - FLOOR_GAP : 0;
  const offX = anchor ? -(anchor.left + anchor.width + OFFSCREEN) : 0;
  const peekX = anchor ? -(anchor.left + anchor.width) + PEEK_VISIBLE : 0;
  const motion: Record<Phase, { x: number; y: number; ms: number; ease: string }> = {
    rage: { x: 0, y: -6, ms: 160, ease: 'ease-out' },
    drop: { x: 0, y: floorY, ms: 600, ease: 'cubic-bezier(0.5, 0, 0.75, 0)' },
    out: { x: offX, y: floorY, ms: 2500, ease: PADDLE },
    gone: { x: offX, y: floorY, ms: 0, ease: 'linear' },
    peek: { x: peekX, y: floorY, ms: 700, ease: 'cubic-bezier(0.2, 0.9, 0.3, 1.2)' },
    retreat: { x: offX, y: floorY, ms: 800, ease: 'cubic-bezier(0.45, 0.05, 0.75, 0.5)' },
    back: { x: 0, y: floorY, ms: 2500, ease: PADDLE },
    rise: { x: 0, y: 0, ms: 600, ease: 'cubic-bezier(0.3, 0.7, 0.4, 1.35)' },
  };
  const swimming = phase === 'out' || phase === 'retreat' || phase === 'back';
  // Рисунок смотрит клювом вправо. Влево — пока уходит; выглядывая из-за левого края
  // и плывя домой, смотрит обратно в окно
  const facingLeft = phase === 'drop' || phase === 'out' || phase === 'gone' || phase === 'retreat';
  // У левого края окна пузырь встаёт справа от утки
  const bubbleSide = phase === 'peek' ? 'right' : 'left';

  // Пузырь и плывущая копия — порталом в body: капсулу рельсы они бы не пересекли
  const overlay = anchor && (phase || quip) && createPortal(
    <div style={{
      position: 'fixed', top: anchor.top, left: anchor.left, width: anchor.width, height: anchor.height,
      zIndex: Z.dropdown, pointerEvents: 'none',
      display: 'flex', alignItems: 'center', justifyContent: 'center',
      ...(phase ? {
        transform: `translate(${motion[phase].x}px, ${motion[phase].y}px)`,
        transition: motion[phase].ms ? `transform ${motion[phase].ms}ms ${motion[phase].ease}` : 'none',
      } : {}),
    }}>
      {phase && (
        <span style={{ transform: `scaleX(${facingLeft ? -1 : 1})`, transition: 'transform 240ms ease' }}>
          {/* Цвет копии — как у иконки в рельсе: подмены по дороге не видно */}
          <span className={swimming ? 'cc-duck-swim' : phase === 'rage' ? 'cc-duck-rage' : undefined} style={{ display: 'block', color: C.textSecondary }}>
            <DuckIcon />
          </span>
        </span>
      )}
      {quip && (
        <span style={{
          position: 'absolute', top: '50%', transform: 'translateY(-50%)',
          ...(bubbleSide === 'right' ? { left: 'calc(100% + 10px)' } : { right: 'calc(100% + 10px)' }),
        }}>
          <Bubble quip={quip} side={bubbleSide} />
        </span>
      )}
    </div>,
    document.body,
  );

  return (
    // Своей капсулы нет: кнопка встаёт хвостом в рельсу панелей (PanelRail.tail)
    <>
      <RailIconButton side="right" label="Утка" hoverSuppressed={!!quip || !!phase} onClick={onClick}>
        <span ref={boxRef} className="cc-duck-hop" style={{ display: 'block', visibility: phase ? 'hidden' : 'visible' }}>
          <DuckIcon />
        </span>
      </RailIconButton>
      {overlay}
      {finaleOpen && <DuckFinale onClose={closeFinale} />}
    </>
  );
}

// Пауза на последнем кадре (утка молча смотрит с осуждением), прежде чем слой уйдёт
const FINALE_LINGER_MS = 800;
// Ролик не загрузился — текст-заглушка висит столько, сколько шёл бы сам ролик
const FINALE_FALLBACK_MS = 4000;

// Финал: утка вызвала подмогу. Ролик на весь экран поверх интерфейса — как кино, без
// карточки и контролов плеера: поля вокруг кадра тёмные, кадр вписан целиком.
// Уходит сам, когда ролик доиграл; раньше — крестиком, Esc или кнопкой извинения.
// Отдельным компонентом — его же показывает витрина (#/ui-kit), не прогоняя три побега
export function DuckFinale({ onClose }: { onClose: () => void }) {
  // Файла ролика нет или браузер его не съел — вместо кадра текст
  const [videoFailed, setVideoFailed] = useState(false);
  const closeTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  // Свежий onClose — в ref: таймер, взведённый раньше, не должен звать устаревший
  const onCloseRef = useRef(onClose);
  useEffect(() => { onCloseRef.current = onClose; }, [onClose]);

  const closeLater = useCallback((ms: number) => {
    if (closeTimer.current) clearTimeout(closeTimer.current);
    closeTimer.current = setTimeout(() => onCloseRef.current(), ms);
  }, []);

  useEffect(() => {
    // preventDefault — Escape обработан, нижележащие слои его не получат (как в Modal)
    const onKey = (e: KeyboardEvent) => {
      if (e.key !== 'Escape' || e.defaultPrevented) return;
      e.preventDefault();
      onCloseRef.current();
    };
    document.addEventListener('keydown', onKey);
    return () => {
      document.removeEventListener('keydown', onKey);
      if (closeTimer.current) clearTimeout(closeTimer.current);
    };
  }, []);

  useEffect(() => { if (videoFailed) closeLater(FINALE_FALLBACK_MS); }, [videoFailed, closeLater]);

  return createPortal(
    <div className="cc-overlay" style={{
      // Выше тостов уведомлений (у них Z.modal + 10): кино не перекрывают плашки
      position: 'fixed', inset: 0, zIndex: Z.modal + 20, background: C.mediaBackdrop,
      display: 'flex', alignItems: 'center', justifyContent: 'center',
    }}>
      {videoFailed ? (
        <div style={{ color: C.onDark, fontFamily: FONT.sans, fontSize: FS.xl, textAlign: 'center', padding: SP.xl }}>
          Подмога застряла в пробке. Но утка всё запомнила.
        </div>
      ) : (
        <video
          src={FINALE_VIDEO}
          autoPlay
          playsInline
          onEnded={() => closeLater(FINALE_LINGER_MS)}
          onError={() => setVideoFailed(true)}
          style={{ display: 'block', width: '100%', height: '100%', objectFit: 'contain' }}
        />
      )}

      <IconButton
        ariaLabel="Закрыть"
        onClick={onClose}
        style={{
          position: 'absolute', top: SP.lg, right: SP.lg,
          background: C.mediaScrim, color: C.onDark, borderRadius: R.full,
        }}
      >
        <X size={ICON_SIZE.md} strokeWidth={ICON_STROKE} />
      </IconButton>

      <div style={{ position: 'absolute', bottom: SP.xl, left: 0, right: 0, display: 'flex', justifyContent: 'center' }}>
        <Button onClick={onClose} style={{ boxShadow: SHADOW.modal }}>Прости, больше не буду</Button>
      </div>
    </div>,
    document.body,
  );
}
