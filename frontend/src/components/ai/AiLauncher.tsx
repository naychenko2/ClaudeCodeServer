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
import { getFabObstacle, subscribeFabObstacle } from '../../lib/ai/fabObstacle';
import { useIsMobile } from '../../lib/breakpoints';
import { FLAGS } from '../../lib/featureFlags';
import { shouldSurface, levelLabel, type SuggestionLevel } from '../../lib/ai/levels';
import { rankContext } from '../../lib/ai/suggest';
import { aiOllamaAvailable } from '../../lib/ai/ollama';
import {
  computeContextState, canShow, markShown, markDismissed,
  isProactiveEnabled, setProactiveEnabled, type Suggestion, type ActionRec,
} from '../../lib/ai/proactive';
import { useContextPersona } from '../../lib/contextPersona';
import { PersonaAvatar } from '../../features/personas/PersonaAvatar';

// Полный размер круглешка — от него считаем налезание (см. useFabObstacleOverlap)
const FAB_FULL = 54;
// Зазор между кнопкой и препятствием: ужимаемся, не дожидаясь касания впритык
const FAB_CLEARANCE = 10;

// Налезает ли нижнее препятствие (композер чата, футер мастера персон) на угол кнопки.
// Замер ведём по геометрии ПОЛНОГО размера (54), а не текущего: иначе ужавшаяся кнопка
// перестала бы пересекаться, выросла обратно и замигала бы туда-сюда.
// Позиция кнопки задана от правого-нижнего края, поэтому её right/bottom от размера не
// зависят — левый-верхний угол полного круга достраиваем от них.
function useFabObstacleOverlap(fabRef: React.RefObject<HTMLElement | null>, active: boolean): boolean {
  const [overlap, setOverlap] = useState(false);
  useEffect(() => {
    if (!active) { setOverlap(false); return; }
    let ro: ResizeObserver | null = null;
    const measure = () => {
      const fab = fabRef.current;
      const ob = getFabObstacle();
      if (!fab || !ob) { setOverlap(false); return; }
      const f = fab.getBoundingClientRect();
      const o = ob.getBoundingClientRect();
      if (o.width === 0 && o.height === 0) { setOverlap(false); return; }
      setOverlap(
        o.right > f.right - FAB_FULL - FAB_CLEARANCE && o.left < f.right + FAB_CLEARANCE &&
        o.bottom > f.bottom - FAB_FULL - FAB_CLEARANCE && o.top < f.bottom + FAB_CLEARANCE,
      );
    };
    // Наблюдаем само препятствие: композер и растёт в высоту (разнос грипом, длинный
    // текст), и меняет ширину, когда открывается панель — оба случая двигают его кромку
    // к кнопке. Плюс resize окна: он двигает саму кнопку, а не препятствие.
    const attach = () => {
      ro?.disconnect();
      const ob = getFabObstacle();
      if (ob) { ro = ro ?? new ResizeObserver(measure); ro.observe(ob); }
      measure();
    };
    const unsub = subscribeFabObstacle(attach);
    window.addEventListener('resize', measure);
    attach();
    return () => { unsub(); ro?.disconnect(); window.removeEventListener('resize', measure); };
  }, [fabRef, active]);
  return overlap;
}

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
  const listRef = useRef<HTMLDivElement>(null);
  const fabRef = useRef<HTMLButtonElement>(null);
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
  // prefers-reduced-motion: на FAB гасим и дыхание, и кольца «Эхо» (в cc-fab-breathe и
  // .cc-echo-ring анимация уже выключается, но статичные элементы оставались бы видимыми —
  // круг-контур и застывшее лицо, поэтому кольца при reduced мы не рендерим вовсе)
  const reduced = typeof window !== 'undefined' && window.matchMedia?.('(prefers-reduced-motion: reduce)').matches;
  // Режим «Стены» — кнопка там всегда компактная (см. эффект на NAV_CHANGE_EVENT)
  const [wallMode, setWallMode] = useState(() => getNav()?.screen === 'wall');
  // Композер чата (или футер мастера персон) дошёл до угла кнопки → ужимаем её
  const obstacleOverlap = useFabObstacleOverlap(fabRef, !open && !isMobile);
  // Компактный круг: на стене всегда, в остальных местах — когда снизу подпёрло
  const fabSmall = wallMode || obstacleOverlap;
  // Лицо AI-хаба (фича default-personas-onboarding): «хозяин контекста», а НЕ собеседник
  // текущего чата. personaId: null осознанно пропускает уровень «персона чата» в резолвере
  // (contextPersona.ts): в проекте кнопка/палитра носят лицо дефолт-персоны проекта (даже
  // если открыт чат с другой персоной), вне проекта — личной дефолт-персоны владельца;
  // null на обоих уровнях — нейтральный логотип. Та же переменная питает и лицо кнопки,
  // и лицо в шапке палитры (searchRow) — они всегда совпадают. (WaitingIndicator в ленте
  // зовёт useContextPersona() без аргументов — там персона чата правильна.)
  const facePersona = useContextPersona({ personaId: null });
  useEffect(() => { api.notes.caps().then(c => setSemanticCaps(c.semantic)).catch(() => {}); }, []);

  // Git-статус текущего проекта — чтобы git-действия (разбор коммитов, ревью diff, история
  // файла) не предлагались в проекте без git ни в палитре, ни в LLM-рекомендациях. Опрашиваем
  // при смене открытого проекта; результат кэшируем по projectId (не перезапрашиваем на каждой
  // навигации внутри проекта — напр. при переключении файлов).
  const [gitRepo, setGitRepo] = useState<{ projectId: string; isRepo: boolean } | null>(null);
  const gitReqPid = useRef<string | null>(null);
  useEffect(() => {
    gitReqPid.current = null; // сброс при смене online — перезапросить статус
    const resolve = () => {
      const n = getNav();
      const pid = n?.screen === 'project' ? (n.project?.id ?? null) : null;
      if (!pid || !online) { gitReqPid.current = null; setGitRepo(null); return; }
      if (gitReqPid.current === pid) return; // статус этого проекта уже запрошен
      gitReqPid.current = pid;
      api.git.status(pid)
        .then(s => setGitRepo({ projectId: pid, isRepo: s.isRepo }))
        .catch(() => setGitRepo({ projectId: pid, isRepo: false }));
    };
    resolve();
    window.addEventListener(NAV_CHANGE_EVENT, resolve);
    return () => window.removeEventListener(NAV_CHANGE_EVENT, resolve);
  }, [online]);

  // Немедленный сброс статуса FAB при смене раздела — не ждём опросного тика (иначе старая
  // подсказка/уровень «залипают» до 1.5 с и кажется, что статус не сбрасывается).
  // Заодно ведём признак «Стена»: там колонок чатов много и композер у каждой свой —
  // крупному кругу места нет, он всегда компактный.
  useEffect(() => {
    const onNav = () => {
      setSuggestion(null); setFabLevel('none'); setRecs([]);
      setWallMode(getNav()?.screen === 'wall');
    };
    window.addEventListener(NAV_CHANGE_EVENT, onNav);
    return () => window.removeEventListener(NAV_CHANGE_EVENT, onNav);
  }, []);

  // Контекст собираем на момент открытия (getNav синхронен вне React)
  const buildCtx = (): AiActionCtx => {
    const nav = getNav();
    // git прокидываем только если кэш относится к текущему открытому проекту —
    // иначе статус из прежнего проекта «протёк» бы в новый
    const pid = nav?.screen === 'project' ? nav.project?.id : undefined;
    const git = gitRepo && pid && gitRepo.projectId === pid ? { isRepo: gitRepo.isRepo } : undefined;
    return { nav, online, flag: getFlag, caps: { semantic: semanticCaps }, chat: getChatContext(), git };
  };

  // Рекомендации модели (id + уровень) — единый источник для: выделения в палитре,
  // hover-балуна FAB и переупорядочивания. Обновляются проактивным тиком и при открытии
  // палитры/наведении. Уровень минорнее medium в палитре тоже помечаем (пользователь видит,
  // что именно советует AI). Пусто — рекомендаций нет.
  const [recs, setRecs] = useState<ActionRec[]>([]);
  const [fabHover, setFabHover] = useState(false);
  // Свайп-закрытие проактивного балуна на тач-экране (смещение вправо за экран)
  const [dragX, setDragX] = useState(0);
  const swipeStart = useRef<number | null>(null);

  // Немедленный сброс статуса FAB при смене раздела — не ждём опросного тика (иначе старая
  // подсказка/уровень «залипают» до 1.5 с и кажется, что статус не сбрасывается).
  useEffect(() => {
    const onNav = () => { setSuggestion(null); setFabLevel('none'); setRecs([]); };
    window.addEventListener(NAV_CHANGE_EVENT, onNav);
    return () => window.removeEventListener(NAV_CHANGE_EVENT, onNav);
  }, []);

  // Список действий пересчитывается на каждый ввод, пока палитра открыта. Рекомендованные
  // (recs) получают уровень (recLevel) для бейджа/подсветки и поднимаются в начало группы.
  const items = useMemo(() => {
    if (!open) return [];
    const ranked = rankedActions(buildCtx(), q).map(r => ({ ...r, recLevel: recs.find(x => x.id === r.action.id)?.level }));
    // Базовый порядок: рекомендованные/контекстные выше (rankedActions уже даёт
    // contextual-first; при наличии recs подтягиваем рекомендованные по их рангу).
    let base = ranked;
    if (recs.length > 0) {
      const pos = (id: string) => { const i = recs.findIndex(x => x.id === id); return i < 0 ? Number.MAX_SAFE_INTEGER : i; };
      const ctxItems = ranked.filter(r => r.contextual).sort((a, b) => pos(a.action.id) - pos(b.action.id));
      const rest = ranked.filter(r => !r.contextual);
      base = [...ctxItems, ...rest];
    }
    // Группируем по зонам (Чат / Проект / Глобально). Порядок зон — по первому
    // появлению в base, т.е. релевантные контексту зоны оказываются выше.
    // sort стабилен → внутризонный порядок (контекстность/ранг) сохраняется.
    const zoneRank: string[] = [];
    for (const it of base) { const z = zoneOf(it.action.section); if (!zoneRank.includes(z)) zoneRank.push(z); }
    return base.slice().sort((a, b) => zoneRank.indexOf(zoneOf(a.action.section)) - zoneRank.indexOf(zoneOf(b.action.section)));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, q, online, semanticCaps, recs, gitRepo]);

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

  // eslint-disable-next-line react-hooks/set-state-in-effect -- сброс активного пункта на 0 при смене поиска
  useEffect(() => { setIdx(0); }, [q, open]);
  // Держим активный (стрелками) пункт в видимой области списка. block:'nearest' —
  // не дёргает, если пункт уже виден (напр. при наведении мышью).
  useEffect(() => {
    if (!open) return;
    listRef.current?.querySelector<HTMLElement>(`[data-idx="${idx}"]`)?.scrollIntoView({ block: 'nearest' });
  }, [idx, open]);
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

  // Пока палитра открыта, кнопки в DOM нет — mouseleave по ней уже не придёт, и признак
  // наведения залипает: закрыв палитру, пользователь видел кнопку в РАЗВЁРНУТОМ виде,
  // хотя курсор давно в стороне. Гасим признак на каждой смене состояния палитры;
  // если курсор и правда над кнопкой, ближайшее движение мыши вернёт наведение.
  useEffect(() => {
    if (hoverTimer.current) { clearTimeout(hoverTimer.current); hoverTimer.current = null; }
    setFabHover(false);
  }, [open]);

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
    // Экраны, где повод живёт своей жизнью: на стене чаты стартуют и завершают ходы
    // без участия человека и без смены навигации, поэтому однажды посчитанный уровень
    // застывал бы до ухода с экрана (кнопка оставалась серой при живом ходе).
    // Статусы чужих чатов сюда не приходят событиями — стена держит группы только
    // своего набора, поэтому периодический перерасчёт, а не подписка.
    const LIVE_RECHECK_MS = 30_000;
    let lastComputed = 0;
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
      if (Date.now() - stableSince < dwell) return;
      // Уже считали для этого места — повторяем только там, где повод меняется сам,
      // и не чаще LIVE_RECHECK_MS
      const live = ctx.nav?.screen === 'wall';
      if (firedForSig === sig && !(live && Date.now() - lastComputed >= LIVE_RECHECK_MS)) return;
      firedForSig = sig; // помечаем сразу, чтобы не дёргать данные каждый тик
      lastComputed = Date.now();
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
    // При выключении гасим и статус FAB: опросный тик при выключенном тумблере сразу
    // делает return и не сбросит уровень сам — иначе уже зажжённый strong-повод оставил
    // бы домик прыгать (cc-fab-hop) до смены раздела.
    if (!next) { setSuggestion(null); setFabLevel('none'); setRecs([]); }
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

  // Прыжок «есть идея»: ровно 3 раза (CSS-count), потом замирает — не скачет без конца.
  // Новая сильная подсказка перезапускает 3 прыжка заново. «Новизну» ловим по сигнатуре
  // повода (ключ подсказки / id топовой рекомендации): пока он тот же — не дёргаем, класс
  // уже отыграл; сменился — ре-триггерим анимацию через reflow (снять класс → reflow → надеть).
  const hopSig = fabStrong && !aiBusy ? (suggestion?.key ?? recs[0]?.id ?? 'strong') : null;
  useEffect(() => {
    const el = fabRef.current;
    if (!el) return;
    el.classList.remove('cc-fab-hop');
    if (!hopSig) return;
    void el.offsetWidth; // форс reflow — анимация начнётся с нуля
    el.classList.add('cc-fab-hop');
  }, [hopSig]);

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
          ref={fabRef}
          onClick={() => { setQ(''); setOpen(true); }}
          aria-label="AI-действия (Ctrl/⌘ + K)"
          title="AI-действия · ⌘K"
          style={{
            ...fabStyle,
            // Ореол сохраняем, но нейтральный: SHADOW.fab из fabStyle — accent-свечение
            // под сплошной оранжевый круг, а здесь заливки нет (весь круг занимает
            // логотип), и оранжевый ореол выглядел беспричинным. Цвет свечения должен
            // идти от самой кнопки — отсюда парный токен той же геометрии.
            background: 'none', padding: 0, overflow: 'visible', boxShadow: SHADOW.fabNeutral,
            // Размер: базовый из var --cc-fab-size (54 без панели / 36 при распахнутой панели),
            // при наведении на десктопе — всегда 54. Растёт влево-вверх (угол приклеен к краю).
            // На мобиле размер фиксирован (36, там наведения и распахнутых панелей нет).
            // Композер (или футер мастера персон), дошедший до угла, тоже ужимает кнопку:
            // она остаётся в углу и просто занимает меньше места, а не уезжает вверх.
            // На «Стене» компактный размер постоянный.
            ...(fabSmall && !isMobile ? { width: 36, height: 36 } : {}),
            // Наведение растит кнопку до полного размера — как в компактном режиме панелей.
            // Исключение — подпёртая композером: там расти некуда, круг накрыл бы поле ввода.
            ...(fabHover && !isMobile && !obstacleOverlap ? { width: 54, height: 54 } : {}),
            ...(isMobile ? { right: 16, width: 36, height: 36 } : {}),
            // Наведение на десктопе: в большом состоянии подъём (--cc-fab-lift = -2px), в
            // малом подъёма нет (0px) — там кнопка вместо этого растёт до 54.
            transform: fabHover && !isMobile ? 'translateY(var(--cc-fab-lift, -2px))' : undefined,
          }}
          onMouseEnter={enterFab}
          onMouseLeave={leaveFab}
        >
          {/* Лицо кнопки: аватар релевантной персоны (фича default-personas-onboarding),
              fallback — логотип «AI Home» на весь круг. fill: аватар растягивается по
              контейнеру и точно совпадает с кругом на ЛЮБОМ кадре анимации размера
              (36↔54), а не по фиксированному size, который рассинхронизировался с переходом.
              Покой — лёгкое приглушение (opacity 0.85, без grayscale); идея — подскок
              (cc-fab-hop на кнопке) + кольца; работа — кольца + дыхание (cc-fab-breathe). */}
          {/* Кольца «Эхо»: «идея» (strong) и «работа» (aiBusy). Тот же приём, что в ленте
              чата — .cc-echo-ring с --cc-echo-color. Цвет разводит состояния: «работа» —
              нейтральный дымок (C.smoke, те же кольца, что в ленте чата), «идея» — акцент
              (C.accent). Если оба состояния сразу — приоритет у работы: персона занята,
              звать не время, оранжевый пульс был бы зазыванием. pointer-events: none в
              самом классе; overflow у кнопки visible, ничто выше по дереву её не клипует.
              Лежат ПОД аватаром: в покое (scale 1) контур скрыт за кругом лица, при
              расширении выходит за края и читается как пульс. */}
          {(fabStrong || aiBusy) && !reduced && (
            <span
              aria-hidden
              // eslint-disable-next-line @typescript-eslint/no-explicit-any
              style={{ position: 'absolute', inset: 0, ['--cc-echo-color' as any]: aiBusy ? C.smoke : C.accent }}
            >
              <span className="cc-echo-ring" />
              <span className="cc-echo-ring cc-echo-ring--2" />
            </span>
          )}
          {facePersona ? (
            <span
              aria-hidden
              className={aiBusy ? 'cc-fab-breathe' : undefined}
              style={{
                position: 'absolute', inset: 0, display: 'block', borderRadius: '50%', overflow: 'hidden',
                // Покой — приглушение без grayscale: обесцвеченное лицо читается как
                // «оффлайн/заблокирован», а сигнал должен быть «спокоен, но на связи».
                ...(fabDim && !aiBusy && !fabHover ? { opacity: 0.85 } : {}),
              }}
            >
              <PersonaAvatar persona={facePersona} fill size={isMobile || (fabSmall && !(fabHover && !obstacleOverlap)) ? 36 : 54} />
            </span>
          ) : (
            <img
              src="/pwa-64x64.png" alt="" aria-hidden
              className={aiBusy ? 'cc-fab-breathe' : undefined}
              style={{
                position: 'absolute', inset: 0, width: '100%', height: '100%',
                objectFit: 'cover', borderRadius: '50%', display: 'block',
                ...(fabDim && !aiBusy && !fabHover ? { opacity: 0.85 } : {}),
              }}
            />
          )}
        </button>
      )}

      {open && createPortal(
        <div style={{ ...overlayStyle, ...(isMobile ? { alignItems: 'flex-end', paddingTop: 0 } : {}) }} onMouseDown={close}>
          <div
            style={{ ...paletteStyle, ...(isMobile ? { width: '100%', maxWidth: '100%', borderRadius: `${R.sheet}px ${R.sheet}px 0 0`, maxHeight: '82vh' } : {}) }}
            onMouseDown={e => e.stopPropagation()} role="dialog" aria-label="AI-палитра">
            {/* Поиск + бейдж контекста; слева — лицо хаба (аватар релевантной персоны,
                fallback — нейтральная искра) */}
            <div style={searchRow}>
              {facePersona
                ? <span style={{ display: 'flex', flex: 'none' }}><PersonaAvatar persona={facePersona} size={22} /></span>
                : <span style={{ color: C.accent, display: 'flex', flex: 'none' }}><SparkleIcon size={18} /></span>}
              <input
                ref={inputRef} value={q} onChange={e => setQ(e.target.value)} onKeyDown={onInputKey}
                placeholder="Что сделать с помощью AI…" autoComplete="off" style={inputStyle}
              />
              {ctxLabel && <span style={ctxBadge}>{ctxLabel}</span>}
            </div>

            {/* Список */}
            <div ref={listRef} style={{ overflowY: 'auto', padding: 8, maxHeight: 'min(52vh, 400px)' }}>
              {items.length === 0 && (
                <div style={emptyStyle}>{q ? 'Ничего не найдено' : 'Нет доступных действий'}</div>
              )}
              {items.map((it, i) => {
                const prev = items[i - 1];
                const zone = zoneOf(it.action.section);
                // Разделитель зоны — перед первым действием зоны (стиль дат в списке чатов)
                const showZoneHeader = !prev || zoneOf(prev.action.section) !== zone;
                return (
                  <div key={it.action.id} data-idx={i}>
                    {showZoneHeader && <ZoneDivider title={ZONE_LABEL[zone]} first={i === 0} />}
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
                      {it.recLevel && <span style={recBadge(it.recLevel)}>{levelLabel(it.recLevel)}</span>}
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

// Зоны палитры: действия чата, действия проекта и всё прочее («Глобально» —
// заметки/задачи/персоны/знания, не привязанные к текущему проекту/чату). Группы
// разделяются в ленте линией-разделителем (стиль дат в списке чатов).
type AiZone = 'chat' | 'project' | 'global';
const zoneOf = (section: string): AiZone => section === 'chat' ? 'chat' : section === 'project' ? 'project' : 'global';
const ZONE_LABEL: Record<AiZone, string> = { chat: 'Чат', project: 'Проект', global: 'Глобально' };

// Разделитель зоны: бледная черта по бокам + подпись в старом моно-uppercase стиле
// (как прежние заголовки групп). Черта — borderLight, чтобы не спорить с контентом.
function ZoneDivider({ title, first }: { title: string; first?: boolean }) {
  const line: React.CSSProperties = { flex: 1, height: 1, background: C.borderLight };
  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: 8, padding: `${first ? 0 : 10}px 8px 5px` }}>
      <div style={line} />
      <span style={{ fontFamily: FONT.mono, fontSize: 10.5, textTransform: 'uppercase', letterSpacing: 0.6, color: C.textMuted, whiteSpace: 'nowrap' }}>{title}</span>
      <div style={line} />
    </div>
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
    case 'wall': return 'Стена';
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
  // Всегда в правом нижнем углу экрана. Боковой отступ — var --cc-fab-inset: обычно
  // 20px (уютный угол), но когда справа открыт остров-панель во всю высоту, PanelZone ставит
  // 6px и кнопка прижимается плотнее к краю (меньше налезает на остров). Нижний отступ
  // отдельный (--cc-fab-bottom): в компактном режиме он равен отступу холста островов,
  // чтобы кнопка стояла на одной линии с их нижней кромкой. Из угла кнопка не уезжает
  // никогда — на подошедший снизу композер она отвечает не подъёмом, а ужиманием
  // (см. useFabObstacleOverlap). Снизу ещё safe-area.
  position: 'fixed', right: 'var(--cc-fab-inset, 20px)',
  bottom: 'calc(env(safe-area-inset-bottom, 0px) + var(--cc-fab-bottom, 20px))',
  // Базовый размер — var --cc-fab-size: по умолчанию 54 (панель не распахнута), а когда
  // справа распахнута панель во всю высоту, PanelZone ставит 36 (компактный, не мешает).
  // При наведении переопределяется на 54 (см. button inline). Меняется плавно (transition).
  width: 'var(--cc-fab-size, 54px)', height: 'var(--cc-fab-size, 54px)',
  borderRadius: '50%', border: 'none', cursor: 'pointer',
  background: C.accent, color: C.onAccent, boxShadow: SHADOW.fab,
  // Ниже выпадающих меню (Z.dropdown): FAB висит в том же углу, что и попапы кнопок
  // композера, и на Z.modal-1 перекрывал их собой. Выше обычного контента остаётся.
  display: 'grid', placeItems: 'center', zIndex: Z.dropdown - 1,
  transition: 'width .18s ease, height .18s ease, transform .16s ease',
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
  display: 'flex', alignItems: 'center', gap: 10, padding: '13px 16px 8px',
};
const inputStyle: React.CSSProperties = {
  flex: 1, minWidth: 0, border: 'none', background: 'transparent', outline: 'none',
  fontFamily: FONT.sans, fontSize: 15.5, color: C.textHeading,
};
const ctxBadge: React.CSSProperties = {
  flex: 'none', fontFamily: FONT.mono, fontSize: 10.5, textTransform: 'uppercase', letterSpacing: 0.4,
  color: C.textMuted, background: C.bgInset, border: `1px solid ${C.border}`, borderRadius: R.sm, padding: '3px 8px',
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
  // Над кнопкой в покое — её нижний отступ (var) + текущая высота кнопки (var, −8 нахлёст)
  position: 'fixed', right: 'var(--cc-fab-inset, 20px)',
  bottom: 'calc(env(safe-area-inset-bottom, 0px) + var(--cc-fab-bottom, 20px) + var(--cc-fab-size, 54px) - 8px)',
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
  // Показывается на наведении, когда кнопка выросла до 54 — её нижний отступ (var) + высота
  position: 'fixed', right: 'var(--cc-fab-inset, 20px)',
  bottom: 'calc(env(safe-area-inset-bottom, 0px) + var(--cc-fab-bottom, 20px) + 46px)',
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
