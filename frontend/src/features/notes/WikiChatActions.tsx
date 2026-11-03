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
// Раскладка (дефект ecec968a): на компактной ширине шапки ряд действий не переносится
// (ChatHeaderBar: isCompact = isMobile || isTablet ⇔ ширина < TABLET_WIDE_MIN), и три
// текстовые кнопки «Вики» переполняют его: на 360 правый край «Откатить всё…» — 565,
// шапка срезает (overflow-x:hidden). Там действия — ОДНА кнопка-меню (ToolbarOverflowMenu:
// боттом-шит/дропдаун по якорю — тот же путь, что у шапкиного «⋯»); логика, статусы,
// тексты и подтверждение — прежние, меняется только оболочка. Статус разбора — значением
// пункта «Разобрать», итог отката — подписью пункта «Откатить»; сигнал, что в меню есть
// сообщение, — точкой на триггере (приём шапкиного «⋯» с точкой срока). При
// ≥ TABLET_WIDE_MIN — прежний ряд.

import { useCallback, useEffect, useRef, useState, type CSSProperties } from 'react';
import { BookOpen, Ban, Check, ScrollText, Star, Undo2 } from 'lucide-react';
import {
  C, FS, SP, FLAGS, useFeature, useWindowWidth, TABLET_WIDE_MIN,
  analyzeWikiChat, ingestWikiNote, loadWikiChatAnalyzeStatus, loadWikiChats, requestOpenWikiPage, retractWikiSource, setWikiChatMark,
  WikiError, type WikiChatAnalyzeOk, type WikiChatMark, type WikiNoteIngestDto,
} from 'aihome_shell/kit';
import { Button } from '../../components/ui/Button';
import { ConfirmDialog } from '../../components/ui/ConfirmDialog';
import { ToolbarOverflowMenu, type OverflowItem } from '../../components/ToolbarOverflowMenu';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { TASK_HINT, isTaskChat } from './wiki/WikiChatTriageList';
import { personalSectionRefusalText, retractResultText, wikiErrorText } from './wiki/wikiLabels';
import type { ChatHeaderSummaryCtx } from '../../lib/subsystems/registryCore';

// Пауза между опросами analyze-status: analyze ждёт исход сам ≤10 с (WikiChatsController),
// 202 значит «долше» — частый опрос не нужен, редкий не успевает за пользователем
const POLL_MS = 4000;

type AnalyzeState =
  | { kind: 'idle' }
  | { kind: 'running' }
  | { kind: 'done'; result: WikiChatAnalyzeOk }
  | { kind: 'error'; text: string };

// «личный раздел — с Ф2» — строка бэка (WikiIngestService.cs:180), не код: заявка заметки
// отдаёт 200 с outcome:'rejected' и reason. Её показывали как есть — внутренняя ссылка бэка
// на Ф2 ничего не говорит человеку (доработка H5); её текст — в wikiLabels.
const PERSONAL_SECTION_REASON = 'личный раздел — с Ф2';

// Текст результата разбора — по исходу. Кодовые отказы (task_execution, marked_skip, …)
// в этот результат не попадают: они WikiError, их текст — wikiErrorText. Неизвестный исход
// (будущее значение контракта) — «отказ» с причиной, если она есть: смысла не выдумываем.
export function analyzeResultText(r: WikiChatAnalyzeOk): string {
  switch (r.outcome) {
    case 'applied': return 'готово';
    case 'awaiting_human': return 'есть спорное — во «Входящих»';
    case 'nothing_new':
    case 'up_to_date': return 'нечего добавить';
    default: return r.reason ? `отказ: ${r.reason}` : 'отказ';
  }
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

export function WikiChatActions({ session, hasMessages, online, compact }: ChatHeaderSummaryCtx) {
  const enabled = useFeature(FLAGS.llmWiki);
  // Тот же порог, что читает сама шапка (ChatHeaderBar:1357 isCompact = isMobile || isTablet
  // ⇔ ширина < TABLET_WIDE_MIN): ниже — ряд действий не переносится, три кнопки «Вики»
  // переполняют его (дефект ecec968a) — оболочка одна кнопка-меню
  // Шапка сама знает, что она сжата (в т.ч. узкая колонка «Стены» при широком окне) — её признак
  // главнее ширины окна; без него (прямой вызов) — по окну
  const windowCompact = useWindowWidth() < TABLET_WIDE_MIN;
  const isCompactHeader = compact ?? windowCompact;
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
          setState(st.last ? { kind: 'done', result: st.last } : { kind: 'idle' });
          // Хронику создаёт применённый разбор: после 202→опроса путь появляется в списке
          // только сейчас — перечитываем его, как при прямом 200 ниже (иначе «готово» без ссылки)
          if (st.last?.packageIds.length) fetchChronicle(sessionId, mine);
        })
        .catch(() => { if (seq.current === mine) setState({ kind: 'error', text: 'Не удалось дождаться результата разбора' }); });
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
        if (st.last) setState({ kind: 'done', result: st.last });
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
        setState({ kind: 'done', result: r });
        // Хронику создаёт применённый разбор: путь появляется в списке только после него
        if (r.packageIds.length) fetchChronicle(session.id, mine);
      })
      .catch(e => {
        if (seq.current !== mine) return;
        setState({ kind: 'error', text: e instanceof WikiError ? wikiErrorText(e) : 'Не удалось начать разбор' });
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
    setWikiChatMark(session.id, mark === kind ? null : kind)
      .then(next => { if (markGen.current === mine) setMark(next); })
      .catch(e => {
        if (markGen.current !== mine) return;
        setMarkError(e instanceof WikiError ? wikiErrorText(e, 'chat') : 'Не удалось сохранить пометку');
      })
      .finally(() => { if (markGen.current === mine) setMarkBusy(false); });
  };

  const doRetract = () => {
    if (retractBusy) return;
    const mine = ++seq.current; // откат делает идущий/прежний разбор неактуальным — поколение поднимается
    setRetractBusy(true);
    retractWikiSource(`session:${session.id}`)
      .then(r => {
        setConfirmOpen(false);
        if (seq.current !== mine) return;
        setRetractText(retractResultText(r, 'чат'));
        setState({ kind: 'idle' });
        setChroniclePath(null);
      })
      .catch(e => {
        setConfirmOpen(false);
        if (seq.current !== mine) return;
        setRetractText(e instanceof WikiError ? wikiErrorText(e) : 'Не удалось откатить всё из этого чата');
      })
      .finally(() => setRetractBusy(false));
  };

  if (!enabled) return null;

  const status = state.kind === 'running'
    ? { text: 'разбираю…', color: C.textMuted }
    : state.kind === 'done'
      ? {
          text: analyzeResultText(state.result),
          color: state.result.outcome === 'applied' ? C.success
            : state.result.outcome === 'awaiting_human' ? C.warning : C.textMuted,
        }
      : state.kind === 'error'
        ? { text: state.text, color: C.dangerText }
        : null;

  // Подтверждение отката — одно и то же в обеих раскладках (модалка — портал в body)
  const confirm = confirmOpen && (
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
      onCancel={() => setConfirmOpen(false)}
    />
  );

  if (isCompactHeader) {
    // Одна кнопка-меню вместо трёх (макет llm-wiki-v2 §13: действия «Вики» в Menu,
    // «Откатить…» цветом C.dangerText — тон даёт ItemRow по флагу danger). Пункты несут
    // те же гарды и тексты, что ряд; статус разбора — значением пункта «Разобрать»,
    // итог отката — подписью пункта «Откатить»; без сообщения точка на триггере не нужна
    const items: OverflowItem[] = [
      {
        key: 'analyze',
        icon: <BookOpen size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />,
        label: 'Разобрать этот чат',
        sublabel: 'сейчас, не дожидаясь ночи',
        value: status ? <span style={{ color: status.color }}>{status.text}</span> : undefined,
        disabled: !online || !hasMessages || state.kind === 'running',
        onClick: analyze,
      },
      ...(chroniclePath ? [{
        key: 'chronicle',
        icon: <ScrollText size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />,
        label: 'Открыть хронику',
        onClick: openChronicle,
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
        onClick: () => toggleMark('important'),
      },
      {
        key: 'mark-skip',
        icon: <Ban size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />,
        label: 'Не разбирать',
        sublabel: markError ?? (taskChat ? TASK_HINT : 'ни ночью, ни кнопкой'),
        value: mark === 'skip' ? <Check size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} /> : undefined,
        active: mark === 'skip',
        disabled: !online || markBusy || taskChat,
        onClick: () => toggleMark('skip'),
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
      <>
        <ToolbarOverflowMenu
          isMobile
          title="Вики"
          triggerLabel="Вики"
          triggerIcon={<BookOpen size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
          triggerTitle="Действия «Вики» в этом чате"
          indicator={status || retractText || markError ? <span style={msgDot(status ? status.color : C.textMuted)} /> : undefined}
          items={items}
        />
        {confirm}
      </>
    );
  }

  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: SP.xs, flexWrap: 'wrap' }}>
      <Button
        variant="secondary" size="sm" dataTestId="wiki-chat-analyze"
        ariaLabel="Разобрать этот чат сейчас, не дожидаясь ночи"
        disabled={!online || !hasMessages || state.kind === 'running'}
        leftIcon={<BookOpen size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
        onClick={analyze}
      >
        Разобрать этот чат
      </Button>
      {chroniclePath && (
        <Button
          variant="ghost" size="sm" dataTestId="wiki-chat-chronicle"
          ariaLabel="Открыть хронику этого чата в вики"
          leftIcon={<ScrollText size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
          onClick={openChronicle}
        >
          Открыть хронику
        </Button>
      )}
      <Button
        variant="ghost" size="sm" dataTestId="wiki-chat-retract"
        ariaLabel="Откатить всё из этого чата"
        disabled={!online || retractBusy}
        leftIcon={<Undo2 size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
        onClick={() => setConfirmOpen(true)}
      >
        Откатить всё из этого чата
      </Button>
      {(['important', 'skip'] as const).map(kind => (
        <Button
          key={kind} variant={mark === kind ? 'secondary' : 'ghost'} size="sm"
          dataTestId={`wiki-chat-mark-${kind}`} ariaPressed={mark === kind}
          title={taskChat ? TASK_HINT : kind === 'important' ? 'Ночью — в первую очередь, минуя отбор' : 'Ни ночью, ни кнопкой'}
          disabled={!online || markBusy || taskChat}
          leftIcon={kind === 'important' ? <Star size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} /> : <Ban size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
          onClick={() => toggleMark(kind)}
        >
          {kind === 'important' ? 'Важный' : 'Не разбирать'}
        </Button>
      ))}
      {markError && (
        <span data-testid="wiki-chat-mark-error" style={{ fontSize: FS.xs, color: C.dangerText, maxWidth: 360 }}>
          {markError}
        </span>
      )}
      {status && (
        <span data-testid="wiki-chat-status" style={{ fontSize: FS.xs, color: status.color, maxWidth: 360 }}>
          {status.text}
        </span>
      )}
      {retractText && (
        <span data-testid="wiki-chat-retract-status" style={{ fontSize: FS.xs, color: C.textMuted, maxWidth: 360 }}>
          {retractText}
        </span>
      )}
      {confirm}
    </div>
  );
}
