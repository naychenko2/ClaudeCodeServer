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
//
// «ИИ поменял моё» (F6b, ADR-038 предохранитель 4): чип-переключатель — серверный фильтр
// overrodeHuman=true (бэк сужает окно сам, фронт по списку не фильтрует); у таких записей в
// строке — вид действия («заменил» / «оспорил») и кнопка. Пары «вернуть моё» (statement/previous)
// строка журнала не несёт: она только в overrides[] страницы — поэтому «Вернуть мою» ведёт на
// страницу существующим каналом requestOpenWikiPage (стор держит один запрос: двойной клик не
// откроет второго), где хук возврата уже подключён (useRestoreWikiStatement, F3b). Спора
// возвращать нечего — текст человека на месте, его действие — «Открыть страницу».
//
// «Решения за ночь» (J-F2): второй вид той же ручки — утренняя сводка: четыре группы
// вердиктов облачного судьи за последнюю ночь и очередь нерешённого (J-F0,
// GET /api/wiki/journal?view=night). Окно считает фронт: бэк принимает только даты,
// а ночное окно сервера переходит через полночь — [вчера, завтра) наименьшее окно,
// накрывающее её целиком. «Откатить пакет» (мера 4) наконец имеет место: строка ночи
// несёт id пакета — дефект ead65823 закрыт этой ручкой.

import { useCallback, useEffect, useRef, useState } from 'react';
import { AlertTriangle, History, Moon } from 'lucide-react';
import {
  C, FONT, FS, R, SHADOW, SP, useIsMobile, openChatById, requestOpenWikiPage, showToast,
  WikiError, loadWikiJournal, retractWikiSource, useWikiTree,
  loadWikiNightDecisions, revertWikiPackage,
  type WikiJournalDto, type WikiJournalEntryOverrideDto,
  type WikiNightDecisionDto, type WikiNightDecisionsDto, type WikiRevertDto,
} from 'aihome_shell/kit';
import { Badge } from '../../../components/ui/Badge';
import { Button } from '../../../components/ui/Button';
import { Chip } from '../../../components/ui/Chip';
import { ConfirmDialog } from '../../../components/ui/ConfirmDialog';
import { EmptyState } from '../../../components/ui/EmptyState';
import { Notice } from '../../../components/ui/Notice';
import { SegmentedControl } from '../../../components/ui/Segmented';
import { Select } from '../../../components/ui/Select';
import { ICON_SIZE, ICON_STROKE } from '../../../components/ui/icons';
import { OVERRIDE_TEXT, ORIGIN_META, countWord, isOne, retractResultText, sectionPrefix, wikiErrorText } from './wikiLabels';

// ─── Словари подписей ───

// Событие журнала — что случилось (op — значения, которые бэк пишет в log.jsonl:
// WikiApplyService / WikiPackageRevert / WikiRetractService / WikiKeeper). Неизвестный op
// получает нейтральную подпись: сырое слово бэка человеку ничего не говорит, а смысл
// словарь незнакомому слову не выдумывает.
const OP_TEXT: Record<string, string> = {
  apply: 'применено',
  revert: 'откатано',
  retract: 'откат по источнику',
  relink: 'перелинкованы связи',
  adopt: 'принято как есть',
};

const UNKNOWN_OP_TEXT = 'другое событие';

// Бейдж статуса/политики говорит только то, чего не говорит слово события: для применённой
// записи — политику («через Входящие» — там был человек); откатанный после применения пакет
// виден на записи применения; «отбит проверками» — отдельный исход. Для записей
// revert/retract/relink/adopt событие уже названо op — бейдж только повторил бы его.
function entryBadge(e: WikiJournalEntryOverrideDto): { tone: 'success' | 'warning' | 'danger' | 'neutral'; text: string } | null {
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

// Вид «ИИ поменял моё» (F6b): «заменил» — ИИ заменил формулировку человека (replaced),
// «оспорил» — оспорил, не переписывая (disputed). Нераспознанный вид остаётся без метки:
// словарь не выдумывает смысл незнакомому слову (тот же приём, что UNKNOWN_OP_TEXT).
function kindBadge(e: WikiJournalEntryOverrideDto): { tone: 'info' | 'warning'; text: string } | null {
  if (!e.overrodeHuman || e.overrideReverted === true) return null;
  if (e.overrodeKind === 'replaced') return { tone: 'info', text: OVERRIDE_TEXT.kindReplaced };
  if (e.overrodeKind === 'disputed') return { tone: 'warning', text: OVERRIDE_TEXT.kindDisputed };
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
function entrySourceRef(e: WikiJournalEntryOverrideDto): string | null {
  if (!e.source || !e.origin) return null;
  const first = e.source.split(', ')[0];
  const rest = first.startsWith(`${e.origin}:`) ? first.slice(e.origin.length + 1) : first;
  const hash = rest.indexOf('#');
  return `${e.origin}:${hash < 0 ? rest : rest.slice(0, hash)}`;
}

// Id чата для перехода: та же граница, что у бэкового IdPartOf (без суффикса диапазона).
function entryChatId(e: WikiJournalEntryOverrideDto): string | null {
  if (e.origin !== 'session') return null;
  const id = entrySourceRef(e)?.slice('session:'.length) ?? '';
  return id || null;
}

const originMeta = (e: WikiJournalEntryOverrideDto) => (e.origin ? ORIGIN_META[e.origin] : undefined);
const opText = (e: WikiJournalEntryOverrideDto) => OP_TEXT[e.op] ?? UNKNOWN_OP_TEXT;

// Название записи: имя источника (название чата, заголовок заметки) — то, что человек узнаёт;
// без имени (чужой или несуществующий чат, origin без имени) — карточка; записи без источника
// (relink, adopt) — само событие.
function entryTitle(e: WikiJournalEntryOverrideDto): string {
  return e.sourceName ?? e.source ?? opText(e);
}

function LoadingSkeleton() {
  const bar = (w: number) => (
    <div key={w} style={{ height: 10, width: `${w}%`, background: C.bgInset, borderRadius: R.sm, margin: `${SP.md}px 0` }} />
  );
  return <div style={{ padding: SP.md, opacity: 0.7 }} aria-hidden>{bar(60)}{bar(85)}{bar(45)}</div>;
}

// ─── Строка списка ───

// Действие строки «ИИ поменял моё» (F6b): вести на страницу существующим каналом открытия
// (requestOpenWikiPage) — там «Вернуть мою» уже подключена к хуку, а стор держит один запрос:
// двойной клик не откроет второго. Записи без страниц вести некуда — кнопки нет.
function rowOpenPage(e: WikiJournalEntryOverrideDto): (() => void) | null {
  if (e.pages.length === 0) return null;
  return () => requestOpenWikiPage({ section: e.section, path: e.overridePageKey ?? e.pages[0] });
}

function JournalRow({ entry, onOpen }: { entry: WikiJournalEntryOverrideDto; onOpen: () => void }) {
  const isMobile = useIsMobile();
  const tree = useWikiTree();
  const meta = originMeta(entry);
  const Icon = meta?.icon ?? History;
  const section = tree?.sections.find(s => s.key === entry.section);
  const badge = entryBadge(entry);
  const kind = kindBadge(entry);
  const openPage = rowOpenPage(entry);
  return (
    <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.sm, alignItems: 'center' }}>
      <button
        type="button"
        data-testid="wiki-journal-row"
        onClick={onOpen}
        style={{
          display: 'flex', gap: SP.sm, padding: `${SP.sm}px ${SP.md}px`, borderRadius: R.md,
          border: 'none', background: 'transparent', cursor: 'pointer', textAlign: 'left',
          flex: '1 1 0', minWidth: 0, fontFamily: FONT.sans, color: C.textPrimary,
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
          {(badge || kind) && (
            <span style={{ display: 'flex', flexWrap: 'wrap', gap: SP.xs, marginTop: 3 }}>
              {badge && <Badge tone={badge.tone}>{badge.text}</Badge>}
              {kind && <Badge tone={kind.tone}>{kind.text}</Badge>}
            </span>
          )}
        </span>
        {!isMobile && (
          <span style={{ flexShrink: 0, fontSize: FS.xs, color: C.textMuted, fontFamily: FONT.mono }}>{timeText(entry.ts)}</span>
        )}
      </button>
      {entry.overrodeHuman && entry.overrideReverted !== true && openPage && (entry.overrodeKind === 'replaced' ? (
        <Button variant="secondary" size="sm" dataTestId="wiki-journal-restore" onClick={openPage}>{OVERRIDE_TEXT.restore}</Button>
      ) : entry.overrodeKind === 'disputed' ? (
        <Button variant="ghost" size="sm" dataTestId="wiki-journal-open-page" onClick={openPage}>{OVERRIDE_TEXT.openPage}</Button>
      ) : null)}
    </div>
  );
}

// ─── Запись журнала ───

function JournalEntry({
  entry, busy, actionError, onBack, onRetract,
}: {
  entry: WikiJournalEntryOverrideDto;
  busy: boolean;
  actionError: string | null;
  onBack: () => void;
  onRetract: () => void;
}) {
  const tree = useWikiTree();
  const meta = originMeta(entry);
  const badge = entryBadge(entry);
  const kind = kindBadge(entry);
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
        {kind && <Badge tone={kind.tone}>{kind.text}</Badge>}
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
            Откатить всё {meta?.genitive ?? 'из этого источника'}
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

// ─── «Решения за ночь» (J-F2) ───

const NIGHT_VIEW_OPTIONS: { value: 'all' | 'night'; label: string }[] = [
  { value: 'all', label: 'Все записи' },
  { value: 'night', label: 'Решения за ночь' },
];

const localDay = (d: Date) => `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;

// Окно «последней ночи»: [вчера, завтра) локальных дат. Полное окно сервера переходит
// через полночь (WikiNightlyService.InWindow: start > end), а from/to бэка — даты без
// времени: любое более узкое окно отрезает часть ночи до или после полуночи. Дневная
// часть окна не случайна: проход судья идёт не только в конце ночи, но и после спорной
// заявки (4a7248841) — сегодняшние вердикты утренней сводке тоже нужны.
function nightWindow(): { from: string; to: string } {
  const d = new Date();
  d.setDate(d.getDate() - 1);
  const from = localDay(d);
  d.setDate(d.getDate() + 2);
  return { from, to: localDay(d) };
}

const dayNum = (key: string) => {
  const [, m, d] = key.split('-');
  return `${d}.${m}`;
};

type NightGroupKey = keyof WikiNightDecisionsDto;

// Группа = поле ответа бэка + её заголовок. В «Отклонено кодом» входят и операции, по которым
// судья не вынес решения за 3 попытки: бэк метит их JudgeModelCode (WikiJudgeSweep.cs:368-374),
// и читатель ночи кладёт в эту же группу (WikiJournalReader.cs:306) — отдельной ветки на
// фронте для них нет.
const NIGHT_GROUPS: { key: NightGroupKey; title: string }[] = [
  { key: 'judgeAccepted', title: 'Принято судьёй' },
  { key: 'judgeRejected', title: 'Отклонено судьёй' },
  { key: 'codeRejected', title: 'Отклонено кодом' },
  { key: 'waiting', title: 'Ждут судью' },
];

// Решение в строке — короткое: полное имя уже в заголовке группы. У «Ждут судью» решения
// нет вовсе — строка без бейджа: пустое поле честнее выдумки.
const GROUP_BADGE: Record<Exclude<NightGroupKey, 'waiting'>, { tone: 'success' | 'danger'; text: string }> = {
  judgeAccepted: { tone: 'success', text: 'принято' },
  judgeRejected: { tone: 'danger', text: 'отклонено' },
  codeRejected: { tone: 'danger', text: 'отклонено кодом' },
};

// Строка ночи не несёт раздела (в отличие от строки журнала): только ключ страницы.
// Раздел берётся из дерева по наиболее длинному префиксу ключа (ключ страницы начинается
// с ключа раздела + '/'): раздела в дереве нет — открыть страницу нечем, строка показывает
// ключ текстом, без ссылки.
function pageSectionKey(tree: ReturnType<typeof useWikiTree>, page: string): string | null {
  let best: string | null = null;
  for (const s of tree?.sections ?? []) {
    if ((page === s.key || page.startsWith(`${s.key}/`)) && (!best || s.key.length > best.length)) best = s.key;
  }
  return best;
}

// Исход отката пакета — текстом (формулировки черновые, тексты фичи за Софьей): счёт —
// по возвращённым страницам, согласование countWord (тот же приём, что retractResultText).
function revertPackageResultText(r: WikiRevertDto): string {
  const n = r.restoredPages.length;
  if (!n) return 'ничего не изменилось';
  // причастие по правилу файла: при «1» согласуется с существительным, при остальных — средний род
  return `${countWord(n, 'страница', 'страницы', 'страниц')} ${isOne(n) ? 'возвращена' : 'возвращено'}`;
}

// Отказы отката пакета — русским текстом по коду бэка (тот же приём, что retractErrorText):
// 'wiki_revert_status' — состояние пакета не «applied» (включая уже откатанный),
// 'wiki_revert_changed' — страницы изменены после применения (перечень страниц в отказе
// несёт WikiRevertError; здесь — короткий текст, перечень человек увидит на странице).
function revertPackageErrorText(e: WikiError): string {
  switch (e.code) {
    case 'wiki_revert_status': return 'Пакет в этом состоянии откатать нельзя';
    case 'wiki_revert_changed': return 'Страницы изменились после применения: проверьте их и повтори';
  }
  return wikiErrorText(e);
}

// Строка операции ночи: ссылка на страницу (открывает её существующим каналом
// requestOpenWikiPage), claim, решение, «почему» судьи, исходная причина спора и
// «Откатить пакет» — только у «Принято судьёй»: откатывают применённый пакет
// (WikiController.cs:178-179), а вердикт судьи замкнут применением (J-J2b): у прочих
// групп откатывать нечего. Всё оборачивается: на 360 px строка не даёт горизонтальной
// прокрутки.
function NightRow({ row, group, busy, onRevert }: {
  row: WikiNightDecisionDto;
  group: NightGroupKey;
  busy: boolean;
  onRevert: () => void;
}) {
  const tree = useWikiTree();
  const sectionKey = pageSectionKey(tree, row.page);
  const section = sectionKey ? tree?.sections.find(s => s.key === sectionKey) : undefined;
  const badge = group === 'waiting' ? null : GROUP_BADGE[group];
  return (
    <div data-testid="wiki-night-row" style={{ padding: `${SP.sm}px ${SP.md}px`, display: 'flex', flexDirection: 'column', gap: SP.xs }}>
      {(section || badge) && (
        <span style={{ display: 'flex', flexWrap: 'wrap', gap: SP.xs, alignItems: 'center' }}>
          {section && (
            <span style={{ fontSize: FS.xs, color: C.textMuted, fontFamily: FONT.mono }}>{sectionPrefix(section)}</span>
          )}
          {badge && <Badge tone={badge.tone}>{badge.text}</Badge>}
        </span>
      )}
      {sectionKey ? (
        <button
          type="button"
          data-testid="wiki-night-open"
          onClick={() => requestOpenWikiPage({ section: sectionKey, path: row.page })}
          style={{
            border: 'none', background: 'transparent', padding: 0, cursor: 'pointer', textAlign: 'left',
            fontFamily: FONT.mono, fontSize: FS.base, fontWeight: 500, color: C.textHeading, overflowWrap: 'anywhere',
          }}
        >
          {row.page}
        </button>
      ) : (
        <span style={{ fontFamily: FONT.mono, fontSize: FS.base, color: C.textMuted, overflowWrap: 'anywhere' }}>{row.page}</span>
      )}
      {row.claim && (
        <span style={{ fontSize: FS.sm, color: C.textSecondary, overflowWrap: 'break-word' }}>{row.claim}</span>
      )}
      {row.judgeReason && (
        <span style={{ fontSize: FS.sm, color: C.textSecondary, overflowWrap: 'break-word' }}>
          <span style={{ color: C.textMuted }}>почему:</span> {row.judgeReason}
        </span>
      )}
      {row.reason && (
        <span style={{ fontSize: FS.sm, color: C.textMuted, overflowWrap: 'break-word' }}>причина спора: {row.reason}</span>
      )}
      {group === 'judgeAccepted' && (
        <span style={{ marginTop: SP.xs }}>
          <Button variant="secondary" size="sm" dataTestId="wiki-night-revert" disabled={busy} onClick={onRevert}>Откатить пакет</Button>
        </span>
      )}
    </div>
  );
}

// ─── Подвкладка ───

// sectionScope (W9b): раздел задаёт снаружи — в панели его выбирает чип в шапке, и Select
// в теле дублировал бы его. undefined — прежняя раскладка (старый WikiTab, подвкладка без
// флага): свой sectionFilter и Select раздела. 'all'/null — весь журнал, без фильтра.
// Смена пропа перезапрашивает журнал: load держит действующий раздел в зависимостях,
// эффект — load; поколение (loadSeq) то же, что у фильтров: ответ прежнего раздела не ляжет
// на экран нового.
export function WikiJournal(
  // selectedKey — прежнее звание из реестра (WikiTab передаёт выбранный раздел): журнал его
  // не читает — свой раздел здесь либо свой Select (без пропа), либо sectionScope.
  { sectionScope }: { selectedKey?: string | null; sectionScope?: string | null | 'all' },
) {
  const tree = useWikiTree();
  const [data, setData] = useState<WikiJournalDto | null>(null);
  const [loadError, setLoadError] = useState<unknown>(null);
  // '' — все разделы / все источники (пункт-заглушка Select); с sectionScope фильтр раздела здесь не живёт
  const [sectionFilter, setSectionFilter] = useState('');
  const [originFilter, setOriginFilter] = useState('');
  // «ИИ поменял моё» (F6b): чип-переключатель — живой, пока не снят; фильтр — серверный
  const [overrodeOnly, setOverrodeOnly] = useState(false);
  const [opened, setOpened] = useState<WikiJournalEntryOverrideDto | null>(null);
  const [confirm, setConfirm] = useState<WikiJournalEntryOverrideDto | null>(null);
  const [actionError, setActionError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  // Вид (J-F2): 'all' — прежний журнал, 'night' — утренняя сводка. Данные вида живут
  // отдельно: при смене вида старый список не рендерится, но и не сбрасывается — тот же
  // порядок, что у фильтров (старый остаётся до ответа нового).
  const [view, setView] = useState<'all' | 'night'>('all');
  // Окно запроса храния рядом с ответом: подпись окна должна соответствовать тем данным,
  // что на экране (пересчёт на рендере показал бы окно другой ночи).
  const [night, setNight] = useState<{ from: string; to: string; data: WikiNightDecisionsDto } | null>(null);
  const [nightError, setNightError] = useState<string | null>(null);
  // Поколение загрузок (урок S5b/S5e, образец — loadSeq в WikiInbox): только последний запрос
  // пишет список; filters живут в запросе — поздний ответ прежнего фильтра не затеряет новый
  const loadSeq = useRef(0);

  // Действующий раздел (W9b): с пропом — из пропа ('all'/null — без фильтра раздела),
  // без пропа — из своего Select. Значение примитивное: load пересоздаётся только при
  // настоящей смене раздела, а не на каждом рендере.
  const scoped = sectionScope !== undefined;
  const activeSection = scoped
    ? (sectionScope === 'all' || sectionScope === null ? null : sectionScope)
    : (sectionFilter || null);

  const load = useCallback(() => {
    const mine = ++loadSeq.current;
    setLoadError(null);
    // Тот же инвариант поколения для обоих видов: поздний ответ одного вида не перезатрёт
    // экран другого (урок S5b/S5e).
    if (view === 'night') {
      const w = nightWindow();
      loadWikiNightDecisions(w.from, w.to)
        .then(d => { if (loadSeq.current === mine) setNight({ ...w, data: d }); })
        .catch(e => { if (loadSeq.current === mine) setLoadError(e); });
      return;
    }
    loadWikiJournal({ section: activeSection, origin: originFilter || null, overrodeHuman: overrodeOnly })
      .then(d => { if (loadSeq.current === mine) setData(d); })
      .catch(e => { if (loadSeq.current === mine) setLoadError(e); });
  }, [view, activeSection, originFilter, overrodeOnly]);

  useEffect(() => {
    // eslint-disable-next-line react-hooks/set-state-in-effect -- старт загрузки при монтировании подвкладки и при смене фильтра: другого места старта нет, список живёт в компоненте
    load();
  }, [load]);

  const entries = data?.entries ?? [];

  const open = (entry: WikiJournalEntryOverrideDto) => {
    setActionError(null);
    setOpened(entry);
  };

  const back = () => {
    setOpened(null);
    setActionError(null);
    load();
  };

  const doRetract = async (entry: WikiJournalEntryOverrideDto) => {
    const sourceRef = entrySourceRef(entry);
    if (!sourceRef || busy) return;
    setBusy(true);
    try {
      const r = await retractWikiSource(sourceRef);
      setConfirm(null);
      // ToastKind не имеет «warning»: частичный исход — это «error» с текстом частичности
      showToast('Откат по источнику', retractResultText(r, 'источник'), r.failed.length ? 'error' : 'success');
      setOpened(null);
      load();
    } catch (e) {
      setConfirm(null);
      setActionError(e instanceof WikiError ? retractErrorText(e) : 'Не удалось откатить всё из этого источника');
    } finally {
      setBusy(false);
    }
  };

  // «Откатить пакет» (мера 4, J-F2): в один клик, без подтверждения — строка уже назвала
  // всё, что откатывается (страница, claim, вердикт). Успех — перечитка ночи: откатанный
  // пакет уходит из «Принято судьёй» (его операций больше нет в окне вердиктов как принятых;
  // сам откат в этой сводке не показывается — она про вердикты, журнал — про события).
  const doRevertPackage = async (row: WikiNightDecisionDto) => {
    if (busy) return;
    setBusy(true);
    try {
      const r = await revertWikiPackage(row.packageId);
      setNightError(null);
      showToast('Пакет откатан', revertPackageResultText(r), r.restoredPages.length ? 'success' : 'error');
      load();
    } catch (e) {
      setNightError(e instanceof WikiError ? revertPackageErrorText(e) : 'Не удалось откатить пакет');
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
      title={`Откатить всё ${confirmMeta?.genitive ?? 'из этого источника'}?`}
      subtitle={
        <>
          <div>Снимется то, что Хранитель взял из этого источника за всё время. Знание из других источников останется.</div>
          <ul style={{ margin: `${SP.xs}px 0 0`, paddingLeft: SP.lg }}>
            <li>В журнале с этим источником: {countWord(related.length, 'применённая запись', 'применённые записи', 'применённых записей')}, {relatedPages.length} стр.</li>
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

  // Переключатель вида — на верхнем уровне обоих видов (в раскрытой записи журнала его
  // нет, как там нет и фильтров: это подэкран одного вида), и в состоянии ошибки тоже
  // (ревью bb48be19): если вид ночи падает (500, обрыв сети), вернуться к «Все записи»
  // должно быть чем — состояние ошибки остаётся одним Notice с «Повторить», добавляется
  // только сам переключатель над ним. Фильтры — только у журнала: бэк игнорирует их для
  // вида ночи (WikiController.cs:615-616), показывать их было бы обещанием, которого
  // вид не выполняет.
  const viewRow = (
    <div style={{ padding: `${SP.sm}px ${SP.md}px 0` }}>
      <SegmentedControl value={view} options={NIGHT_VIEW_OPTIONS} onChange={setView} />
    </div>
  );

  if (loadError) {
    const text = loadError instanceof WikiError
      ? wikiErrorText(loadError)
      : (view === 'night' ? 'Не удалось прочитать «Решения за ночь»' : 'Не удалось прочитать «Журнал»');
    return (
      <div data-testid="wiki-journal-error" style={{ padding: SP.sm }}>
        {viewRow}
        <Notice tone="danger" icon={AlertTriangle} title={text} action={<Button variant="ghost" size="sm" onClick={load}>Повторить</Button>} />
      </div>
    );
  }
  const sectionOptions = (tree?.sections ?? []).map(s => ({ value: s.key, label: sectionPrefix(s) }));
  const originOptions = Object.entries(ORIGIN_META).map(([value, m]) => ({ value, label: m.label }));
  // Число на чипе — записи «ИИ поменял моё» в текущем окне: окно читается то же с фильтром и
  // без него (бэк сужает тот же хвост), поэтому число не «прыгает» при включении фильтра
  const overrodeCount = entries.filter(e => e.overrodeHuman).length;
  // Фильтры на экране и во время первой загрузки: сменить фильтр «во время загрузки» —
  // ровно тот сценарий, ради которого держится поколение запросов
  const filterRow = (
    <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.sm, padding: `${SP.sm}px ${SP.md}px 0` }}>
      {/* Select раздела — только без пропа: с sectionScope раздел выбирает чип в шапке панели (W9b) */}
      {!scoped && <Select value={sectionFilter} onChange={setSectionFilter} placeholder="Все разделы" options={sectionOptions} />}
      <Select value={originFilter} onChange={setOriginFilter} placeholder="Все источники" options={originOptions} />
      <Chip variant="toggle" selected={overrodeOnly} onClick={() => setOverrodeOnly(v => !v)}>
        {OVERRIDE_TEXT.filter}
        {data ? <span style={{ fontWeight: 700 }}>{overrodeCount}</span> : null}
      </Chip>
    </div>
  );

  if (view === 'night') {
    if (!night) return <>{viewRow}<LoadingSkeleton /></>;
    // Пустая группа не показывается вовсе: «Отклонено кодом: 0» — это не сводка, а шум.
    const groups = NIGHT_GROUPS.map(g => ({ ...g, rows: night.data[g.key] })).filter(g => g.rows.length > 0);
    return (
      <div style={{ padding: `0 0 ${SP.md}px` }}>
        {viewRow}
        <div data-testid="wiki-night-window" style={{ padding: `${SP.sm}px ${SP.md}px 0`, fontSize: FS.xs, color: C.textMuted, fontFamily: FONT.mono }}>
          {`Окно вердиктов: с ${dayNum(night.from)} (включая) по ${dayNum(night.to)} (не включая); очередь — вне окна: она про «сейчас»`}
        </div>
        {nightError && (
          <div style={{ marginTop: SP.sm }}>
            <Notice tone="danger" icon={AlertTriangle} title={nightError} />
          </div>
        )}
        {groups.length === 0 ? (
          <EmptyState
            icon={<Moon size={ICON_SIZE.xl} strokeWidth={ICON_STROKE} />}
            title="За ночь решений нет"
            subtitle={
              <span style={{ maxWidth: 360 }}>
                Судья в этом окне ничего не решил, и пакетов, ждущих решения, тоже нет.
              </span>
            }
          />
        ) : (
          groups.map(g => (
            <div key={g.key} data-testid="wiki-night-group">
              <div style={{ padding: `${SP.md}px ${SP.md}px ${SP.xs}px`, display: 'flex', gap: SP.sm, alignItems: 'baseline' }}>
                <span style={{ fontSize: FS.sm, fontWeight: 600, color: C.textHeading }}>{g.title}</span>
                <span style={{ fontSize: FS.xs, fontFamily: FONT.mono, color: C.textMuted }}>{g.rows.length}</span>
              </div>
              {g.rows.map(r => (
                <NightRow key={`${g.key}|${r.packageId}|${r.page}`} row={r} group={g.key} busy={busy} onRevert={() => { void doRevertPackage(r); }} />
              ))}
            </div>
          ))
        )}
      </div>
    );
  }

  if (!data) return <>{viewRow}{filterRow}<LoadingSkeleton /></>;

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
      {viewRow}
      {filterRow}
      {data.invalidLines > 0 && (
        <div style={{ padding: `${SP.sm}px ${SP.md}px 0` }}>
          <Notice tone="warning" icon={AlertTriangle} title={`Нечитаемых строк журнала: ${data.invalidLines}`}>
            {`Число — по всему прочитанному окну (${data.linesRead} строк), а не по текущему фильтру. Такие строки в журнал не берутся.`}
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
              {' '}{activeSection || originFilter || overrodeOnly ? 'Под выбранный фильтр ничего нет.' : ''}
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
        </>
      )}
      <div data-testid="wiki-journal-window" style={{ padding: `${SP.md}px ${SP.md}px 0`, fontSize: FS.xs, color: C.textMuted }}>
        {`Окно журнала — последние ${data.take} строк, прочитано ${data.linesRead}; показано записей: ${entries.length}. Откат — 90 дней, дальше прообразы стираются.`}
      </div>
      {confirmModal}
    </div>
  );
}
