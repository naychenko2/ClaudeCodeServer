import { useCallback, useEffect, useRef, useState } from 'react';
import { C, FONT, FS, R, SP } from '../../lib/design';

// Змейка на экране ошибки — из той же оперы, что динозаврик Chrome: пока
// человек ждёт (или собирается с мыслями перед перезагрузкой), ему есть чем
// занять руки. Открывается по ссылке и по умолчанию скрыта: кнопки выхода из
// сбоя важнее, и игра не должна с ними спорить.
//
// Без canvas: змея и еда — это десяток абсолютных div'ов поверх поля, поэтому
// цвета берутся токенами дизайн-системы, а не резолвятся в конкретные значения,
// как пришлось бы для контекста рисования.

// Метка для ссылки, открывающей игру: змейка одной линией в стилистике дудлов.
// Ссылка со значком заметна ровно настолько, чтобы её нашли, но не спорит
// с кнопками выхода из сбоя — поэтому значок мелкий и той же тушью, что текст.
export function SnakeMark() {
  return (
    <svg width="22" height="12" viewBox="0 0 22 12" fill="none" stroke="currentColor"
         strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round" aria-hidden style={{ flexShrink: 0 }}>
      <path d="M1.5 9.5c2 0 2-4 4.5-4s2.5 4 5 4 2.5-4 5-4" />
      <circle cx="18.2" cy="5.5" r="2.1" />
      <path d="M20.4 5.5h1.2" />
    </svg>
  );
}

const COLS = 21;
const ROWS = 13;
const CELL = 14;
const TICK_MS = 130;
const BEST_KEY = 'cc_snake_best';

type Point = { x: number; y: number };
type Phase = 'idle' | 'running' | 'over';
type Death = 'wall' | 'self';

const START: Point[] = [{ x: 7, y: 6 }, { x: 6, y: 6 }, { x: 5, y: 6 }];
const same = (a: Point, b: Point) => a.x === b.x && a.y === b.y;

function randomFood(snake: Point[]): Point {
  // Свободные клетки перебором: поле маленькое, а «кидать наугад, пока не
  // попадём в пустую» на длинной змее вырождается в долгий цикл
  const free: Point[] = [];
  for (let y = 0; y < ROWS; y++) {
    for (let x = 0; x < COLS; x++) {
      if (!snake.some(s => s.x === x && s.y === y)) free.push({ x, y });
    }
  }
  return free[Math.floor(Math.random() * free.length)] ?? { x: 0, y: 0 };
}

export function SnakeGame({ onEat, onDie }: { onEat?: () => void; onDie?: () => void }) {
  const [snake, setSnake] = useState<Point[]>(START);
  const [food, setFood] = useState<Point>(() => randomFood(START));
  const [phase, setPhase] = useState<Phase>('idle');
  const [death, setDeath] = useState<Death>('wall');
  const [score, setScore] = useState(0);
  const [best, setBest] = useState(() => Number(localStorage.getItem(BEST_KEY)) || 0);

  // Направление — в ref: за один тик приходит несколько нажатий, и state
  // не успел бы примениться между ними (змея разворачивалась бы в себя).
  const dir = useRef<Point>({ x: 1, y: 0 });
  const pendingDir = useRef<Point>({ x: 1, y: 0 });
  // Позиции — тоже в ref: ход считается от них, а не внутри updater'а setState.
  // StrictMode прогоняет updater дважды, и побочные эффекты внутри него (новая
  // еда, счёт, реакция дудла) в dev срабатывали бы по два раза за одну ягоду.
  const snakeRef = useRef<Point[]>(START);
  const foodRef = useRef<Point>(food);

  const reset = useCallback(() => {
    dir.current = { x: 1, y: 0 };
    pendingDir.current = { x: 1, y: 0 };
    const f = randomFood(START);
    snakeRef.current = START;
    foodRef.current = f;
    setSnake(START);
    setFood(f);
    setScore(0);
    setPhase('running');
  }, []);

  const turn = useCallback((d: Point) => {
    // Разворот на 180° запрещён — иначе мгновенная смерть о собственную шею
    if (d.x === -dir.current.x && d.y === -dir.current.y) return;
    pendingDir.current = d;
  }, []);

  // Клавиатура. preventDefault обязателен: стрелки и пробел иначе прокручивают
  // страницу, и поле уезжает из вида прямо во время игры.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      const k = e.key;
      const map: Record<string, Point> = {
        ArrowUp: { x: 0, y: -1 }, ArrowDown: { x: 0, y: 1 },
        ArrowLeft: { x: -1, y: 0 }, ArrowRight: { x: 1, y: 0 },
        w: { x: 0, y: -1 }, s: { x: 0, y: 1 }, a: { x: -1, y: 0 }, d: { x: 1, y: 0 },
        ц: { x: 0, y: -1 }, ы: { x: 0, y: 1 }, ф: { x: -1, y: 0 }, в: { x: 1, y: 0 },
      };
      const d = map[k] ?? map[k.toLowerCase()];
      if (d) { e.preventDefault(); if (phase === 'running') turn(d); else reset(); return; }
      if (k === ' ' || k === 'Enter') { e.preventDefault(); if (phase !== 'running') reset(); }
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [phase, turn, reset]);

  // Ход игры
  useEffect(() => {
    if (phase !== 'running') return;
    const id = window.setInterval(() => {
      dir.current = pendingDir.current;
      const prev = snakeRef.current;
      const head = { x: prev[0].x + dir.current.x, y: prev[0].y + dir.current.y };
      const hitWall = head.x < 0 || head.y < 0 || head.x >= COLS || head.y >= ROWS;
      // Хвост в этот ход уходит из-под головы, поэтому последний сегмент
      // столкновением не считается — иначе плотный клубок убивал бы зря
      const hitSelf = prev.slice(0, -1).some(s => same(s, head));
      if (hitWall || hitSelf) {
        setDeath(hitWall ? 'wall' : 'self');
        setPhase('over');
        onDie?.();
        return;
      }

      const ate = same(head, foodRef.current);
      const next = [head, ...(ate ? prev : prev.slice(0, -1))];
      snakeRef.current = next;
      setSnake(next);
      if (ate) {
        const f = randomFood(next);
        foodRef.current = f;
        setFood(f);
        setScore(s => s + 1);
        onEat?.();
      }
    }, TICK_MS);
    return () => window.clearInterval(id);
    // Еда в зависимостях больше не нужна — ход читает её из ref, и интервал
    // не пересоздаётся на каждой ягоде (иначе сбивался ритм хода)
  }, [phase, onEat, onDie]);

  // Рекорд — отдельным эффектом, а не по ходу тика: запись в localStorage это
  // побочный эффект, и внутри updater'а он бы дублировался под StrictMode
  useEffect(() => {
    if (score > best) {
      setBest(score);
      localStorage.setItem(BEST_KEY, String(score));
    }
  }, [score, best]);

  // Свайпы — чтобы игра работала и с телефона
  const touch = useRef<Point | null>(null);
  const onTouchStart = (e: React.TouchEvent) => {
    const t = e.touches[0];
    touch.current = { x: t.clientX, y: t.clientY };
  };
  const onTouchEnd = (e: React.TouchEvent) => {
    const start = touch.current;
    if (!start) return;
    const t = e.changedTouches[0];
    const dx = t.clientX - start.x;
    const dy = t.clientY - start.y;
    if (Math.abs(dx) < 24 && Math.abs(dy) < 24) { if (phase !== 'running') reset(); return; }
    if (phase !== 'running') { reset(); return; }
    turn(Math.abs(dx) > Math.abs(dy) ? { x: Math.sign(dx), y: 0 } : { x: 0, y: Math.sign(dy) });
  };

  const cellStyle = (p: Point, head: boolean): React.CSSProperties => ({
    position: 'absolute',
    left: p.x * CELL + 1,
    top: p.y * CELL + 1,
    width: CELL - 2,
    height: CELL - 2,
    borderRadius: head ? 4 : 3,
    background: C.accent,
    opacity: head ? 1 : 0.72,
  });

  return (
    <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', gap: SP.sm }}>
      <div style={{
        display: 'flex', justifyContent: 'space-between', width: COLS * CELL,
        fontSize: FS.xs, color: C.textMuted, fontFamily: FONT.mono,
      }}>
        <span>очки: {score}</span>
        <span>рекорд: {best}</span>
      </div>

      <div
        onTouchStart={onTouchStart}
        onTouchEnd={onTouchEnd}
        onClick={() => { if (phase !== 'running') reset(); }}
        style={{
          position: 'relative', width: COLS * CELL, height: ROWS * CELL,
          background: C.bgInset, borderRadius: R.xl, border: `1px solid ${C.borderLight}`,
          overflow: 'hidden', cursor: phase === 'running' ? 'default' : 'pointer',
          touchAction: 'none', flexShrink: 0,
        }}
      >
        {snake.map((s, i) => <div key={i} style={cellStyle(s, i === 0)} />)}
        <div style={{
          position: 'absolute', left: food.x * CELL + 3, top: food.y * CELL + 3,
          width: CELL - 6, height: CELL - 6, borderRadius: R.full, background: C.warning,
        }} />

        {phase !== 'running' && (
          <div style={{
            position: 'absolute', inset: 0, display: 'flex', flexDirection: 'column',
            alignItems: 'center', justifyContent: 'center', gap: 2,
            background: C.overlay, color: C.onDark, fontSize: FS.sm, textAlign: 'center', padding: SP.sm,
          }}>
            {phase === 'over' && (
              <div style={{ fontWeight: 600 }}>
                {death === 'wall' ? 'Приехали, стена.' : 'Съел сам себя. Бывает.'}
              </div>
            )}
            <div>{phase === 'over' ? 'Ещё разок?' : 'Стрелки или свайп — поехали'}</div>
          </div>
        )}
      </div>
    </div>
  );
}
