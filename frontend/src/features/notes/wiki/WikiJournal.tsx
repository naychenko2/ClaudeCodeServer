// «Журнал» (S7c-1, макет llm-wiki-v2 §6): проекция применённых пакетов своей вики — когда,
// какой раздел, какой источник, политика («само» / «через Входящие»), что изменилось — и обе
// кнопки отката (меры 4–5 ADR-035 §12г).
//
// Данные: GET /api/wiki/packages без inbox — «журнал состояний» из контракта самой ручки.
// Отдельной ручки журнала у бэка нет (пункт 5 «Нужно от бэка» макета) — когда она придёт,
// источник данных переключится на неё; откат и retract от этого не зависят. Поэтому здесь
// показаны состояния пакетов: applied, rejected_by_checks («отбит проверками»), reverted
// («откат»). proposed живёт в «Входящих»; rejected/apply_failed/stale — не факты изменения
// вики, их история — на экране пакета.
//
// «Что изменилось» — намерения операций как «предполагаемое изменение» через общий DiffView:
// отрендеренного результата в DTO пакета нет, обещания результата не даём.
//
// Порядок запросов — тот же инвариант, что у «Входящих» (урок S5b/S5e): ответ кладётся,
// только если его поколение последнее. Кнопки действий заблокированы на время запроса.
// Недоверенный контент (тексты операций, ссылки источников, причины) — только текстом.
// Отказ бэка показывается как есть, с перечнем изменённых страниц (wiki_revert_changed).

import { useCallback, useEffect, useRef, useState } from 'react';
import { AlertTriangle, History, Undo2 } from 'lucide-react';
import { C, FONT, FS, R, SHADOW, SP } from '../../../lib/design';
import { Badge } from '../../../components/ui/Badge';
import { Button } from '../../../components/ui/Button';
import { ConfirmDialog } from '../../../components/ui/ConfirmDialog';
import { EmptyState } from '../../../components/ui/EmptyState';
import { Notice } from '../../../components/ui/Notice';
import { Select } from '../../../components/ui/Select';
import { ICON_SIZE, ICON_STROKE } from '../../../components/ui/icons';
import { DiffView } from '../../../components/DiffView';
import { useIsMobile } from '../../../lib/breakpoints';
import { openChatById } from '../../../lib/openChat';
import { showToast } from '../../../lib/toast';
import {
  WikiError,
  WikiRevertError,
  listWikiJournal,
  revertWikiPackage,
  retractWikiSource,
  useWikiTree,
  type WikiPackageDto,
  type WikiPackageOperationDto,
  type WikiPackageSourceDto,
} from '../../../lib/wiki';
import { ORIGIN_META } from './WikiInbox';
import { sectionPrefix, wikiErrorText } from './wikiLabels';

// ─── Словари подписей ───

// Что из журнала состояний показывать в «Журнале» (см. шапку файла).
const JOURNAL_STATUSES = new Set(['applied', 'rejected_by_checks', 'reverted']);

// Метки операций — чем они были для страницы (намерение, не результат).
const OP_LABEL: Record<string, string> = {
  create: 'новая страница', append: 'дописано', supersede: 'замещено',
  conflict: 'противоречие', link: 'связь',
};

// Политика пакета — по его операциям (решение 12г): через «Входящие» шло то, где был человек.
type Policy = 'auto' | 'inbox' | 'checks' | 'reverted';
function packagePolicy(p: WikiPackageDto): Policy {
  if (p.status === 'rejected_by_checks') return 'checks';
  if (p.status === 'reverted') return 'reverted';
  return p.operations.some(o => o.decision === 'inbox') ? 'inbox' : 'auto';
}

const POLICY_BADGE: Record<Policy, { tone: 'success' | 'warning' | 'danger' | 'neutral'; text: string }> = {
  auto: { tone: 'success', text: 'применилось само' },
  inbox: { tone: 'warning', text: 'через Входящие' },
  checks: { tone: 'danger', text: 'отбит проверками' },
  reverted: { tone: 'neutral', text: 'откат' },
};

// Отказы отката — русским текстом по коду бэка (тот же приём, что pkgErrorText для «Входящих»).
// Формулировки — черновые, тексты фичи за Софьей.
function revertErrorText(e: WikiError): string {
  switch (e.code) {
    case 'wiki_revert_changed': return 'Страницу меняли после этого пакета — по прообразам её не вернуть';
    case 'wiki_revert_status': return 'В своём состоянии пакет не откатывается';
    case 'wiki_revert_no_applied': return 'Состояния применения не записано — откатывать нечего';
    case 'wiki_revert_no_preimages': return 'Прообразы страниц не сохранились — откат невозможен';
    case 'wiki_revert_claim': return 'Откат уже выполняется';
    case 'wiki_revert_manifest': return 'Манифест раздела не прочитан';
    case 'wiki_revert_off': return 'Откат для этой вики недоступен';
    case 'wiki_revert_shape': return 'Не удалось отправить запрос отката';
    case 'wiki_revert_internal': return 'Внутренний сбой отката';
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

// Источник пакета для отката по нему — форма SourceRef бэка «{origin}:{id}» БЕЗ диапазона
// окна: Ref окна храненияется с диапазоном («session:{id}#m1-9», захват в WikiChatIngestService),
// а бэк по карточке без диапазона накрывает все пакеты источника в любой его части (RefMatches,
// WikiRetractService.cs). Здесь — зеркало бэковых OriginOf/IdPartOf: префикс по origin,
// карточка — до первого «#». Для note:/file: без диапазона в ref ничего не меняется.
// По этой же нормальной форме модалка считает применённые пакеты источника: сравнение
// целых ref дало бы 1 вместо реального числа окон одного чата.
function sourceRefOf(s: WikiPackageSourceDto): string {
  const raw = s.ref.startsWith(`${s.origin}:`) ? s.ref.slice(s.origin.length + 1) : s.ref;
  const hash = raw.indexOf('#');
  return `${s.origin}:${hash < 0 ? raw : raw.slice(0, hash)}`;
}

// Id чата для перехода: та же граница, что у бэкового IdPartOf (без суффикса диапазона).
function sessionChatId(s: WikiPackageSourceDto): string {
  return s.ref.replace(/^session:/, '').split('#')[0];
}

function primarySource(p: WikiPackageDto): WikiPackageSourceDto | null {
  return p.sources[0] ?? null;
}

// «Предполагаемое изменение» страницы — намерения её операций как unified-diff:
// замещаемое — «-», добавляемое — «+». Отрендеренного результата нет — DiffView рисует
// то, что Хранитель собирался сделать, а не то, что получилось.
function expectedDiffFor(ops: WikiPackageOperationDto[]): string {
  const lines: string[] = [];
  for (const o of ops) {
    if (o.op === 'supersede' && o.target) lines.push(`- ${o.target}`);
    if (o.text) lines.push(`+ ${o.text}`);
    else if (o.target) lines.push(`+ ${o.target}`);
  }
  return lines.join('\n');
}

function LoadingSkeleton() {
  const bar = (w: number) => (
    <div key={w} style={{ height: 10, width: `${w}%`, background: C.bgInset, borderRadius: R.sm, margin: `${SP.md}px 0` }} />
  );
  return <div style={{ padding: SP.md, opacity: 0.7 }} aria-hidden>{bar(60)}{bar(85)}{bar(45)}</div>;
}

// ─── Строка списка ───

function JournalRow({ pkg, onOpen }: { pkg: WikiPackageDto; onOpen: () => void }) {
  const isMobile = useIsMobile();
  const tree = useWikiTree();
  const src = primarySource(pkg);
  const meta = src ? ORIGIN_META[src.origin] : undefined;
  const Icon = meta?.icon ?? History;
  const section = tree?.sections.find(s => s.key === pkg.section);
  const pol = POLICY_BADGE[packagePolicy(pkg)];
  const pages = uniq(pkg.operations.map(o => o.page));
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
          {section ? sectionPrefix(section) : pkg.section} · {meta?.label ?? src?.origin ?? 'источник'}
        </span>
        <span style={{ display: 'block', fontSize: FS.base, fontWeight: 500, color: C.textHeading, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
          {src?.ref ?? 'Без источника'}
        </span>
        <span style={{ display: 'block', fontSize: FS.sm, color: C.textMuted, marginTop: 2 }}>
          {pkg.operations.length} операций · {pages.length} стр.
        </span>
        <span style={{ display: 'block', marginTop: 3 }}><Badge tone={pol.tone}>{pol.text}</Badge></span>
      </span>
      {!isMobile && (
        <span style={{ flexShrink: 0, fontSize: FS.xs, color: C.textMuted, fontFamily: FONT.mono }}>{timeText(pkg.updatedAt)}</span>
      )}
    </button>
  );
}

// ─── Запись журнала ───

function JournalEntry({
  pkg, busy, actionError, onBack, onRevert, onRetract,
}: {
  pkg: WikiPackageDto;
  busy: boolean;
  actionError: { text: string; pages: string[] } | null;
  onBack: () => void;
  onRevert: () => void;
  onRetract: () => void;
}) {
  const tree = useWikiTree();
  const src = primarySource(pkg);
  const meta = src ? ORIGIN_META[src.origin] : undefined;
  const pol = POLICY_BADGE[packagePolicy(pkg)];
  const section = tree?.sections.find(s => s.key === pkg.section);
  const pages = uniq(pkg.operations.map(o => o.page));
  const byPage = new Map<string, WikiPackageOperationDto[]>();
  for (const o of pkg.operations) {
    const arr = byPage.get(o.page) ?? [];
    arr.push(o);
    byPage.set(o.page, arr);
  }
  const chatId = src && src.origin === 'session' ? sessionChatId(src) : null;
  return (
    <div style={{ padding: `0 ${SP.md}px ${SP.md}px` }}>
      <Button variant="ghost" size="sm" dataTestId="wiki-journal-back" ariaLabel="Вернуться к списку «Журнала»" onClick={onBack}>Назад</Button>
      <div style={{ fontSize: FS.xs, color: C.textMuted, fontFamily: FONT.mono, marginTop: SP.sm }}>
        Журнал · {timeText(pkg.updatedAt)}
      </div>
      <div style={{ fontSize: FS.xs, color: C.textMuted, marginTop: 2 }}>
        {section ? sectionPrefix(section) : pkg.section}
      </div>
      <h2 style={{ margin: `${SP.xs}px 0 0`, fontFamily: FONT.serif, fontSize: FS.lg, color: C.textHeading, overflowWrap: 'break-word' }}>
        {src?.ref ?? 'Без источника'}
      </h2>
      <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.sm, alignItems: 'center', marginTop: SP.sm }}>
        <Badge tone={pol.tone}>{pol.text}</Badge>
        {src && <span style={{ fontSize: FS.xs, fontFamily: FONT.mono, color: C.textMuted, overflowWrap: 'anywhere' }}>{sourceRefOf(src)}</span>}
        <span style={{ fontSize: FS.xs, color: C.textMuted }}>{pkg.operations.length} операций · {pages.length} стр.</span>
      </div>
      {pkg.status === 'rejected_by_checks' && (
        <div style={{ marginTop: SP.md }}>
          <Notice tone="danger" icon={AlertTriangle} title="Отбит проверками кода — в вики ничего не записано">
            {pkg.statusReason ?? 'Такие правки не применяются и во «Входящие» не идут: показывать нечего.'}
          </Notice>
        </div>
      )}
      {pkg.status === 'reverted' && (
        <div style={{ marginTop: SP.md }}>
          <Notice tone="info" icon={Undo2} title="Пакет откатан">
            {pkg.statusReason ?? 'Страницы возвращены к состоянию до пакета.'}
          </Notice>
        </div>
      )}
      {actionError && (
        <div style={{ marginTop: SP.md }}>
          <Notice tone="danger" icon={AlertTriangle} title={actionError.text}>
            {actionError.pages.length > 0 && (
              <>
                <span>Изменённые страницы:</span>
                <ul style={{ margin: `${SP.xs}px 0 0`, paddingLeft: SP.lg }}>
                  {actionError.pages.map(p => (
                    <li key={p} style={{ fontFamily: FONT.mono, fontSize: FS.sm, overflowWrap: 'anywhere' }}>{p}</li>
                  ))}
                </ul>
              </>
            )}
          </Notice>
        </div>
      )}
      <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.sm, marginTop: SP.md }}>
        {pkg.status === 'applied' && (
          <Button variant="ghost" size="sm" dataTestId="wiki-journal-revert" disabled={busy} onClick={onRevert}>
            Откатить пакет
          </Button>
        )}
        {src && (
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
      {pkg.status !== 'rejected_by_checks' && byPage.size > 0 && (
        <>
          <h3 style={{ margin: `${SP.lg}px 0 ${SP.sm}px`, fontSize: FS.md, color: C.textHeading }}>Предполагаемое изменение</h3>
          <div style={{ fontSize: FS.sm, color: C.textMuted, marginBottom: SP.sm }}>
            Так Хранитель собирался изменить страницы; итоговый результат может отличаться.
          </div>
          {[...byPage.entries()].map(([page, ops]) => (
            <div
              key={page}
              style={{ border: `1px solid ${C.border}`, borderRadius: R.md, padding: SP.sm, marginBottom: SP.sm, background: C.bgPanel, boxShadow: SHADOW.card }}
            >
              <div style={{ fontSize: FS.sm, fontFamily: FONT.mono, color: C.textSecondary, overflowWrap: 'anywhere' }}>
                {page} · {uniq(ops.map(o => OP_LABEL[o.op] ?? o.op)).join(', ')}
              </div>
              <DiffView diff={expectedDiffFor(ops)} />
            </div>
          ))}
        </>
      )}
    </div>
  );
}

// ─── Подвкладка ───

export function WikiJournal() {
  const tree = useWikiTree();
  const [data, setData] = useState<{ packages: WikiPackageDto[]; invalidCount: number } | null>(null);
  const [loadError, setLoadError] = useState<unknown>(null);
  // '' — все разделы / все источники (пункт-заглушка Select)
  const [sectionFilter, setSectionFilter] = useState('');
  const [originFilter, setOriginFilter] = useState('');
  const [openedId, setOpenedId] = useState<string | null>(null);
  const [confirm, setConfirm] = useState<{ kind: 'revert' | 'retract'; pkg: WikiPackageDto } | null>(null);
  const [actionError, setActionError] = useState<{ text: string; pages: string[] } | null>(null);
  const [busy, setBusy] = useState(false);
  // Поколение загрузок (урок S5b/S5e, образец — loadSeq в WikiInbox): только последний
  // запрос пишет список; поздний ответ прежней загрузки его не затрёт
  const loadSeq = useRef(0);

  const load = useCallback(() => {
    const mine = ++loadSeq.current;
    setLoadError(null);
    listWikiJournal()
      .then(d => { if (loadSeq.current === mine) setData(d); })
      .catch(e => { if (loadSeq.current === mine) setLoadError(e); });
  }, []);

  useEffect(() => {
    // eslint-disable-next-line react-hooks/set-state-in-effect -- старт загрузки при монтировании подвкладки: другого места старта нет, список живёт в компоненте
    load();
  }, [load]);

  const visible = (data?.packages ?? [])
    .filter(p => JOURNAL_STATUSES.has(p.status))
    .filter(p => !sectionFilter || p.section === sectionFilter)
    .filter(p => !originFilter || p.sources.some(s => s.origin === originFilter))
    .sort((a, b) => Date.parse(b.updatedAt) - Date.parse(a.updatedAt));

  const opened = openedId ? visible.find(p => p.id === openedId) ?? null : null;

  const open = (pkg: WikiPackageDto) => {
    setActionError(null);
    setOpenedId(pkg.id);
  };

  const back = () => {
    setOpenedId(null);
    setActionError(null);
    load();
  };

  const failAction = (e: unknown, fallback: string) => {
    const we = e instanceof WikiError ? e : null;
    setActionError({
      text: we ? revertErrorText(we) : fallback,
      pages: e instanceof WikiRevertError ? e.pages : [],
    });
  };

  const doRevert = async (pkg: WikiPackageDto) => {
    if (busy) return;
    setBusy(true);
    try {
      const res = await revertWikiPackage(pkg.id);
      setConfirm(null);
      showToast('Пакет откатан', `Страницы возвращены к состоянию до пакета: ${res.restoredPages.length} стр.`, 'success');
      setOpenedId(null);
      load();
    } catch (e) {
      setConfirm(null);
      failAction(e, 'Не удалось откатить пакет');
    } finally {
      setBusy(false);
    }
  };

  const doRetract = async (pkg: WikiPackageDto) => {
    const src = primarySource(pkg);
    if (!src || busy) return;
    setBusy(true);
    try {
      const r = await retractWikiSource(sourceRefOf(src));
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
      setOpenedId(null);
      load();
    } catch (e) {
      setConfirm(null);
      failAction(e, 'Не удалось откатить всё из этого источника');
    } finally {
      setBusy(false);
    }
  };

  // Что снимется при откате по источнику — из того, что видно в журнале сейчас: точный
  // исход (что по прообразам, что останется из-за другого источника) знает только бэк.
  const retractSrc = confirm?.kind === 'retract' ? primarySource(confirm.pkg) : null;
  const retractRelated = confirm?.kind === 'retract' && retractSrc
    ? visible.filter(p => p.status === 'applied' && p.sources.some(s => sourceRefOf(s) === sourceRefOf(retractSrc)))
    : [];
  const retractPages = uniq(retractRelated.flatMap(p => p.operations.map(o => o.page)));
  const revertPages = confirm?.kind === 'revert' ? uniq(confirm.pkg.operations.map(o => o.page)) : [];
  // Модалка подтверждения — в обеих ветках (списка и записи): кнопки отката живут в записи
  const confirmModal = !confirm ? null : confirm.kind === 'revert' ? (
    <ConfirmDialog
      title="Откатить пакет?"
      subtitle={
        <>
          <div>Страницы вернутся к состоянию до этого пакета:</div>
          <ul style={{ margin: `${SP.xs}px 0 0`, paddingLeft: SP.lg }}>
            {revertPages.map(p => (
              <li key={p} style={{ fontFamily: FONT.mono, fontSize: FS.sm, overflowWrap: 'anywhere' }}>{p}</li>
            ))}
          </ul>
          <div style={{ marginTop: SP.xs }}>Если страницу меняли после пакета — бэк не даст откатить и покажет, какие именно.</div>
        </>
      }
      confirmLabel="Откатить"
      confirmVariant="danger"
      onConfirm={() => doRevert(confirm.pkg)}
      onCancel={() => setConfirm(null)}
    />
  ) : (
    <ConfirmDialog
      title={`Откатить всё из этого ${retractSrc ? ORIGIN_META[retractSrc.origin]?.label ?? 'источника' : 'источника'}?`}
      subtitle={
        <>
          <div>Снимется то, что Хранитель взял из этого источника за всё время. Знание из других источников останется.</div>
          <ul style={{ margin: `${SP.xs}px 0 0`, paddingLeft: SP.lg }}>
            <li>В журнале с этим источником: {retractRelated.length} применённых пакетов, {retractPages.length} стр.</li>
            {retractSrc?.origin === 'session' && (
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
      onConfirm={() => doRetract(confirm.pkg)}
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
  if (!data) return <LoadingSkeleton />;

  if (opened) {
    return (
      <>
        <JournalEntry
          pkg={opened}
          busy={busy}
          actionError={actionError}
          onBack={back}
          onRevert={() => setConfirm({ kind: 'revert', pkg: opened })}
          onRetract={() => setConfirm({ kind: 'retract', pkg: opened })}
        />
        {confirmModal}
      </>
    );
  }

  const sectionOptions = (tree?.sections ?? []).map(s => ({ value: s.key, label: sectionPrefix(s) }));
  const originOptions = Object.entries(ORIGIN_META).map(([value, m]) => ({ value, label: m.label }));

  let lastDay: string | null = null;
  return (
    <div style={{ padding: `0 0 ${SP.md}px` }}>
      <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.sm, padding: `${SP.sm}px ${SP.md}px 0` }}>
        <Select value={sectionFilter} onChange={setSectionFilter} placeholder="Все разделы" options={sectionOptions} />
        <Select value={originFilter} onChange={setOriginFilter} placeholder="Все источники" options={originOptions} />
      </div>
      {data.invalidCount > 0 && (
        <div style={{ padding: `${SP.sm}px ${SP.md}px 0` }}>
          <Notice tone="warning" icon={AlertTriangle} title={`Нечитаемых файлов пакета: ${data.invalidCount}`}>
            Они видны как сломанные и в журнал не берутся.
          </Notice>
        </div>
      )}
      {visible.length === 0 ? (
        <EmptyState
          icon={<History size={ICON_SIZE.xl} strokeWidth={ICON_STROKE} />}
          title="Журнал пустой"
          subtitle={
            <span style={{ maxWidth: 360 }}>
              Здесь появятся применённые правки вики — каждая с откатом по пакету
              и по источнику. {sectionFilter || originFilter ? 'Под выбранный фильтр ничего нет.' : ''}
            </span>
          }
        />
      ) : (
        <>
          {visible.map(p => {
            const day = dayKey(p.updatedAt);
            const header = day !== lastDay ? dayTitle(day) : null;
            lastDay = day;
            return (
              <div key={p.id}>
                {header && (
                  <div style={{ padding: `${SP.md}px ${SP.md}px ${SP.xs}px`, fontSize: FS.xs, color: C.textMuted, fontFamily: FONT.mono }}>
                    {header}
                  </div>
                )}
                <JournalRow pkg={p} onOpen={() => open(p)} />
              </div>
            );
          })}
          <div style={{ padding: `${SP.md}px ${SP.md}px 0`, fontSize: FS.xs, color: C.textMuted }}>
            Откат по пакету и по источнику — 90 дней, дальше прообразы стираются.
          </div>
        </>
      )}
      {confirmModal}
    </div>
  );
}
