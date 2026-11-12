// «Решения» (S7a + J-F1, макет llm-wiki-v2 §4, ADR-039 решение 9): подвкладка вики.
// Режим — тем же тумблером «Применять без ревью» (autoApplyEnabled из стора настроек, тот же
// источник, что у WikiApplySettings):
// - выключен → ручная очередь «Входящие»: спорные операции решает человек на экране пакета
//   (S7a, поведение без изменений);
// - включён → «Решения судьи»: очередь — судья, список только для чтения — вердикт по каждой
//   спорной операции, его причина, ссылка на страницу, «Откатить пакет» (мера 4). Решённое
//   судьёй заново решить нельзя (И1): путь человека — откат. Пока снимок настроек не прочитан,
//   список ручной (прежнее лицо экрана); отказ чтения настроек не отказ списка и здесь не
//   показывается.
// Пакеты со статусом proposed. Операции с решением политики auto здесь не показываются
// вовсе: их решает код, не человек (контракт S4a: через REST auto не решается, 409
// wiki_pkg_auto). Неспорное и применённое — «Журнал» (S7b), не этот экран.
//
// Данные: GET /api/wiki/packages?inbox=true (стор не задействован — список живёт в компоненте,
// он не читается другими экранами). Область раздела (W9a) — клиентский фильтр по этому списку:
// проп sectionScope не меняет запрос и не перечитывает его. Порядок запросов — тот же
// инвариант, что у стора S5a:
// ответ кладётся, только если его поколение последнее (урок S5b/S5e); смена экрана или
// закрытие пакета поднимает новое поколение, поздний ответ прежней загрузки его не затрёт.
//
// Клик по строке открывает экран пакета (WikiPackageView) на данных строки: список уже несёт
// полный пакет (DTO List включает операции). Закрытие перечитывает список: решённый пакет
// мог уйти из «Входящих», и строка-«пропosed» не должна остаться в списке.

import { useCallback, useEffect, useRef, useState } from 'react';
import { AlertTriangle, Gavel, Inbox } from 'lucide-react';
import {
  C, FONT, FS, R, SP, useIsMobile,
  WikiError, listWikiPackages, useWikiTree,
  loadWikiSettings, requestOpenWikiPage, useWikiSettings,
  type WikiPackageDto, type WikiPackageOperationDto,
} from 'aihome_shell/kit';
// Транспорт отката пакета (мера 4) в экспортную строку кита не добавлен (тот файл — не в
// списке задачи, его правят другие шаги этой ветки) — прямой импорт того же модуля:
// экземпляр стора тот же, дубля состояния нет.
import { revertWikiPackage, WikiRevertError } from '../../../lib/wiki';
import { Badge } from '../../../components/ui/Badge';
import { Button } from '../../../components/ui/Button';
import { EmptyState } from '../../../components/ui/EmptyState';
import { Notice } from '../../../components/ui/Notice';
import { ICON_SIZE, ICON_STROKE } from '../../../components/ui/icons';
import { sectionPrefix, wikiErrorText } from './WikiTab';
import {
  INBOX_TEXT, JUDGE_TEXT, OP_META, ORIGIN_META, OVERRIDE_TEXT,
  isHumanTargetClass, wikiInboxReasonText,
} from './wikiLabels';
import { WikiPackageView } from './WikiPackageView';
import { refreshWikiRailCounts } from './useWikiRailCounts';

// Отказы «Входящих» — русским текстом по коду бэка (тот же порядок, что wikiErrorText для
// страниц; здесь свои коды, их в словаре страниц нет). Формулировки — черновые, тексты
// фичи за Софьей.
export function pkgErrorText(e: WikiError): string {
  switch (e.code) {
    case 'wiki_pkg_not_decidable': return 'Пакет уже решается в другом месте — список обновится';
    case 'wiki_pkg_auto': return 'Это не решается здесь: неспорное применяет код';
    case 'wiki_pkg_already_decided': return 'Эта операция уже решена — решение не меняется';
    case 'wiki_pkg_reason_required': return 'Напишите причину отказа';
    case 'wiki_pkg_op_index': return 'Операция не найдена в пакете';
    case 'wiki_pkg_nothing_to_decide': return 'В пакете не осталось спорных операций';
    case 'wiki_pkg_invalid': return 'Файл пакета не читается';
    case 'wiki_pkg_not_found': return 'Пакета больше нет — возможно, его удалили';
    case 'wiki_pkg_request': return 'Не удалось отправить решение';
    // Откат пакета (мера 4, J-F1): коды WikiPackageRevert. «changed» несёт в теле перечень
    // изменённых страниц (WikiRevertError) — его показывает вызывающий экран.
    case 'wiki_revert_off': return 'Вики этого владельца не заведена';
    case 'wiki_revert_claim': return 'Пакета больше нет — откатывать нечего';
    case 'wiki_revert_shape': return 'Файл пакета не читается';
    case 'wiki_revert_status': return 'Откатывают только применённый пакет';
    case 'wiki_revert_no_preimages':
    case 'wiki_revert_no_applied': return 'Прообразов пакета нет — откатывать нечего';
    case 'wiki_revert_changed': return 'Страницы изменились после применения — откат не прошёл';
    case 'wiki_revert_manifest':
    case 'wiki_revert_internal': return 'Откат не удался';
  }
  return wikiErrorText(e);
}

// Спорная операция ждёт решения, только если его не вынес ни человек, ни судья: у операции
// ровно одно решение (И1), вердикт судьи — решение той же операции (J-J2b, хвост ревью
// R-J-J2a: решённая судьёй не считается «на решение» и не просит человека заново).
export function pendingContested(p: WikiPackageDto): WikiPackageOperationDto[] {
  return p.operations.filter(o => o.decision === 'inbox' && !o.humanDecision && !o.judgeDecision);
}

// Возраст пакета — коротко: минуты/часы свежие, дальше дата. Возраст — второстепенная
// мета строки, точность не нужна
function ageText(iso: string): string {
  const t = Date.parse(iso);
  if (!Number.isFinite(t)) return '';
  const mins = Math.floor((Date.now() - t) / 60_000);
  if (mins < 60) return `${Math.max(mins, 0)} мин`;
  if (mins < 24 * 60) return `${Math.floor(mins / 60)} ч`;
  const d = new Date(t);
  return `${String(d.getDate()).padStart(2, '0')}.${String(d.getMonth() + 1).padStart(2, '0')}`;
}

function LoadingSkeleton() {
  const bar = (w: number) => (
    <div key={w} style={{ height: 10, width: `${w}%`, background: C.bgInset, borderRadius: R.sm, margin: `${SP.md}px 0` }} />
  );
  return <div style={{ padding: SP.md, opacity: 0.7 }} aria-hidden>{bar(60)}{bar(85)}{bar(45)}</div>;
}

// ─── Строка списка ───

function PackageRow({ pkg, onOpen }: { pkg: WikiPackageDto; onOpen: () => void }) {
  const isMobile = useIsMobile();
  const tree = useWikiTree();
  const src = pkg.sources[0];
  const meta = src ? ORIGIN_META[src.origin] : undefined;
  const Icon = meta?.icon ?? Inbox;
  const section = tree?.sections.find(s => s.key === pkg.section);
  // «Почему здесь» строки — причина первой ещё нерешённой спорной операции: у каждой
  // операции в «Входящих» причина есть по форме пакета (ShapeErrors), список показывает
  // первую, остальные — на экране пакета.
  // Цель — утверждение человека, если class оканчивается на @human (ADR-038 «Входящие»):
  // у такой причины в строке списка — короткий текст пункта по коду (таблица ADR),
  // нераспознанный код — запасной нейтральный текст: сырая строка бэка в строку списка
  // не выходит (на экране пакета причина показывается как есть — WikiPackageView).
  // Прочие причины — как есть (их тексты — не таблица ADR).
  const first = pendingContested(pkg)[0];
  const reasonShown = first
    ? (isHumanTargetClass(first.class) ? wikiInboxReasonText(first.reason) : first.reason)
    : null;
  return (
    <button
      type="button"
      data-testid="wiki-pkg-row"
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
        {reasonShown && (
          <span style={{ display: 'block', fontSize: FS.sm, marginTop: 2 }}>
            <b style={{ color: C.textMuted, fontWeight: 600 }}>Почему здесь:</b>{' '}
            <span style={{ color: C.textPrimary }}>{reasonShown}</span>
          </span>
        )}
        <span style={{ display: 'block', fontSize: FS.xs, color: C.textMuted, marginTop: 2 }}>
          {pendingContested(pkg).length} на решение
        </span>
      </span>
      {!isMobile && (
        <span style={{ flexShrink: 0, fontSize: FS.xs, color: C.textMuted, fontFamily: FONT.mono }}>{ageText(pkg.createdAt)}</span>
      )}
    </button>
  );
}

// ─── Строка «Решений судьи» (J-F1) ───

// Страница операции всегда её раздела пакета (тот же факт, что читает экран пакета):
// в строке — путь без префикса раздела, он уже в мета-строке.
function pageName(pkg: WikiPackageDto, page: string): string {
  const prefix = `${pkg.section}/`;
  return page.startsWith(prefix) ? page.slice(prefix.length) : page;
}

// Бейдж вердикта: чужое решение называется (И1: человек видит, чьё решение стоит);
// judgeModel === 'code' — отказ вынес код без вызова модели (И4′), это не вердикт судья;
// вердикта нет — честное «вердикта ещё нет», а не пустая строка: такой пакет здесь и есть
// (проход отложил его или не допустил).
function judgeVerdict(o: WikiPackageOperationDto) {
  if (o.judgeDecision === 'accepted') return <Badge tone="success" size="xs">{JUDGE_TEXT.verdictAccepted}</Badge>;
  if (o.judgeDecision === 'rejected') {
    return o.judgeModel === 'code'
      ? <Badge tone="neutral" size="xs">{JUDGE_TEXT.verdictByCode}</Badge>
      : <Badge tone="danger" size="xs">{JUDGE_TEXT.verdictRejected}</Badge>;
  }
  return <Badge tone="neutral" size="xs">{JUDGE_TEXT.verdictNone}</Badge>;
}

// Строка только для чтения: перехода на экран пакета нет — решать здесь судья, а не человек;
// действия строки — ссылка на страницу (тот же канал, что «Открыть страницу» в «Журнале») и
// «Откатить пакет» (мера 4). Мета строки — та же, что у ручной строки: те же данные, тот же вид.
function JudgeRow({ pkg, busy, onRevert }: { pkg: WikiPackageDto; busy: boolean; onRevert: () => void }) {
  const isMobile = useIsMobile();
  const tree = useWikiTree();
  const src = pkg.sources[0];
  const meta = src ? ORIGIN_META[src.origin] : undefined;
  const Icon = meta?.icon ?? Inbox;
  const section = tree?.sections.find(s => s.key === pkg.section);
  const contested = pkg.operations.filter(o => o.decision === 'inbox');
  return (
    <div data-testid="wiki-judge-row" style={{ padding: `${SP.sm}px ${SP.md}px` }}>
      <div style={{ display: 'flex', gap: SP.sm }}>
        <span style={{ flexShrink: 0, color: C.textMuted, paddingTop: 2 }}><Icon size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} /></span>
        <span style={{ minWidth: 0, flex: 1 }}>
          <span style={{ display: 'block', fontSize: FS.xs, color: C.textMuted, fontFamily: FONT.mono }}>
            {section ? sectionPrefix(section) : pkg.section} · {meta?.label ?? src?.origin ?? 'источник'}
          </span>
          <span style={{ display: 'block', fontSize: FS.base, fontWeight: 500, color: C.textHeading, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
            {src?.ref ?? 'Без источника'}
          </span>
        </span>
        {!isMobile && (
          <span style={{ flexShrink: 0, fontSize: FS.xs, color: C.textMuted, fontFamily: FONT.mono }}>{ageText(pkg.createdAt)}</span>
        )}
      </div>
      {contested.map(o => {
        const opMeta = OP_META[o.op];
        return (
          <div key={o.index} data-testid="wiki-judge-op" style={{ border: `1px solid ${C.border}`, borderRadius: R.md, padding: SP.sm, marginTop: SP.sm }}>
            <span style={{ display: 'flex', alignItems: 'center', gap: SP.xs, flexWrap: 'wrap' }}>
              <span style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted }}>№{o.index + 1}</span>
              {opMeta && <Badge tone={opMeta.tone} size="xs">{opMeta.text}</Badge>}
              {judgeVerdict(o)}
              <span style={{ fontSize: FS.sm, color: C.textSecondary, overflow: 'hidden', textOverflow: 'ellipsis' }}>
                {pageName(pkg, o.page)}
              </span>
              <span style={{ marginLeft: 'auto' }}>
                <Button
                  variant="ghost" size="sm" dataTestId={`wiki-judge-open-page-${o.index}`}
                  onClick={() => requestOpenWikiPage({ section: pkg.section, path: o.page })}
                >
                  {OVERRIDE_TEXT.openPage}
                </Button>
              </span>
            </span>
            {/* «почему» судьи — только когда она есть: причина обязательна отказу (WikiPackage.cs:270),
                у принятия её может не быть */}
            {o.judgeReason && (
              <span style={{ display: 'block', fontSize: FS.sm, marginTop: SP.xs }}>
                <b style={{ color: C.textMuted, fontWeight: 600 }}>{JUDGE_TEXT.whyTitle}</b>{' '}
                <span style={{ color: C.textPrimary }}>{o.judgeReason}</span>
              </span>
            )}
          </div>
        );
      })}
      <div style={{ marginTop: SP.sm }}>
        <Button variant="secondary" size="sm" dataTestId="wiki-pkg-revert" disabled={busy} onClick={onRevert}>
          {JUDGE_TEXT.revert}
        </Button>
      </div>
    </div>
  );
}

// ─── Подвкладка ───

// Область раздела (W9a): необязательный проп панели с чипом раздела. undefined / 'all' / null —
// все пакеты (старая раскладка WikiTab его не передаёт, её вид не меняется); строка раздела —
// только пакеты этого раздела. Фильтр поверх уже загруженного списка: смена пропa не трогает
// загрузку и её поколение (loadSeq). selectedKey в типе пропов — контракт старого реестра
// подвкладок (WikiTab передаёт его всем, wikiTabs.tsx:41): компонент его не читает —
// область «Входящих» задаёт sectionScope, не выбор в дереве.
export function WikiInbox({ sectionScope }: { sectionScope?: string | null | 'all'; selectedKey?: string | null }) {
  const [data, setData] = useState<{ packages: WikiPackageDto[]; invalidCount: number } | null>(null);
  const [loadError, setLoadError] = useState<unknown>(null);
  const [opened, setOpened] = useState<WikiPackageDto | null>(null);
  // Дерево нужно только имени пустого состояния области — тот же источник, что у строк списка
  const tree = useWikiTree();
  // Поколение загрузок (урок S5b/S5e, образец — openSeq в WikiTab): только последний
  // запрос пишет список; поздний ответ прежней загрузки его не затрёт
  const loadSeq = useRef(0);
  // Режим — тем же тумблером «Применять без ревью» (J-F1): снимок стора, тот же источник,
  // что у блока «Применение». Снимка ещё нет → ручная очередь (прежнее лицо экрана):
  // отказ чтения настроек не отказ списка и здесь не показывается.
  const settings = useWikiSettings();
  const judgeMode = settings?.autoApplyEnabled === true;
  const [revertError, setRevertError] = useState<unknown>(null);
  const [reverting, setReverting] = useState(false);

  const load = useCallback(() => {
    const mine = ++loadSeq.current;
    setLoadError(null);
    listWikiPackages()
      .then(d => { if (loadSeq.current === mine) setData(d); })
      .catch(e => { if (loadSeq.current === mine) setLoadError(e); });
  }, []);

  useEffect(() => {
    // eslint-disable-next-line react-hooks/set-state-in-effect -- старт загрузки при монтировании подвкладки: другого места старта нет, список живёт в компоненте
    load();
  }, [load]);

  useEffect(() => {
    // Снимок настроек — не данных этого экрана: читаем его один раз при монтировании,
    // дальше живёт на подписке useWikiSettings (правка тумблера в «Настройках» доходит сюда сама)
    if (settings) return;
    loadWikiSettings().catch(() => { /* режима нет → ручная очередь: отказ настроек не показывает здесь */ });
  }, [settings]);

  // Закрытие пакета перечитывает список: решённый пакет мог выйти из «Входящих»
  const closePackage = useCallback(() => {
    setOpened(null);
    load();
    // Счётчик «Входящих» на рельсе (W12): решённый пакет мог выйти из inbox — число перечитывается
    refreshWikiRailCounts();
  }, [load]);

  // «Откатить пакет» (мера 4): единственное действие вида «Решения судьи». Успех — перечитка
  // списка и счётчика: откатанный пакет из очереди уходит (статус reverted — не proposed).
  // Отказ — текст по коду (pkgErrorText); у wiki_revert_changed дополнительно перечень
  // изменённых страниц (WikiRevertError, контракт S4b-3) — человек видит, на что смотреть.
  const revertPackage = useCallback((p: WikiPackageDto) => {
    if (reverting) return;
    setReverting(true);
    setRevertError(null);
    revertWikiPackage(p.id)
      .then(() => { load(); refreshWikiRailCounts(); })
      .catch(e => setRevertError(e))
      .finally(() => setReverting(false));
  }, [reverting, load]);

  if (opened) return <WikiPackageView pkg={opened} onBack={closePackage} />;

  if (loadError) {
    const text = loadError instanceof WikiError ? pkgErrorText(loadError) : 'Не удалось прочитать список';
    return (
      <div data-testid="wiki-inbox-error" style={{ padding: SP.sm }}>
        <Notice tone="danger" icon={AlertTriangle} title={text} action={<Button variant="ghost" size="sm" onClick={load}>Повторить</Button>} />
      </div>
    );
  }
  if (!data) return <LoadingSkeleton />;

  // Область раздела (W9a): 'all'/null/пустая строка/undefined — области нет (все пакеты,
  // прежнее поведение); строка раздела — только его пакеты. Фильтр поверх data: запроса нет
  const scope = typeof sectionScope === 'string' && sectionScope !== 'all' && sectionScope !== '' ? sectionScope : null;
  const packages = scope ? data.packages.filter(p => p.section === scope) : data.packages;
  // Имя раздела пустого состояния — по tree.sections тем же форматтером, что и строки;
  // раздела нет в дереве — ключ как есть (тот же фолбэк, что у строки списка)
  const scopeSection = scope ? tree?.sections.find(s => s.key === scope) ?? null : null;
  const empty = packages.length === 0;
  return (
    <div style={{ padding: `0 0 ${SP.md}px` }}>
      <div style={{ padding: `${SP.sm}px ${SP.md}px 0` }}>
        {judgeMode ? (
          <Notice tone="info" icon={Gavel} title={JUDGE_TEXT.title}>
            {JUDGE_TEXT.caption}
          </Notice>
        ) : (
          <Notice tone="info" icon={Inbox} title="Здесь только то, где нужен человек">
            {INBOX_TEXT.caption}
          </Notice>
        )}
      </div>
      {revertError ? (
        <div style={{ padding: `${SP.sm}px ${SP.md}px 0` }}>
          <Notice
            tone="danger" icon={AlertTriangle}
            title={revertError instanceof WikiError ? pkgErrorText(revertError) : 'Не удалось откатить пакет'}
            action={<Button variant="ghost" size="sm" onClick={() => setRevertError(null)}>Закрыть</Button>}
          >
            {/* wiki_revert_changed несёт перечень изменённых страниц (S4b-3): на них надо
                посмотреть перед повторной попыткой */}
            {revertError instanceof WikiRevertError && revertError.pages.length > 0
              ? <>Страницы, изменённые после применения: {revertError.pages.join(', ')}</>
              : null}
          </Notice>
        </div>
      ) : null}
      {data.invalidCount > 0 && (
        <div style={{ padding: `${SP.sm}px ${SP.md}px 0` }}>
          <Notice tone="warning" icon={AlertTriangle} title={`Нечитаемых файлов пакета: ${data.invalidCount}`}>
            Они видны как сломанные и в решение не берутся.
          </Notice>
        </div>
      )}
      {empty ? (
        scope ? (
          // Непустой фильтр и пустой результат: по разделу всё разобрано — это не «ничего
          // нет», а «здесь решено всё» (W9a)
          <EmptyState
            icon={<Inbox size={ICON_SIZE.xl} strokeWidth={ICON_STROKE} />}
            title="Разобрано"
            subtitle={
              <span style={{ maxWidth: 360 }}>
                {`Всё, что ИИ предлагал по разделу «${scopeSection ? sectionPrefix(scopeSection) : scope}», применено или отклонено.`}
              </span>
            }
          />
        ) : (
          <EmptyState
            icon={<Inbox size={ICON_SIZE.xl} strokeWidth={ICON_STROKE} />}
            title="Спорного нет"
            subtitle={
              <span style={{ maxWidth: 360 }}>
                Всё, что Хранитель сделал, применилось само. Смотреть в «Журнале» —
                он придёт следующим шагом.
              </span>
            }
          />
        )
      ) : (
        <>
          {packages.map(p => judgeMode ? (
            // Вид судьи: строка только для чтения, экран пакета (место решения человека) не открывается
            <div key={p.id}><JudgeRow pkg={p} busy={reverting} onRevert={() => revertPackage(p)} /></div>
          ) : (
            <div key={p.id}><PackageRow pkg={p} onOpen={() => setOpened(p)} /></div>
          ))}
          <div style={{ padding: `${SP.md}px ${SP.md}px 0`, fontSize: FS.xs, color: C.textMuted }}>
            При 20 открытых пакетов ночной разбор останавливается, а не копит очередь.
          </div>
        </>
      )}
    </div>
  );
}
