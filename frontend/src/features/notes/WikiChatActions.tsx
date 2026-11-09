// Действия чата фичи «Вики» (S7b, макет llm-wiki-v2 §«Вики в чате»): «Разобрать этот чат»,
// «Открыть хронику», «Откатить всё из этого чата». Точка входа — шапка чата: вклад слота
// 'chat-header-action' в manifest.tsx (тот же способ регистрации, что у SessionSummaryAction).
// За флагом llm-wiki: при выключенном флаге — ни контролов, ни запросов.
//
// Поток. «Разобрать» — analyzeWikiChat: 200 → исход сразу; 202 {running} → опрос
// loadWikiChatAnalyzeStatus (live, без офлайн-кэша) до running:false, показывать last.outcome.
// При монтировании — analyze-status: восстановить «идёт» после перезагрузки; loadWikiChats —
// только за ссылкой хроники (для «идёт» доверяем analyze-status, не списку). «Открыть хронику»
// (OPEN-1) — запрос в стор (requestOpenWikiPage: раздел из session.projectId, путь из списка)
// + переход в «Заметки» существующим каналом 'cc-open-note'; без chroniclePath кнопки нет вовсе.
// «Откатить всё из этого чата» — ConfirmDialog («чат получит пометку „не разбирать“»), REST
// только после подтверждения; sourceRef — нормальная форма «session:{id}» БЕЗ диапазона окна
// (та же, что entrySourceRef в «Журнале»: бэк по карточке накрывает все окна — RefMatches).
//
// Конкурентность (урок S5b/S5e, образец WikiHealth): двойное нажатие — кнопка выключена,
// пока идёт; опрос останавливается при размонтировании и при смене чата; ответ прежнего чата
// не затирает новый — поколение запроса в seq (последняя заявка побеждает).
//
// Заявка заметки «Разобрать в базу» — логика здесь (requestNoteIngest), кнопка на NoteView:
// функция никогда не бросает — любой отказ становится текстом, экран не падает.
//
// Оболочка (WM-2): ряда кнопок больше нет — действия живут ОДНИМ меню «Вики»
// (WikiChatMenu на базе ToolbarOverflowMenu), его же рисует вклад 'wiki-chat-menu'
// (в ядре — шапка, «⋯», карточка). Внешнее открытие — по якорю (open), без своего
// триггера — headless. Итог разбора — человеческим текстом значением пункта «Разобрать»,
// на триггере точка статуса; точка гаснет, когда человек открыл меню (cc_wiki_seen_<chat>
import { useCallback, useEffect, useRef, useState, type CSSProperties } from 'react';
import { BookOpen, Ban, Check, ScrollText, Star, Undo2 } from 'lucide-react';
import {
  C, SP, FLAGS, useFeature,
  analyzeWikiChat, ingestWikiNote, loadWikiChatAnalyzeStatus, loadWikiChats, requestOpenWikiPage, retractWikiSource, setWikiChatMark,
  WikiError, type WikiChatAnalyzeOk, type WikiChatMark, type WikiNoteIngestDto,
} from 'aihome_shell/kit';
import { ConfirmDialog } from '../../components/ui/ConfirmDialog';
import { ToolbarOverflowMenu, type OverflowItem } from '../../components/ToolbarOverflowMenu';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { TASK_HINT, isTaskChat } from './wiki/WikiChatTriageList';
import { analyzeRefusalText, personalSectionRefusalText, retractResultText, wikiErrorText } from './wiki/wikiLabels';
import type { ChatWikiMenuCtx } from '../../lib/subsystems/registryCore';

// Пауза между опросами analyze-status: analyze ждёт исход сам ≤10 с (WikiChatsController),
// 202 значит «долше» — частый опрос не нужен, редкий не успевает за пользователем
const POLL_MS = 4000;

type AnalyzeState =
  | { kind: 'idle' }
  | { kind: 'running' }
  | { kind: 'done'; result: WikiChatAnalyzeOk; finishedAt: string | null }
  | { kind: 'error'; text: string };

// «личный раздел — с Ф2» — строка бэка (WikiIngestService.cs:180), не код: заявка заметки
// отдаёт 200 с outcome:'rejected' и reason. Её показывали как есть — внутренняя ссылка бэка
// на Ф2 ничего не говорит человеку (доработка H5); её текст — в wikiLabels.
const PERSONAL_SECTION_REASON = 'личный раздел — с Ф2';

// Текст результата разбора — по исходу. Кодовые отказы (task_execution, marked_skip, …)
// в этот результат не попадают: они WikiError, их текст — wikiErrorText. Отказ (rejected и
// любой неизвестный исход) — человеческой причиной из wikiLabels, сырая строка бэка сюда не идёт.
export function analyzeResultText(r: WikiChatAnalyzeOk): string {
  switch (r.outcome) {
    case 'applied': return 'Разобрано — записано в вики';
    case 'awaiting_human': return 'Есть спорное — во «Входящих»';
    case 'nothing_new':
    case 'up_to_date': return 'Нового нет';
    default: return analyzeRefusalText(r.reason);
  }
}

// Исходы не-отказа: всё остальное (rejected и будущие значения контракта) — отказ
const KNOWN_OUTCOMES: string[] = ['applied', 'awaiting_human', 'nothing_new', 'up_to_date'];

// Отметка «увидел итог»: finishedAt последнего исхода, ключ — на чат. Любой сбой хранилища —
// как «не видел» (точка горит), исключение наружу не выходит
const seenKey = (sessionId: string) => `cc_wiki_seen_${sessionId}`;
function readSeen(sessionId: string, finishedAt: string | null): boolean {
  if (!finishedAt) return false;
  try { return localStorage.getItem(seenKey(sessionId)) === finishedAt; } catch { return false; }
}
function writeSeen(sessionId: string, finishedAt: string) {
  try { localStorage.setItem(seenKey(sessionId), finishedAt); } catch { /* точка просто останется после перезагрузки */ }
}

// Исход заявки заметки — текстом. packageId в текст не идёт: человек идёт в «Входящие».
// Причина отказа — строка бэка: известные внутренние причины заменяются понятным текстом
// из wikiLabels (личный раздел — H5), чужая строка остаётся как есть: смысла не выдумываем.
function noteIngestText(r: WikiNoteIngestDto): string {
  switch (r.outcome) {
    case 'applied': return 'пакет собран и применён';
    case 'awaiting_human': return 'пакет во «Входящих»: ждёт твоего решения';
    case 'nothing_new': return 'нечего добавить';
    default:
      if (r.reason === PERSONAL_SECTION_REASON) return personalSectionRefusalText;
      return r.reason ? `отказ: ${r.reason}` : 'отказ';
  }
}

// «Разобрать в базу» (S7b0b): никогда не бросает — результат и любой отказ (404 not_found,
// 409 packages_limit/wiki_not_initialized, 400 wiki_note_id) становятся текстом.
// target не передаём: «common» для источника вне проекта — отдельное решение человека (ADR-036 §3).
export async function requestNoteIngest(noteId: string): Promise<string> {
  try {
    return noteIngestText(await ingestWikiNote(noteId));
  } catch (e) {
    return e instanceof WikiError ? wikiErrorText(e) : 'Не удалось разобрать заметку';
  }
}

// Точка на триггере, когда в меню есть сообщение (приём шапкиного «⋯» с точкой срока):
// цвет — цвет статуса; для итога отката — приглушённый. Сама сообщение — внутри пункта
function msgDot(color: string): CSSProperties {
  return {
    position: 'absolute', top: 0, right: 0, width: 8, height: 8, borderRadius: '50%',
    background: color, border: `2px solid ${C.bgPanel}`, pointerEvents: 'none',
  };
}

export function WikiChatMenu({ session, hasMessages, online, isMobile, headless, open, onClose }: ChatWikiMenuCtx) {
  const enabled = useFeature(FLAGS.llmWiki);
  const [state, setState] = useState<AnalyzeState>({ kind: 'idle' });
  const [chroniclePath, setChroniclePath] = useState<string | null>(null);
  const [confirmOpen, setConfirmOpen] = useState(false);
  const [retractBusy, setRetractBusy] = useState(false);
  const [retractText, setRetractText] = useState<string | null>(null);
  // Пометка чата («Важный» / «Не разбирать») берётся из списка отбора (поле mark), как и
  // chroniclePath; у чата исполнения задач пометки не действуют (признак — isTaskChat списка)
  const [mark, setMark] = useState<WikiChatMark | null>(null);
  const [taskChat, setTaskChat] = useState(false);
  const [markBusy, setMarkBusy] = useState(false);
  const [markError, setMarkError] = useState<string | null>(null);
  // Итог (разбор, откат, отказ пометки) человек уже видел — точка на триггере погашена
  const [seen, setSeen] = useState(false);
  // Поколение запросов (образец WikiHealth): ответ и опрос пишут состояние, только если их
  // поколение последнее; смена чата, «Разобрать» и откат поднимают новое — запоздалый ответ
  // прежнего чата нового не затрёт
  const seq = useRef(0);
  // Поколение чата для пометки: «Разобрать»/откат поднимают seq, но пометка от них не зависит —
  // отбрасывается только ответ чата, который уже сменился
  const markGen = useRef(0);
  const timers = useRef<number[]>([]);

  const clearTimers = useCallback(() => {
    for (const t of timers.current) clearTimeout(t);
    timers.current = [];
  }, []);

  // Список чатов — только за chroniclePath своей записи (для «идёт» доверяем analyze-status)
  const fetchChronicle = useCallback((sessionId: string, mine: number) => {
    loadWikiChats()
      .then(items => {
        if (seq.current !== mine) return;
        const rec = items.find(i => i.sessionId === sessionId);
        setChroniclePath(rec?.chroniclePath ?? null);
        setMark(rec?.mark === 'important' || rec?.mark === 'skip' ? rec.mark : null);
        setTaskChat(rec ? isTaskChat(rec) : false);
      })
      .catch(() => { /* ссылки нет — кнопки нет; это не ошибка экрана */ });
  }, []);

  // Опрос после 202: одна отложенная проверка за раз; running → следующая; не-running →
  // последний исход на экран; размонтирование/смена чата снимают таймер (effect cleanup)
  const poll = useCallback((sessionId: string, mine: number) => {
    const t = setTimeout(() => {
      if (seq.current !== mine) return;
      loadWikiChatAnalyzeStatus(sessionId)
        .then(st => {
          if (seq.current !== mine) return;
          if (st.running) { poll(sessionId, mine); return; }
          if (st.last) { setState({ kind: 'done', result: st.last, finishedAt: st.last.finishedAt }); setSeen(readSeen(sessionId, st.last.finishedAt)); }
          else setState({ kind: 'idle' });
          // Хронику создаёт применённый разбор: после 202→опроса путь появляется в списке
          // только сейчас — перечитываем его, как при прямом 200 ниже (иначе «готово» без ссылки)
          if (st.last?.packageIds.length) fetchChronicle(sessionId, mine);
        })
        .catch(() => { if (seq.current === mine) { setState({ kind: 'error', text: 'Не удалось дождаться результата разбора' }); setSeen(false); } });
    }, POLL_MS);
    timers.current.push(t);
  }, [fetchChronicle]);

  useEffect(() => {
    const mine = ++seq.current;
    // Смена чата (и монтирование): состояние прежнего чата не переходит в новый
    // eslint-disable-next-line react-hooks/set-state-in-effect -- сброс и старт загрузки при смене session.id: другого места у них нет
    setState({ kind: 'idle' });
    setChroniclePath(null);
    setMark(null);
    setTaskChat(false);
    setMarkError(null);
    setSeen(false);
    markGen.current += 1;
    setMarkBusy(false);
    setConfirmOpen(false);
    setRetractText(null);
    clearTimers();
    if (!enabled) return;
    loadWikiChatAnalyzeStatus(session.id)
      .then(st => {
        if (seq.current !== mine) return;
        if (st.running) { setState({ kind: 'running' }); poll(session.id, mine); return; }
        if (st.last) { setState({ kind: 'done', result: st.last, finishedAt: st.last.finishedAt }); setSeen(readSeen(session.id, st.last.finishedAt)); }
      })
      .catch(() => { /* сбой чтения статуса — не ошибка экрана: пользователь может начать сам */ });
    fetchChronicle(session.id, mine);
    return clearTimers;
  }, [enabled, session.id, clearTimers, poll, fetchChronicle]);

  const analyze = () => {
    // Двойное нажатие: пока идёт — кнопка выключена и второй клик ничего не шлёт
    if (!online || !hasMessages || state.kind === 'running') return;
    const mine = ++seq.current;
    clearTimers();
    setRetractText(null);
    setState({ kind: 'running' });
    analyzeWikiChat(session.id)
      .then(r => {
        if (seq.current !== mine) return;
        if ('running' in r) { poll(session.id, mine); return; }
        setState({ kind: 'done', result: r, finishedAt: null });
        setSeen(false);
        // Хронику создаёт применённый разбор: путь появляется в списке только после него
        if (r.packageIds.length) fetchChronicle(session.id, mine);
      })
      .catch(e => {
        if (seq.current !== mine) return;
        setState({ kind: 'error', text: e instanceof WikiError ? wikiErrorText(e) : 'Не удалось начать разбор' });
        setSeen(false);
      });
  };

  const openChronicle = () => {
    // Хроника бывает только у проектного дома (ChroniclePath null для внепроективного),
    // поэтому раздел берётся прямо из projectId чата — путь на фронте не разбираем
    if (!chroniclePath || !session.projectId) return;
    // Запрос — в стор вики (единственное место состояния, OPEN-1): смонтированный WikiTab
    // откроет раздел и страницу. Переход в «Заметки» — существующим приёмом: слушатель
    // App на 'cc-open-note' (App.tsx:357) ставит вкладку и пишет адрес #/notes; сам
    // navPush экрана не переключает — экран выбирается по hubTab, а его меняет только App.
    requestOpenWikiPage({ section: `projects/${session.projectId}`, path: chroniclePath });
    window.dispatchEvent(new Event('cc-open-note'));
  };

  // Пометка чата: клик по уже отмеченному снимает (null), по другому — переключает (один cmark,
  // взаимоисключающие). Состояние меняется только по ответу бэка; отказ — текстом, UI прежний
  const toggleMark = (kind: WikiChatMark) => {
    if (!online || markBusy || taskChat) return;
    const mine = markGen.current;
    setMarkBusy(true);
    setMarkError(null);
    setSeen(false);
    setWikiChatMark(session.id, mark === kind ? null : kind)
      .then(next => { if (markGen.current === mine) setMark(next); })
      .catch(e => {
        if (markGen.current !== mine) return;
        setMarkError(e instanceof WikiError ? wikiErrorText(e, 'chat') : 'Не удалось сохранить пометку');
      })
      .finally(() => { if (markGen.current === mine) setMarkBusy(false); });
  };

  // Диалог закрыт (подтвердили или отменили) — только теперь сообщаем наружу: карточка снимает вклад по onClose
  const closeConfirm = () => { setConfirmOpen(false); onClose?.(); };

  const doRetract = () => {
    if (retractBusy) return;
    const mine = ++seq.current; // откат делает идущий/прежний разбор неактуальным — поколение поднимается
    setRetractBusy(true);
    retractWikiSource(`session:${session.id}`)
      .then(r => {
        closeConfirm();
        if (seq.current !== mine) return;
        setRetractText(retractResultText(r, 'чат'));
        setSeen(false);
        setState({ kind: 'idle' });
        setChroniclePath(null);
      })
      .catch(e => {
        closeConfirm();
        if (seq.current !== mine) return;
        setRetractText(e instanceof WikiError ? wikiErrorText(e) : 'Не удалось откатить всё из этого чата');
        setSeen(false);
      })
      .finally(() => setRetractBusy(false));
  };

  // Человек открыл меню: итог увиден. Для исхода с finishedAt отметка переживает перезагрузку;
  // чужой чат не пишем — состояние могло ещё принадлежать прежнему session.id
  const markSeen = () => {
    setSeen(true);
    if (state.kind === 'done' && state.finishedAt) writeSeen(session.id, state.finishedAt);
  };
  // Внешний запрос на открытие (строка «⋯», подменю карточки) — тоже открытие меню
  const prevOpen = useRef(open?.counter ?? 0);
  useEffect(() => {
    if (!open || open.counter === prevOpen.current) return;
    prevOpen.current = open.counter;
    // eslint-disable-next-line react-hooks/set-state-in-effect -- реакция на внешний счётчик открытия: другого места нет
    markSeen();
    // markSeen читает свежее состояние на каждый рендер; эффект — только по счётчику
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open?.counter]);

  if (!enabled) return null;

  const status = state.kind === 'running'
    ? { text: 'разбираю…', color: C.textMuted, raw: null as string | null }
    : state.kind === 'done'
      ? {
          text: analyzeResultText(state.result),
          color: state.result.outcome === 'applied' ? C.success
            : state.result.outcome === 'awaiting_human' ? C.warning
              : state.result.outcome === 'nothing_new' || state.result.outcome === 'up_to_date' ? C.textMuted : C.dangerText,
          raw: KNOWN_OUTCOMES.includes(state.result.outcome) ? null : state.result.reason,
        }
      : state.kind === 'error'
        ? { text: state.text, color: C.dangerText, raw: null as string | null }
        : null;
  const refused = state.kind === 'done' && !KNOWN_OUTCOMES.includes(state.result.outcome);
  // Точка: пока идёт разбор — всегда; итог — пока человек не открыл меню
  const dotColor = state.kind === 'running' ? C.textMuted
    : !seen && (status || retractText || markError) ? (status ? status.color : C.textMuted)
      : null;

  // Действие пункта закрывает меню (ItemRow зовёт onDone после onClick) — наружу сообщаем так же,
  // инлайном в каждом onClick. Пункт с внутренним подтверждением (откат) зовёт onClose только после
  // закрытия диалога: иначе карточка снимет вклад, а с ним и диалог
  const items: OverflowItem[] = [
    {
      key: 'analyze',
      icon: <BookOpen size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />,
      label: refused ? 'Разобрать ещё раз' : 'Разобрать этот чат',
      sublabel: 'сейчас, не дожидаясь ночи',
      // Длинный человеческий итог переносится по словам: nowrap моно-значения съел бы подпись
      value: status ? (
        <span title={status.raw ?? undefined} style={{ color: status.color, display: 'inline-block', maxWidth: 170, whiteSpace: 'normal', textAlign: 'right' }}>
          {status.text}
        </span>
      ) : undefined,
      disabled: !online || !hasMessages || state.kind === 'running',
      onClick: () => { analyze(); onClose?.(); },
    },
    ...(chroniclePath ? [{
      key: 'chronicle',
      icon: <ScrollText size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />,
      label: 'Открыть хронику',
      onClick: () => { openChronicle(); onClose?.(); },
    }] : []),
    {
      key: 'mark-important',
      icon: <Star size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />,
      label: 'Важный',
      sublabel: taskChat ? TASK_HINT : 'ночью — в первую очередь, минуя отбор',
      value: mark === 'important' ? <Check size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} /> : undefined,
      active: mark === 'important',
      separator: true,
      disabled: !online || markBusy || taskChat,
      onClick: () => { toggleMark('important'); onClose?.(); },
    },
    {
      key: 'mark-skip',
      icon: <Ban size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />,
      label: 'Не разбирать',
      sublabel: markError ?? (taskChat ? TASK_HINT : 'ни ночью, ни кнопкой'),
      value: mark === 'skip' ? <Check size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} /> : undefined,
      active: mark === 'skip',
      disabled: !online || markBusy || taskChat,
      onClick: () => { toggleMark('skip'); onClose?.(); },
    },
    {
      key: 'retract',
      icon: <Undo2 size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />,
      label: 'Откатить всё из этого чата',
      sublabel: retractText ?? undefined,
      separator: true,
      danger: true,
      disabled: !online || retractBusy,
      onClick: () => setConfirmOpen(true),
    },
  ];

  return (
    // display:contents — обёртка не меняет раскладку шапки; захват клика по триггеру гасит точку
    <span style={{ display: 'contents' }} data-testid="wiki-chat-menu" onClickCapture={markSeen}>
      <ToolbarOverflowMenu
        isMobile={isMobile}
        title="Вики"
        triggerLabel="Вики"
        triggerIcon={<BookOpen size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
        triggerTitle="Действия «Вики» в этом чате"
        indicator={dotColor ? <span style={msgDot(dotColor)} /> : undefined}
        items={items}
        openTrigger={open}
        renderTrigger={headless ? () => null : undefined}
      />
      {confirmOpen && (
        <ConfirmDialog
          title="Откатить всё из этого чата?"
          subtitle={(
            <>
              <div>Снимется всё, что Хранитель взял из этого чата: хроника и знания. Знание из других источников останется.</div>
              <div style={{ marginTop: SP.xs }}>Чат получит пометку «не разбирать» — иначе следующая ночь разберёт его заново.</div>
            </>
          )}
          confirmLabel="Откатить всё"
          confirmVariant="danger"
          onConfirm={doRetract}
          onCancel={closeConfirm}
        />
      )}
    </span>
  );
}
