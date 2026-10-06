import { useState, useEffect, useRef } from 'react';
import { C, FS, SP } from '../../lib/design';
import {
  captionLeadMs, waitingToolCaption, FAILED_RE, nextTypewriterStep, typewriterDelay, typewriterText,
  TYPEWRITER_AT_REST, TYPEWRITER_START, type TypewriterState,
} from '../../lib/toolTiming';
import type { Operation } from '../../lib/toolLabels';
import { OPERATION_ICON } from '../../lib/operationIcons';
import { ICON_SIZE, ICON_STROKE } from './icons';
import { useRunningElapsed } from '../../hooks/useRunningElapsed';
import { pickVerb } from '../chat/thinkingVerbs';
import aiHome from '../../assets/ai-home.png';
import { useContextPersona } from '../../lib/contextPersona';
import { PersonaAvatar } from '../../features/personas/PersonaAvatar';

// Высота бокса лица индикатора. Аватар — 28px; сверху и снизу резерв по 14px, чтобы
// максимальный размах колец «Эхо» (scale 1.85 от 28 ≈ +12px во все стороны) лежал
// ВНУТРИ бокса, а не торчал наружу. Торчащие transform'ом кольца входят в scrollable
// overflow контейнера ленты → scrollHeight пульсирует в такт анимации → лента дрожит.
// С резервом visual overflow бокса нулевой, scrollHeight стабилен. Подтверждено стендом
// (.cc-attachments/jitter-verify): при резерве 0 ΔscrollHeight=9px, при ≥12 — Δ=0.
const ECHO_FACE_W = 28;
const ECHO_FACE_H = 56;

// Ширина колонки времени инструмента: под «9 мин 59 с» — время стоит неподвижно, пока
// печатается и стирается текст справа. Под редкое «59 мин 59 с» не держим: на частом коротком
// времени колонка зияла дырой, а сдвиг на пару знаков раз в десять минут незаметен
const WAIT_CLOCK_MIN_W = '9ch';

// Живой индикатор ожидания: значок-логотип «AI Home» с расходящимися кольцами «Эхо»
// вокруг аватара персоны (или логотипа, если персоны нет) + «печатная машинка» по синонимам.
// Текст печатается посимвольно с курсором, в конце дописывается «…», держит паузу,
// затем стирается и сменяется новым случайным синонимом. Общий для чата и любых
// других долгих ИИ-операций (подбор/генерация по кнопке «✨ …») — hint поясняет,
// что именно происходит и сколько примерно ждать.
// В режиме awaitingResponse=true — фиксированный текст «Ожидаю ответа…» без анимации,
// т.к. Claude ждёт ввода пользователя.
//
// Идёт долгий инструмент (activeToolLabel) — вместо глагола его русская подпись и время:
// «Синхронизирую транскрипты · 52 с». Пропы примитивами: объект пересоздавался бы на каждом
// рендере ленты. Подпись встаёт только с порога TOOL_TIMER_MIN_MS, чтобы быстрые Read/Grep
// не мигали поверх глаголов; дальше машинка по кругу печатает подпись и счётчик этапа.
export function WaitingIndicator({ planning, hint, awaitingResponse, waitingReason, waitingTicks, activeToolLabel, activeToolStartedAt, activeToolTimer = true, activeToolAppearedAt, activeToolDetail, activeToolOnScreen = false, activeToolOperation = null }: {
  planning?: 'planning' | 'replanning';
  hint?: string;
  awaitingResponse?: boolean;
  // Причина ожидания из маркера `<waiting>` цикла «до готово» — показываем под индикатором,
  // чтобы пользователь понимал, чего ждёт ход. null — обычный ход, без причины.
  waitingReason?: string | null;
  // Счётчик тиков ожидания (Loop:WaitingTickSeconds, дефолт 300 с). 0 — не показываем
  waitingTicks?: number;
  // Подпись идущего инструмента (description либо имя по-русски); null — крутятся глаголы
  activeToolLabel?: string | null;
  // Старт инструмента по часам сервера (Unix-мс) — после F5 отсчёт продолжается
  activeToolStartedAt?: number | null;
  // false — фактический старт ещё не пришёл (awaitsToolStart): подпись без времени
  activeToolTimer?: boolean;
  // Момент появления вызова до сдвига старта на tool_started; null — старт не сдвигался
  activeToolAppearedAt?: number | null;
  // Счётчик текущего этапа прогона («412 из 7951 · упало 1») — после времени, режется первым
  activeToolDetail?: string | null;
  // Карточка инструмента видна в ленте: подпись, время и счётчик не повторяем — вместо них
  // глаголы машинки, как без инструмента
  activeToolOnScreen?: boolean;
  // Типовая операция (тесты, сборка…) — иконка перед временем, та же, что в шапке карточки
  activeToolOperation?: Operation | null;
} = {}) {
  const reduced = typeof window !== 'undefined'
    && window.matchMedia?.('(prefers-reduced-motion: reduce)').matches;
  // Шутливые глаголы крутятся всегда; в режиме планирования отличается только цвет колец (индиго)
  const pulseColor = planning ? C.plan : C.accent;
  // Цвет колец «Эхо»: нейтральный smoke по умолчанию, plan — в режиме планирования
  const ringColor = planning ? C.plan : C.smoke;
  // Лицо индикатора = релевантная персона контекста.
  // Аватар 28px с расходящимися кольцами «Эхо» поверх.
  const facePersona = useContextPersona();

  const [text, setText] = useState(awaitingResponse ? 'Ожидаю ответа…' : '');

  useEffect(() => {
    // Режим «Ожидаю ответа» — фиксированный текст без анимации печатания (кольца остаются)
    // eslint-disable-next-line react-hooks/set-state-in-effect -- анимация «печатания» текста таймерами
    if (awaitingResponse) { setText('Ожидаю ответа…'); return; }
    // При reduced-motion — статичная подпись без анимации печати
    if (reduced) { setText(pickVerb() + '…'); return; }
    let timer = 0;
    let verb = pickVerb();
    let shown = '';
    let phase: 'typing' | 'pausing' | 'deleting' = 'typing';
    const tick = () => {
      const full = verb + '…';
      if (phase === 'typing') {
        shown = full.slice(0, shown.length + 1);
        setText(shown);
        if (shown.length >= full.length) { phase = 'pausing'; timer = window.setTimeout(tick, 1700); }
        else timer = window.setTimeout(tick, 55 + Math.random() * 50);
      } else if (phase === 'pausing') {
        phase = 'deleting';
        timer = window.setTimeout(tick, 35);
      } else {
        shown = shown.slice(0, -1);
        setText(shown);
        if (shown.length === 0) { verb = pickVerb(verb); phase = 'typing'; timer = window.setTimeout(tick, 260); }
        else timer = window.setTimeout(tick, 26);
      }
    };
    timer = window.setTimeout(tick, 140);
    return () => clearTimeout(timer);
  }, [reduced, awaitingResponse]);

  // Отсчёт идущего инструмента; до первого тика — null, и подпись не встаёт (порог)
  const toolElapsed = useRunningElapsed(activeToolStartedAt, !!activeToolLabel && !awaitingResponse);
  // Порог — от появления вызова: подпись, вставшая до tool_started, на старте не уходит в глаголы
  const tool = waitingToolCaption(activeToolLabel, toolElapsed, activeToolTimer, !!awaitingResponse,
    captionLeadMs(activeToolStartedAt, activeToolAppearedAt));

  // Печатная машинка по делу (вариант D): по кругу «подпись → счётчик этапа», стирая и печатая
  // заново; без счётчика подпись впечатывается один раз и стоит. Курсора у инструмента нет —
  // живость несут сама печать, шиммер и кольца. Логика шагов — nextTypewriterStep (lib).
  // Подпись, что уже стояла при монтировании (F5 посреди инструмента), — сразу целиком и держится
  const toolText = tool?.label ?? null;
  const phrases = toolText ? (activeToolDetail ? [toolText, activeToolDetail] : [toolText]) : [];
  // Фразы читаются через ref: секундные тики счётчика не перезапускают цикл
  const phrasesRef = useRef(phrases);
  useEffect(() => { phrasesRef.current = phrases; });
  // Состояние привязано к своей подписи: при её смене до первого шага не мелькает прежняя
  const [tw, setTw] = useState<{ of: string | null; st: TypewriterState }>({ of: toolText, st: TYPEWRITER_AT_REST });
  const twRef = useRef(tw);
  useEffect(() => { twRef.current = tw; });
  const prevToolText = useRef(toolText);
  const hasDetail = !!activeToolDetail;
  useEffect(() => {
    const changed = prevToolText.current !== toolText;
    prevToolText.current = toolText;
    if (!toolText || reduced) return;
    let st = changed ? TYPEWRITER_START : (twRef.current.of === toolText ? twRef.current.st : TYPEWRITER_AT_REST);
    // Подпись стояла одна, а счётчик появился — цикл продолжается с показа
    if (st.phase === 'done' && phrasesRef.current.length > 1) st = { ...st, phase: 'hold' };
    if (changed) setTw({ of: toolText, st });
    let timer = 0;
    const run = (s: TypewriterState) => {
      const delay = typewriterDelay(s);
      if (delay === null) return;
      timer = window.setTimeout(() => {
        const next = nextTypewriterStep(s, phrasesRef.current);
        setTw({ of: toolText, st: next });
        run(next);
      }, delay);
    };
    run(st);
    return () => clearTimeout(timer);
  }, [toolText, hasDetail, reduced]);
  const typedTool = reduced
    ? phrases.join(' · ')
    : tw.of === toolText ? typewriterText(tw.st, phrases) : '';
  // Тихий режим: инструмент идёт, но его карточка на экране — подпись и время не дублируем,
  // вместо них крутятся глаголы с курсором (ход жив, а факты — на карточке)
  const quietTool = !!tool && activeToolOnScreen;
  const OpIcon = activeToolOperation ? OPERATION_ICON[activeToolOperation] : null;

  // Обёртка лица: внешний бокс с вертикальным резервом (ECHO_FACE_H), внутри — аватар
  // 28px по центру с двумя кольцами «Эхо» поверх. Резерв вмещает размах колец, чтобы они
  // не торчали за бокс (см. ECHO_FACE_H). --cc-echo-color на аватар-обёртке задаёт цвет
  // border колец — так одна анимация работает для smoke и plan режимов.
  const faceBox = (inner: React.ReactNode) => (
    <span
      style={{
        position: 'relative', width: ECHO_FACE_W, height: ECHO_FACE_H, flexShrink: 0,
        display: 'inline-flex', alignItems: 'center', justifyContent: 'center',
      }}
    >
      <span
        style={{
          position: 'relative', width: ECHO_FACE_W, height: ECHO_FACE_W, display: 'block',
          // eslint-disable-next-line @typescript-eslint/no-explicit-any
          ['--cc-echo-color' as any]: ringColor,
        }}
      >
        {inner}
        {!reduced && (
          <>
            <span className="cc-echo-ring cc-echo-ring--gutter" />
            <span className="cc-echo-ring cc-echo-ring--2 cc-echo-ring--gutter" />
          </>
        )}
      </span>
    </span>
  );

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 4, minWidth: 0 }}>
      {/* minHeight = бокс лица (ECHO_FACE_H): строка НЕ меняет высоту ни при смене глагола,
          ни в пустой фазе печати. Сам бокс уже вмещает весь размах колец, так что visual
          overflow нулевой — ResizeObserver на contentRef не дёргается, scrollHeight ленты
          стабилен. */}
      <div style={{ display: 'flex', alignItems: 'center', gap: 10, minHeight: ECHO_FACE_H }}>
        {/* Лицо: аватар релевантной персоны, fallback — логотип «AI Home»
            маской (тонируется под тему/режим) */}
        {facePersona ? faceBox(
          <span style={{
            position: 'absolute', inset: 0, display: 'block',
            borderRadius: '50%', overflow: 'hidden',
          }}>
            <PersonaAvatar persona={facePersona} size={28} />
          </span>
        ) : faceBox(
          <span style={{
            display: 'block', width: 28, height: 28, background: pulseColor,
            WebkitMaskImage: `url(${aiHome})`, maskImage: `url(${aiHome})`,
            WebkitMaskRepeat: 'no-repeat', maskRepeat: 'no-repeat',
            WebkitMaskPosition: 'center', maskPosition: 'center',
            WebkitMaskSize: 'contain', maskSize: 'contain',
          }} />
        )}
        {/* Текст + курсор. nowrap + ellipsis: длинный глагол не переносится (высота
            стабильна), а обрезается многоточием — у типичных коротких вариантов
            («Думаю», «Работаю») места хватает на любой ширине. alignItems center, а не
            baseline: в пустой фазе (между глаголами) baseline задаёт один курсор, и
            строку чуть перекашивало по высоте каждый цикл. */}
        {tool && !quietTool ? (
          // Идёт инструмент: [иконка операции] [время] [печатаемый текст]. Иконка и время не
          // сжимаются и стоят на месте (время — фиксированной ширины): стирание текста их не
          // двигает; режется многоточием текст. Тихий режим (карточка видна) — факты не
          // повторяем, а печатаем глаголы: иначе без курсора индикатор выглядел мёртвым
          (
            <span data-waiting-tool="" style={{ display: 'inline-flex', alignItems: 'center', minHeight: 17, minWidth: 0, overflow: 'hidden' }}>
              {OpIcon && (
                <OpIcon size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} color={C.textMuted} style={{ flexShrink: 0, marginRight: SP.xs + 2 }} />
              )}
              {tool.clock && (
                <span data-waiting-clock="" style={{
                  flexShrink: 0, minWidth: WAIT_CLOCK_MIN_W, marginRight: SP.sm, fontSize: FS.sm, color: C.textMuted,
                  whiteSpace: 'nowrap', fontVariantNumeric: 'tabular-nums',
                }}>
                  {tool.clock}
                </span>
              )}
              <span className="cc-shimmer-text" title={phrases.join(' · ')} style={{
                fontSize: 13, fontWeight: 600, fontFamily: 'inherit',
                whiteSpace: 'nowrap', textOverflow: 'ellipsis', overflow: 'hidden', minWidth: 0,
              }}>
                {/* «упало K» — цветом ошибки поверх шиммера (тот же признак, что у карточки) */}
                {typedTool.split(FAILED_RE).map((part, i) => i % 2
                  ? <span key={i} style={{ background: 'none', WebkitTextFillColor: C.dangerText, color: C.dangerText }}>{part}</span>
                  : part)}
              </span>
            </span>
          )
        ) : (
          <span style={{ display: 'inline-flex', alignItems: 'center', minHeight: 17, minWidth: 0, overflow: 'hidden' }}>
            <span className="cc-shimmer-text" style={{
              fontSize: 13, fontWeight: 600, fontFamily: 'inherit',
              whiteSpace: 'nowrap', textOverflow: 'ellipsis', overflow: 'hidden',
            }}>
              {text}
            </span>
            {/* Курсор печатной машинки — только у глаголов */}
            <span style={{
              display: 'inline-block', width: 2, height: '0.95em', marginLeft: 2, flexShrink: 0,
              background: pulseColor, borderRadius: 1, alignSelf: 'center',
              animation: (reduced || awaitingResponse) ? 'none' : 'blink 1s step-start infinite',
            }} />
          </span>
        )}
      </div>
      {hint && (
        <span style={{
          fontSize: 11.5, color: C.textMuted, marginLeft: 38, fontFamily: 'inherit',
          maxWidth: '100%',
        }}>
          {hint}
        </span>
      )}
      {/* Причина ожидания по маркеру `<waiting>` цикла «до готово»: показываем под основной
          строкой индикатора. Рядом — счётчик тиков, чтобы было видно, что цикл живой, а не
          завис: бэкенд тикает раз в Loop:WaitingTickSeconds (дефолт 5 минут), и N тиков —
          это N интервалов ожидания. Не показываем при ожидании по живой задаче (reason=null). */}
      {waitingReason && (
        <span style={{
          fontSize: 11.5, color: C.textMuted, marginLeft: 38, fontFamily: 'inherit',
          maxWidth: '100%', display: 'inline-flex', alignItems: 'baseline', gap: 6,
        }}>
          <span>ожидание: {waitingReason}</span>
          {waitingTicks && waitingTicks > 0 ? (
            <span style={{ opacity: 0.75 }}>· тик {waitingTicks}</span>
          ) : null}
        </span>
      )}
    </div>
  );
}
