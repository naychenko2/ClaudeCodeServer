// «Журнал» (S7c-1/S7c-2, макет llm-wiki-v2 §6): хронология своей вики из GET /api/wiki/journal
// (S7c-0): когда, какой раздел, какой источник (с именем), что случилось (apply/revert/retract/
// relink/adopt), политика («само» / «через Входящие»), какие страницы — и откат всего источника
// (мера 5 ADR-035 §12г).
//
// Данные — строки log.jsonl, склеенные с пакетами (контракт ручки, WikiJournalReader.cs):
// status/policy/classes — из склейки, а где события пакета нет (relink, adopt) — их нет вовсе:
// пустое поле честнее выдумки. Имя источника — данные, не разметка: чужой или несуществующий
// чат приходит без имени, строка тогда показывает карточку.
//
// «Откатить пакет» (мера 4) на экране нет: запись журнала применённого пакета не несёт его id
// (дефект ead65823, бэку запрошено) — откат всего источника здесь и закрывает возврат источника. «Предполагаемое изменение» (версия S7c-1 из операций пакета) ушло вместе со старым
// источником: журнал не несёт текстов операций, запись показывает список затронутых страниц.
//
// Порядок запросов — тот же инвариант, что у «Входящих» (урок S5b/S5e): ответ кладётся, только
// если его поколение последнее. Фильтры живут в запросе (бэк сужает окно сам): ответ прежнего
// фильтра не затеряет новый. Кнопки действий заблокированы на время запроса. Недоверенный
// контент (имена источников, карточки, страницы) — только текстом. Отказ бэка показывается как есть.

import { useCallback, useEffect, useRef, useState } from 'react';
import { AlertTriangle, History } from 'lucide-react';
import { C, FONT, FS, R, SHADOW, SP } from '../../../lib/design';
import { Badge } from '../../../components/ui/Badge';
import { Button } from '../../../components/ui/Button';
import { ConfirmDialog } from '../../../components/ui/ConfirmDialog';
import { EmptyState } from '../../../components/ui/EmptyState';
import { Notice } from '../../../components/ui/Notice';
import { Select } from '../../../components/ui/Select';
import { ICON_SIZE, ICON_STROKE } from '../../../components/ui/icons';
import { useIsMobile } from '../../../lib/breakpoints';
import { openChatById } from '../../../lib/openChat';
import { showToast } from '../../../lib/toast';
import {
  WikiError,
  loadWikiJournal,
  retractWikiSource,
  useWikiTree,
  type WikiJournalDto,
  type WikiJournalEntryDto,
} from '../../../lib/wiki';
import { ORIGIN_META } from './WikiInbox';
import { sectionPrefix, wikiErrorText } from './wikiLabels';

// ─── Словари подписей ───

// Событие журнала — что случилось (op — значения, которые бэк пишет в log.jsonl:
// WikiApplyService / WikiPackageRevert / WikiRetractService / WikiKeeper). Неизвестный op
// показан как есть: словарь не выдумывает смысл незнакомому слову.
const OP_TEXT: Record<string, string> = {
  apply: 'применено',
  revert: 'откатано',
  retract: 'откат по источнику',
  relink: 'перелинкованы связи',
  adopt: 'принято как есть',
};

// Бейдж статуса/политики говорит только то, чего не говорит слово события: для применённой
// записи — политику («через Входящие» — там был человек); откатанный после применения пакет
// виден на записи применения; «отбит проверками» — отдельный исход. Для записей
// revert/retract/relink/adopt событие уже названо op — бейдж только повторил бы его.
function entryBadge(e: WikiJournalEntryDto): { tone: 'success' | 'warning' | 'danger' | 'neutral'; text: string } | null {
  if (e.op !== 'apply') return null;
  if (e.status === 'applied') {
    return e.policy === 'inbox'
      ? { tone: 'warning', text: 'через Входящие' }
      : { tone: 'success', text: 'применилось само' };
  }
  if (e.status === 'rejected_by_checks') return { tone: 'danger', text: 'отбит проверками' };
  if (e.status === 'reverted') return { tone: 'neutral', text: 'откатано' };
  return null;
}

// Отказы отката по источнику — русским текстом по коду бэка (тот же приём, что pkgErrorText для
// «Входящих»). 'wiki_retract_source' здесь нет: его текст — в wikiErrorText (общий словарь, S7c-2).
// Формулировки — черновые, тексты фичи за Софьей.
function retractErrorText(e: WikiError): string {
  switch (e.code) {
    case 'wiki_retract_off': return 'Откат по источнику для этой вики недоступен';
    case 'wiki_retract_shape': return 'Не удалось отправить запрос отката';
    case 'wiki_retract_internal': return 'Внутренний сбой отката по источнику';
  }
  return wikiErrorText(e);
}

// ─── Помощники строки и записи ───

const pad = (n: number) => String(n).padStart(2, '0');

function dayKey(iso: string): string {
  const d = new Date(iso);
  return Number.isFinite(d.getTime()) ? `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}` : '';
}

function dayTitle(key: string): string {
  const today = dayKey(new Date().toISOString());
  const [, m, d] = key.split('-');
  if (key === today) return `Сегодня, ${d}.${m}`;
  return `${d}.${m}.${key.slice(0, 4)}`;
}

function timeText(iso: string): string {
  const d = new Date(iso);
  if (!Number.isFinite(d.getTime())) return '';
  return `${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

const uniq = (xs: string[]) => [...new Set(xs)];

// Источник записи для отката по нему — форма SourceRef бэка «{origin}:{id}» БЕЗ диапазона
// окна: Ref окна храненияется с диапазоном («session:{id}#m1-9», захват в WikiChatIngestService),
// а бэк по карточке без диапазона накрывает все пакеты источника в любой части диапазона
// (RefMatches, WikiRetractService.cs). Здесь — зеркало бэковых OriginOf/IdPartOf. По этой же
// нормальной форме модалка считает применённые записи источника: сравнение целых ref дало бы
// 1 вместо реального числа окон одного чата. Строка может нести несколько карточек через «, »
// (так их пишет SourceOf пакета) — берётся первая, тот же приём, что бэкового FirstSourcePart:
// отдельного поля под множество стрика не несёт.
function entrySourceRef(e: WikiJournalEntryDto): string | null {
  if (!e.source || !e.origin) return null;
  const first = e.source.split(', ')[0];
  const rest = first.startsWith(`${e.origin}:`) ? first.slice(e.origin.length + 1) : first;
  const hash = rest.indexOf('#');
  return `${e.origin}:${hash < 0 ? rest : rest.slice(0, hash)}`;
}

// Id чата для перехода: та же граница, что у бэкового IdPartOf (без суффикса диапазона).
function entryChatId(e: WikiJournalEntryDto): string | null {
  if (e.origin !== 'session') return null;
  const id = entrySourceRef(e)?.slice('session:'.length) ?? '';
  return id || null;
}

const originMeta = (e: WikiJournalEntryDto) => (e.origin ? ORIGIN_META[e.origin] : undefined);
const opText = (e: WikiJournalEntryDto) => OP_TEXT[e.op] ?? e.op;

// Название записи: имя источника (название чата, заголовок заметки) — то, что человек узнаёт;
// без имени (чужой или несуществующий чат, origin без имени) — карточка; записи без источника
// (relink, adopt) — само событие.
function entryTitle(e: WikiJournalEntryDto): string {
  return e.sourceName ?? e.source ?? opText(e);
}

function LoadingSkeleton() {
  const bar = (w: number) => (
    <div key={w} style={{ height: 10, width: `${w}%`, background: C.bgInset, borderRadius: R.sm, margin: `${SP.md}px 0` }} />
  );
  return <div style={{ padding: SP.md, opacity: 0.7 }} aria-hidden>{bar(60)}{bar(85)}{bar(45)}</div>;
}

// ─── Строка списка ───

function JournalRow({ entry, onOpen }: { entry: WikiJournalEntryDto; onOpen: () => void }) {
  const isMobile = useIsMobile();
  const tree = useWikiTree();
  const meta = originMeta(entry);
  const Icon = meta?.icon ?? History;
  const section = tree?.sections.find(s => s.key === entry.section);
  const badge = entryBadge(entry);
  return (
    <button
      type="button"
      data-testid="wiki-journal-row"
      onClick={onOpen}
      style={{
        display: 'flex', gap: SP.sm, padding: `${SP.sm}px ${SP.md}px`, borderRadius: R.md,
        border: 'none', background: 'transparent', cursor: 'pointer', textAlign: 'left',
        width: '100%', fontFamily: FONT.sans, color: C.textPrimary,
      }}
    >
      <span style={{ flexShrink: 0, color: C.textMuted, paddingTop: 2 }}><Icon size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} /></span>
      <span style={{ minWidth: 0, flex: 1 }}>
        <span style={{ display: 'block', fontSize: FS.xs, color: C.textMuted, fontFamily: FONT.mono }}>
          {section ? sectionPrefix(section) : entry.section}{meta ? ` · ${meta.label}` : ''}
        </span>
        <span style={{ display: 'block', fontSize: FS.base, fontWeight: 500, color: C.textHeading, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
          {entryTitle(entry)}
        </span>
        <span style={{ display: 'block', fontSize: FS.sm, color: C.textMuted, marginTop: 2 }}>
          {opText(entry)} · {entry.pages.length} стр.
        </span>
        {badge && <span style={{ display: 'block', marginTop: 3 }}><Badge tone={badge.tone}>{badge.text}</Badge></span>}
      </span>
      {!isMobile && (
        <span style={{ flexShrink: 0, fontSize: FS.xs, color: C.textMuted, fontFamily: FONT.mono }}>{timeText(entry.ts)}</span>
      )}
    </button>
  );
}

// ─── Запись журнала ───

function JournalEntry({
  entry, busy, actionError, onBack, onRetract,
}: {
  entry: WikiJournalEntryDto;
  busy: boolean;
  actionError: string | null;
  onBack: () => void;
  onRetract: () => void;
}) {
  const tree = useWikiTree();
  const meta = originMeta(entry);
  const badge = entryBadge(entry);
  const section = tree?.sections.find(s => s.key === entry.section);
  const sourceRef = entrySourceRef(entry);
  const chatId = entryChatId(entry);
  return (
    <div style={{ padding: `0 ${SP.md}px ${SP.md}px` }}>
      <Button variant="ghost" size="sm" dataTestId="wiki-journal-back" ariaLabel="Вернуться к списку «Журнала»" onClick={onBack}>Назад</Button>
      <div style={{ fontSize: FS.xs, color: C.textMuted, fontFamily: FONT.mono, marginTop: SP.sm }}>
        Журнал · {timeText(entry.ts)}
      </div>
      <div style={{ fontSize: FS.xs, color: C.textMuted, marginTop: 2 }}>
        {section ? sectionPrefix(section) : entry.section}
      </div>
      <h2 style={{ margin: `${SP.xs}px 0 0`, fontFamily: FONT.serif, fontSize: FS.lg, color: C.textHeading, overflowWrap: 'break-word' }}>
        {entryTitle(entry)}
      </h2>
      <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.sm, alignItems: 'center', marginTop: SP.sm }}>
        {badge && <Badge tone={badge.tone}>{badge.text}</Badge>}
        <span style={{ fontSize: FS.sm, color: C.textSecondary }}>{opText(entry)}</span>
        {sourceRef && <span style={{ fontSize: FS.xs, fontFamily: FONT.mono, color: C.textMuted, overflowWrap: 'anywhere' }}>{sourceRef}</span>}
        <span style={{ fontSize: FS.xs, color: C.textMuted }}>{entry.pages.length} стр.</span>
      </div>
      {entry.classes.length > 0 && (
        <div style={{ fontSize: FS.xs, fontFamily: FONT.mono, color: C.textMuted, marginTop: SP.xs, overflowWrap: 'anywhere' }}>
          класс: {entry.classes.join(', ')}
        </div>
      )}
      {entry.status === 'rejected_by_checks' && (
        <div style={{ marginTop: SP.md }}>
          <Notice tone="danger" icon={AlertTriangle} title="Отбит проверками кода — в вики ничего не записано">
            Такие правки не применяются и во «Входящие» не идут: показывать нечего.
          </Notice>
        </div>
      )}
      {actionError && (
        <div style={{ marginTop: SP.md }}>
          <Notice tone="danger" icon={AlertTriangle} title={actionError} />
        </div>
      )}
      <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.sm, marginTop: SP.md }}>
        {sourceRef && (
          <Button variant="ghost" size="sm" dataTestId="wiki-journal-retract" disabled={busy} onClick={onRetract}>
            Откатить всё из этого {meta?.label ?? 'источника'}
          </Button>
        )}
        {chatId && (
          <Button variant="secondary" size="sm" dataTestId="wiki-journal-chat" onClick={() => { void openChatById(chatId); }}>
            Открыть чат
          </Button>
        )}
      </div>
      {entry.pages.length > 0 && (
        <>
          <h3 style={{ margin: `${SP.lg}px 0 ${SP.sm}px`, fontSize: FS.md, color: C.textHeading }}>Изменённые страницы</h3>
          <div style={{ fontSize: FS.sm, color: C.textMuted, marginBottom: SP.sm }}>
            Что затронуло это событие; содержимое страниц — в каталоге раздела.
          </div>
          <div
            style={{ border: `1px solid ${C.border}`, borderRadius: R.md, padding: SP.sm, marginBottom: SP.sm, background: C.bgPanel, boxShadow: SHADOW.card }}
          >
            <ul style={{ margin: 0, paddingLeft: SP.lg }}>
              {entry.pages.map(p => (
                <li key={p} style={{ fontFamily: FONT.mono, fontSize: FS.sm, overflowWrap: 'anywhere' }}>{p}</li>
              ))}
            </ul>
          </div>
        </>
      )}
    </div>
  );
}

// ─── Подвкладка ───

export function WikiJournal() {
  const tree = useWikiTree();
  const [data, setData] = useState<WikiJournalDto | null>(null);
  const [loadError, setLoadError] = useState<unknown>(null);
  // '' — все разделы / все источники (пункт-заглушка Select)
  const [sectionFilter, setSectionFilter] = useState('');
  const [originFilter, setOriginFilter] = useState('');
  const [opened, setOpened] = useState<WikiJournalEntryDto | null>(null);
  const [confirm, setConfirm] = useState<WikiJournalEntryDto | null>(null);
  const [actionError, setActionError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  // Поколение загрузок (урок S5b/S5e, образец — loadSeq в WikiInbox): только последний запрос
  // пишет список; filters живут в запросе — поздний ответ прежнего фильтра не затеряет новый
  const loadSeq = useRef(0);

  const load = useCallback(() => {
    const mine = ++loadSeq.current;
    setLoadError(null);
    loadWikiJournal({ section: sectionFilter || null, origin: originFilter || null })
      .then(d => { if (loadSeq.current === mine) setData(d); })
      .catch(e => { if (loadSeq.current === mine) setLoadError(e); });
  }, [sectionFilter, originFilter]);

  useEffect(() => {
    // eslint-disable-next-line react-hooks/set-state-in-effect -- старт загрузки при монтировании подвкладки и при смене фильтра: другого места старта нет, список живёт в компоненте
    load();
  }, [load]);

  const entries = data?.entries ?? [];

  const open = (entry: WikiJournalEntryDto) => {
    setActionError(null);
    setOpened(entry);
  };

  const back = () => {
    setOpened(null);
    setActionError(null);
    load();
  };

  const doRetract = async (entry: WikiJournalEntryDto) => {
    const sourceRef = entrySourceRef(entry);
    if (!sourceRef || busy) return;
    setBusy(true);
    try {
      const r = await retractWikiSource(sourceRef);
      setConfirm(null);
      const parts = [
        r.revertedPackages.length ? `${r.revertedPackages.length} пакетов откатано` : '',
        r.unlinkedPackages.length ? `${r.unlinkedPackages.length} стр. очищено от сносок` : '',
        r.removedChronicles.length ? `${r.removedChronicles.length} хроник снято` : '',
        r.remainingPackages.length ? `${r.remainingPackages.length} пакетов остались (другой источник)` : '',
        r.markedSkip ? 'источник помечен «не разбирать»' : '',
      ].filter(Boolean);
      const failed = r.failed.length ? ` Частично: ${r.failed.join('; ')}` : '';
      // ToastKind не имеет «warning»: частичный исход — это «error» с текстом частичности
      showToast('Откат по источнику', `${parts.join(', ') || 'ничего не изменилось'}.${failed}`, r.failed.length ? 'error' : 'success');
      setOpened(null);
      load();
    } catch (e) {
      setConfirm(null);
      setActionError(e instanceof WikiError ? retractErrorText(e) : 'Не удалось откатить всё из этого источника');
    } finally {
      setBusy(false);
    }
  };

  // Что снимется при откате по источнику — из того, что видно в журнале сейчас: точный
  // исход (что по прообразам, что останется из-за другого источника) знает только бэк.
  // Число — по нормальной форме источника: окна одного чата считаются все (урок c1 S7c-1).
  const confirmRef = confirm ? entrySourceRef(confirm) : null;
  const related = confirmRef ? entries.filter(e => e.status === 'applied' && entrySourceRef(e) === confirmRef) : [];
  const relatedPages = uniq(related.flatMap(e => e.pages));
  const confirmMeta = confirm ? originMeta(confirm) : undefined;
  const confirmModal = !confirm ? null : (
    <ConfirmDialog
      title={`Откатить всё из этого ${confirmMeta?.label ?? 'источника'}?`}
      subtitle={
        <>
          <div>Снимется то, что Хранитель взял из этого источника за всё время. Знание из других источников останется.</div>
          <ul style={{ margin: `${SP.xs}px 0 0`, paddingLeft: SP.lg }}>
            <li>В журнале с этим источником: {related.length} применённых записей, {relatedPages.length} стр.</li>
            {confirm.origin === 'session' && (
              <li>Источник получит пометку «не разбирать» — иначе следующая ночь разберёт его заново.</li>
            )}
          </ul>
          <div style={{ marginTop: SP.xs }}>
            Точный исход бэк скажет при выполнении: что откатится по прообразам, а на каких страницах
            останутся утверждения с другим источником. Снятое хранится в журнале 90 дней.
          </div>
        </>
      }
      confirmLabel="Откатить всё"
      confirmVariant="danger"
      onConfirm={() => doRetract(confirm)}
      onCancel={() => setConfirm(null)}
    />
  );

  if (loadError) {
    const text = loadError instanceof WikiError ? wikiErrorText(loadError) : 'Не удалось прочитать «Журнал»';
    return (
      <div data-testid="wiki-journal-error" style={{ padding: SP.sm }}>
        <Notice tone="danger" icon={AlertTriangle} title={text} action={<Button variant="ghost" size="sm" onClick={load}>Повторить</Button>} />
      </div>
    );
  }
  const sectionOptions = (tree?.sections ?? []).map(s => ({ value: s.key, label: sectionPrefix(s) }));
  const originOptions = Object.entries(ORIGIN_META).map(([value, m]) => ({ value, label: m.label }));
  // Фильтры на экране и во время первой загрузки: сменить фильтр «во время загрузки» —
  // ровно тот сценарий, ради которого держится поколение запросов
  const filterRow = (
    <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.sm, padding: `${SP.sm}px ${SP.md}px 0` }}>
      <Select value={sectionFilter} onChange={setSectionFilter} placeholder="Все разделы" options={sectionOptions} />
      <Select value={originFilter} onChange={setOriginFilter} placeholder="Все источники" options={originOptions} />
    </div>
  );

  if (!data) return <>{filterRow}<LoadingSkeleton /></>;

  if (opened) {
    return (
      <>
        <JournalEntry
          entry={opened}
          busy={busy}
          actionError={actionError}
          onBack={back}
          onRetract={() => setConfirm(opened)}
        />
        {confirmModal}
      </>
    );
  }

  let lastDay: string | null = null;
  return (
    <div style={{ padding: `0 0 ${SP.md}px` }}>
      {filterRow}
      {data.invalidLines > 0 && (
        <div style={{ padding: `${SP.sm}px ${SP.md}px 0` }}>
          <Notice tone="warning" icon={AlertTriangle} title={`Нечитаемых строк журнала: ${data.invalidLines}`}>
            Они видны как сломанные и в журнал не берутся.
          </Notice>
        </div>
      )}
      {entries.length === 0 ? (
        <EmptyState
          icon={<History size={ICON_SIZE.xl} strokeWidth={ICON_STROKE} />}
          title="Журнал пустой"
          subtitle={
            <span style={{ maxWidth: 360 }}>
              Здесь появятся события вашей вики: применения, откаты, системные операции.
              {' '}{sectionFilter || originFilter ? 'Под выбранный фильтр ничего нет.' : ''}
            </span>
          }
        />
      ) : (
        <>
          {entries.map((e, i) => {
            const day = dayKey(e.ts);
            const header = day !== lastDay ? dayTitle(day) : null;
            lastDay = day;
            return (
              <div key={`${e.ts}|${e.section}|${e.op}|${i}`}>
                {header && (
                  <div style={{ padding: `${SP.md}px ${SP.md}px ${SP.xs}px`, fontSize: FS.xs, color: C.textMuted, fontFamily: FONT.mono }}>
                    {header}
                  </div>
                )}
                <JournalRow entry={e} onOpen={() => open(e)} />
              </div>
            );
          })}
          <div style={{ padding: `${SP.md}px ${SP.md}px 0`, fontSize: FS.xs, color: C.textMuted }}>
            Окно журнала — последние события; откат — 90 дней, дальше прообразы стираются.
          </div>
        </>
      )}
      {confirmModal}
    </div>
  );
}
