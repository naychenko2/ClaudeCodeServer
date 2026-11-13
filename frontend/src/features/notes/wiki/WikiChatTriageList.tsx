// «Отбор чатов» — теневой список чатов с причиной их отбора (S7d-2, макет llm-wiki-v2 #triage&cut):
// зона, балл и причина на каждый свой чат, который посчитал отбор, плюс пометки «Важный» /
// «Не разбирать» в строке (N1b: пометка живёт на чате — setWikiChatMark; зон и порядка списка
// она не меняет, их даст следующая перечитка).
//
// Данные — GET /api/wiki/chats как есть: порядок бэка (по зонам, внутри зоны — по баллу) фронт
// не меняет, причину показывает бэковую: это «причина для человека» из контракта списка.
// Подписи зон — фронтовые, по макету: сырое слово зоны человеку ничего не говорит; пометка
// человека перекрывает посчитанную зону (zoneOf макета, ADR-035 §12а) — строка показывает
// решение человека, а не зону, посчитанную до него. Исключение — действия, требующие
// человека (awaiting_human, rejected_skipped): их бейдж ставится поверх пометки, пометка
// остаётся вторым бейджем (возврат QA-2, дефект 82567033). Пороги, фильтр по разделам и
// «пересчитать» — вне среза 02.11; контейнер «Настройки» и подвкладка — шаг S7d-3.
//
// Состояния: загрузка — скелетон; отказ чтения — плашка с «Повторить»; пустой список — пустое
// состояние. Пометка НЕ оптимистична: строка берёт значение из ответа сервера (оптимизм здесь
// ничего не выигрывает — пометка не меняет зон и порядка, а неверный оптимизм пришлось бы
// откатывать). На время запроса кнопки пометок заблокированы. У чата исполнения задач кнопки
// выключены с подсказкой (сигнал — см. TASK_REASON ниже).
//
// Фильтры и поиск (M1a, wiki-panels-v1-proposal «Отбор чатов» А1): над рамкой — SegmentedControl
// «Ждут вас / Важные / Все» со счётчиками; по умолчанию «Ждут вас», если есть чат в action-зоне
// (тот же ACTION_ZONES), иначе «Все»; выбор не запоминается. Поиск (TextField) — только при >10
// чатов, клиентский, по name ?? sessionId без учёта регистра, вместе с сегментом. Пусто под
// фильтром — EmptyState compact inline с текстом по состоянию; загрузка, отказ и «чатов нет
// вообще» — как раньше.
//
// Рамка и карточка (M1b, план «Рамка»/«Фокус» и П2/П3): рамка с пределом высоты LIST_MAX_H и
// своей прокруткой, шапка закреплена: «Показано N из M» — число равно тому, что под ней
// (иначе шапка лжёт о пустом списке). В карточке нет колонок: балл — справа от строки
// названия, мета — строкой через «·», причина — 2 строки с полным текстом в title. Раскладка —
// по narrow (useWikiNarrow, M0): узко — пометки своим рядом, широко — справа; тач-размеры
// остаются на isMobile. При смене фильтра скролл рамки возвращается в начало.

import { useCallback, useEffect, useRef, useState, type CSSProperties } from 'react';
import { AlertTriangle, Ban, MessageSquare, SearchX, Star } from 'lucide-react';
// TextField — из кита, а не из components/ui/Field: Field.tsx:3 тянет нелистовую цепочку
// VoiceMicButton → useVoiceInput → lib/toast и lib/talkDiag → lib/offline, и remote «Заметок»
// собрал бы вторую копию сторов ядра (дефект c86f08b8, блокёр мержа M1a). Остальные ui-контролы
// этого файла листовые: через них идут только design/breakpoints (Badge.tsx:14-16, Button.tsx:2,
// EmptyState.tsx:2, Notice.tsx:10-12, Segmented.tsx:1), что плагин сборки разрешает
// (modules/notes/guardLists.mjs:99-101).
import {
  C, FONT, FS, LIST_MAX_H, R, SHADOW, SP, TextField, useIsMobile,
  WikiError, loadWikiChats, setWikiChatMark,
  type WikiChatMark, type WikiChatTriage,
} from 'aihome_shell/kit';
import { Badge, type BadgeTone } from '../../../components/ui/Badge';
import { Button } from '../../../components/ui/Button';
import { EmptyState } from '../../../components/ui/EmptyState';
import { Notice } from '../../../components/ui/Notice';
import { SegmentedControl } from '../../../components/ui/Segmented';
import { ICON_SIZE, ICON_STROKE } from '../../../components/ui/icons';
import { wikiErrorText } from './wikiLabels';
import { useWikiNarrow } from './useWikiNarrow';

// ─── Подписи состояний строки ───

type RowMeta = { tone: BadgeTone; text: string };

// Зоны отбора — значения бэкового стора (WikiChatScorer.ZoneTriage/ZoneGray/ZoneSkipped/
// ZoneExcluded + WikiChatIngestService.ZoneNothingNew/ZoneRejectedSkipped/ZoneAwaitingHuman).
// Подписи — по макету (#triage&cut). rejected_skipped — чат, пропущенный через окно после 3
// отказов разбора подряд (C2R-Sh2); awaiting_human — чат, чей разобранный исход — пакет во
// «Входящих», ждущий решения человека (C2R-Sh2b); причина строки — бэковая, показывается как
// у всех зон. awaiting_human и rejected_skipped — действия, требующие человека: их бейдж
// ставится поверх пометки (возврат QA-2, дефект 82567033) — см. ACTION_ZONES.
// Неизвестная зона (значение будущего шага бэка) — нейтральная подпись: строка остаётся,
// смысл ей не выдумывается (тот же приём, что UNKNOWN_OP_TEXT в «Журнале»).
const ZONE_META: Record<string, RowMeta> = {
  triage: { tone: 'success', text: 'разбирается' },
  gray: { tone: 'warning', text: 'серая зона' },
  skipped: { tone: 'neutral', text: 'пропущено' },
  nothing_new: { tone: 'neutral', text: 'нечего добавить' },
  rejected_skipped: { tone: 'danger', text: 'пропущено после 3 отказов' },
  awaiting_human: { tone: 'warning', text: 'ждёт решения в «Решениях»' },
  excluded: { tone: 'neutral', text: 'не разбирается' },
};
const UNKNOWN_ZONE: RowMeta = { tone: 'neutral', text: 'неизвестное состояние' };

// Пометка человека перекрывает посчитанную зону (ADR-035 §12а, zoneOf макета): вместо
// автоматической зоны строка показывает то, что решил человек. Исключение — действия,
// требующие человека (ACTION_ZONES ниже): там пометка не заменяет зону, а становится
// вторым бейджем строки.
const MARK_META: Record<WikiChatMark, RowMeta> = {
  important: { tone: 'accent', text: 'важный' },
  skip: { tone: 'neutral', text: 'не разбирать' },
};

// Действия, требующие человека: пакет во «Входящих» ждёт решения, чат отвергнут после 3
// отказов. Их бейдж важнее пометки (решение продукта, возврат QA-2, дефект 82567033):
// человек не должен пропустить, что Хранитель ждёт его решения именно по этому чату.
const ACTION_ZONES = new Set(['awaiting_human', 'rejected_skipped']);

// Чат исполнения задач — единственный его признак в данных списка: причина ступени 0 бэка,
// литерал в коде бэка (WikiChatScorer.cs:167 «ступень 0: чат исполнения задачи») — отдельного
// поля в DTO нет. Зеркало литерала, не догадка: если бэк сменит формулировку, кнопки останутся
// активными (пометка примется, но на разбор не повлияет) — расхождение видно, а не молчит.
const TASK_REASON = 'ступень 0: чат исполнения задачи';
export const TASK_HINT = 'Чат исполнителя задачи: пометки не действуют, знание приходит итогом задачи';

export const isTaskChat = (i: WikiChatTriage): boolean => i.reason === TASK_REASON;

// ─── Фильтры и поиск (M1a, план «Над рамкой» / «Что видно по умолчанию» / «Поиск») ───

export type TriageFilter = 'awaiting' | 'important' | 'all';

// Что видно по умолчанию: «Ждут вас», если есть хоть один чат в action-зоне (тот же
// ACTION_ZONES), иначе «Все». Выбор не запоминается: в следующий раз человек должен сначала
// увидеть то, что ждёт его решения.
export const defaultFilter = (items: WikiChatTriage[]): TriageFilter =>
  items.some(i => ACTION_ZONES.has(i.zone)) ? 'awaiting' : 'all';

// Счётчики сегментов: «Ждут вас» — action-зоны, «Важные» — пометка человека 'important'
// (N1b). Сегмент с нулём остаётся живым (muted), а не выключенным: он показывает, где такой
// чат мог бы появиться, — завтрашняя картина списка не повод выключать выбор сегодня.
export const triageCounts = (items: WikiChatTriage[]): Record<TriageFilter, number> => ({
  awaiting: items.filter(i => ACTION_ZONES.has(i.zone)).length,
  important: items.filter(i => i.mark === 'important').length,
  all: items.length,
});

// Клиентский фильтр: сегмент плюс поиск по name ?? sessionId без учёта регистра (план:
// «вместе с сегментом»). Порядок бэка не меняется: отфильтрованное сохраняет его.
// Пробельный запрос — не поиск: он не фильтрует.
export const filterTriage = (items: WikiChatTriage[], filter: TriageFilter, query: string): WikiChatTriage[] => {
  const seg = items.filter(i =>
    filter === 'awaiting' ? ACTION_ZONES.has(i.zone)
    : filter === 'important' ? i.mark === 'important'
    : true);
  const q = query.trim().toLowerCase();
  return q ? seg.filter(i => (i.name ?? i.sessionId).toLowerCase().includes(q)) : seg;
};

// Поиск показывается только в длинном списке (план «Поиск»): в коротком все имена видны
// сразу, и поле — лишний контрол над пятью строками.
const SEARCH_MIN_CHATS = 10;

// ─── Шапка рамки (M1b, план «Рамка») ───

// «Показано N из M» — всегда с «из M»: шапка говорит не только сколько показано, но и на
// каком основании считается это число (в макете рамка «Все» — «100 из 100»). Формулировка
// живёт в этих константах: и текст, и рендер (числа в нём — FONT.mono) собираются из них.
const SHOWN_LEAD = 'Показано ';
const SHOWN_MID = ' из ';
export function shownText(shown: number, total: number): string {
  return `${SHOWN_LEAD}${shown}${SHOWN_MID}${total}`;
}

// Бейджи строки. Порядок — «действие поверх пометки» (дефект 82567033): для action-зон
// первым идёт бейдж зоны, пометка — вторым (ряд заголовка flexWrap: непоместившийся
// бейдж уходит на вторую строку, действие остаётся видимым). Обычная зона с пометкой —
// как раньше: один бейдж пометки перекрывает зону (ADR-035 §12а).
const rowBadgesOf = (i: WikiChatTriage): RowMeta[] => {
  if (isTaskChat(i)) return [ZONE_META.excluded];
  const mark = i.mark === 'important' || i.mark === 'skip' ? MARK_META[i.mark] : null;
  const zone = ZONE_META[i.zone] ?? UNKNOWN_ZONE;
  if (mark && ACTION_ZONES.has(i.zone)) return [zone, mark];
  return [mark ?? zone];
};

// Дата последней оценки — dd.mm из triagedAt (DateTimeOffset бэка) в локальном времени;
// нечитаемое — без даты, строка не притворяется свежей
function triagedDate(iso: string): string {
  const d = new Date(iso);
  if (!Number.isFinite(d.getTime())) return '';
  const p = (n: number) => String(n).padStart(2, '0');
  return `${p(d.getDate())}.${p(d.getMonth() + 1)}`;
}

function LoadingSkeleton() {
  const bar = (w: number) => (
    <div key={w} style={{ height: 10, width: `${w}%`, background: C.bgInset, borderRadius: R.sm, margin: `${SP.md}px 0` }} />
  );
  return <div style={{ padding: SP.md, opacity: 0.7 }} aria-hidden>{bar(60)}{bar(85)}{bar(45)}</div>;
}

// ─── Строка списка ───

// Карточка строка (M1b, план «Карточка чата» и П3): без колонок. Строка 1 — название в одну
// строку с многоточием и балл справа («без балла», когда балла нет); строка 2 — бейджи и мета
// через «·»; строка 3 — «Почему» в 2 строки, полный текст в title. Пометки: узко — своим рядом
// под текстом (выровнены по его левому краю), широко — справа карточки. narrow даёт
// useWikiNarrow (замер рамки вызывающим); тач-размеры — по-прежнему на isMobile.
function TriageRow({ item, busy, narrow, onMark }: {
  item: WikiChatTriage;
  busy: boolean;
  narrow: boolean;
  onMark: (item: WikiChatTriage, mark: WikiChatMark | null) => void;
}) {
  const isMobile = useIsMobile();
  const badges = rowBadgesOf(item);
  const task = isTaskChat(item);
  const title = item.name ?? item.sessionId;
  const metaParts = [
    item.triagedAt ? triagedDate(item.triagedAt) : '',
    item.running ? 'разбор идёт' : '',
    item.cursor > 0 ? `разобран до m${item.cursor}` : '',
  ].filter(Boolean);

  // Кнопка-переключатель пометки (макет .mk2): aria-pressed по состоянию, нажатая —
  // accent-заливка для «Важного» и bgSelected для «Не разбирать»; на тач — цель 40 px
  const markButton = (kind: Exclude<WikiChatMark, null>) => {
    const pressed = item.mark === kind;
    const style: CSSProperties = {
      ...(pressed
        ? kind === 'important'
          ? { background: C.accentLight, border: `1px solid ${C.accentMuted}`, color: C.accent }
          : { background: C.bgSelected, border: `1px solid ${C.border}`, color: C.textHeading }
        : {}),
      ...(isMobile ? { height: 'auto', minHeight: 40, padding: `0 ${SP.md}px` } : {}),
    };
    return (
      <Button
        variant="ghost" size="xs"
        dataTestId={`wiki-triage-mark-${kind}-${item.sessionId}`}
        ariaLabel={`${kind === 'important' ? 'Пометить важным' : 'Пометить «не разбирать»'}: ${title}`}
        ariaPressed={pressed}
        title={task ? TASK_HINT : undefined}
        disabled={task || busy}
        leftIcon={kind === 'important'
          ? <Star size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
          : <Ban size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
        style={style}
        // Чат исполнения задач: пометки не действуют — действия у строки нет вовсе
        onClick={task ? undefined : () => onMark(item, pressed ? null : kind)}
      >
        {kind === 'important' ? 'Важный' : 'Не разбирать'}
      </Button>
    );
  };

  const marks = (
    <span style={{ display: 'flex', gap: SP.xs, flexShrink: 0 }}>
      {markButton('important')}
      {markButton('skip')}
    </span>
  );

  return (
    <div
      data-testid="wiki-triage-row"
      style={{
        display: 'flex', gap: SP.md, alignItems: 'flex-start',
        padding: `${SP.sm}px ${SP.md}px`, borderBottom: `1px solid ${C.borderLight}`,
      }}
    >
      <span style={{ flex: 1, minWidth: 0 }}>
        {/* строка 1: название в одну строку с многоточием, балл — справа (П3: колонки балла нет) */}
        <span style={{ display: 'flex', gap: SP.sm, alignItems: 'baseline' }}>
          <span style={{ flex: 1, minWidth: 0, fontSize: FS.base, fontWeight: 600, color: C.textHeading, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
            {title}
          </span>
          <span style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted, flexShrink: 0 }}>
            {item.score === null ? 'без балла' : `балл ${item.score}`}
          </span>
        </span>
        {/* строка 2: бейджи и мета через «·» (FONT.mono FS.xs C.textMuted — по П3) */}
        <span style={{ display: 'flex', flexWrap: 'wrap', gap: SP.xs, alignItems: 'center', marginTop: SP.xs }}>
          {badges.map(b => <Badge key={b.text} tone={b.tone} size="xs">{b.text}</Badge>)}
          {metaParts.length > 0 && (
            <span style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted }}>{metaParts.join(' · ')}</span>
          )}
        </span>
        {/* строка 3: причина в 2 строки, полный текст — в title (обрезанное не потеряно) */}
        {item.reason && (
          <span
            title={item.reason}
            style={{
              display: '-webkit-box', WebkitLineClamp: 2, WebkitBoxOrient: 'vertical', overflow: 'hidden',
              fontSize: FS.sm, color: C.textSecondary, marginTop: SP.xs, overflowWrap: 'break-word',
            }}
          >
            <b style={{ color: C.textMuted, fontWeight: 600 }}>Почему:</b> {item.reason}
          </span>
        )}
        {/* узко: пометки — свой ряд под текстом, выровнен по его левому краю (П3) */}
        {narrow && marks}
      </span>
      {/* широко: пометки — справа карточки */}
      {!narrow && marks}
    </div>
  );
}

// ─── Блок «Отбор чатов» ───

export function WikiChatTriageList() {
  const isMobile = useIsMobile();
  const [items, setItems] = useState<WikiChatTriage[] | null>(null);
  const [loadError, setLoadError] = useState<unknown>(null);
  // Фильтр и запрос (M1a) живут в состоянии и нигде больше: выбор не запоминается —
  // в следующий раз список открывается с дефолтного сегмента (defaultFilter)
  const [filter, setFilter] = useState<TriageFilter | null>(null);
  const [query, setQuery] = useState('');
  const [actionError, setActionError] = useState<string | null>(null);
  // Одна идущая пометка: вторая раньше ответа первой показала бы состояние, которого
  // сервер ещё не подтвердил (пометки на чате — одно поле, последняя побеждает)
  const [busy, setBusy] = useState(false);
  // Поколение загрузок (урок S5b/S5e, образец — loadSeq в WikiJournal): только последний
  // запрос пишет список
  const loadSeq = useRef(0);
  // Узкость меряется по рамке (П1): карточки живут внутри неё и равны её ширине; до первого
  // замера — широкая раскладка (тот же договор, что у useWikiNarrow)
  const [listRef, narrow] = useWikiNarrow<HTMLDivElement>();
  // Тот же узел ещё и для сброса скролла при смене фильтра
  const frameRef = useRef<HTMLDivElement | null>(null);
  const attachFrame = useCallback((node: HTMLDivElement | null) => {
    frameRef.current = node;
    const detach = listRef(node);
    return () => { frameRef.current = null; detach?.(); };
  }, [listRef]);

  const load = useCallback(() => {
    const mine = ++loadSeq.current;
    setLoadError(null);
    loadWikiChats()
      .then(list => { if (loadSeq.current === mine) setItems(list); })
      .catch(e => { if (loadSeq.current === mine) setLoadError(e); });
  }, []);

  useEffect(() => {
    // eslint-disable-next-line react-hooks/set-state-in-effect -- старт загрузки при монтировании блока: другого места старта нет, список живёт в компоненте
    load();
  }, [load]);

  // Смена фильтра (план «Фокус»): скролл рамки — в начало, новый отбор начинается с верхнего
  // края нового списка, а не с середины прежнего
  const chooseFilter = (f: TriageFilter) => {
    setFilter(f);
    if (frameRef.current) frameRef.current.scrollTop = 0;
  };

  const doMark = async (item: WikiChatTriage, mark: WikiChatMark | null) => {
    if (busy) return;
    setBusy(true);
    setActionError(null);
    try {
      const next = await setWikiChatMark(item.sessionId, mark);
      // Пометка живёт на чате, не в сторе отбора: строка берёт значение из ответа; зоны и
      // порядок — дело следующей загрузки, перечитывать весь список здесь нечем
      setItems(prev => (prev ?? []).map(it => (it.sessionId === item.sessionId ? { ...it, mark: next } : it)));
    } catch (e) {
      // Отказ пометки — тот же контракт, что у разбора чата (not_found — «чат», :204-205)
      setActionError(e instanceof WikiError ? wikiErrorText(e, 'chat') : 'Не удалось сохранить пометку');
    } finally {
      setBusy(false);
    }
  };

  if (loadError) {
    const text = loadError instanceof WikiError ? wikiErrorText(loadError) : 'Не удалось прочитать «Отбор чатов»';
    return (
      <div data-testid="wiki-triage-error" style={{ padding: SP.sm }}>
        <Notice tone="danger" icon={AlertTriangle} title={text} action={<Button variant="ghost" size="sm" onClick={load}>Повторить</Button>} />
      </div>
    );
  }

  // Что видно на экране: выбранный сегмент, а если человек не выбирал — дефолтный (M1a);
  // запрос обрезается по пробелам: запрос из одних пробелов — не поиск
  const q = query.trim();
  const activeFilter: TriageFilter = items ? (filter ?? defaultFilter(items)) : 'all';
  const counts = triageCounts(items ?? []);
  const shown = items ? filterTriage(items, activeFilter, q) : [];

  return (
    <div style={{ padding: `${SP.sm}px ${SP.md}px ${SP.md}px` }}>
      <h2 style={{ margin: `${SP.xs}px 0 0`, fontFamily: FONT.serif, fontSize: FS.lg, color: C.textHeading }}>
        Отбор чатов
      </h2>
      <div style={{ fontSize: FS.sm, color: C.textSecondary, marginTop: SP.xs }}>
        Какие разговоры Хранитель разбирает ночью и почему. Ваша пометка «Важный» или «Не разбирать»
        перекрывает автомат; состояния, требующие вашего решения, показываются в любом случае.
      </div>

      {actionError && (
        <div style={{ marginTop: SP.md }}>
          <Notice tone="danger" icon={AlertTriangle} title={actionError} />
        </div>
      )}

      {!items ? <LoadingSkeleton /> : items.length === 0 ? (
        <EmptyState
          icon={<MessageSquare size={ICON_SIZE.xl} strokeWidth={ICON_STROKE} />}
          title="Чатов пока нет"
          subtitle={<span style={{ maxWidth: 360 }}>Чаты появятся здесь после первого разговора. Отбор считает код, он бесплатный и идёт сам.</span>}
        />
      ) : (
        <>
          {/* Над рамкой — сегменты со счётчиками (M1a, план «Над рамкой»): ноль — живой
              (muted) сегмент, не выключенный; на тач — touch-раскладка сегментов */}
          <div style={{ marginTop: SP.md }}>
            <SegmentedControl
              value={activeFilter}
              onChange={chooseFilter}
              touch={isMobile}
              options={[
                { value: 'awaiting', label: `Ждут вас · ${counts.awaiting}`, muted: counts.awaiting === 0 },
                { value: 'important', label: `Важные · ${counts.important}`, muted: counts.important === 0 },
                { value: 'all', label: `Все · ${counts.all}` },
              ]}
            />
          </div>
          {/* Поиск — только в длинном списке (план «Поиск»): в коротком имена видны сразу */}
          {items.length > SEARCH_MIN_CHATS && (
            <div style={{ marginTop: SP.sm }}>
              <TextField value={query} onChange={setQuery} placeholder="Найти чат по названию" inputMode="search" />
            </div>
          )}
          {shown.length === 0 ? (
            /* Пусто под фильтром (план «Состояния»): текст по состоянию. Сегмент «Все» с
               пустым запросом не бывает пустым — последней ветки для него не случается */
            <div style={{ marginTop: SP.md }}>
              <EmptyState
                compact inline
                icon={q
                  ? <SearchX size={ICON_SIZE.lg} strokeWidth={ICON_STROKE} />
                  : <MessageSquare size={ICON_SIZE.lg} strokeWidth={ICON_STROKE} />}
                title={q
                  ? `Ничего не нашлось по «${q}»`
                  : activeFilter === 'awaiting' ? 'Ваших решений не ждёт ни один чат' : 'Важных чатов нет.'}
                subtitle={q || activeFilter === 'awaiting' ? undefined : 'Пометьте звёздочкой то, что нельзя пропустить'}
                action={q ? <Button variant="ghost" size="sm" onClick={() => setQuery('')}>Сбросить поиск</Button> : undefined}
              />
            </div>
          ) : (
            <>
              <div
                data-testid="wiki-triage-list"
                ref={attachFrame}
                style={{
                  border: `1px solid ${C.border}`, borderRadius: R.md, background: C.bgPanel, boxShadow: SHADOW.card,
                  // M1b (П2): список выше предела скроллится внутри рамки и не выталкивает
                  // соседние блоки за нижний край панели; overscrollBehavior — прокрутка списка
                  // не отдаёт колесо странице
                  maxHeight: LIST_MAX_H, overflowY: 'auto', overscrollBehavior: 'contain',
                  marginTop: SP.md,
                }}
              >
                <div
                  data-testid="wiki-triage-header"
                  style={{
                    // закреплённая шапка рамки (П2): счёт виден, пока список скроллится;
                    // фон — inset, строки под ней не просвечивают
                    position: 'sticky', top: 0, background: C.bgInset,
                    padding: `${SP.sm}px ${SP.md}px`, borderBottom: `1px solid ${C.border}`,
                  }}
                >
                  <span style={{ fontSize: FS.sm, color: C.textSecondary }}>
                    {SHOWN_LEAD}<span style={{ fontFamily: FONT.mono }}>{shown.length}</span>{SHOWN_MID}<span style={{ fontFamily: FONT.mono }}>{items.length}</span>
                  </span>
                </div>
                {shown.map(it => (
                  <TriageRow key={it.sessionId} item={it} busy={busy} narrow={narrow} onMark={(item, mark) => { void doMark(item, mark); }} />
                ))}
              </div>
              <div style={{ marginTop: SP.sm, fontSize: FS.xs, color: C.textMuted }}>
                «Важный» минует балл и модель, но не исключение чатов исполнителя задач. Закреплённый чат
                и чат с «Итогом» считаются важными.
              </div>
            </>
          )}
        </>
      )}
    </div>
  );
}
