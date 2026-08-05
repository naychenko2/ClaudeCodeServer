import { useEffect, useRef, useState } from 'react';

// Герой экрана ошибки: монитор-персонаж в стилистике фоновых дудлов (тонкая
// линия «от руки», currentColor — темизация бесплатная). Живой: зрачки следят
// за курсором и персонаж моргает.
//
// Почему не статичная картинка: экран ошибки застаёт человека в худший момент,
// и «мёртвая» иллюстрация (крестики вместо глаз) добивает. Персонаж, который
// смотрит на курсор, читается как «я тут, всё под контролем» — та же информация,
// другое настроение.

const EYE_L = { x: 52, y: 46 };
const EYE_R = { x: 80, y: 46 };
const PUPIL_RANGE = 2.6;     // насколько зрачок отходит от центра глаза

type Mood = 'calm' | 'happy' | 'sad';

// cheer/grief — счётчики поводов порадоваться (съеденная ягода) и огорчиться
// (змейка погибла). Именно счётчики, а не флаги: два очка подряд должны дать
// две реакции, а булево значение во второй раз не изменилось бы и эффект
// не сработал.
export function LiveDoodle({ cheer = 0, grief = 0 }: { cheer?: number; grief?: number }) {
  const ref = useRef<SVGSVGElement>(null);
  const [pupil, setPupil] = useState({ dx: 0, dy: 0 });
  const [blink, setBlink] = useState(false);
  const [mood, setMood] = useState<Mood>('calm');

  // Короткая вспышка радости на каждое очко
  useEffect(() => {
    if (cheer === 0) return;
    setMood('happy');
    const t = window.setTimeout(() => setMood('calm'), 700);
    return () => window.clearTimeout(t);
  }, [cheer]);

  // Сочувствие проигрышу — держится дольше радости: игрок в этот момент
  // смотрит на экран проигрыша, и мгновенная реакция прошла бы мимо него
  useEffect(() => {
    if (grief === 0) return;
    setMood('sad');
    const t = window.setTimeout(() => setMood('calm'), 1400);
    return () => window.clearTimeout(t);
  }, [grief]);

  const happy = mood === 'happy';
  const sad = mood === 'sad';
  const sadGaze = sad ? 1.8 : 0;    // опущенный взгляд на время печали

  // Зрачки за курсором. Считаем от центра лица, а не от каждого глаза:
  // так оба зрачка смотрят в одну точку, а не косят в разные стороны.
  useEffect(() => {
    const onMove = (e: MouseEvent) => {
      const box = ref.current?.getBoundingClientRect();
      if (!box) return;
      const cx = box.left + box.width / 2;
      const cy = box.top + box.height * 0.45;
      const dx = e.clientX - cx;
      const dy = e.clientY - cy;
      const len = Math.hypot(dx, dy) || 1;
      // Нормируем направление и гасим у самого лица, чтобы зрачки не дёргались
      const k = Math.min(len / 140, 1) * PUPIL_RANGE;
      setPupil({ dx: (dx / len) * k, dy: (dy / len) * k });
    };
    window.addEventListener('mousemove', onMove);
    return () => window.removeEventListener('mousemove', onMove);
  }, []);

  // Моргание вразнобой: ровный интервал читался бы как метроном
  useEffect(() => {
    let timer = 0;
    const schedule = () => {
      timer = window.setTimeout(() => {
        setBlink(true);
        window.setTimeout(() => setBlink(false), 120);
        schedule();
      }, 2600 + Math.random() * 3400);
    };
    schedule();
    return () => window.clearTimeout(timer);
  }, []);

  const eyeStyle = {
    transformBox: 'fill-box',
    transformOrigin: 'center',
    transform: `scaleY(${blink ? 0.08 : 1})`,
    transition: 'transform 90ms ease',
  } as const;

  return (
    // Подскок радости и оседание от огорчения — CSS-трансформом на всём знаке:
    // у внутренней группы свой SVG-атрибут transform (наклон), и CSS перебил бы его
    <svg ref={ref} width="132" height="104" viewBox="0 0 132 104" fill="none" stroke="currentColor"
         strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden
         style={{ transform: `translateY(${happy ? -5 : sad ? 3 : 0}px)`, transition: 'transform 180ms ease' }}>
      <g transform="rotate(-2 66 50)">
        {/* Корпус со «шторкой» заголовка — тот же терминал, что и на фоне-холсте */}
        <rect x="20" y="16" width="92" height="58" rx="9" />
        <path d="M20 30h92" />
        <path d="M31 23h.01M39 23h.01M47 23h.01" />

        {/* Брови. Спокойно — лёгкое сведение к центру (сосредоточенность);
            радость — подняты; грусть — «домиком»: внутренние концы ВВЕРХ,
            внешние вниз. Опустить внутренние концы нельзя ни при какой печали —
            это ровно та геометрия, которой рисуют злость */}
        <path
          d={happy ? 'M45 32l7-2M87 32l-7-2'
            : sad ? 'M45 36.5l7-3.5M87 36.5l-7-3.5'
            : 'M45 35.5l7-1.5M87 35.5l-7-1.5'}
          strokeWidth="1.8"
        />

        {/* Очки: оправа вокруг каждого глаза, перемычка и дужки к корпусу.
            Дешёвый и безошибочно читаемый признак «этот парень соображает» */}
        <g strokeWidth="1.7">
          <circle cx={EYE_L.x} cy={EYE_L.y} r="9.5" />
          <circle cx={EYE_R.x} cy={EYE_R.y} r="9.5" />
          <path d="M61.5 46h9M42.5 44l-5-2M89.5 44l5-2" />
        </g>

        {/* Глаза: белок + зрачок, который ходит за курсором. При грусти взгляд
            уходит вниз — одних бровей мало, чтобы печаль читалась однозначно */}
        <g style={eyeStyle}>
          <circle cx={EYE_L.x} cy={EYE_L.y} r="6" />
          <circle cx={EYE_L.x + pupil.dx} cy={EYE_L.y + pupil.dy + sadGaze} r="2.3" fill="currentColor" stroke="none" />
        </g>
        <g style={eyeStyle}>
          <circle cx={EYE_R.x} cy={EYE_R.y} r="6" />
          <circle cx={EYE_R.x + pupil.dx} cy={EYE_R.y + pupil.dy + sadGaze} r="2.3" fill="currentColor" stroke="none" />
        </g>

        {/* Обычно — ухмылка с приподнятым уголком («уже понял, в чём дело»);
            на очко — широкая улыбка; на проигрыш — уголками вниз.
            Прямая линия читалась как пустое лицо */}
        <path d={happy ? 'M54 60c4 7 14 7 19 0'
          : sad ? 'M55 65c4.5-5 13.5-5 18 0'
          : 'M57 63c4.5 1.6 9 1.4 13-2'} />

        <path d="M56 74v10M76 74v10M46 88h40" />
      </g>

      {/* Гаечный ключ рядом: «уже чиним» */}
      <g transform="translate(96 6) rotate(18)" strokeWidth="1.9">
        <path d="M12 3a6 6 0 00-8.2 7.2L-4 18l4 4 7.8-7.8A6 6 0 0015 6.2l-3.6 3.6-3-3z" />
      </g>
    </svg>
  );
}
