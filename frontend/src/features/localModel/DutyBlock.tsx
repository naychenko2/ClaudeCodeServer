// «Дежурство» — последний блок стенда второй модели (Bonsai): почему уступила карту,
// когда вернётся, куда идут места прямо сейчас и доля ответов за час (макет
// docs/mockups/bonsai-toolbar.md, раздел 5). Данные — duty из stats?engine= (контракт Ф0);
// нет поля или null — блока нет. Все подписи строит dutyFormat.ts (тесты — там же).
//
// HTML из строк не подмешиваем нигде: строки арбитра (lease, gpu-команда, неизвестный
// kind) — недоверенные, React рендерит их текстовыми узлами.
//
// Приглушение: «арбитр недоступен» (reachable:false) — приглушено ТОЛЬКО тело блока
// (макет §6): статус модели наверху — по её собственным метрикам, он не приглушается.

import { Fragment, useEffect, useMemo, useState, type CSSProperties } from 'react';
import { ChevronRight } from 'lucide-react';
import { C, FONT, FS, ICON_SIZE, ICON_STROKE, R, SHADOW, SP } from 'aihome_shell/kit';
import type { LocalModelDuty, LocalModelDutyAnswers, LocalModelStats } from './api';
import { formatSince } from './format';
import {
  answersSummary, answersWindowTitle, CHAIN_LABEL, CHAIN_LAST_TITLE, CHAIN_ORDER,
  CHAIN_SUFFIX, currentChainLink, DUTY_MODE_CHIP, dutyCounters, dutyReasonsSummary,
  dutySwitchTimes, dutyWhoRows, formatClockLeft, nextChainHint, type AnswersSegment, type DutyCallRow,
} from './dutyFormat';

// === Оболочка карточки — та же, что у блоков стенда (TextTab.Card) ===
function Card({ title, right, children }: { title: string; right?: React.ReactNode; children?: React.ReactNode }) {
  return (
    <section style={{ background: C.bgWhite, border: `1px solid ${C.borderLight}`, borderRadius: R.xl, padding: SP.md, minWidth: 0 }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: `${SP.xs}px ${SP.sm}px`, marginBottom: SP.sm, minWidth: 0, flexWrap: 'wrap' }}>
        <h3 style={{ margin: 0, fontSize: FS.xs, fontWeight: 700, textTransform: 'uppercase', letterSpacing: '0.06em', color: C.textSecondary, whiteSpace: 'nowrap' }}>{title}</h3>
        {right && <span style={{ marginLeft: 'auto', fontSize: FS.xs, color: C.textMuted, whiteSpace: 'nowrap' }}>{right}</span>}
      </div>
      {children}
    </section>
  );
}

// === Строки «ключ — значение» (спецификация §6) ===
const ROW: CSSProperties = { display: 'flex', justifyContent: 'space-between', gap: SP.md, alignItems: 'baseline', padding: `${SP.xxs}px 0`, minWidth: 0 };
const ROW_K: CSSProperties = { color: C.textMuted, flexShrink: 0, fontSize: FS.sm };
const ROW_V: CSSProperties = { fontFamily: FONT.mono, fontWeight: 600, fontSize: FS.sm, textAlign: 'right', minWidth: 0, overflowWrap: 'anywhere' };
// Текст вне моно (причины, «после паузы»): sans
const ROW_V_TEXT: CSSProperties = { ...ROW_V, fontFamily: FONT.sans };
// Подсказка под значением («если не придёт новое задание», период счётчиков)
const HINT: CSSProperties = { fontSize: FS.xs, color: C.textMuted, textAlign: 'right' };
const SEPR: CSSProperties = { height: 1, background: C.bgInset, margin: `${SP.md}px 0` };

function Row({ k, v, mono }: { k: string; v: React.ReactNode; mono?: boolean }) {
  return (
    <div style={ROW}>
      <span style={ROW_K}>{k}</span>
      <span style={mono ? ROW_V : ROW_V_TEXT}>{v}</span>
    </div>
  );
}

function Hint({ children }: { children: React.ReactNode }) {
  return <div style={HINT}>{children}</div>;
}

// Секционный заголовок внутри карточки («Цепочка», «Ответы мест…»)
function Cap({ children }: { children: React.ReactNode }) {
  return <div style={{ fontSize: FS.xs, textTransform: 'uppercase', letterSpacing: '0.05em', color: C.textMuted, marginBottom: SP.xs }}>{children}</div>;
}

// «→ Медиа»: role="button", Enter/Space; подчёркивание при наведении, SHADOW.focus
// на фокусе (макет §5) — inline-стили не знают :hover/:focus, поэтому состояния
// держат обработчики.
function MediaLink({ onOpen }: { onOpen: () => void }) {
  const [hover, setHover] = useState(false);
  const [focus, setFocus] = useState(false);
  return (
    <span
      role="button"
      tabIndex={0}
      aria-label="Открыть вкладку «Медиа»"
      onClick={onOpen}
      onKeyDown={e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); onOpen(); } }}
      onMouseEnter={() => setHover(true)}
      onMouseLeave={() => setHover(false)}
      onFocus={() => setFocus(true)}
      onBlur={() => setFocus(false)}
      style={{
        color: C.accent, cursor: 'pointer', borderRadius: R.sm, fontWeight: 600, whiteSpace: 'nowrap',
        textDecoration: hover ? 'underline' : 'none', boxShadow: focus ? SHADOW.focus : 'none',
      }}
    >→ Медиа</span>
  );
}

// === Цепочка «Bonsai → страховка → облако» ===
// Текущее звено — куда пойдёт СЛЕДУЮЩИЙ вызов места (currentChainLink): фон accentLight,
// рамка accent, текст textHeading 700; пройденные — textMuted; следующие — textSecondary.
// На узкой ширине (мобильная раскладка) плашки идут в столбик, стрелка поворачивается.
function ChainBlock({ status, isMobile }: { status: LocalModelStats['status']; isMobile: boolean }) {
  const link = currentChainLink(status);
  const cur = CHAIN_ORDER.indexOf(link);
  return (
    <>
      <div style={{
        display: 'flex', alignItems: 'center', gap: SP.xs, margin: `${SP.xs}px 0`,
        flexDirection: isMobile ? 'column' : 'row', flexWrap: isMobile ? 'nowrap' : 'wrap',
      }}>
        {CHAIN_ORDER.map((chainLink, i) => {
          const state = i === cur ? 'cur' : i < cur ? 'past' : 'next';
          return (
            <Fragment key={chainLink}>
              {i > 0 && (
                <ChevronRight
                  size={ICON_SIZE.xs} strokeWidth={ICON_STROKE}
                  style={{ color: C.textMuted, flexShrink: 0, transform: isMobile ? 'rotate(90deg)' : undefined }}
                />
              )}
              <span
                {...(chainLink === 'claude' ? { title: CHAIN_LAST_TITLE } : {})}
                style={{
                  border: `1px solid ${state === 'cur' ? C.accent : C.border}`,
                  borderRadius: R.sm, padding: `${SP.xxs}px ${SP.sm}px`, fontSize: FS.xs, whiteSpace: 'nowrap',
                  background: state === 'cur' ? C.accentLight : 'transparent',
                  color: state === 'cur' ? C.textHeading : state === 'past' ? C.textMuted : C.textSecondary,
                  fontWeight: state === 'cur' ? 700 : 400,
                }}
              >
                {CHAIN_LABEL[chainLink]}
                {CHAIN_SUFFIX[chainLink] && <small style={{ color: C.textMuted, fontWeight: 400 }}>{CHAIN_SUFFIX[chainLink]}</small>}
              </span>
            </Fragment>
          );
        })}
      </div>
      <div style={{ fontSize: FS.xs, color: C.textMuted }}>{nextChainHint(link)}</div>
    </>
  );
}

// === Ответы мест за час ===
const SEG_COLOR: Record<AnswersSegment['key'], string> = {
  bonsai: C.success, insurance: C.info, claude: C.accent, missed: C.danger,
};

// nowMs — момент получения снимка (якорь DutyBlock), не плавающее «сейчас»:
// окно доли и его подпись должны относиться к тем же данным, что рисуются
function AnswersBlock({ answers, nowMs }: { answers: LocalModelDutyAnswers | null; nowMs: number }) {
  const { total, segments } = answersSummary(answers);
  if (total === 0 || answers == null) {
    // ни данных (блок null), ни одного ответа за окно — «—», а не NaN и не пустая полоса
    return (
      <>
        <Row k="Кто ответил" v="—" mono />
        <Hint>{answers == null ? 'Данных пока нет.' : 'Ни одного ответа за окно.'}</Hint>
      </>
    );
  }
  const title = answersWindowTitle(answers, nowMs);
  return (
    <>
      {/* полоса 6 px, R.max (спецификация §6); нулевой сегмент полосой не рисуется */}
      <div style={{ height: 6, borderRadius: R.max, background: C.track, overflow: 'hidden', display: 'flex', margin: `${SP.xs}px 0` }}>
        {segments.map(s => s.widthPerc != null && (
          <span key={s.key} style={{ display: 'block', height: '100%', width: `${s.widthPerc}%`, background: SEG_COLOR[s.key] }} />
        ))}
      </div>
      {/* легенда — все четыре корзины, нулевые с числом 0 */}
      <div style={{ display: 'flex', gap: `${SP.xs}px ${SP.md}px`, flexWrap: 'wrap', fontSize: FS.xs, color: C.textSecondary }}>
        {segments.map(s => (
          <span key={s.key} style={{ display: 'inline-flex', alignItems: 'center', gap: SP.xs, whiteSpace: 'nowrap' }}>
            <i style={{ width: 8, height: 8, borderRadius: R.sm, background: SEG_COLOR[s.key], display: 'inline-block', flexShrink: 0 }} />
            {s.label} {s.count}
          </span>
        ))}
      </div>
      {title.hint && <Hint>{title.hint}</Hint>}
    </>
  );
}

// Начальный остаток «Вернётся»: значение сервера есть только в «Ждёт возврата»
// (yielded без причин); в остальных состояниях строки нет
function initialLeft(duty: LocalModelDuty | null | undefined): number | null {
  const a = duty?.arbiter;
  return a && a.state === 'yielded' && !(a.reasons?.length ?? 0) ? a.quietLeftS ?? null : null;
}

// === «Дежурство» ===
export function DutyBlock({ data, isMobile, onOpenMedia }: {
  data: LocalModelStats;
  isMobile?: boolean;
  // переход «→ Медиа» для заданий ComfyUI с этого сервера; без обработчика ссылки нет
  onOpenMedia?: () => void;
}) {
  const duty = data.duty ?? null;

  // Момент получения снимка: новый опрос даёт новый объект data.duty, якорь
  // переставляется на нём. Окно «ответов за час» считается от него, а не от
  // плавающего «сейчас» (часы клиента с ним и так же расходятся, как с Video).
  // eslint-disable-next-line react-hooks/exhaustive-deps, react-hooks/purity -- data.duty — триггер якоря: новый снимок = новый объект
  const snapAt = useMemo(() => Date.now(), [data.duty]);

  // «Вернётся через»: на новом ответе сервера берём его значение, между опросами
  // отсчитываем сами раз в 1 с (макет §5). Сброс на новом снимке — setState в рендере
  // (рекомендованный React способ смены состояния по смене пропa), в эффекте — только тик.
  const [leftS, setLeftS] = useState<number | null>(() => initialLeft(duty));
  const [srcDuty, setSrcDuty] = useState<LocalModelDuty | null | undefined>(data.duty);
  if (srcDuty !== data.duty) {
    setSrcDuty(data.duty);
    setLeftS(initialLeft(data.duty));
  }
  const ticking = leftS != null && leftS > 0;
  useEffect(() => {
    if (!ticking) return undefined;
    const id = window.setInterval(() => setLeftS(v => Math.max(0, (v ?? 0) - 1)), 1000);
    return () => window.clearInterval(id);
  }, [ticking]);

  if (!duty) return null;
  const a = duty.arbiter;
  const state = a?.state ?? null;
  const hasReasons = (a?.reasons?.length ?? 0) > 0;

  // Строки причин и возврата — по состоянию арбитра (макет §5)
  const summary = hasReasons ? dutyReasonsSummary(a?.reasons) : { main: null, more: 0 };
  const showReasonRow = state === 'yielded' || state === 'waiting-vram';
  const returnRow = state === 'yielded';
  // «Авария с активным арбитром»: метрики молчат, арбитр говорит, что модель активна —
  // это не уступка (макет §4, состояние «Авария»)
  const notYieldedByArbiter = data.status === 'down' && a?.reachable === true && a.state === 'ready';
  const hasTopRows = showReasonRow || returnRow || notYieldedByArbiter;

  // «Арбитр недоступен» / «арбитр не подключён» — строка warningText и приглушение тела
  const arbiterSilent = a != null && a.reachable === false;
  const warn = arbiterSilent
    ? `Арбитр не отвечает${a.lastSeenAt ? ` с ${formatSince(a.lastSeenAt)}` : ' пока'}. Ниже — последние данные; статус модели наверху — по её собственным метрикам.`
    : a == null
      ? 'Арбитр не подключён: почему уступила карта и когда модель вернётся — неизвестно.'
      : null;
  const counters = dutyCounters(a);
  const switchTimes = dutySwitchTimes(a);
  const modeChip = a?.mode ? DUTY_MODE_CHIP[a.mode] : null;
  const answersTitle = duty.answersLastHour
    ? answersWindowTitle(duty.answersLastHour, snapAt)
    : { title: 'Ответы мест за час' };

  return (
    <Card title="Дежурство" right={modeChip ? <span style={{ fontSize: FS.xs, color: C.textMuted, border: `1px solid ${C.borderLight}`, borderRadius: R.max, padding: `0 ${SP.sm}px`, whiteSpace: 'nowrap' }}>{modeChip}</span> : undefined}>
      {warn && <p style={{ margin: `0 0 ${SP.sm}px`, fontSize: FS.sm, color: C.warningText }}>{warn}</p>}
      <div style={arbiterSilent ? { opacity: 0.55, filter: 'grayscale(0.6)', pointerEvents: 'none' } : undefined}>
        {showReasonRow && (
          hasReasons ? (
            <>
              <Row
                k="Причина"
                v={
                  <>
                    {summary.main?.text}
                    {summary.main?.toMedia && onOpenMedia && <> <MediaLink onOpen={onOpenMedia} /></>}
                    {summary.more > 0 && <> и ещё {summary.more}</>}
                  </>
                }
              />
              {summary.main?.mono && (
                <div style={ROW}>
                  <span style={ROW_K}></span>
                  <span style={{ ...ROW_V, display: 'block', maxWidth: 220, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', fontWeight: 400, color: C.textMuted, marginLeft: 'auto' }}>{summary.main.mono}</span>
                </div>
              )}
            </>
          ) : (
            <Row k="Причина" v={state === 'yielded' ? <span style={{ fontWeight: 400, color: C.textMuted }}>задание закончилось</span> : 'не хватает памяти карты'} />
          )
        )}
        {returnRow && (
          <>
            <Row
              k="Вернётся"
              v={hasReasons
                ? <span style={{ fontWeight: 400, color: C.textMuted }}>после паузы, когда задание закончится</span>
                : leftS == null ? '—' : leftS <= 0 ? 'вот-вот' : `через ${formatClockLeft(leftS)}`}
              mono={!hasReasons}
            />
            {!hasReasons && leftS != null && leftS > 0 && <Hint>если не придёт новое задание</Hint>}
          </>
        )}
        {notYieldedByArbiter && (
          <>
            <Row k="Арбитр" v="Bonsai не выгружал" />
            <Hint>Смотрите журнал bonsai.service на Video.</Hint>
          </>
        )}
        {hasTopRows && <div style={SEPR} />}
        <Cap>Цепочка</Cap>
        <ChainBlock status={data.status} isMobile={isMobile === true} />
        <div style={SEPR} />
        <Cap>{answersTitle.title}</Cap>
        <AnswersBlock answers={duty.answersLastHour ?? null} nowMs={snapAt} />
        <div style={SEPR} />
        {counters && (
          <>
            <Row k="Уступила · вернулась" v={counters.value} mono />
            <Hint>{counters.period}</Hint>
          </>
        )}
        {switchTimes && <Row k="Уступает · возвращается" v={switchTimes} mono />}
      </div>
    </Card>
  );
}

// === «Кто сейчас на модели» для Bonsai: строки из duty.callsNow ===
// Это журнал вызовов мест, а не чаты: строка — div, не кнопка (36/44), клика и перехода
// нет (макет §3, §6). Для основного движка остаётся WhoCard из TextTab.
export function DutyCallsCard({ data, isMobile }: { data: LocalModelStats; isMobile?: boolean }) {
  const rows = dutyWhoRows(data.duty?.callsNow);
  return (
    <Card title="Кто сейчас на модели" right={rows.length > 0 ? <span>{rows.length}</span> : undefined}>
      {rows.length === 0 ? (
        <p style={{ margin: 0, fontSize: FS.sm, color: C.textMuted }}>Пока никто</p>
      ) : (
        <>
          <div style={{ display: 'flex', flexDirection: 'column', margin: `0 ${-SP.sm}px` }}>
            {rows.map(r => <CallRow key={r.key} row={r} isMobile={isMobile === true} />)}
          </div>
          <div style={{ fontSize: FS.xs, color: C.textMuted, marginTop: SP.xs }}>
            Здесь места сервера, а не чаты: строки не кликабельны, владельца и промпта нет.
          </div>
        </>
      )}
    </Card>
  );
}

function CallRow({ row, isMobile }: { row: DutyCallRow; isMobile: boolean }) {
  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, minHeight: isMobile ? 44 : 36, padding: `${SP.sm}px ${SP.sm}px` }}>
      <span style={{
        width: 24, height: 24, flex: '0 0 24px', borderRadius: R.full,
        background: C.accentMuted, color: C.textHeading, fontSize: FS.xxs, fontWeight: 700,
        display: 'flex', alignItems: 'center', justifyContent: 'center',
      }}>{(row.place[0] ?? '·').toUpperCase()}</span>
      <span style={{ flex: 1, minWidth: 0 }}>
        <b style={{ display: 'block', fontSize: FS.sm, fontWeight: 600, color: C.textHeading, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>{row.place}</b>
        <span style={{ display: 'block', fontSize: FS.xs, color: C.textMuted, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>{row.linkText}</span>
      </span>
      {row.duration && (
        <span style={{ fontFamily: FONT.mono, fontSize: FS.sm, whiteSpace: 'nowrap', color: row.queued ? C.warningText : C.textSecondary }}>{row.duration}</span>
      )}
    </div>
  );
}
