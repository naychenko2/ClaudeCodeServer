// Блок «Ночной разбор» настроек вики (F2; макет llm-wiki-v2 «Срез Ф2 02.11»,
// docs/mockups/llm-wiki-v2-proposal.md — тексты и ошибки оттуда слово в слово).
// Данные — тот же снимок настроек стора, что у блока «Применение» (loadWikiSettings /
// useWikiSettings / putWikiNightly, шаг F1): стор оптимистичен и сам откатывает при
// отказе; блок только вызывает стор и показывает его состояния.
//
// Состояния: загрузка (снимка ещё нет) — скелетон; отказ чтения — плашка с «Повторить»;
// тумблер выключен — info-плашка, поля ниже затемнены, но правятся (макет); lastNight
// отсутствует — карточка «Ночного разбора ещё не было» (текст по состоянию тумблера);
// lastNight есть — карточка итога и предупреждение по исходу (тексты — wikiLabels).
//
// Чего в блоке нет намеренно:
// — имени модели: контракту настроек его поля нет (снимок WikiSettingsSnapshot), блок
//   называет только места и кто их назначает («Qwen3.8-27B» в макете — пример макета);
//   вторая фраза подсказки записки («локальная модель…») — только когда модель локальная,
//   фронт этого не знает;
// — времён остановки («Закончилась в 05:12»): date в контракте — DateOnly без времени
//   (WikiNightlyService.FormatDate), поля времени остановки нет — придумывать нечем.
//
// Ошибки ввода — локальные, до запроса (записка: Field с ошибкой): равные часы окна,
// число ≤ 0, выше потолка. Числовые поля пишутся черновиком и уходят PUT'ом на blur:
// «7000» посреди набора «700000» — уже допустимое значение, отправлять его по каждой
// букве значило бы PUT на каждую цифру. Отказ сохранения — стор откатил оптимистичное, полю
// остаётся показать прежнее значение, блок называет отказ (записка: «Не сохранилось —
// попробуйте ещё раз.»).
//
// onOpenInbox / onOpenJournal — переходы к «Входящим» и «Журналу»: смена подвкладки —
// состояние контейнера (F3). Без колбэка блок кнопки не рисует: кнопка, которая ничего
// не делает, хуже отсутствия кнопки.

import { useCallback, useEffect, useState, type CSSProperties } from 'react';
import { AlertTriangle, Clock, Inbox, Lock } from 'lucide-react';
import {
  C, FONT, FS, R, SHADOW, SP, useIsMobile,
  WikiError,
  loadWikiSettings, putWikiNightly, useWikiSettings,
  type WikiLastNight, type WikiNightlyOutcome, type WikiNightlyPatch, type WikiSettingsSnapshot,
} from 'aihome_shell/kit';
import { Button } from '../../../components/ui/Button';
import { Field, TextField } from '../../../components/ui/Field';
import { Notice } from '../../../components/ui/Notice';
import { ProgressBar } from '../../../components/ui/ProgressBar';
import { Select } from '../../../components/ui/Select';
import { Toggle } from '../../../components/ui/Toggle';
import { ICON_SIZE, ICON_STROKE } from '../../../components/ui/icons';
import {
  NIGHTLY_COMPLETED_LINE, NIGHTLY_MAX_PACKAGES, NIGHTLY_MAX_TOKENS, NIGHTLY_NONE_TITLE,
  nightOutcomeNotice, nightlyNoneText,
} from './wikiLabels';

// ─── Формат и разбор чисел ───

const pad2 = (n: number) => String(n).padStart(2, '0');

// Разделение — обычный пробел: toLocaleString('ru-RU') даёт NBSP, и сравнение с
// текстами записки («700 000») не должно зависеть от невидимого символа
const fmtNum = (n: number): string => String(Math.trunc(n)).replace(/\B(?=(\d{3})+(?!\d))/g, ' ');
const parseNum = (s: string): number | null => {
  const digits = s.replace(/\D/g, '');
  return digits ? Number(digits) : null;
};

// «Прошлая ночь · 26 → 27.10»: ночь идёт от дня перед датой прохода до самой даты
// (date — бэковый DateOnly 'yyyy-MM-dd', проход ставится после успеха — N7b). Дата
// нечитаемая — суффикса нет. Разбор по UTC: дата строки не должна съезжать от пояса
// прогона.
function nightRangeText(date: string): string {
  const m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(date);
  if (!m) return '';
  const d = new Date(Date.UTC(Number(m[1]), Number(m[2]) - 1, Number(m[3])));
  if (Number.isNaN(d.getTime())) return '';
  const prev = new Date(d.getTime() - 86_400_000);
  return ` · ${prev.getUTCDate()} → ${d.getUTCDate()}.${pad2(d.getUTCMonth() + 1)}`;
}

// ─── Валидация числовых полей (зеркало бэкового IsValidNightly) ───

type NumKey = 'maxPackagesPerNight' | 'maxPackagesPerSection' | 'inputTokenBudget';

const NUM_MAX: Record<NumKey, number> = {
  maxPackagesPerNight: NIGHTLY_MAX_PACKAGES,
  maxPackagesPerSection: NIGHTLY_MAX_PACKAGES,
  inputTokenBudget: NIGHTLY_MAX_TOKENS,
};

// Тексты — по записке («Срез Ф2 02.11»); «на раздел» бэк мерит тем же потолком, что и
// «на всю вики» (IsValidNightly), поэтому и причина та же
const OVER_MAX_TEXT: Record<NumKey, string> = {
  maxPackagesPerNight: `Не больше ${NIGHTLY_MAX_PACKAGES} — иначе ночь упрётся в лимит «Входящих»`,
  maxPackagesPerSection: `Не больше ${NIGHTLY_MAX_PACKAGES} — иначе ночь упрётся в лимит «Входящих»`,
  inputTokenBudget: `Не больше ${fmtNum(NIGHTLY_MAX_TOKENS)}`,
};

// ─── Мелкие блоки ───

function LoadingSkeleton() {
  const bar = (w: number) => (
    <div key={w} style={{ height: 10, width: `${w}%`, background: C.bgInset, borderRadius: R.sm, margin: `${SP.md}px 0` }} />
  );
  return <div style={{ padding: SP.md, opacity: 0.7 }} aria-hidden>{bar(60)}{bar(85)}{bar(45)}</div>;
}

const CARD: CSSProperties = {
  padding: SP.md, border: `1px solid ${C.border}`, borderRadius: R.md,
  background: C.bgPanel, boxShadow: SHADOW.card,
};

const SECTION_H3 = { margin: `${SP.lg}px 0 ${SP.xs}px`, fontSize: FS.md, color: C.textHeading } as const;

// Иконка предупреждения по исходу: окно и выключение — часы, сбой — тревога,
// «Входящие» — их икона (макет рисует Clock/Inbox для warning-исходов)
const OUTCOME_ICON: Record<WikiNightlyOutcome, typeof Clock> = {
  budget: Clock,
  open_limit: Inbox,
  window_closed: Clock,
  disabled_midway: Clock,
  error: AlertTriangle,
  completed: Clock, // карточка completed — без предупреждения, иконка не используется
};

function kvCell(v: number, label: string) {
  return (
    <div key={label}>
      <div style={{ fontSize: FS.lg, fontWeight: 600, color: C.textHeading }}>{v}</div>
      <div style={{ fontSize: FS.xs, color: C.textMuted }}>{label}</div>
    </div>
  );
}

// Итог прошлой ночи: предупреждение по исходу (тексты — wikiLabels), карточка четырёх
// чисел и оценочный расход («≈»: оценка, не факт — ProgressBar estimate: пунктир).
function LastNightBlock({ last, snapshot, isMobile, onOpenInbox, onOpenJournal }: {
  last: WikiLastNight;
  snapshot: WikiSettingsSnapshot;
  isMobile: boolean;
  onOpenInbox?: () => void;
  onOpenJournal?: () => void;
}) {
  const notice = nightOutcomeNotice(last.outcome, last);
  // «≈ N из M» и полоса — против ТЕКУЩЕГО «Бюджета за ночь» (решение F4, хвост R-F2 (4)):
  // файла с бюджетом на момент той ночи нет (N7c — только estTokens), а текущий человек
  // видит в поле рядом; сравнение честно ровно в эту сторону и сказано в title строки
  const budget = Math.max(1, snapshot.nightly.inputTokenBudget);
  const pct = Math.max(0, Math.min(100, Math.round((last.estTokens / budget) * 100)));
  return (
    <>
      {notice && (
        <div style={{ marginTop: SP.md }}>
          <Notice
            tone={notice.tone}
            icon={OUTCOME_ICON[last.outcome] ?? AlertTriangle}
            title={notice.title ?? undefined}
            action={last.outcome === 'open_limit' && onOpenInbox
              ? <Button variant="secondary" size="sm" dataTestId="wiki-nightly-open-inbox" onClick={onOpenInbox}>Открыть «Входящие»</Button>
              : undefined}
          >
            {notice.text}
          </Notice>
        </div>
      )}
      <div data-testid="wiki-nightly-card" style={{ ...CARD, marginTop: SP.md }}>
        <div style={{ display: 'grid', gridTemplateColumns: isMobile ? '1fr 1fr' : 'repeat(4, 1fr)', gap: SP.md }}>
          {kvCell(last.selected, 'чатов отобрано')}
          {kvCell(last.analyzed, 'разобрано')}
          {kvCell(last.applied, 'пакетов применено само')}
          {kvCell(last.toInbox, 'ждут вас во «Входящих»')}
        </div>
        <div
          title="Расход той ночи против текущего «Бюджета за ночь»"
          style={{ display: 'flex', gap: SP.md, alignItems: 'center', marginTop: SP.md }}
        >
          <span style={{ flex: 1, minWidth: 0 }}><ProgressBar value={pct} estimate /></span>
          <span style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textSecondary }}>
            ≈ {fmtNum(last.estTokens)} из {fmtNum(snapshot.nightly.inputTokenBudget)}
          </span>
        </div>
        <div style={{ fontSize: FS.sm, color: C.textMuted, marginTop: SP.xs }}>
          {last.outcome === 'completed' ? NIGHTLY_COMPLETED_LINE : ''}
          {onOpenJournal && (
            <Button variant="ghost" size="sm" dataTestId="wiki-nightly-open-journal" onClick={onOpenJournal}>Открыть «Журнал»</Button>
          )}
        </div>
      </div>
    </>
  );
}

// ─── Блок ───

export function WikiNightlyBlock({ onOpenInbox, onOpenJournal }: {
  /** Контейнер (F3) передаёт переход к «Входящим»: без колбэка кнопки нет */
  onOpenInbox?: () => void;
  /** …то же для «Журнала» */
  onOpenJournal?: () => void;
}) {
  const snapshot = useWikiSettings();
  const isMobile = useIsMobile();
  const [loadError, setLoadError] = useState<unknown>(null);
  const [actionError, setActionError] = useState<string | null>(null);
  // Одна идущая правка (тумблер, окно или поле): слот стора один, вторая правка до
  // ответа первой показала бы состояние, которое бэк ещё не подтвердил
  const [busy, setBusy] = useState(false);
  // Неровное окно — не запрос, а локальная ошибка поля
  const [winError, setWinError] = useState<string | null>(null);
  // Черновик и ошибка числового поля: значение уходит на blur (не по каждой букве),
  // пока поле в работе — в нём черновик, а не значение стора
  const [drafts, setDrafts] = useState<Record<string, string>>({});
  const [numErrors, setNumErrors] = useState<Record<string, string>>({});

  const load = useCallback(() => {
    setLoadError(null);
    // Снимок и его поколение — у стора; здесь только старт и отказ для плашки
    loadWikiSettings().catch(e => setLoadError(e));
  }, []);

  useEffect(() => {
    // eslint-disable-next-line react-hooks/set-state-in-effect -- старт загрузки при монтировании блока: другого места старта нет, снимок стора здесь не инициализируется
    load();
  }, [load]);

  const runAction = async (action: () => Promise<{ ok: boolean; error?: WikiError }>) => {
    if (busy) return;
    setBusy(true);
    setActionError(null);
    try {
      const r = await action();
      // Отказ контракта: стор уже откатил оптимистичное (putWikiNightly, F1) — полю
      // остаётся показать прежнее значение, блоку — назвать отказ текстом записки
      if (!r.ok) setActionError('Не сохранилось — попробуйте ещё раз.');
    } catch {
      // Сетевой сбой/таймаут — не отказ контракта (та же позиция стора): откат тот же
      setActionError('Не удалось сохранить настройки');
    } finally {
      setBusy(false);
    }
  };

  const validateNum = (k: NumKey, raw: string): string | null => {
    if (raw.trim() === '') return null; // пустой черновик — не ошибка: на blur поле вернётся к текущему значению
    const v = parseNum(raw);
    if (v === null) return 'Нужно число';
    if (v <= 0) return 'Должно быть больше 0';
    if (v > NUM_MAX[k]) return OVER_MAX_TEXT[k];
    return null;
  };

  const setDraft = (k: NumKey, raw: string) => {
    setDrafts(d => ({ ...d, [k]: raw }));
    const err = validateNum(k, raw);
    setNumErrors(p => { const n = { ...p }; if (err) n[k] = err; else delete n[k]; return n; });
  };

  const dropDraft = (k: NumKey) => {
    setDrafts(d => { const n = { ...d }; delete n[k]; return n; });
    setNumErrors(p => { const n = { ...p }; delete n[k]; return n; });
  };

  const commitNum = (k: NumKey) => {
    const raw = drafts[k];
    if (raw === undefined) return; // поле не трогали
    if (raw.trim() === '') { dropDraft(k); return; } // стёрли — не изменение, возврат к текущему значению
    if (numErrors[k]) return; // ошибка у поля: черновик виден, человек исправит или отменит
    const v = parseNum(raw);
    if (v === null) return;
    // Черновик снимается ДО запроса: при отказе поле покажет значение стора — прежнее
    dropDraft(k);
    void runAction(() => putWikiNightly({ [k]: v } as WikiNightlyPatch));
  };

  const tryWindow = (start: number, end: number) => {
    if (start === end) { setWinError('Начало и конец окна совпадают'); return; }
    setWinError(null);
    void runAction(() => putWikiNightly({ windowStartHour: start, windowEndHour: end }));
  };

  if (loadError) {
    // Текст отказа здесь общий: этот блок читает одни настройки, и любой его отказ —
    // «настройки не прочитались» (понятные тексты по кодам — у стора и других экранов)
    return (
      <div data-testid="wiki-nightly-error" style={{ padding: `${SP.sm}px ${SP.md}px` }}>
        <Notice tone="danger" icon={AlertTriangle} title="Не удалось прочитать настройки" action={<Button variant="ghost" size="sm" onClick={load}>Повторить</Button>} />
      </div>
    );
  }

  if (!snapshot) return <LoadingSkeleton />;

  const nightly = snapshot.nightly;
  const gridCols = isMobile ? '1fr' : '1fr 1fr';

  const numInput = (k: NumKey, unit: string) => (
    <span
      data-testid={`wiki-nightly-input-${k}`}
      style={{ flex: '1 1 160px', minWidth: 0, display: 'flex', gap: SP.sm, alignItems: 'center' }}
    >
      <span style={{ flex: 1, minWidth: 0 }}>
        <TextField
          value={drafts[k] ?? fmtNum(nightly[k])}
          onChange={(v) => setDraft(k, v)}
          onFocus={() => { setDrafts(d => (k in d ? d : { ...d, [k]: fmtNum(nightly[k]) })); }}
          onBlur={() => commitNum(k)}
          onEscape={() => dropDraft(k)}
          inputMode="numeric"
          invalid={!!numErrors[k]}
          disabled={busy}
        />
      </span>
      <span style={{ fontSize: FS.sm, color: C.textMuted, whiteSpace: 'nowrap' }}>{unit}</span>
    </span>
  );

  const hourSelect = (value: number, onPick: (h: number) => void) => (
    <span style={{ flex: 1, minWidth: 0 }}>
      <Select
        value={String(value)}
        options={Array.from({ length: 24 }, (_, i) => ({ value: String(i), label: `${pad2(i)}:00` }))}
        disabled={busy}
        onChange={(v) => { if (v !== '') onPick(Number(v)); }}
      />
    </span>
  );

  return (
    <div style={{ padding: `${SP.sm}px ${SP.md}px ${SP.md}px` }}>
      <h2 style={{ margin: `${SP.xs}px 0 0`, fontFamily: FONT.serif, fontSize: FS.lg, color: C.textHeading }}>
        Ночной разбор
      </h2>
      <div style={{ fontSize: FS.sm, color: C.textSecondary, marginTop: SP.xs }}>
        Раз в ночь Хранитель разбирает новое в ваших разговорах. Неспорное применяется само,
        спорное ждёт вас во «Входящих». Утром — уведомление с итогом.
      </div>

      <div
        data-testid="wiki-nightly-switch"
        style={{
          display: 'flex', gap: SP.md, alignItems: 'center', marginTop: SP.md,
          padding: SP.md, border: `1px solid ${C.border}`, borderRadius: R.md,
          background: C.bgPanel, boxShadow: SHADOW.card,
        }}
      >
        <div style={{ flex: 1, minWidth: 0 }}>
          <div style={{ fontSize: FS.base, fontWeight: 500, color: C.textHeading }}>Разбирать по ночам</div>
          <div style={{ fontSize: FS.sm, color: C.textMuted, marginTop: 2 }}>
            По умолчанию выключено — включите, когда будете готовы. Без него вики пополняется
            только кнопкой «Разобрать этот чат».
          </div>
        </div>
        <Toggle
          checked={nightly.enabled}
          disabled={busy}
          focusable
          ariaLabel="Разбирать по ночам"
          onChange={(v) => { void runAction(() => putWikiNightly({ enabled: v })); }}
        />
      </div>

      {!nightly.enabled && (
        <div style={{ marginTop: SP.md }}>
          <Notice tone="info" icon={Clock}>
            Ночью ничего не разбирается. Отбор чатов всё равно считается — его видно в «Отборе чатов».
          </Notice>
        </div>
      )}

      {actionError && (
        <div style={{ marginTop: SP.md }}>
          <Notice tone="danger" icon={AlertTriangle} title={actionError} />
        </div>
      )}

      {/* Поля: при выключенном тумблере затемнены, но правятся (макет) */}
      <div style={{ display: 'grid', gridTemplateColumns: gridCols, gap: SP.md, marginTop: SP.md, opacity: nightly.enabled ? 1 : 0.6 }}>
        <Field
          label="Окно"
          hint="Время сервера. Не успели к концу окна — остальное следующей ночью."
          error={winError}
        >
          <div data-testid="wiki-nightly-window" style={{ display: 'flex', gap: SP.sm, alignItems: 'center' }}>
            {hourSelect(nightly.windowStartHour, (h) => tryWindow(h, nightly.windowEndHour))}
            <span style={{ color: C.textMuted }}>—</span>
            {hourSelect(nightly.windowEndHour, (h) => tryWindow(nightly.windowStartHour, h))}
          </div>
        </Field>

        <Field
          label="Бюджет за ночь"
          hint="Примерно 20–30 длинных разговоров. Кончился — остальное следующей ночью."
          error={numErrors.inputTokenBudget}
        >
          <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.sm }}>
            {numInput('inputTokenBudget', 'токенов')}
          </div>
        </Field>

        <Field
          label="Пакетов за ночь"
          hint="Чтобы один шумный проект не занял всю ночь. Чатов — до 10 на раздел, хроники в лимит не входят."
          error={numErrors.maxPackagesPerNight ?? numErrors.maxPackagesPerSection}
        >
          <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.sm }}>
            {numInput('maxPackagesPerNight', 'на всю вики')}
            {numInput('maxPackagesPerSection', 'на раздел')}
          </div>
        </Field>
      </div>

      <h3 style={SECTION_H3}>Чем разбирать</h3>
      {/* Только текст, без контролов: имени модели в контракте настроек нет — плашки
          называют места, кто их назначает — строкой ниже (решение среза: смена модели
          — в «Поставщиках моделей») */}
      <div data-testid="wiki-nightly-engine" style={{ display: 'grid', gridTemplateColumns: gridCols, gap: SP.sm }}>
        {['Разбор', 'Отбор спорных чатов'].map((t) => (
          <div key={t} style={{ display: 'flex', gap: SP.sm, alignItems: 'center', padding: `${SP.sm}px ${SP.md}px`, background: C.bgInset, borderRadius: R.md, border: `1px solid ${C.borderLight}` }}>
            <Lock size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ color: C.textMuted, flexShrink: 0 }} />
            <span style={{ fontSize: FS.sm, color: C.textHeading }}>{t}</span>
          </div>
        ))}
      </div>
      <div style={{ display: 'flex', gap: 6, alignItems: 'center', fontSize: FS.xs, color: C.textMuted, marginTop: SP.xs }}>
        <Lock size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ color: C.textMuted, flexShrink: 0 }} />
        <span>Назначает администратор в «Поставщиках моделей».</span>
      </div>

      <h3 style={SECTION_H3}>Прошлая ночь{snapshot.lastNight ? nightRangeText(snapshot.lastNight.date) : ''}</h3>
      {snapshot.lastNight ? (
        <LastNightBlock last={snapshot.lastNight} snapshot={snapshot} isMobile={isMobile} onOpenInbox={onOpenInbox} onOpenJournal={onOpenJournal} />
      ) : (
        <div data-testid="wiki-nightly-none" style={{ ...CARD, marginTop: SP.md }}>
          <div style={{ fontSize: FS.base, fontWeight: 500, color: C.textHeading }}>{NIGHTLY_NONE_TITLE}</div>
          <div style={{ fontSize: FS.sm, color: C.textMuted, marginTop: SP.xs }}>
            {nightlyNoneText(nightly.enabled, `${pad2(nightly.windowStartHour)}:00`, `${pad2(nightly.windowEndHour)}:00`)}
          </div>
        </div>
      )}
    </div>
  );
}
