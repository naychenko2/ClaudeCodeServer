// Общая анимированная оболочка полос над композером (Гит / Картинки / Руки).
// Заменила три похожих оболочки, которые каждая полоса собирала сама
// (composerLip('top') / composerLip('top', { tab: true }) + телефонная плашка).
// Теперь оболочка одна: на смене `collapsed` она плавно меняет высоту, ширину,
// поля и отступы, а содержимое проявляется через opacity с задержкой —
// текст не «мнётся», пока геометрия ещё не устаканилась.
//
// Дизайн и тайминги — LIP_ANIM из lib/design.ts (токен добавлен на шаге 1).
// При prefers-reduced-motion: reduce переход мгновенный.
//
// Содержимое обоих состояний рендерится всегда: ушко — для замера ширины,
// полная полоса — для мгновенной готовности к развороту. Неактивное скрыто
// `position: absolute; visibility: hidden`, чтобы scrollWidth читался.
import { forwardRef, useEffect, useLayoutEffect, useRef, useState, type CSSProperties, type ReactNode } from 'react';
import { C, R, COMPOSER_LIP, LIP_ANIM, composerLip } from '../../lib/design';

// Единый transition для морфа оболочки: всё, что меняется на переходе
// collapsed→expanded и обратно, проходит через одну и ту же кривую. margin-left
// не морфится (ушко и развёрнутая полоса стоят у одного края), перечисляем
// margin-top/margin-bottom точечно — на margin-left просто не заявляем переход.
const SHELL_TRANSITION =
  `height ${LIP_ANIM.ms}ms ${LIP_ANIM.ease},` +
  `width ${LIP_ANIM.ms}ms ${LIP_ANIM.ease},` +
  `padding ${LIP_ANIM.ms}ms ${LIP_ANIM.ease},` +
  `margin-top ${LIP_ANIM.ms}ms ${LIP_ANIM.ease},` +
  `margin-bottom ${LIP_ANIM.ms}ms ${LIP_ANIM.ease}`;

// Проявление содержимого: задержка 60 мс, чтобы оболочка уже почти набрала
// размер — текст не перекомпонуется, пока строка дёргается.
const FADE_TRANSITION =
  `opacity ${LIP_ANIM.fadeMs}ms ${LIP_ANIM.ease} ${LIP_ANIM.fadeDelayMs}ms`;

const HIDDEN_INNER: CSSProperties = {
  // В стеке поверх активного, но не в потоке — оболочке не мешает
  position: 'absolute', top: 0, left: 0, right: 0,
  // Место занимает, чтобы измерение scrollWidth дало корректную ширину
  visibility: 'hidden', pointerEvents: 'none',
};

// Обёртка над matchMedia('(prefers-reduced-motion: reduce)'): в node-тестах
// matchMedia может не быть, держим дефолт «без ограничений»
function usePrefersReducedMotion(): boolean {
  const [reduced, setReduced] = useState(false);
  useEffect(() => {
    if (typeof window === 'undefined' || !window.matchMedia) return;
    const mq = window.matchMedia('(prefers-reduced-motion: reduce)');
    setReduced(mq.matches);
    const onChange = () => setReduced(mq.matches);
    mq.addEventListener('change', onChange);
    return () => mq.removeEventListener('change', onChange);
  }, []);
  return reduced;
}

// Вытащить геометрию composerLip без width/maxWidth — их задаёт оболочка,
// чтобы измерять scrollWidth мини-содержимого и плавно менять пиксели.
function shellGeom(side: 'top' | 'bottom', tab: boolean): CSSProperties {
  const base = composerLip(side, { tab });
  // eslint-disable-next-line @typescript-eslint/no-unused-vars -- width/maxWidth выбрасываем, оболочка управляет сама
  const { width, maxWidth, ...rest } = base as CSSProperties & { width?: unknown; maxWidth?: unknown };
  return rest as CSSProperties;
}

type Props = {
  collapsed: boolean;
  isMobile: boolean;
  // Планшет slim-геометрия (на десктопе игнорируется): меняет только marginTop,
  // остальное (composerLip-высота, паддинг) — то же.
  isSlim?: boolean;
  // Стиль внутреннего контейнера активного слоя: display:flex, gap, alignItems,
  // свой фон, если composerLip его не задаёт. Высоту и поля оболочка берёт на себя.
  miniStyle?: CSSProperties;
  fullStyle?: CSSProperties;
  // Атрибуты активного слоя: тесты и селекторы (`data-git-strip="full"` и т.п.).
  // Применяются КАК к активному, так и к неактивному (если задано), чтобы тест
  // находил оба варианта вне зависимости от состояния. Принимаем простой record
  // (а не HTMLAttributes) — JSX-типизация HTMLAttributes не включает data-*.
  miniAttrs?: Record<string, string | number | boolean | undefined>;
  fullAttrs?: Record<string, string | number | boolean | undefined>;
  miniNode: ReactNode;
  fullNode: ReactNode;
};

export const ComposerLipShell = forwardRef<HTMLDivElement, Props>(function ComposerLipShell({
  collapsed, isMobile, isSlim = false,
  miniStyle, fullStyle, miniAttrs, fullAttrs,
  miniNode, fullNode,
}: Props, ref) {
  const reduced = usePrefersReducedMotion();
  const miniRef = useRef<HTMLDivElement>(null);
  // Ширина ушка: padX*2 + scrollWidth содержимого. Меряем через layout-effect —
  // до отрисовки, чтобы не было скачка.
  const [miniWidth, setMiniWidth] = useState<number | null>(null);
  // overflow:hidden нужен на время морфа, иначе текст/тени ушка «светят» в полный
  // размер при раскрытии. После окончания — снимаем, чтобы тень и углы оболочки
  // не клипались, и меню переключателя не страдало.
  const [clipping, setClipping] = useState(true);
  // На первом рендере анимация не запускается — иначе маунт нового чата
  // мигнул бы переходом. Переход — только на смену `collapsed` у той же полосы.
  const mounted = useRef(false);

  // Меряем ушко после маунта и при смене isMobile. На телефоне ушко во всю
  // ширину — измерять нечего, контейнер 100%.
  useLayoutEffect(() => {
    if (isMobile) { setMiniWidth(null); return; }
    const el = miniRef.current;
    if (!el) return;
    // Берём максимум из scrollWidth за два кадра, чтобы успели отрисоваться
    // шрифты и иконки; в первый маунт offsetWidth может быть 0
    const w = Math.max(el.scrollWidth, el.offsetWidth);
    if (w > 0) setMiniWidth(w + COMPOSER_LIP.padX * 2);
  }, [isMobile, collapsed, miniNode, fullNode]);

  // Первый рендер: снимаем clipping сразу, не запускаем анимацию
  useLayoutEffect(() => {
    if (!mounted.current) {
      mounted.current = true;
      // Полная полоса — клипать нечего, шеврон/меню должны рисоваться свободно
      if (!collapsed) setClipping(false);
    }
  }, []);

  // При смене collapsed (не на маунте) — клипаем на время морфа
  useEffect(() => {
    if (!mounted.current) return;
    if (reduced) {
      setClipping(false);
      return;
    }
    setClipping(true);
    const t = setTimeout(() => setClipping(false), LIP_ANIM.ms + 30);
    return () => clearTimeout(t);
  }, [collapsed, reduced]);

  // Геометрия оболочки. Ушко и развёрнутая полоса стоят у одного левого края
  // (margin-left: 0 в обоих состояниях — на десктопе и slim) — меняются только
  // высота, ширина, отступ сверху, проявление содержимого.
  const fullBaseStyle: CSSProperties = isMobile
    ? {
        height: 44, padding: '0 6px',
        background: C.bgPanel, border: `1px solid ${C.border}`,
        borderRadius: R.xxl, margin: '6px 0', boxSizing: 'border-box',
      }
    : {
        ...shellGeom('top', false),
        // shellGeom не даёт width — замещаем тем, что нужно в обоих состояниях
        // (width задаётся в финальном объекте, см. ниже).
        marginTop: isSlim ? 6 : 10,
      };
  const miniBaseStyle: CSSProperties = isMobile
    ? {
        height: 30, padding: '0 6px 0 4px',
        background: C.bgPanel, border: `1px solid ${C.border}`,
        borderRadius: R.lg, margin: '4px 0 6px', boxSizing: 'border-box',
      }
    : {
        ...shellGeom('top', true),
        marginTop: 4,
      };

  // Какое состояние активно
  const activeBase = collapsed ? miniBaseStyle : fullBaseStyle;
  // Ширина оболочки: на десктопе в свёрнутом — измеренная, в развёрнутом — 100%.
  // На телефоне — всегда 100%, ширина ушка не играет. Ушко у левого края
  // (`margin-left: 0` по умолчанию, отдельного maxWidth не задаём), как и развёрнутая
  // полоса — между ними не морфится.
  const shellStyle: CSSProperties = {
    ...activeBase,
    position: 'relative',  // нужно для absolute-позиции неактивного ребёнка
    width: isMobile ? '100%' : (collapsed ? (miniWidth ?? 'auto') : '100%'),
    boxSizing: 'border-box',
    // overflow:hidden клипает содержимое во время морфа; снимаем после transitionend
    overflow: clipping ? 'hidden' : 'visible',
    // Переход снимаем при reduced-motion и на первом рендере (через mounted-флаг)
    transition: !mounted.current || reduced ? 'none' : SHELL_TRANSITION,
  };

  // Активный слой: рендерится, виден. Стиль — переданный из полосы + height:100%.
  // Проявление через opacity — стартует с 1, на смене collapsed кладём 0→1.
  const activeInnerBase: CSSProperties = {
    display: 'flex', alignItems: 'center', minWidth: 0,
    height: '100%', width: '100%',
    opacity: 1,
    transition: !mounted.current || reduced ? 'none' : FADE_TRANSITION,
  };
  // Скрытый слой: тот же стиль, но absolute + visibility:hidden + opacity 0
  const hiddenInnerBase: CSSProperties = { ...HIDDEN_INNER, opacity: 0 };

  return (
    <div
      ref={ref}
      data-composer-lip=""
      style={shellStyle}
    >
      <div
        ref={miniRef}
        style={collapsed
          ? { ...activeInnerBase, ...(miniStyle ?? null) }
          : { ...activeInnerBase, ...(miniStyle ?? null), ...hiddenInnerBase }}
        {...(miniAttrs ?? {})}
      >
        {miniNode}
      </div>
      <div
        style={collapsed
          ? { ...activeInnerBase, ...(fullStyle ?? null), ...hiddenInnerBase }
          : { ...activeInnerBase, ...(fullStyle ?? null) }}
        {...(fullAttrs ?? {})}
      >
        {fullNode}
      </div>
    </div>
  );
});
