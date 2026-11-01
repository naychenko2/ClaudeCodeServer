// Действия чата фичи «Вики» (S7b, макет llm-wiki-v2 §«Вики в чате»): «Разобрать этот чат»,
// «Открыть хронику», «Откатить всё из этого чата». Точка входа — шапка чата: вклад слота
// 'chat-header-action' в manifest.tsx (тот же способ регистрации, что у SessionSummaryAction).
// За флагом llm-wiki: при выключенном флаге — ни контролов, ни запросов.
//
// Поток. «Разобрать» — analyzeWikiChat: 200 → исход сразу; 202 {running} → опрос
// loadWikiChatAnalyzeStatus (live, без офлайн-кэша) до running:false, показывать last.outcome.
// При монтировании — analyze-status: восстановить «идёт» после перезагрузки; loadWikiChats —
// только за ссылкой хроники (для «идёт» доверяем analyze-status, не списку). «Открыть хронику»
// — событие 'cc-open-wiki-page' с path: слушатель (открытие страницы в вкладке «Вики») —
// следующий шаг, сегодня его нет; без chroniclePath кнопки нет вовсе.
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

import { useCallback, useEffect, useRef, useState } from 'react';
import { BookOpen, ScrollText, Undo2 } from 'lucide-react';
import { C, FS, SP } from '../../lib/design';
import { Button } from '../../components/ui/Button';
import { ConfirmDialog } from '../../components/ui/ConfirmDialog';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { FLAGS, useFeature } from '../../lib/featureFlags';
import {
  analyzeWikiChat, ingestWikiNote, loadWikiChatAnalyzeStatus, loadWikiChats, retractWikiSource,
  WikiError, type WikiChatAnalyzeOk, type WikiNoteIngestDto, type WikiRetractDto,
} from '../../lib/wiki';
import { wikiErrorText } from './wiki/wikiLabels';
import type { ChatHeaderSummaryCtx } from '../../lib/subsystems/registryCore';

// Пауза между опросами analyze-status: analyze ждёт исход сам ≤10 с (WikiChatsController),
// 202 значит «долше» — частый опрос не нужен, редкий не успевает за пользователем
const POLL_MS = 4000;

type AnalyzeState =
  | { kind: 'idle' }
  | { kind: 'running' }
  | { kind: 'done'; result: WikiChatAnalyzeOk }
  | { kind: 'error'; text: string };

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
function noteIngestText(r: WikiNoteIngestDto): string {
  switch (r.outcome) {
    case 'applied': return 'пакет собран и применён';
    case 'awaiting_human': return 'пакет во «Входящих»: ждёт твоего решения';
    case 'nothing_new': return 'нечего добавить';
    default: return r.reason ? `отказ: ${r.reason}` : 'отказ';
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

// Согласование по числу (тот же приём, что stepWord в FeatureProdCardsView):
// 1 / 2–4 / 5+; 11–14 — как 5+. Коротое причастие при «1» согласуется с существительным,
// при остальних — средний род: так оно читается с числительным-подметом.
function isOne(n: number): boolean {
  const m10 = n % 10, m100 = n % 100;
  return m10 === 1 && m100 !== 11;
}
function countWord(n: number, one: string, few: string, many: string): string {
  const m10 = n % 10, m100 = n % 100;
  const w = (m10 >= 2 && m10 <= 4 && (m100 < 10 || m100 >= 20)) ? few : many;
  return `${n} ${isOne(n) ? one : w}`;
}

// Итог отката по источнику — текстом, как в «Журнале» (та же форма частей)
export function retractResultText(r: WikiRetractDto): string {
  const parts = [
    r.revertedPackages.length ? `${countWord(r.revertedPackages.length, 'пакет', 'пакета', 'пакетов')} ${isOne(r.revertedPackages.length) ? 'откатан' : 'откатано'}` : '',
    r.unlinkedPackages.length ? `${r.unlinkedPackages.length} стр. ${isOne(r.unlinkedPackages.length) ? 'очищена' : 'очищено'} от сносок` : '',
    r.removedChronicles.length ? `${countWord(r.removedChronicles.length, 'хроника', 'хроники', 'хроник')} ${isOne(r.removedChronicles.length) ? 'снята' : 'снято'}` : '',
    r.remainingPackages.length ? `${countWord(r.remainingPackages.length, 'пакет', 'пакета', 'пакетов')} ${isOne(r.remainingPackages.length) ? 'остался' : 'осталось'} (другой источник)` : '',
    r.markedSkip ? 'чат помечен «не разбирать»' : '',
  ].filter(Boolean);
  const failed = r.failed.length ? ` Частично: ${r.failed.join('; ')}` : '';
  return `${parts.join(', ') || 'ничего не изменилось'}.${failed}`;
}

export function WikiChatActions({ session, hasMessages, online }: ChatHeaderSummaryCtx) {
  const enabled = useFeature(FLAGS.llmWiki);
  const [state, setState] = useState<AnalyzeState>({ kind: 'idle' });
  const [chroniclePath, setChroniclePath] = useState<string | null>(null);
  const [confirmOpen, setConfirmOpen] = useState(false);
  const [retractBusy, setRetractBusy] = useState(false);
  const [retractText, setRetractText] = useState<string | null>(null);
  // Поколение запросов (образец WikiHealth): ответ и опрос пишут состояние, только если их
  // поколение последнее; смена чата, «Разобрать» и откат поднимают новое — запоздалый ответ
  // прежнего чата нового не затрёт
  const seq = useRef(0);
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
    if (!chroniclePath) return;
    // Именованный контракт для слушателя (открытие страницы вики по пути в вкладке «Вики») —
    // слушателя заводит следующий шаг; здесь — только сигнал с путём из списка
    window.dispatchEvent(new CustomEvent('cc-open-wiki-page', { detail: { path: chroniclePath } }));
  };

  const doRetract = () => {
    if (retractBusy) return;
    const mine = ++seq.current; // откат делает идущий/прежний разбор неактуальным — поколение поднимается
    setRetractBusy(true);
    retractWikiSource(`session:${session.id}`)
      .then(r => {
        setConfirmOpen(false);
        if (seq.current !== mine) return;
        setRetractText(retractResultText(r));
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
          onCancel={() => setConfirmOpen(false)}
        />
      )}
    </div>
  );
}
