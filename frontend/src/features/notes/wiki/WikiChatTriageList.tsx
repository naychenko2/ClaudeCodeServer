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

import { useCallback, useEffect, useRef, useState, type CSSProperties } from 'react';
import { AlertTriangle, Ban, MessageSquare, Star } from 'lucide-react';
import {
  C, FONT, FS, R, SHADOW, SP, useIsMobile,
  WikiError, loadWikiChats, setWikiChatMark,
  type WikiChatMark, type WikiChatTriage,
} from 'aihome_shell/kit';
import { Badge, type BadgeTone } from '../../../components/ui/Badge';
import { Button } from '../../../components/ui/Button';
import { EmptyState } from '../../../components/ui/EmptyState';
import { Notice } from '../../../components/ui/Notice';
import { ICON_SIZE, ICON_STROKE } from '../../../components/ui/icons';
import { countWord, wikiErrorText } from './wikiLabels';

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
  awaiting_human: { tone: 'warning', text: 'ждёт решения во «Входящих»' },
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

function TriageRow({ item, busy, onMark }: {
  item: WikiChatTriage;
  busy: boolean;
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

  return (
    <div
      data-testid="wiki-triage-row"
      style={{
        display: 'flex', flexWrap: 'wrap', gap: SP.sm, alignItems: 'flex-start',
        padding: `${SP.sm}px ${SP.md}px`, borderBottom: `1px solid ${C.borderLight}`,
      }}
    >
      <span style={{ fontFamily: FONT.mono, fontSize: FS.sm, color: C.textSecondary, width: 28, textAlign: 'right', flexShrink: 0, paddingTop: 2 }}>
        {item.score ?? '—'}
      </span>
      <span style={{ flex: 1, minWidth: 0 }}>
        <span style={{ display: 'flex', flexWrap: 'wrap', gap: SP.xs, alignItems: 'center' }}>
          <span style={{ fontSize: FS.base, fontWeight: 600, color: C.textHeading, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', minWidth: 0 }}>
            {title}
          </span>
          {badges.map(b => <Badge key={b.text} tone={b.tone} size="xs">{b.text}</Badge>)}
        </span>
        {item.reason && (
          <span style={{ display: 'block', fontSize: FS.sm, color: C.textSecondary, marginTop: 2, overflowWrap: 'break-word' }}>
            <b style={{ color: C.textMuted, fontWeight: 600 }}>Почему:</b> {item.reason}
          </span>
        )}
        {metaParts.length > 0 && (
          <span style={{ display: 'flex', flexWrap: 'wrap', gap: SP.sm, marginTop: 3, fontSize: FS.xs, color: C.textMuted, fontFamily: FONT.mono }}>
            {metaParts.map(p => <span key={p}>{p}</span>)}
          </span>
        )}
      </span>
      {/* на 360 пометки — своей строю под содержимым (макет .app.m .marks) */}
      <span style={{ display: 'flex', gap: SP.xs, ...(isMobile ? { width: '100%' } : { flexShrink: 0 }) }}>
        {markButton('important')}
        {markButton('skip')}
      </span>
    </div>
  );
}

// ─── Блок «Отбор чатов» ───

export function WikiChatTriageList() {
  const [items, setItems] = useState<WikiChatTriage[] | null>(null);
  const [loadError, setLoadError] = useState<unknown>(null);
  const [actionError, setActionError] = useState<string | null>(null);
  // Одна идущая пометка: вторая раньше ответа первой показала бы состояние, которого
  // сервер ещё не подтвердил (пометки на чате — одно поле, последняя побеждает)
  const [busy, setBusy] = useState(false);
  // Поколение загрузок (урок S5b/S5e, образец — loadSeq в WikiJournal): только последний
  // запрос пишет список
  const loadSeq = useRef(0);

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
          <div style={{ border: `1px solid ${C.border}`, borderRadius: R.md, background: C.bgPanel, boxShadow: SHADOW.card, overflow: 'hidden', marginTop: SP.md }}>
            <div style={{ display: 'flex', gap: SP.sm, alignItems: 'center', flexWrap: 'wrap', padding: `${SP.sm}px ${SP.md}px`, borderBottom: `1px solid ${C.border}`, background: C.bgInset }}>
              <b style={{ color: C.textHeading, fontSize: FS.md, flex: 1 }}>{countWord(items.length, 'чат', 'чата', 'чатов')}</b>
              <span style={{ fontSize: FS.xs, color: C.textMuted }}>балл · состояние · причина · пометка</span>
            </div>
            {items.map(it => (
              <TriageRow key={it.sessionId} item={it} busy={busy} onMark={(item, mark) => { void doMark(item, mark); }} />
            ))}
          </div>
          <div style={{ marginTop: SP.sm, fontSize: FS.xs, color: C.textMuted }}>
            «Важный» минует балл и модель, но не исключение чатов исполнителя задач. Закреплённый чат
            и чат с «Итогом» считаются важными.
          </div>
        </>
      )}
    </div>
  );
}
