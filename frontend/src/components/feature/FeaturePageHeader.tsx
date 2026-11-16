import { useEffect, useState } from 'react';
import type { ReactNode } from 'react';
import { Check, GitBranch, ListChecks, ListEnd, ListX, MessageSquare, Pause, Play, RotateCcw, Zap } from 'lucide-react';
import { C, R, FS, SP, FONT, MODAL_W, TB } from '../../lib/design';
import { refetchFeaturePage } from '../../lib/featurePage';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';
import { ToolbarOverflowMenu, type OverflowItem } from '../ToolbarOverflowMenu';
import { Button } from '../ui/Button';
import { Badge } from '../ui/Badge';
import { Modal } from '../ui/Modal';
import { FeatureGlyph } from '../chat/feature/FeatureGlyphs';
import { alarmView } from '../chat/feature/featureTruthModel';
import { formatDuration, queueOrdinalWord, queuePhraseParts } from '../../lib/chatGroups';
import { getPersonaById } from '../../lib/personas';
import { openChatById } from '../../lib/openChat';
import { executorStopVisual, openTaskInSection } from '../../lib/tasks';
import { recordFeatureReturn } from '../../lib/featureBackNav';
import { buildNavMarks, type NavMarks } from '../../lib/featureNavMarks';
import { showToast } from '../../lib/toast';
import type { FeatureRun, FeatureState, FeatureAttention, FeatureNow, FeatureQueue, FeatureHold } from '../../types/featureRun';
import { api } from '../../lib/api';
import type { FeatureIndexFeature } from '../../types/featureIndex';
import { NavTargetButton } from './NavTargetButton';
import { FeatureWorkflowConfirm } from './FeatureWorkflowConfirm';
import { routeBlockState, runRouteKind } from './FeatureWorkflowConfirmModel';
import { useTemplateName } from './FeatureProcessGraphSection';
import { analysisLineState } from './FeatureAnalysisModel';
import { minutesSince } from './FeatureStatsBits';
import { useNow } from '../../lib/useNow';
import { markdownToPlain } from '../../lib/markdownPlain';

// Статусный тег живёт в FeatureStatusTag.tsx (W5-F3a): шапка и строка анализа зовут
// его оттуда — модель больше не тянет модуль шапки, цикл FeaturePageHeader ⇄
// FeatureAnalysisModel разомкнут. Рё-экспорт — совместимость для существующих
// потребителей (страница, view исполнения, тесты): их импорты не меняются.
export { featureRunStatusTag, featurePageStatusTag, toRunTag, StatusTag, type FeatureRunTag, type FeatureRunTagTone } from './FeatureStatusTag';

// === Главное действие (макет §1/§2/§11) ===
//
// wait → «Ответить {имя}» (в чат), stopped → «Чат {имя}» (в чат), merge → «Мерж…»
// (действие Ф6: кнопка открывает общую модалку подтверждения, POST /merge).
// Имени нет (персона не резолвится) — нейтральная подпись. Тревоги нет → null.
export interface FeaturePrimaryAction {
  kind: 'chat' | 'merge';
  label: string;
  // Чат, который открывает действие; null — чата нет.
  chatId: string | null;
}

export function featurePrimaryAction(a: FeatureAttention | null | undefined, personaName?: string | null): FeaturePrimaryAction | null {
  if (!a) return null;
  if (a.kind === 'merge') return { kind: 'merge', label: 'Мерж…', chatId: a.linkedSessionId ?? null };
  const name = personaName ?? null;
  if (a.kind === 'stopped') {
    return { kind: 'chat', label: name ? `Чат ${name}` : 'Открыть чат', chatId: a.linkedSessionId ?? null };
  }
  return { kind: 'chat', label: name ? `Ответить ${name}` : 'Ответить', chatId: a.linkedSessionId ?? null };
}

// Имя персоны тревоги (null — не резолвится).
function attentionPersonaName(a: FeatureAttention | null | undefined): string | null {
  if (!a?.personaId) return null;
  return getPersonaById(a.personaId)?.name ?? null;
}

// === Пауза и черновик (W4-HOLD-F2, макет feature-hold-1) ===
//
// Ручки бэка: POST/DELETE /api/tasks/features/{id}/hold (W4-HOLD-B2). Кнопка «Пауза»
// разрешена при состояниях работы; «где кнопки нет»: merging, merged, closed, draft,
// paused. Пункт «Вернуть в черновик» — только когда бэк отдаёт признак «шагов не
// запускалось»; до него пункта нет. Страница обновляется событием агрегата (стор
// featurePage слушает taskChanged) — оптимистично тег не меняем.
// Потолок обёртки тега на 360 (расчёт ширины названия — в комментарии у обёртки).
export const TAG_MAX_W_MOBILE = 64;

const HOLD_ALLOWED: ReadonlySet<FeatureState> = new Set(['running', 'waiting', 'stopped', 'merge_pending', 'not_started', 'idle']);

// Текст тоста 409: причина из ответа бэка (body.error); пустая — фолбэк из спецификации.
function holdErrorText(e: unknown, state: FeatureState): string {
  const err = e as Error & { status?: number; body?: unknown };
  if (err.status === 409) {
    const body = err.body as { error?: string } | undefined;
    if (body?.error) return body.error;
    if (state === 'merging') return 'Мерж уже идёт — его не останавливают посреди';
    if (state === 'draft') return 'У фичи уже запускались шаги — в черновик не вернуть';
    return 'Состояние фичи изменилось — удержание не поставилось';
  }
  return `Не получилось: ${err.message || 'сервер не ответил'}`;
}

// Склонение «шаг/шага/шагов».
function pluralSteps(n: number): string {
  const d = n % 10, h = n % 100;
  if (d === 1 && h !== 11) return 'шаг';
  if (d >= 2 && d <= 4 && (h < 10 || h >= 20)) return 'шага';
  return 'шагов';
}

// Предупреждение в модалке закрытия (дефект a1067076): согласование по числу —
// один шаг «Идет … он не остановится», несколько «Идут … они не остановятся».
function runningWarning(n: number): string {
  return n === 1
    ? `Идет 1 шаг — он не остановится, фича перейдёт в «Закрытые».`
    : `Идут ${n} ${pluralSteps(n)} — они не остановятся, фича перейдёт в «Закрытые».`;
}

// Подсказка пункта ⋯ (макет): «Прервёт N идущих шагов»; для одного — «1 идущий шаг».
function runningHint(n: number): string {
  return n === 1 ? 'Прервёт 1 идущий шаг' : `Прервёт ${n} идущих ${pluralSteps(n)}`;
}

// Строка «{Персона} · {stepRef}» из run.now — единственный доступный источник
// идущих/прерванных шагов: отдельный список прерванных бэк ещё не отдаёт (макет §«Чего нет в API»).
function nowLine(n: FeatureNow): string {
  const name = n.personaId ? getPersonaById(n.personaId)?.name ?? null : null;
  if (name && n.stepRef) return `${name} · ${n.stepRef}`;
  return name ?? n.stepRef ?? n.title;
}

// Постановка паузы (reason manual — пауза человека; «черновик» фронтом не ставится).
async function doHoldPause(fid: string, state: FeatureState, nowCount: number): Promise<void> {
  try {
    await api.tasks.setFeatureHold(fid, 'manual');
    showToast('Фича на паузе', nowCount > 0 ? `Прервано ${nowCount} ${pluralSteps(nowCount)}. Снять паузу — на странице фичи.` : 'Снять паузу — на странице фичи.');
  } catch (e) {
    showToast('Не получилось', holdErrorText(e, state));
  }
}

// Снятие удержания: «Снять паузу» (paused) и «Запустить фичу» (draft) — обе ручки
// DELETE hold, без подтверждения (макет: «Снять паузу» и «Запустить фичу» — без модалки).
// Ответ — {resumed, expired} (вердикт R-HOLD-H): resumed — задачи, которым пауза
// мешала запуститься и теперь разрешены (не очередь автостарта), их и называем, без
// числа (R-HOLD-H2); expired не показываем. Метод хранилища ClearHold сам ничего не
// возобновляет (ClearHold_DoesNotAutoResumeAnything), но ручка DELETE сразу после него
// зовёт ResumeFeatureTurnsAfterHold (TasksController.cs:941) — прерванные ходы
// продолжаются; текст тоста остаётся честным для гейт-множества.
async function doHoldClear(fid: string, state: FeatureState): Promise<void> {
  try {
    // Тело ответа может быть пустым (Ok() без контента — request отдаёт undefined,
    // offline.ts:447): деструктуризация undefined бросила бы TypeError.
    const { resumed } = (await api.tasks.clearFeatureHold(fid)) ?? {};
    const resumedIds = resumed ?? []; // страховка: ответ без тела
    if (state === 'draft') {
      // Бэк не обещает немедного старта конвейера — честный текст до решения
      // по дефекту bec00347 (карточка «фича запущена»).
      showToast('Черновик снят', 'Шаги запустит куратор.');
    } else if (resumedIds.length > 0) {
      showToast('Фича снята с паузы', 'Задачи, которым пауза мешала, снова разрешены.');
    } else {
      showToast('Пауза снята', '');
    }
  } catch (e) {
    showToast('Не получилось', holdErrorText(e, state));
  }
}

// === Очередь фич (волна 6, макет feature-queue-1) ===
//
// Очередь — пауза с причиной queue: все три действия идут на существующие ручки hold.
// «В очередь» — POST hold {queue}; «Вынуть» — POST hold {manual} (переход queue → manual,
// разрез §7: место освобождается, фича стоит на паузе); «Запустить вне очереди» —
// DELETE hold (тот же вызов, что «Снять паузу», но с обязательным подтверждением).
// «В очередь» нет у фичи, которая уже уступила слот или завершена.
const QUEUE_DENIED: ReadonlySet<FeatureState> = new Set(['merge_pending', 'merging', 'merged', 'closed']);

// Порядковое слово позиции — общий словарь очереди (chatGroups.queueOrdinalWord,
// Q5I-1): своей копии списка здесь больше нет, у модалки и блока страницы один источник.
async function doQueueIn(fid: string, state: FeatureState): Promise<void> {
  try {
    await api.tasks.setFeatureHold(fid, 'queue');
    showToast('Фича в очереди', 'Запустится сама, когда освободится место.');
  } catch (e) {
    showToast('Не получилось', holdErrorText(e, state));
  }
}

async function doQueueOut(fid: string, state: FeatureState): Promise<void> {
  try {
    await api.tasks.setFeatureHold(fid, 'manual');
    showToast('Вынута из очереди', 'Фича на паузе. Вернуть в очередь или снять паузу — на странице фичи.');
  } catch (e) {
    showToast('Не получилось', holdErrorText(e, state));
  }
}

async function doRunOutOfQueue(fid: string, state: FeatureState): Promise<void> {
  try {
    const { resumed } = (await api.tasks.clearFeatureHold(fid)) ?? {};
    const n = (resumed ?? []).length;
    showToast('Фича запущена вне очереди', n > 0 ? `Продолжены ${n} ${pluralSteps(n)}, куратор в курсе.` : 'Куратор в курсе.');
  } catch (e) {
    showToast('Не получилось', holdErrorText(e, state));
  }
}

function QueueInModal({ now, busy, onClose, onConfirm }: { now: FeatureNow[]; busy: boolean; onClose: () => void; onConfirm: () => void }) {
  useEffect(() => {
    const el = typeof document !== 'undefined' ? document.querySelector('[data-testid="feature-page-queue-cancel"]') : null;
    (el as { focus?: () => void } | null)?.focus?.();
  }, []);
  return (
    <Modal
      width={MODAL_W.confirm}
      title="Поставить фичу в очередь?"
      onClose={onClose}
      footer={
        <span style={{ display: 'flex', gap: SP.xs }}>
          <Button size="sm" variant="ghost" dataTestId="feature-page-queue-cancel" onClick={onClose}>Отмена</Button>
          <Button size="sm" variant="primary" loading={busy} dataTestId="feature-page-queue-confirm" onClick={onConfirm}>
            {busy ? 'Ставлю в очередь…' : 'В очередь'}
          </Button>
        </span>
      }
    >
      <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
        <div style={{ fontSize: FS.base, color: C.textPrimary }}>
          Идущие шаги прервутся сразу: <b>{now.map(nowLine).join(' и ')}</b>
        </div>
        <div style={{ fontSize: FS.sm, color: C.textSecondary }}>
          Фича встанет в конец очереди и запустится сама, когда до неё дойдёт черёд, — прерванные шаги продолжатся с того же места.
        </div>
      </div>
    </Modal>
  );
}

function QueueBypassModal({ queue, busy, onClose, onConfirm }: { queue: FeatureQueue | null | undefined; busy: boolean; onClose: () => void; onConfirm: () => void }) {
  useEffect(() => {
    const el = typeof document !== 'undefined' ? document.querySelector('[data-testid="feature-page-bypass-cancel"]') : null;
    (el as { focus?: () => void } | null)?.focus?.();
  }, []);
  return (
    <Modal
      width={MODAL_W.confirm}
      title="Запустить вне очереди?"
      onClose={onClose}
      footer={
        <span style={{ display: 'flex', gap: SP.xs }}>
          <Button size="sm" variant="ghost" dataTestId="feature-page-bypass-cancel" onClick={onClose}>Отмена</Button>
          <Button
            size="sm"
            variant="primary"
            leftIcon={<Zap size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
            loading={busy}
            dataTestId="feature-page-bypass-confirm"
            onClick={onConfirm}
          >
            {busy ? 'Запускаю…' : 'Запустить вне очереди'}
          </Button>
        </span>
      }
    >
      <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
        <div style={{ fontSize: FS.base, color: C.textPrimary }}>
          {queue ? `Фича стоит в очереди ${queueOrdinalWord(queue.position)}. ` : 'Фича стоит в очереди. '}
          Запуск в обход очереди поднимет её сразу, <b>сверх числа одновременно идущих фич</b>, заданного для проекта.
        </div>
        <div style={{ fontSize: FS.sm, color: C.textSecondary }}>
          Остальные фичи очереди сохранят порядок. Прерванные шаги продолжатся с того же места.
        </div>
      </div>
    </Modal>
  );
}

// Модалка подтверждения паузы (макет §«Подтверждение паузы»): MODAL_W.confirm,
// кнопки ghost «Отмена» + primary «Поставить на паузу» (не danger: действие обратимо).
// Фокус при открытии — на «Отмена»; Esc закрывает (штатно у Modal).
function HoldConfirmModal({ now, busy, onClose, onConfirm }: { now: FeatureNow[]; busy: boolean; onClose: () => void; onConfirm: () => void }) {
  useEffect(() => {
    const el = typeof document !== 'undefined' ? document.querySelector('[data-testid="feature-page-hold-cancel"]') : null;
    (el as { focus?: () => void } | null)?.focus?.();
  }, []);
  return (
    <Modal
      width={MODAL_W.confirm}
      title="Поставить фичу на паузу?"
      onClose={onClose}
      footer={
        <span style={{ display: 'flex', gap: SP.xs }}>
          <Button size="sm" variant="ghost" dataTestId="feature-page-hold-cancel" onClick={onClose}>Отмена</Button>
          <Button size="sm" variant="primary" loading={busy} dataTestId="feature-page-hold-confirm" onClick={onConfirm}>
            {busy ? 'Ставлю паузу…' : 'Поставить на паузу'}
          </Button>
        </span>
      }
    >
      <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
        <div style={{ fontSize: FS.base, color: C.textPrimary }}>
          Идущие шаги прервутся сразу: <b>{now.map(nowLine).join(' и ')}</b>
        </div>
        <div style={{ fontSize: FS.sm, color: C.textSecondary }}>
          Файлы в worktree останутся как есть. Новые шаги не запустятся, пока вы не снимете паузу, — тогда прерванные продолжатся с того же места.
        </div>
      </div>
    </Modal>
  );
}

// === Закрытие фичи человеком (статусная модель, F2) ===
//
// Ручка бэка — обычная PUT /api/tasks/{epicId} {status:'done'}: гард B2 принимает
// пользовательский JWT (кнопка в UI) и отказывает сервисный токен. Фича уходит в
// группу «Закрытые», состояние «закрыта» — считает проектор (FeatureProjector),
// страница обновляется перезапросом. «Возобновить» — отдельная функция ниже (F3).
// Возвращает текст отказа или null при успехе: модалка при отказе остаётся открытой
// и показывает причину в себе (хвост 1 вердикта F2-R), тост — только дополнение.
async function doCloseFeature(epicId: string): Promise<string | null> {
  try {
    await api.tasks.update(epicId, { status: 'done' });
    showToast('Фича закрыта', 'Эпик отмечён «Готово» — фича в группе «Закрытые».');
    refetchFeaturePage(epicId);
    return null;
  } catch (e) {
    const err = e as Error & { status?: number; body?: unknown };
    const body = err.body as { error?: string } | undefined;
    const msg = body?.error || err.message || 'сервер не ответил';
    showToast('Не получилось', msg);
    return msg;
  }
}

// Возобновление закрытой фичи (статусная модель, F3): та же PUT-ручка, status:'todo'.
// Подтверждения нет — возобновление не разрушительное (решение продукта): файлы,
// чаты и задачи никуда не денутся, проектор сам пересчитает состояние после refetch.
// Гейт тот же, что у закрытия: только владелец проекта (canClose).
async function doResumeFeature(epicId: string): Promise<string | null> {
  try {
    await api.tasks.update(epicId, { status: 'todo' });
    // Без обещания группы: куда фича попадёт после refetch, решает проектор (хвост
    // вердикта bb0a70c7) — тост обещает только смену статуса эпика.
    showToast('Фича возобновлена', 'Эпик снова «В работе».');
    refetchFeaturePage(epicId);
    return null;
  } catch (e) {
    const err = e as Error & { status?: number; body?: unknown };
    const body = err.body as { error?: string } | undefined;
    const msg = body?.error || err.message || 'сервер не ответил';
    showToast('Не получилось', msg);
    return msg;
  }
}

// Модалка подтверждения закрытия: MODAL_W.confirm, «Отмена» (ghost) + «Закрыть»
// (primary — действие конечное, но не опасное: мерж и задачи остаются на месте).
// Фокус при открытии — на «Отмена» (тот же приём, что у модалки паузы).
// При отказе бэка модалка НЕ закрывается — причина показывается в ней (хвост 1).
// Если идут шаги (nowCount > 0) — предупреждение: закрытие эпика их не гасит,
// фича уйдёт в «Закрытые» под работающими шагами (решение продукта, хвост 3).
// Хвост 2 вердикта F2-R: пока запрос «в полёте» (busy), модалку закрыть нельзя —
// крестик, Esc, оверлей и «Отмена» — no-op: диалог не должен закрыться до ответа
// бэка, иначе причина отказа (хвост 1), которая живёт в модалке, исчезает вместе с ней.
function CloseConfirmModal({ title, busy, error, nowCount, onClose, onConfirm }: {
  title: string;
  busy: boolean;
  error?: string | null;
  nowCount: number;
  onClose: () => void;
  onConfirm: () => void;
}) {
  useEffect(() => {
    const el = typeof document !== 'undefined' ? document.querySelector('[data-testid="feature-page-close-cancel"]') : null;
    (el as { focus?: () => void } | null)?.focus?.();
  }, []);
  const tryClose = () => { if (busy) return; onClose(); };
  return (
    <Modal
      width={MODAL_W.confirm}
      title={`Закрыть «${title}»?`}
      onClose={tryClose}
      footer={
        <span style={{ display: 'flex', gap: SP.xs }}>
          <Button size="sm" variant="ghost" dataTestId="feature-page-close-cancel" onClick={tryClose}>Отмена</Button>
          <Button size="sm" variant="primary" loading={busy} dataTestId="feature-page-close-confirm" onClick={onConfirm}>
            {busy ? 'Закрываю…' : 'Закрыть'}
          </Button>
        </span>
      }
    >
      <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
        <div style={{ fontSize: FS.base, color: C.textPrimary }}>
          Эпик будет отмечен «Готово», фича уйдёт в группу «Закрытые».
        </div>
        {nowCount > 0 && (
          <div style={{ fontSize: FS.sm, color: C.textSecondary }} data-testid="feature-page-close-running">
            {runningWarning(nowCount)}
          </div>
        )}
        <div style={{ fontSize: FS.sm, color: C.textSecondary }}>
          Чаты, задачи и файлы останутся на месте. «Возобновить» фичу — отдельный шаг.
        </div>
        {error && (
          <div style={{ fontSize: FS.sm, color: C.dangerText, background: C.dangerBg, border: `1px solid ${C.dangerBorder}`, padding: `${SP.xs}px ${SP.sm}px`, borderRadius: R.sm }} data-testid="feature-page-close-error">
            {error}
          </div>
        )}
      </div>
    </Modal>
  );
}

// === Шапка (Toolbar): тег (1280) · «Пауза» (1280) · главное действие (1280) · меню ⋯ ===
//
// На 360 в шапке только ⋯ (40): тег уходит в мету, главное действие — в блок
// тревоги (макет §11). ⋯ — общий ToolbarOverflowMenu (десктоп — дропдаун,
// мобила — шторка, пункты 44): «Скопировать ветку», «Открыть эпик в «Задачах»»,
// «Открыть ключевой чат».
export interface FeaturePageToolbarProps {
  // null — данных страницы ещё нет (loading): строится по индексу.
  run: FeatureRun | null;
  feature: FeatureIndexFeature | null;
  isMobile: boolean;
  // «Мерж…»: открыть общую модалку подтверждения (Ф6); undefined — без действия.
  onMerge?: () => void;
  // Гейт владельца для «Закрыть фичу» (F2) и «Возобновить» (F3): true — только
  // владелец проекта (гейт считает FeaturePage); бэковый гард — пользовательский
  // JWT. undefined — ни одной кнопки.
  canClose?: boolean;
}

export function FeaturePageToolbar({ run, feature, isMobile, onMerge, canClose }: FeaturePageToolbarProps) {
  const branch = feature?.branch ?? null;
  const keyChat = feature?.keySessionId ?? null;
  const epic = run?.epic ?? null;

  // Копировать ветку (тот же тост, что в мете).
  const copyBranch = () => {
    if (!branch) return;
    if (typeof navigator !== 'undefined' && navigator.clipboard) {
      void navigator.clipboard.writeText(branch).catch(() => {});
    }
    showToast('Ветка скопирована', branch);
  };

  // Пауза (W4-HOLD-F2): «Пауза» — при состояниях работы; при paused главное действие
  // — «Снять паузу»; при draft — «Запустить фичу» (та же DELETE-ручка). На 360
  // «Пауза» — первый пункт шторки ⋯, а не кнопка в ряду (макет A).
  const state = run?.state;
  const fid = run?.featureId ?? feature?.featureId ?? '';
  const now = run?.now ?? [];
  const canPause = state != null && HOLD_ALLOWED.has(state);
  const paused = state === 'paused';
  const draft = state === 'draft';
  const [holdBusy, setHoldBusy] = useState(false);
  const [holdModal, setHoldModal] = useState(false);
  // Очередь: «В очередь…» — модалка только при идущих ходах; «вне очереди» — всегда.
  const queued = run?.hold?.reason === 'queue';
  const canQueue = state != null && !QUEUE_DENIED.has(state) && !queued;
  const [queueModal, setQueueModal] = useState(false);
  const [bypassModal, setBypassModal] = useState(false);
  const runQueueIn = () => {
    setHoldBusy(true);
    void doQueueIn(fid, state!).finally(() => {
      setHoldBusy(false);
      setQueueModal(false);
    });
  };
  const queueInClick = () => {
    if (holdBusy) return;
    if (now.length > 0) { setQueueModal(true); return; }
    runQueueIn();
  };
  const queueOutClick = () => {
    if (holdBusy) return;
    setHoldBusy(true);
    void doQueueOut(fid, state!).finally(() => setHoldBusy(false));
  };
  const runBypass = () => {
    setHoldBusy(true);
    void doRunOutOfQueue(fid, state!).finally(() => {
      setHoldBusy(false);
      setBypassModal(false);
    });
  };

  const runPause = () => {
    setHoldBusy(true);
    void doHoldPause(fid, state!, now.length).finally(() => {
      setHoldBusy(false);
      setHoldModal(false);
    });
  };
  const pauseClick = () => {
    if (holdBusy) return;
    // Идущие ходы — сначала подтверждение (макет §«Подтверждение паузы»); их нет —
    // сразу запрос и тост.
    if (now.length > 0) { setHoldModal(true); return; }
    runPause();
  };
  const clearClick = () => {
    if (holdBusy) return;
    setHoldBusy(true);
    void doHoldClear(fid, state!).finally(() => setHoldBusy(false));
  };

  // Закрытие (F2): только владелец (canClose считает FeaturePage) и только пока
  // эпик не «Готово» — у закрытой фичи кнопки нет (состояние «закрыта» считает
  // проектор, элемент в группе «Закрытые» уже на месте).
  const canCloseNow = !!canClose && !!epic && epic.status !== 'done';
  // Возобновление (F3): тот же гейт владельца, но только у закрытой фичи — закрытость
  // считает состояние проектора (run.state === 'closed'), а не epic.status: у смерженной
  // фичи с закрытым эпиком «Возобновить» нет, иначе тост обещал бы возврат в активные,
  // которого проектор не даст (хвост вердикта bb0a70c7). Подтверждения нет:
  // возобновление не разрушительное (решение продукта).
  const canResumeNow = !!canClose && run?.state === 'closed';
  const [closeBusy, setCloseBusy] = useState(false);
  const [closeModal, setCloseModal] = useState(false);
  // Причина отказа бэка живёт в модалке (хвост 1): при отказе она остаётся открытой;
  // ошибка снимается при закрытии и при новой попытке.
  const [closeError, setCloseError] = useState<string | null>(null);
  const closeClick = () => {
    if (closeBusy) return;
    setCloseError(null);
    setCloseModal(true);
  };
  const closeModalNow = () => {
    setCloseModal(false);
    setCloseError(null);
  };
  const confirmClose = async () => {
    if (closeBusy || !epic) return;
    setCloseBusy(true);
    // try/finally (хвост 3 вердикта F2-R): «в полёте» снимается всегда — и при
    // успехе, и при отказе, и при неожиданном выбросе; иначе кнопка навсегда
    // остаётся locked.
    try {
      const err = await doCloseFeature(epic.id);
      if (err) {
        setCloseError(err);
      } else {
        setCloseModal(false);
      }
    } finally {
      setCloseBusy(false);
    }
  };
  const [resumeBusy, setResumeBusy] = useState(false);
  const resumeClick = async () => {
    if (resumeBusy || !epic) return;
    setResumeBusy(true);
    try {
      await doResumeFeature(epic.id);
    } finally {
      setResumeBusy(false);
    }
  };

  // Главное действие: wait/stopped → в чат исполнителя; merge → «Мерж…» (Ф6: общая
  // модалка, POST /merge); не начата → «Открыть ключевой чат» (ghost, макет §1).
  // При паузе/черновике главное действие — снятие удержания: тревогу бэк гасит, но
  // старыми данными UI обязан пережить и то, и другое — hold важнее.
  const attention = run?.attention ?? null;
  const primary = featurePrimaryAction(attention, attentionPersonaName(attention));
  // «Мерж…» — только когда state === 'merge_pending'; никогда при merging/merged/closed.
  const mergePending = run?.state === 'merge_pending';
  const effectivePrimary = (primary?.kind === 'merge' && !mergePending) ? null : primary;
  // У фичи в очереди главной кнопки нет: «Снять паузу» там был бы запуском вне очереди без предупреждения.
  const holdAction: 'unpause' | 'start' | null = paused && !queued ? 'unpause' : draft ? 'start' : null;
  const showPrimary = !isMobile && (holdAction != null || effectivePrimary != null);
  // Название фичи для плашки «← к фиче {название}» в открытом чате — из пропов
  // компонента, как в блоке тревоги: переход в чат из шапки тоже пишет возврат (FS-FKEY).
  const featureTitle = run?.epic?.title ?? null;
  const onPrimary = () => {
    if (holdAction) { clearClick(); return; }
    if (effectivePrimary?.kind === 'chat' && effectivePrimary.chatId) {
      recordFeatureReturn(fid, { kind: 'chat', id: effectivePrimary.chatId }, featureTitle);
      void openChatById(effectivePrimary.chatId);
      return;
    }
    if (effectivePrimary?.kind === 'merge') {
      onMerge?.();
      return;
    }
  };
  // «Ключевой чат» (F09): отдельная кнопка, не главное действие; запись возврата — как у остальных переходов.
  const openKeyChat = () => {
    if (!keyChat) return;
    recordFeatureReturn(fid, { kind: 'chat', id: keyChat }, featureTitle);
    void openChatById(keyChat);
  };

  // Пункты ⋯: только существующие (ветки/эпика/ключевого чата нет — пункта нет).
  const items: OverflowItem[] = [];
  if (isMobile && canPause) {
    // «Поставить на паузу…» — первый пункт шторки (макет A); подсказка про идущие
    // шаги — только если что-то идёт (N = 0 — строки нет).
    items.push({
      key: 'hold',
      icon: <Pause size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />,
      label: 'Поставить на паузу…',
      sublabel: now.length > 0 ? runningHint(now.length) : undefined,
      onClick: pauseClick,
    });
  }
  if (canQueue) {
    items.unshift({
      key: 'queue-in',
      icon: <ListEnd size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />,
      label: 'В очередь…',
      sublabel: now.length > 0 ? `${runningHint(now.length)} — продолжатся при запуске из очереди` : undefined,
      onClick: queueInClick,
    });
  }
  if (queued) {
    items.push({
      key: 'queue-out',
      icon: <ListX size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />,
      label: 'Вынуть из очереди',
      sublabel: 'Фича встанет на паузу, место в очереди освободится',
      onClick: queueOutClick,
    }, {
      key: 'queue-bypass',
      icon: <Zap size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />,
      label: 'Запустить вне очереди…',
      sublabel: 'В обход порядка и лимита одновременно идущих фич',
      warning: true,
      separator: true,
      onClick: () => { if (!holdBusy) setBypassModal(true); },
    });
  }
  if (branch) {
    items.push({
      key: 'branch',
      separator: queued,
      icon: <GitBranch size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />,
      label: 'Скопировать ветку',
      onClick: copyBranch,
    });
  }
  if (epic) {
    items.push({
      key: 'epic',
      icon: <ListChecks size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />,
      label: 'Открыть эпик в «Задачах»',
      onClick: () => openTaskInSection({ id: epic.id, projectId: epic.projectId ?? undefined }),
    });
  }
  // «Закрыть фичу…» — последний пункт (конечное действие, не первый по тапу).
  // «Возобновить фичу» (F3) — тоже в конце: для закрытой фичи это главное действие,
  // но в ряд на 360 оно не лезет (макет A: на мобиле действия — только в шторке).
  // Видимой кнопки «Закрыть фичу» нет (Ш4): конечное действие живёт только здесь, и на
  // десктопе, и на 360 — рядом с главным действием оно соседствовало с тревогой.
  if (canCloseNow) {
    items.push({
      key: 'close',
      icon: <Check size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />,
      label: 'Закрыть фичу…',
      onClick: closeClick,
    });
  }
  if (canResumeNow && isMobile) {
    items.push({
      key: 'resume',
      icon: <RotateCcw size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />,
      label: 'Возобновить фичу',
      onClick: () => void resumeClick(),
    });
  }
  // На мобиле кнопки «Ключевой чат» в ряду нет (не лезет в 360) — пункт шторки; на десктопе — дубль кнопки.
  if (keyChat) {
    items.push({
      key: 'key-chat',
      icon: <MessageSquare size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />,
      label: 'Открыть ключевой чат',
      onClick: openKeyChat,
    });
  }

  return (
    <>
      {canPause && !isMobile && (
        <span data-testid="feature-page-pause-wrap" style={{ display: 'inline-flex', flexShrink: 0 }}>
          <Button
            size="sm"
            variant="secondary"
            leftIcon={<Pause size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
            loading={holdBusy}
            dataTestId="feature-page-pause"
            title="Остановить идущие шаги фичи"
            onClick={pauseClick}
          >
            {holdBusy ? 'Ставлю паузу…' : 'Пауза'}
          </Button>
        </span>
      )}
      {showPrimary && (
        <span data-testid="feature-page-main-action" style={{ display: 'inline-flex', flexShrink: 0 }}>
          <Button
            size="sm"
            variant={holdAction ? 'primary' : effectivePrimary ? 'primary' : 'ghost'}
            leftIcon={holdAction ? <Play size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} /> : undefined}
            loading={holdBusy}
            dataTestId={holdAction ? 'feature-page-unpause' : undefined}
            onClick={onPrimary}
          >
            {holdAction === 'unpause' ? (holdBusy ? 'Снимаю паузу…' : 'Снять паузу')
              : holdAction === 'start' ? (holdBusy ? 'Запускаю…' : 'Запустить фичу')
              : effectivePrimary?.label}
          </Button>
        </span>
      )}
      {!isMobile && keyChat && (
        <span data-testid="feature-page-key-chat-wrap" style={{ display: 'inline-flex', flexShrink: 0 }}>
          <Button size="xs" variant="ghost" dataTestId="feature-page-key-chat" onClick={openKeyChat}>
            Ключевой чат
          </Button>
        </span>
      )}
      {canResumeNow && !isMobile && (
        <span data-testid="feature-page-resume-wrap" style={{ display: 'inline-flex', flexShrink: 0 }}>
          <Button
            size="sm"
            variant="secondary"
            leftIcon={<RotateCcw size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
            loading={resumeBusy}
            dataTestId="feature-page-resume-btn"
            title="Возобновить фичу: эпик снова «В работе»"
            onClick={() => void resumeClick()}
          >
            {resumeBusy ? 'Возобновляю…' : 'Возобновить'}
          </Button>
        </span>
      )}
      {items.length > 0 && (
        <ToolbarOverflowMenu
          isMobile={isMobile}
          title="Ещё"
          items={items}
        />
      )}
      {holdModal && (
        <HoldConfirmModal now={now} busy={holdBusy} onClose={() => setHoldModal(false)} onConfirm={runPause} />
      )}
      {queueModal && (
        <QueueInModal now={now} busy={holdBusy} onClose={() => setQueueModal(false)} onConfirm={runQueueIn} />
      )}
      {bypassModal && (
        <QueueBypassModal queue={run?.queue} busy={holdBusy} onClose={() => setBypassModal(false)} onConfirm={runBypass} />
      )}
      {closeModal && (
        <CloseConfirmModal
          title={feature?.title ?? epic?.title ?? 'фичу'}
          busy={closeBusy}
          error={closeError}
          nowCount={now.length}
          onClose={closeModalNow}
          onConfirm={confirmClose}
        />
      )}
    </>
  );
}

// === Блок тревоги / «ждёт вас» (макет §2) ===
//
// Единственная тревога страницы (Ш4): стоит под тулбаром, над вкладками — видна на всех
// шести. Рисуется, когда run.attention есть (wait/stopped/merge), когда сторож говорит
// «молчит/стоит/тихо» (analysisLineState) или фича не начата (нейтральный блок «Фича ещё
// не начата»). Тон — alarmView (F2): wait/merge/молчит — warn (warningBg), стоит — stop
// (dangerBg), не начата — bgSelected + рамка. Каркас: R.lg, поля 10 12, gap 9, 13,
// глиф 14 сверху, отступ сверху 10. Действия: главное (на 360; на 1280 оно в
// шапке) + пара «чат · задача» (NavTargetButton; у wait/stopped — только «задача»,
// чат — и есть главное действие).
export interface FeaturePageAlertProps {
  // null — данных страницы ещё нет (loading): тревоги нет.
  run: FeatureRun | null;
  feature: FeatureIndexFeature | null;
  isMobile: boolean;
  // «Мерж…»: открыть общую модалку подтверждения (Ф6); undefined — без действия.
  onMerge?: () => void;
  // Пометки «в архиве»/«задача закрыта» (страница даёт полные, с чатами); без пропа —
  // из самого агрегата.
  marks?: NavMarks;
}

// Глифы «hold» и «draft» (макет feature-hold-1, §«Глифы»): пауза — круг с двумя
// брусками, черновик — пунктирное кольцо. Обводка textMuted: в тёмной теме C.dashed
// сливается с фоном (та же причина, что у глифа absent, замечание M1).
function HoldGlyph({ kind, size }: { kind: 'hold' | 'draft'; size: number }) {
  const v = { width: size, height: size, viewBox: '0 0 16 16', 'aria-hidden': true as const };
  if (kind === 'hold') {
    return (
      <svg {...v}>
        <circle cx="8" cy="8" r="7" fill={C.bgInset} stroke={C.textMuted} strokeWidth="1" />
        <rect x="5.3" y="4.6" width="1.9" height="6.8" rx="0.6" fill={C.textSecondary} />
        <rect x="8.8" y="4.6" width="1.9" height="6.8" rx="0.6" fill={C.textSecondary} />
      </svg>
    );
  }
  return (
    <svg {...v}>
      <circle cx="8" cy="8" r="6.2" fill="none" stroke={C.textMuted} strokeWidth="1.5" strokeDasharray="2.4 2" />
    </svg>
  );
}

// Блок состояния «на паузе»/«черновик» (макет §«Блок состояния»): не тревога —
// bgInset + рамка border / прозрачный + пунктир. Список прерванных шагов — из
// run.now (списка прерванных бэк не отдаёт): N = 0 — строки нет. На 360 главное
// действие («Снять паузу»/«Запустить фичу») — здесь, на 1280 — в шапке.
function HoldStateBlock({ run, feature, isMobile }: { run: FeatureRun; feature: FeatureIndexFeature | null; isMobile: boolean }) {
  const fid = run.featureId ?? feature?.featureId ?? '';
  const now = run.now ?? [];
  const paused = run.state === 'paused';
  const since = run.hold?.since ? new Date(run.hold.since).getTime() : NaN;
  // Возраст паузы — через useNow (механизм живого времени проекта, lib/useNow): Date.now()
  // в ренере флагует react-hooks/purity и морозит «назад» на последнем ренере
  // (хвост RV-K-Q5I-2). Интервал живёт, только пока пауза действительно показывается.
  const live = useNow(60_000, paused && Number.isFinite(since));
  const dur = Number.isFinite(since) ? formatDuration(Math.max(0, Math.round((live - since) / 60000))) : null;
  const [busy, setBusy] = useState(false);
  const act = () => {
    if (busy) return;
    setBusy(true);
    void doHoldClear(fid, run.state).finally(() => setBusy(false));
  };
  const keyChat = feature?.keySessionId ?? run.epic?.linkedSessionId ?? null;

  return (
    <div
      data-testid="feature-page-alarm"
      data-tone={paused ? 'hold' : 'draft'}
      role="status"
      aria-live="polite"
      style={{
        display: 'flex', gap: SP.sm, alignItems: 'flex-start',
        marginTop: SP.md, padding: `${SP.sm}px ${SP.md}px`, borderRadius: R.lg,
        background: paused ? C.bgInset : 'transparent',
        border: paused ? `1px solid ${C.border}` : `1px dashed ${C.textMuted}`,
        fontSize: FS.base, fontFamily: FONT.sans, color: C.textPrimary,
      }}
    >
      <span style={{ paddingTop: SP.xxs, display: 'inline-flex', flexShrink: 0 }}>
        <HoldGlyph kind={paused ? 'hold' : 'draft'} size={14} />
      </span>
      <div style={{ minWidth: 0 }}>
        <div>
          <b>{paused ? `Фича на паузе${dur ? ` ${dur}` : ''}.` : 'Черновик.'}</b>
          <span> {paused ? 'Поставили вы. Новые шаги не запускаются.' : 'Шаги не запускаются — разрез, план и дизайн идут как обычно. Запустите фичу, когда план готов: куратор начнёт конвейер.'}</span>
        </div>
        {paused && now.length > 0 && (
          <div style={{ marginTop: SP.sm, fontSize: FS.sm, color: C.textSecondary }}>
            <div>Прервано {now.length} {pluralSteps(now.length)} — продолжатся с того же места после снятия паузы:</div>
            {now.map(n => <div key={n.taskId}>{nowLine(n)}</div>)}
          </div>
        )}
        {isMobile && run.hold?.reason !== 'queue' && (
          <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.xs, marginTop: SP.sm }}>
            <Button
              size="xs"
              variant="primary"
              style={{ background: C.bgWhite, minHeight: TB.iconHitMobile }}
              loading={busy}
              dataTestId="feature-page-unpause"
              onClick={act}
            >
              {paused ? 'Снять паузу' : 'Запустить фичу'}
            </Button>
            {!paused && keyChat && (
              <Button
                size="xs"
                variant="ghost"
                style={{ background: C.bgWhite, minHeight: TB.iconHitMobile }}
                onClick={() => { void openChatById(keyChat); }}
              >
                Открыть ключевой чат
              </Button>
            )}
          </div>
        )}
      </div>
    </div>
  );
}

// === Блок «в очереди» (Q5I-2, макет feature-queue-1 §«Состояния») ===
//
// Очередь — пауза с причиной 'queue' (state остаётся paused): тон тот же, что у паузы
// (bgInset + рамка border, тревоги нет), различаются глиф и слова. Признак ветки —
// hold.reason, не state: state у очереди тот же paused.
// Позиция — готовая фраза из queuePhraseParts (Q5I-1): потребитель берёт phrase целиком,
// не собирает строку из ordinal/count/sep (урок R-Q5D-D). Нет queue (бэк не отдал) —
// блок без позиции, нулей не показываем (тот же признак, что у queueLabelParts).
// Длительность — тем же приёмом, что возраст паузы в шапке: minutesSince + formatDuration,
// живое значение — через useNow (lib/useNow): у стоящей в очереди фичи событий может не быть
// вовсе, а Date.now() в ренере морозит «назад» на последнем ренере (хвост RV-K-Q5I-2, п.1).
// Нижнюю границу 1 мин здесь не дублируем: formatDuration отдаёт «1 мин» при minutes < 1
// (chatGroups.ts:338) — единственный источник правила (хвост RV-K-Q5I-2, п.2).
// Главного действия в блоке нет: «Запустить вне очереди» живёт в ⋯ (Q5B); «Снять паузу»
// не показываем — это и есть «вне очереди», только без предупреждения (макет).
// Хелпер чистый: «сейчас» приходит параметром, нечистость — в useNow (Date.now() в ренере
// флагует react-hooks/purity; такие места в файле есть вне этого блока — AnalysisTag,
// FeaturePageAlert).
function queueHoldDuration(hold: FeatureHold | null | undefined, nowMs: number): string | null {
  const sinceMin = minutesSince(hold?.since, nowMs);
  return sinceMin != null ? formatDuration(sinceMin) : null;
}

function QueueStateBlock({ run }: { run: FeatureRun }) {
  // Интервал нужен, только когда есть от чего считать (useNow: enabled).
  const live = useNow(60_000, run.hold?.since != null);
  const parts = queuePhraseParts(run.queue);
  const dur = queueHoldDuration(run.hold, live);
  return (
    <div
      data-testid="feature-page-alarm"
      data-tone="queue"
      role="status"
      aria-live="polite"
      style={{
        display: 'flex', gap: SP.sm, alignItems: 'flex-start',
        marginTop: SP.md, padding: `${SP.sm}px ${SP.md}px`, borderRadius: R.lg,
        background: C.bgInset, border: `1px solid ${C.border}`,
        fontSize: FS.base, fontFamily: FONT.sans, color: C.textPrimary,
      }}
    >
      <span style={{ paddingTop: SP.xxs, display: 'inline-flex', flexShrink: 0 }}>
        <FeatureGlyph kind="queue" size={14} />
      </span>
      <div style={{ minWidth: 0 }}>
        <div>
          <b>{parts ? `Фича в очереди: ${parts.phrase}.` : 'Фича в очереди.'}</b>
          <span> {dur ? `Поставили вы ${dur} назад.` : 'Поставили вы.'} Шаги не запускаются.</span>
        </div>
        <div style={{ marginTop: SP.sm, fontSize: FS.sm, color: C.textSecondary }}>
          <div>Запустится сама, когда одна из идущих фич проекта закончится или уйдёт на мерж. Прерванные шаги продолжатся с того же места.</div>
          {run.queue?.position === 1 && (
            <div>Если место освободилось, а фича не запустилась за минуту, запустите её вручную: ⋯ → «Запустить вне очереди».</div>
          )}
        </div>
      </div>
    </div>
  );
}

export function FeaturePageAlert({ run, feature, isMobile, onMerge, marks: marksProp }: FeaturePageAlertProps) {
  // Очередь — отдельный блок состояния (Q5I-2): стоит выше общей паузы, её признак —
  // hold.reason === 'queue' (state при этом остаётся paused, ниже не досчитывается).
  if (run?.hold?.reason === 'queue') {
    return <QueueStateBlock run={run} />;
  }
  // Пауза и черновик — блок состояния вместо тревоги: при paused тревога не рисуется
  // (paused стоит выше stopped и waiting в приоритете бэка, W4-HOLD-B1).
  if (run?.state === 'paused' || run?.state === 'draft') {
    return <HoldStateBlock run={run} feature={feature} isMobile={isMobile} />;
  }
  const a = run?.attention ?? null;
  const marks = marksProp ?? buildNavMarks(run);
  const notStarted = run?.state === 'not_started';
  // «Молчит/стоит» (Ш4): состояния сторожа из analysisLineState переехали сюда из тега
  // шапки и строки анализа — тревога страницы одна, на всех вкладках. Текст — модели
  // строки анализа (своей копии условий нет), тон — alarmView (F2).
  const nowMs = Date.now();
  const lineSt = run ? analysisLineState(run, nowMs) : null;
  // Приоритет: attention говорит словами alarmView; сторож — только если attention нет.
  const silent = !a && lineSt && (lineSt.key === 'stuck' || lineSt.key === 'nudged' || lineSt.key === 'quiet') ? lineSt : null;
  if (!a && !notStarted && !silent) return null;

  // Тон блока — от alarmView: «стоит» — stop (danger), «ждёт вас»/«ждёт согласия»/
  // «молчит» — warn, не начата — нейтральный.
  const alarmState = silent
    ? (silent.key === 'stuck' ? 'stopped' : 'waiting')
    : a?.kind === 'stopped' ? 'stopped' : a?.kind === 'merge' ? 'merge_pending' : 'waiting';
  const name = attentionPersonaName(a);
  const view = silent || a ? alarmView(alarmState, a?.reason ?? null, name, isMobile) : null;
  const tone: 'warn' | 'stop' | 'neutral' = view ? view.tone : 'neutral';
  const bg = tone === 'stop' ? C.dangerBg : tone === 'warn' ? C.warningBg : C.bgSelected;
  const emphasis = tone === 'stop' ? C.dangerText : C.textHeading;

  // Глиф: warn — ромб, stop — квадрат, не начата — нейтральный run.
  const glyphKind = tone === 'stop' ? 'stopped' : tone === 'warn' ? 'wait' : 'run';

  // Длительность: с attention.since до сейчас.
  const sinceMin = a?.since
    ? Math.max(0, Math.round((nowMs - new Date(a.since).getTime()) / 60000))
    : null;
  const dur = sinceMin != null ? formatDuration(sinceMin) : null;

  // Главная фраза: слова alarmView + длительность; детали — отдельной строкой.
  let head: string;
  let reason: string | null = null;
  if (silent) {
    head = silent.badge?.text ?? silent.strong;
    reason = [silent.badge ? silent.strong : '', silent.tail].filter(Boolean).join(' ') || null;
  } else if (a?.kind === 'stopped') {
    head = `${view!.text}${dur ? ` · ${dur}` : ''}`;
    // Дефект 5519f071: причина — формулировка из executorStopVisual, как на карточке
    // и в панели деталей; сырой ключ (feature_held_expired) человеку ничего не говорит.
    // Неизвестный ключ — нейтральная «Исполнение прервано», без ключа.
    reason = executorStopVisual(a.reason).text;
  } else if (a?.kind === 'merge') {
    head = view!.text;
  } else if (a) {
    head = `${view!.text}${dur ? ` · ${dur}` : ''}`;
    // На 360 причина во фразу не лезет (alarmView) — идёт второй строкой.
    reason = isMobile ? markdownToPlain(a.reason ?? '') || null : null;
  } else {
    head = 'Фича ещё не начата';
    reason = 'Запустить её можно из ключевого чата — куратор начнёт с разреза.';
  }

  // Действия: главное (360) + «чат» (только merge) + «задача».
  // «Мерж…» — только когда state === 'merge_pending'; при merging/merged/closed —
  // только пара «чат · задача» (кнопки нет).
  const primary = featurePrimaryAction(a, name);
  const mergePending = run?.state === 'merge_pending';
  const effectivePrimary = (primary?.kind === 'merge' && !mergePending) ? null : primary;
  // Id фичи для точки возврата «страница фичи → чат/задача» (Ф9b).
  const fid = run?.featureId ?? feature?.featureId ?? '';
  // Название фичи (epic.title) — для «← к фиче {название}» в шапке чата (Ф-pl-b).
  const featureTitle = run?.epic?.title ?? null;
  const acts: ReactNode[] = [];
  if (isMobile && effectivePrimary) {
    acts.push(
      <Button
        key="primary"
        size="xs"
        variant="ghostFilled"
        style={{ minHeight: TB.iconHitMobile }}
        onClick={() => {
          if (effectivePrimary.kind === 'chat' && effectivePrimary.chatId) {
            recordFeatureReturn(fid, { kind: 'chat', id: effectivePrimary.chatId }, featureTitle);
            void openChatById(effectivePrimary.chatId);
          }
          else if (effectivePrimary.kind === 'merge') onMerge?.();
        }}
      >
        {effectivePrimary.label}
      </Button>,
    );
  }
  // «Молчит/стоит»: переход в чат шага — главного действия у такой тревоги в шапке нет.
  for (const [i, act] of (silent?.actions ?? []).entries()) {
    if (act.kind !== 'openChat' || !act.sessionId || marks.chatArchived(act.sessionId)) continue;
    const sid = act.sessionId;
    acts.push(
      <Button
        key={`silent-chat-${i}`}
        size="xs"
        variant="secondary"
        style={{ background: C.bgWhite, minHeight: isMobile ? TB.iconHitMobile : undefined }}
        onClick={() => {
          recordFeatureReturn(fid, { kind: 'chat', id: sid }, featureTitle);
          void openChatById(sid);
        }}
      >
        {act.label}
      </Button>,
    );
  }
  if (a?.kind === 'merge' && a.linkedSessionId) {
    acts.push(
      <NavTargetButton
        key="chat"
        kind="chat"
        chatId={a.linkedSessionId}
        chatArchived={marks.chatArchived(a.linkedSessionId)}
        label="чат"
        isMobile={isMobile}
        title="Открыть чат. Страница фичи скроется, вернётся по плашке."
        onOpen={() => {
          recordFeatureReturn(fid, { kind: 'chat', id: a.linkedSessionId! }, featureTitle);
          void openChatById(a.linkedSessionId!);
        }}
      />,
    );
  }
  if (a?.taskId) {
    acts.push(
      <NavTargetButton
        key="task"
        kind="task"
        taskId={a.taskId}
        taskClosed={marks.taskClosed(a.taskId)}
        label="задача"
        isMobile={isMobile}
        title="Открыть задачу. Страница фичи скроется, вернётся по плашке."
        onOpen={() => {
          recordFeatureReturn(fid, { kind: 'task', id: a.taskId! }, featureTitle);
          openTaskInSection({ id: a.taskId!, projectId: run?.epic?.projectId ?? undefined });
        }}
      />,
    );
  }
  // Не начата: «Открыть ключевой чат» (ghost) — на 360 в блоке, на 1280 в шапке.
  if (!a && isMobile) {
    const keyChat = feature?.keySessionId ?? run?.epic?.linkedSessionId ?? null;
    if (keyChat) {
      acts.push(
        <Button
          key="keychat"
          size="xs"
          variant="ghost"
          style={{ background: C.bgWhite, minHeight: TB.iconHitMobile }}
          onClick={() => { void openChatById(keyChat); }}
        >
          Открыть ключевой чат
        </Button>,
      );
    }
  }

  return (
    <div
      data-testid="feature-page-alarm"
      data-tone={tone}
      role="status"
      aria-live="polite"
      style={{
        display: 'flex', gap: SP.sm, alignItems: 'flex-start',
        marginTop: SP.md, padding: `${SP.sm}px ${SP.md}px`, borderRadius: R.lg,
        background: bg, fontSize: FS.base, fontFamily: FONT.sans, color: C.textPrimary,
        ...(tone === 'neutral' ? { border: `1px solid ${C.borderLight}` } : {}),
      }}
    >
      <span style={{ paddingTop: SP.xxs, display: 'inline-flex', flexShrink: 0 }}>
        <FeatureGlyph kind={glyphKind} size={14} />
      </span>
      <div style={{ minWidth: 0 }}>
        <div>
          <b style={{ color: emphasis }}>{head}</b>
          {reason && <span> {reason}</span>}
        </div>
        {acts.length > 0 && (
          <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.xs, marginTop: SP.sm }}>{acts}</div>
        )}
      </div>
    </div>
  );
}

// Подтверждение маршрута в шапке страницы (C3b): у эпика с предложением или подтверждённым
// маршрутом — блок C2 в тихом режиме (рисуется, только когда нужен ответ человека или
// «шаблон изменился»). Легаси-эпик (ни предложения, ни маршрута) — блока нет вовсе, каталог
// шаблонов для него не запрашивается.
export function hasWorkflowRoute(run: FeatureRun | null | undefined): boolean {
  const epic = run?.epic;
  return !!epic && routeBlockState(epic.routeRequired, epic.workflowProposal, epic.workflow) !== null;
}

// Состояния run, у которых маршрут уже не нужен: «Выбрать шаблон» у них бессмысленно.
const ROUTE_FINISHED: readonly FeatureState[] = ['merged', 'closed'];

// Легаси-строка показывается, пока фича жива: у влитой/закрытой выбирать шаблон поздно.
export function showLegacyRouteRow(run: FeatureRun | null | undefined): boolean {
  return !!run && runRouteKind(run.epic) === 'legacy' && !ROUTE_FINISHED.includes(run.state);
}

// Предложение ждёт человека (F08): есть предложение шаблона и подтверждённого маршрута нет.
export function proposalAwaitsConfirm(run: FeatureRun | null | undefined): boolean {
  return !!run && runRouteKind(run.epic) === 'proposal';
}

// Компактная строка шапки (макет P10/P12): тег, текст и одна кнопка раскрытия.
function RouteHeaderRow({ testId, tone, tag, children, actionLabel, onAction, isMobile }: {
  testId: string;
  tone: 'plan' | 'neutral';
  tag: string;
  children: ReactNode;
  actionLabel: string;
  onAction: () => void;
  isMobile: boolean;
}) {
  return (
    <div
      data-testid={testId}
      style={{
        display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap',
        padding: `${SP.sm}px ${SP.md}px`, borderRadius: R.lg, marginBottom: SP.md,
        fontFamily: FONT.sans, fontSize: FS.sm, color: C.textPrimary,
        background: tone === 'plan' ? C.planLight : C.bgWhite,
        border: `1px solid ${tone === 'plan' ? C.planBorder : C.borderLight}`,
      }}
    >
      <Badge tone={tone === 'plan' ? 'warning' : 'neutral'} size="xs">{tag}</Badge>
      <span style={{ flex: '1 1 220px', minWidth: 0 }}>{children}</span>
      <Button
        variant={tone === 'plan' ? 'primary' : 'ghost'} size={isMobile ? 'sm' : 'xs'}
        fullWidth={isMobile} style={isMobile ? { minHeight: TB.iconHitMobile } : undefined}
        dataTestId={`${testId}-action`} onClick={onAction}
      >
        {actionLabel}
      </Button>
    </div>
  );
}

// Строка предложения: имя шаблона берётся из каталога (пока нет — id).
function ProposalRow({ run, onOpen, isMobile }: { run: FeatureRun; onOpen: () => void; isMobile: boolean }) {
  const proposal = run.epic?.workflowProposal ?? null;
  const name = useTemplateName(proposal?.templateId, proposal?.scope, run.epic?.projectId ?? undefined);
  const label = name || proposal?.templateId || 'шаблон';
  return (
    <RouteHeaderRow
      testId="feature-page-route-proposal" tone="plan" tag="ждёт вас"
      actionLabel="Посмотреть и подтвердить" onAction={onOpen} isMobile={isMobile}
    >
      Модель предлагает «{label}». Пока не подтвердите, исполнители не стартуют.
    </RouteHeaderRow>
  );
}

// Подтверждение маршрута в шапке: предложение — компактная строка, «Посмотреть и подтвердить»
// раскрывает блок F03 (open держит страница: «Подтвердить» из области этапа открывает то же);
// легаси — строка «старая фича» с «Выбрать шаблон»; прочие состояния — блок в тихом режиме.
export function FeaturePageWorkflowConfirm({ run, featureId, projectId, isMobile, open = false, onOpen, onSheetClosed }: {
  run: FeatureRun | null;
  featureId: string;
  projectId?: string;
  isMobile: boolean;
  open?: boolean;
  onOpen?: () => void;
  // Шторку «Маршрут фичи» закрыли — страница снимает флаг запроса на открытие (R-F05).
  onSheetClosed?: () => void;
}) {
  if (!run) return null;
  const block = (quiet: boolean) => (
    <FeatureWorkflowConfirm
      quiet={quiet}
      featureId={featureId}
      projectId={projectId}
      proposal={run.epic?.workflowProposal ?? null}
      confirmed={run.epic?.workflow ?? null}
      routeRequired={run.epic?.routeRequired ?? null}
      isMobile={isMobile}
      sheetOpenRequest={open}
      onSheetClosed={onSheetClosed}
    />
  );
  // Мобила (F05): предложение — компактная строка блока и шторка «Маршрут фичи», а не ProposalRow.
  if (proposalAwaitsConfirm(run) && isMobile) {
    return <div data-testid="feature-page-workflow-confirm">{block(true)}</div>;
  }
  if (proposalAwaitsConfirm(run) && !open) {
    return (
      <div data-testid="feature-page-workflow-confirm">
        <ProposalRow run={run} onOpen={() => onOpen?.()} isMobile={isMobile} />
      </div>
    );
  }
  if (showLegacyRouteRow(run)) {
    return (
      <div data-testid="feature-page-workflow-confirm">
        {open ? (
          <div style={{ marginBottom: SP.md }}>{block(false)}</div>
        ) : (
          <RouteHeaderRow
            testId="feature-page-route-legacy" tone="neutral" tag="старая фича"
            actionLabel="Выбрать шаблон" onAction={() => onOpen?.()} isMobile={isMobile}
          >
            Создана до шаблонов: маршрута нет, замеры приблизительные.
          </RouteHeaderRow>
        )}
      </div>
    );
  }
  if (!hasWorkflowRoute(run)) return null;
  return <div data-testid="feature-page-workflow-confirm">{block(true)}</div>;
}
