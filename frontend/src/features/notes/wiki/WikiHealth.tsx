// «Здоровье» раздела (S5f, макет llm-wiki-v2 §15): что нашёл последний прогон проверки и
// когда он был. Источник — _health.md раздела как его отдаёт GET /api/wiki/health/{section}:
// своего DTO у бэка нет, строки находок пишет бэковый RenderHealth (WikiLintService.cs:434),
// и разбор здесь — зеркало той же формы (вид · страница · сообщение · связанные · ключ · счёт).
// Метрики прогона (keeper/metrics.jsonl) отдельной ручкой не читаются: их здесь нет вовсе,
// вместо них — честная пометка «динамика позже» и счётчики последнего отчёта.
//
// Порядок и состояние — уроки S5b/S5e/S7a/S7c-1: запрос живёт в компоненте с поколением
// (поздний ответ прежней загрузки не затирает свежий); кнопка блокируется на время запроса;
// недоверенное из текста файла (пути страниц, ключи, «связанные») — текстом, не разметкой и
// не ссылкой; отказ — русским текстом по коду бэка, не сырым кодом и не «Находки (0)».
//
// Повышение знания (ADR-036 §5): здесь только предложение. Решает человек и только кнопкой,
// а кнопки нет: повышение отложено в Ф2.
//
// «Принять как мою правку» (F5c, ADR-038 «Контракт для фронта», §adopt): у страницы с дрейфом
// на строке находки — действие человека: внешняя правка становится его правкой (пакет автора
// human). Только под обоими флагами (llm-wiki и «Заметки и Вики»), только после подтверждения.
// Конкурентность — тот же узор, что у «Вернуть мою» (useRestoreWikiStatement): замок в ref на
// двойной клик; ответ, пришедший после смены страницы, отбрасывается по ключу «раздел + путь».
// Честность исхода (вердикт R-F5c): «Здоровье» — отчёт ПОСЛЕДНЕГО прогона линта: GET health
// отдаёт _health.md как есть (WikiController.cs:492-512); пишет его служба линта
// (WikiLintService.cs:251,274,281 → WikiKeeper.cs:391) — в пути применения его не пишет никто.
// Поэтому после принятия строка «дрейфа» остаётся в отчёте до следующей проверки; тексты
// плашки ведут к «Проверить сейчас», а не обещают снятие строки.
//
// M7 (записка wiki-panels-v1, П3 и П5): находка — вертикальная карточка на всех ширинах
// (путь → сообщение → мета одной строкой через « · » → действие своим рядом), группа
// показывает первые FINDINGS_PREVIEW находок, остальные — по кнопке «Показать ещё»
// (локальное состояние группы). Сетка счётчиков — от narrow из useWikiNarrow (М0), не от
// ширины окна: узко — 2 колонки, широко — 4.

import { useCallback, useEffect, useRef, useState } from 'react';
import type { ReactNode } from 'react';
import { AlertTriangle, ArrowUpCircle, CheckCircle2, HeartPulse, RotateCw } from 'lucide-react';
import {
  C, FONT, FS, R, SP, useIsMobile,
  FLAGS, useFeature, WikiError, adoptWikiPageMine, getWikiHealth, runWikiLint, useWikiTree,
  PanelHeaderSlot, useHasPanelHeader,
} from 'aihome_shell/kit';
import { Badge, type BadgeTone } from '../../../components/ui/Badge';
import { Button } from '../../../components/ui/Button';
import { ConfirmDialog } from '../../../components/ui/ConfirmDialog';
import { EmptyState } from '../../../components/ui/EmptyState';
import { Notice } from '../../../components/ui/Notice';
import { ICON_SIZE, ICON_STROKE } from '../../../components/ui/icons';
import { sectionPrefix, wikiErrorText } from './wikiLabels';
import { refreshWikiRailCounts } from './useWikiRailCounts';
import { useWikiNarrow } from './useWikiNarrow';

// ─── Разбор _health.md (зеркало бэкового RenderHealth/FindingLine) ───

// Вид находки. Метки — ровно строки бэкового KindLabel (WikiLintService.cs:467): чужой
// метки в файле быть не может, нераспознанную не выдумываем — она остаётся как есть ('unknown').
export type HealthKind =
  | 'dangling' | 'nearDuplicate' | 'missingSources' | 'drift' | 'outOfReach'
  | 'orphan' | 'stale' | 'unlinkedMention' | 'ownsConflict' | 'promote' | 'unknown';

interface KindMeta { label: string; tone: BadgeTone; title: string }

// Порядок таблицы — порядок показа на экране. Подписи видов — по ADR-036 §4–5 и контракту
// линта; формулировки черновые, тексты фичи за Софьей.
const KIND_META: Record<Exclude<HealthKind, 'unknown'>, KindMeta> = {
  dangling: { label: 'призрак', tone: 'warning', title: 'Ссылка ведёт в пустоту: страницы с таким ключом нет' },
  nearDuplicate: { label: 'почти дубль', tone: 'warning', title: 'Две страницы об одном и том же' },
  missingSources: { label: 'нет источников', tone: 'warning', title: 'Утверждения страницы не на что опереться' },
  drift: { label: 'дрейф', tone: 'danger', title: 'Файл изменён мимо Хранителя' },
  outOfReach: { label: 'ссылка вне раздела', tone: 'danger', title: 'Ссылка ведёт за пределы цепочки «вверх» (ADR-036 §4): чинится пакетом или кнопкой человека' },
  orphan: { label: 'сирота', tone: 'neutral', title: 'На эту страницу никто не ссылается' },
  stale: { label: 'без изменений', tone: 'neutral', title: 'Страница не менялась 90 дней' },
  unlinkedMention: { label: 'упоминание без ссылки', tone: 'neutral', title: 'Имя названо текстом, но ссылки на страницу нет' },
  ownsConflict: { label: 'один owns у нескольких', tone: 'warning', title: 'Одна сущность в нескольких страницах: сигнал к повышению (ADR-036 §5)' },
  promote: { label: 'предложение повышения', tone: 'accent', title: 'Код предлагает поднять страницу в раздел выше (ADR-036 §5)' },
};

// «Повышение» — вне списка находок: свой блок «Просится наверх» (кнопки нет, Ф2)
const KIND_ORDER: HealthKind[] = [
  'dangling', 'nearDuplicate', 'missingSources', 'drift', 'outOfReach',
  'orphan', 'stale', 'unlinkedMention', 'ownsConflict',
];

// Метка → вид. Ключи — строки бэка, менять их здесь нельзя
// Map, а не объект: метка из файла — недоверенная строка, «constructor» или «toString» в
// объекте нашли бы прототип вместо вида
const KIND_BY_LABEL = new Map<string, HealthKind>(
  (Object.keys(KIND_META) as Exclude<HealthKind, 'unknown'>[]).map(k => [KIND_META[k].label, k]),
);

// «Счёт» находки — разный по виду (WikiFinding.Count): для призрака и общего owns/повышения —
// число страниц, для дубля — расстояние между заголовками, для «без изменений» — дни. У
// «упоминания без ссылки» и «ссылки вне раздела» он всегда 1 и ничего не добавляет — скрыт.
// Незнакомый вид: число показано без пояснения, смысл не выдумываем.
function countText(kind: HealthKind, n: number): string | null {
  switch (kind) {
    case 'dangling': return `ссылаются страниц: ${n}`;
    case 'nearDuplicate': return `различий в заголовках: ${n}`;
    case 'stale': return `дней без изменений: ${n}`;
    case 'ownsConflict':
    case 'promote': return `страниц: ${n}`;
    case 'unlinkedMention':
    case 'outOfReach': return null;
    default: return `счёт: ${n}`;
  }
}

export interface HealthFinding {
  kind: HealthKind;
  /** Метка как она стоит в файле — для 'unknown' это единственное, что мы знаем */
  kindLabel: string;
  /** Путь страницы или null: «раздел» — находка о разделе целиком */
  page: string | null;
  message: string;
  related: string[];
  key: string | null;
  count: number | null;
}

export interface HealthReport {
  /** Строка «Проверено: …» без хвоста «Код: …» — когда она есть */
  checkedAt: string | null;
  /** Число из шапки «## Находки (N)» — когда оно есть */
  total: number | null;
  findings: HealthFinding[];
  /** «## Отказ» — прогон этого раздела не завершился (RenderFailureHealth) */
  failure: string | null;
  /** «## Не проверено»: страниц без фактов — «нет источников» и «без изменений» их не видят */
  unchecked: number | null;
}

// Бэк экранирует недоверенное (Escape): backtick → «\`». Обратно — здесь, иначе человек
// видит обратный слэш вместо кавычки
const unescape = (s: string) => s.replace(/\\`/g, '`');

// Строка в backtick-ограде → её содержимое
const QUOTED = /`((?:[^`\\]|\\.)*)`/g;

function parseFinding(line: string): HealthFinding {
  // Порядок частей — порядок бэкового FindingLine: вид · страница · сообщение ·
  // связанные · ключ · счёт. Сообщение — всё, что осталось между ними (в нём может
  // быть « · »: Escape его не убирает), поэтому хвост снимается с конца, а не делением
  const parts = line.split(' · ');
  const kindLabel = parts[0].trim();
  const kind = KIND_BY_LABEL.get(kindLabel) ?? 'unknown';
  let from = 1;
  let page: string | null = null;
  const head = parts[1] ?? '';
  if (head === 'раздел') {
    from = 2;
  } else if (head.startsWith('страница ')) {
    const q = [...head.matchAll(QUOTED)];
    if (q.length) { page = unescape(q[0][1]); from = 2; }
  }
  let count: number | null = null;
  let key: string | null = null;
  let related: string[] = [];
  const tail = [...parts];
  while (tail.length > from) {
    const last = tail[tail.length - 1].trim();
    const mCount = last.match(/^счёт: (\d+)$/);
    const mKey = last.match(/^ключ: `((?:[^`\\]|\\.)*)`$/);
    const mRel = last.match(/^связанные: (.+)$/);
    if (mCount && count === null) { count = Number(mCount[1]); tail.pop(); continue; }
    if (mKey && key === null) { key = unescape(mKey[1]); tail.pop(); continue; }
    if (mRel && related.length === 0) {
      related = [...mRel[1].matchAll(QUOTED)].map(m => unescape(m[1]));
      tail.pop();
      continue;
    }
    break;
  }
  const message = unescape(tail.slice(from).join(' · ').trim());
  return { kind, kindLabel, page, message, related, key, count };
}

export function parseHealth(content: string): HealthReport {
  const out: HealthReport = { checkedAt: null, total: null, findings: [], failure: null, unchecked: null };
  let where: 'none' | 'findings' | 'failure' | 'unchecked' = 'none';
  const failure: string[] = [];
  for (const raw of content.split(/\r?\n/)) {
    const line = raw.trim();
    if (line.startsWith('## ')) {
      const head = line.slice(3);
      if (head.startsWith('Находки')) {
        where = 'findings';
        const n = head.match(/\((\d+)\)/);
        if (n) out.total = Number(n[1]);
      } else if (head === 'Отказ') {
        where = 'failure';
      } else if (head === 'Не проверено') {
        where = 'unchecked';
      } else {
        where = 'none';
      }
      continue;
    }
    if (line.startsWith('Проверено:')) {
      const rest = line.slice('Проверено:'.length).trim();
      const cut = rest.indexOf('. Код:');
      out.checkedAt = (cut >= 0 ? rest.slice(0, cut) : rest).trim() || null;
      continue;
    }
    if (!line.startsWith('- ')) continue;
    const body = line.slice(2).trim();
    if (!body) continue;
    if (where === 'findings') out.findings.push(parseFinding(body));
    else if (where === 'failure') failure.push(unescape(body));
    else if (where === 'unchecked') {
      // «- страниц без фактов (…): N» — число в конце строки (RenderHealth)
      const m = body.match(/:\s*(\d+)\s*$/);
      if (m) out.unchecked = Number(m[1]);
    }
  }
  if (failure.length) out.failure = failure.join(' ');
  return out;
}

// Группы по виду — в порядке таблицы; чужая метка дописывается в конец как есть
interface Group { kind: HealthKind; label: string; tone: BadgeTone; title: string; items: HealthFinding[] }

function groupsOf(findings: HealthFinding[]): Group[] {
  const byKind = new Map<HealthKind, HealthFinding[]>();
  for (const f of findings) {
    const arr = byKind.get(f.kind);
    if (arr) arr.push(f); else byKind.set(f.kind, [f]);
  }
  const order: HealthKind[] = [...KIND_ORDER];
  for (const k of byKind.keys()) if (!order.includes(k)) order.push(k);
  const groups: Group[] = [];
  for (const kind of order) {
    const items = byKind.get(kind);
    if (!items || !items.length) continue;
    const meta = kind === 'unknown'
      ? { label: items[0].kindLabel || 'находка', tone: 'neutral' as BadgeTone, title: 'Вид находки не описан этой версией проверки' }
      : KIND_META[kind];
    groups.push({ kind, ...meta, items });
  }
  return groups;
}

// M7 (П5): предел группы — первые FINDINGS_PREVIEW находок, остальные по кнопке.
// Чистая функция, вынесена из компонента: проверяется тестом без DOM.
export const FINDINGS_PREVIEW = 5;

export function visibleFindings<T>(list: T[], expanded: boolean): { shown: T[]; rest: number } {
  if (expanded || list.length <= FINDINGS_PREVIEW) return { shown: list, rest: 0 };
  return { shown: list.slice(0, FINDINGS_PREVIEW), rest: list.length - FINDINGS_PREVIEW };
}

// Время проверки — коротко и по-человечески; нераспознанную строку показываем как есть,
// выдуманного времени у нас нет
function checkedText(iso: string | null): string | null {
  if (!iso) return null;
  const t = Date.parse(iso);
  if (!Number.isFinite(t)) return iso;
  const d = new Date(t);
  const p = (n: number) => String(n).padStart(2, '0');
  return `${p(d.getDate())}.${p(d.getMonth() + 1)} в ${p(d.getHours())}:${p(d.getMinutes())}`;
}

// Отказы прогона — русским текстом по коду бэка (тот же приём, что pkgErrorText в «Входящих»)
export function healthErrorText(e: WikiError): string {
  switch (e.code) {
    case 'wiki_llm_wiki_off': return 'Проверка не выполнена: функция «Вики» выключена';
    case 'wiki_lint_running': return 'Проверка уже идёт — результат появится, когда она закончится';
  }
  return wikiErrorText(e);
}

// ─── «Принять как мою правку» (F5c) ───

type MineNotice = { tone: 'info' | 'warning' | 'danger'; title: string; text?: string };

// Отказы «Принять как мою правку» — русским текстом по коду (тот же приём, что healthErrorText):
// 409 wiki_page_changed — «перечитай дрейф, не перезаписывай»; 409 wiki_unified_off и прочие
// коды — общий текст по коду; 400 wiki_edit_invalid — причины гейта (issues), без них — message
// бэка как есть. Сырого кода на экране нет.
function mineErrorNotice(e: unknown): MineNotice {
  if (e instanceof WikiError && e.code === 'wiki_page_changed') {
    return { tone: 'warning', title: ADOPT_MINE.changedTitle, text: ADOPT_MINE.changedText };
  }
  if (e instanceof WikiError && e.code === 'wiki_edit_invalid') {
    const detail = e.issues.length ? e.issues.map(i => i.message).join(' ') : e.message;
    return { tone: 'danger', title: ADOPT_MINE.invalidTitle, text: detail || ADOPT_MINE.invalidText };
  }
  if (e instanceof WikiError) return { tone: 'danger', title: wikiErrorText(e) };
  return { tone: 'danger', title: ADOPT_MINE.failTitle, text: ADOPT_MINE.failText };
}

// ─── Строки ───

// adopt — действие над этой страницей (для «дрейфа» — «Принять как мою правку», F5c);
// для прочих видов его нет
//
// M7 (П3): вертикальная карточка на всех ширинах: путь страницы → сообщение → мета одной
// строкой через « · » → действие своим рядом. Путь — одна строка с многоточием, полный —
// в title (в узкой панели он не должен резать карточку). Мета: «связанные: …», «ключ: …»,
// счёт; значения путей и ключей — по-прежнему mono: это данные файла, а не проза.
function FindingRow({ f, adopt }: { f: HealthFinding; adopt?: ReactNode }) {
  const meta = f.kind === 'unknown' ? null : KIND_META[f.kind];
  const countLabel = f.count !== null ? countText(f.kind, f.count) : null;
  const metaParts: ReactNode[] = [];
  if (f.related.length > 0) {
    metaParts.push(
      <span key="rel">
        связанные:{' '}
        {f.related.map((r, i) => (
          <span key={i} style={{ fontFamily: FONT.mono }}>{i ? ', ' : ''}{r}</span>
        ))}
      </span>,
    );
  }
  if (f.key) {
    metaParts.push(<span key="key">ключ: <span style={{ fontFamily: FONT.mono }}>{f.key}</span></span>);
  }
  if (countLabel) metaParts.push(<span key="cnt">{countLabel}</span>);
  return (
    <div
      data-testid="wiki-health-row"
      style={{
        display: 'flex', flexDirection: 'column', gap: SP.xs,
        padding: `${SP.sm}px ${SP.md}px`, borderTop: `1px solid ${C.borderLight}`,
      }}
    >
      {f.page && (
        <span
          data-testid="wiki-health-row-path"
          title={f.page}
          style={{
            fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted,
            overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
          }}
        >
          {f.page}
        </span>
      )}
      {f.message && <span style={{ fontSize: FS.base, color: C.textPrimary }}>{f.message}</span>}
      {metaParts.length > 0 && (
        <span data-testid="wiki-health-row-meta" style={{ fontSize: FS.xs, color: C.textMuted }}>
          {metaParts.map((p, i) => <span key={i}>{i ? ' · ' : ''}{p}</span>)}
        </span>
      )}
      {meta?.title && !f.message && <span style={{ fontSize: FS.sm, color: C.textMuted }}>{meta.title}</span>}
      {adopt}
    </div>
  );
}

// M7 (П5): тело группы находок: первые FINDINGS_PREVIEW строк, остальные — по кнопке
// «Показать ещё N». Расширение — локальное состояние группы: живёт, пока открыта панель
// (смена раздела снимает группы вместе с отчётом — раскрытое не переходит в новый раздел)
function FindingsGroup({ items, testId, renderRow }: {
  items: HealthFinding[];
  testId: string;
  renderRow: (f: HealthFinding) => ReactNode;
}) {
  const [expanded, setExpanded] = useState(false);
  const { shown, rest } = visibleFindings(items, expanded);
  return (
    <div>
      {shown.map((f, i) => <div key={i}>{renderRow(f)}</div>)}
      {rest > 0 && (
        <div style={{ padding: `0 ${SP.md}px` }}>
          <Button
            variant="ghost" size="sm" dataTestId={`wiki-health-expand-${testId}`}
            onClick={() => setExpanded(true)}
          >
            Показать ещё {rest}
          </Button>
        </div>
      )}
    </div>
  );
}

function LoadingSkeleton() {
  const bar = (w: number) => (
    <div key={w} style={{ height: 10, width: `${w}%`, background: C.bgInset, borderRadius: R.sm, margin: `${SP.md}px 0` }} />
  );
  return <div style={{ padding: SP.md, opacity: 0.7 }} aria-hidden>{bar(40)}{bar(70)}{bar(55)}</div>;
}

// ─── Подвкладка ───

export function WikiHealth({ selectedKey }: { selectedKey: string | null }) {
  const isMobile = useIsMobile();
  // М7: узкость панели (М0, П1) решает только сетку счётчиков (2/4 колонки). Тач-размеры
  // кнопок остаются на isMobile: узкая панель на десктопе — это всё ещё мышь
  const [panelRef, narrow] = useWikiNarrow<HTMLDivElement>();
  const tree = useWikiTree();
  const hasHeader = useHasPanelHeader(); // место кнопки: шапка панели или тело (W11)
  const [report, setReport] = useState<HealthReport | null>(null);
  const [loadError, setLoadError] = useState<unknown>(null);
  const [noReport, setNoReport] = useState(false);
  const [actionError, setActionError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);
  // Поколение запросов (урок S5b/S5e): ответ кладётся, только если его поколение последнее;
  // смена раздела или запуск проверки поднимают новое — запоздалый ответ его не затрёт
  const seq = useRef(0);

  const load = useCallback(() => {
    const mine = ++seq.current;
    // Отчёт чужого раздела уходит сразу при смене ключа (доработка S5f, major):
    // под шапкой «B» не должно быть находок A — ни при загрузке B, ни при её отказе.
    // Прежний отчёт здесь остаётся только в run() («Проверить сейчас» своего раздела),
    // где он свой и отказ прогона его не затирает.
    setReport(null);
    setLoadError(null);
    setNoReport(false);
    if (!selectedKey) return;
    getWikiHealth(selectedKey)
      .then(d => { if (seq.current === mine) setReport(parseHealth(d.content)); })
      .catch(e => {
        if (seq.current !== mine) return;
        // 404 — отчёта нет: это «нет данных — прогоните проверку», а не ошибка экрана
        if (e instanceof WikiError && e.status === 404) { setNoReport(true); setReport(null); return; }
        setLoadError(e);
      });
  }, [selectedKey]);

  useEffect(() => {
    // eslint-disable-next-line react-hooks/set-state-in-effect -- старт загрузки при монтировании и при смене раздела: другого места старта нет, отчёт живёт в компоненте
    load();
  }, [load]);

  const run = useCallback(() => {
    if (busy || !selectedKey) return;
    const mine = ++seq.current;
    setBusy(true);
    setActionError(null);
    void (async () => {
      let ranOk = false;
      try {
        await runWikiLint();
        ranOk = true;
        // Счётчик «Здоровья» на рельсе (W12): прогон переписал отчёты разделов — числа перечитываются
        refreshWikiRailCounts();
        if (seq.current !== mine) return;
        const d = await getWikiHealth(selectedKey);
        if (seq.current === mine) { setNoReport(false); setReport(parseHealth(d.content)); }
      } catch (e) {
        if (seq.current !== mine) return;
        // прогон прошёл, но отчёта этого раздела нет (404): это «нет данных», а не отказ проверки
        if (e instanceof WikiError && e.status === 404 && ranOk) { setNoReport(true); setReport(null); return; }
        setActionError(e);
      } finally {
        setBusy(false);
      }
    })();
  }, [busy, selectedKey]);

  // ─── «Принять как мою правку» (F5c) ───
  // Действие есть только под обоими флагами: бэк проверяет те же два до ветки mode:mine
  // (UnifiedOffRefusal), без них запрос не начать.
  // Оба useFeature — безусловные (F5c-rw2, образец WikiPageView.tsx:328-330): при «&&» прямо
  // второй вызов пропускается при выключенном первом, и переключение флага при открытом
  // «Здоровье» меняло бы число хуков между рендерами (rules-of-hooks).
  const wikiFlag = useFeature(FLAGS.llmWiki);
  const canAdoptMine = wikiFlag;
  const [mineAsk, setMineAsk] = useState<string | null>(null); // путь страницы под подтверждением
  const [mineUi, setMineUi] = useState<{ id: string; busy: boolean; notice: MineNotice | null } | null>(null);
  // Текущий ключ действия «раздел\nпуть»: ref пишется в эффектах и обработчиках, не во время
  // рендера (урок F4a-rw; образец useRestoreWikiStatement). Ответ, пришедший после смены
  // страницы (другой раздел или другое действие), отбрасывается по этому ключу.
  const activeRef = useRef<string | null>(null);
  const inFlightRef = useRef<string | null>(null); // замок на двойной клик: state не успевает
  useEffect(() => {
    // Смена раздела: действие прежнего стало чужим — его подтверждение и плашка не переходят
    // в новый раздел (тот же порядок, что сброс F5b при смене заметки)
    activeRef.current = selectedKey !== null ? `${selectedKey}\n` : null;
    // eslint-disable-next-line react-hooks/set-state-in-effect -- сброс состояния действия прежнего раздела при смене раздела
    { setMineAsk(null); setMineUi(null); }
  }, [selectedKey]);
  useEffect(() => () => { activeRef.current = null; }, []);

  const runAdoptMine = useCallback(async (pageKey: string) => {
    if (!selectedKey) return;
    const key = `${selectedKey}\n${pageKey}`;
    if (inFlightRef.current === key) return; // второй клик по подтверждению ничего не шлёт
    inFlightRef.current = key;
    activeRef.current = key;
    setMineUi({ id: key, busy: true, notice: null });
    try {
      const res = await adoptWikiPageMine(selectedKey, pageKey);
      // Страница сменилась, пока запрос шёл: ответ отбрасывается по пути — ни плашки, ни
      // перечитки. Мутация «снять эту проверку» — красный тест «ответ после смены страницы»
      if (activeRef.current !== key) return;
      setMineAsk(null); // подтверждение закрыто исходом
      setMineUi({
        id: key, busy: false,
        notice: res.status === 'applied'
          ? { tone: 'info', title: ADOPT_MINE.doneTitle, text: ADOPT_MINE.doneText }
          : { tone: 'info', title: ADOPT_MINE.unchangedTitle, text: ADOPT_MINE.unchangedText },
      });
      // Перечитать здоровье: GET health отдаёт _health.md как есть — его пишет служба линта,
      // в пути применения его не пишет никто, поэтому строка «дрейфа» в перечитанном отчёте
      // остаётся до следующей проверки; обещать её снятие здесь нельзя (текст плашки — ADOPT_MINE.doneText)
      load();
    } catch (e) {
      if (activeRef.current !== key) return;
      setMineAsk(null);
      setMineUi({ id: key, busy: false, notice: mineErrorNotice(e) });
      // «файл правили ещё раз»: ничего не перезаписано; перечитка даёт тот же отчёт
      // последнего прогона — строка «дрейфа» в нём остаётся до следующей проверки
      if (e instanceof WikiError && e.code === 'wiki_page_changed') load();
    } finally {
      if (inFlightRef.current === key) inFlightRef.current = null;
      // запоздалый ответ не оставляет у страницы вечного «занято»
      setMineUi(st => (st && st.id === key && st.busy ? { ...st, busy: false } : st));
    }
  }, [selectedKey, load]);

  const section = selectedKey ? tree?.sections.find(s => s.key === selectedKey) ?? null : null;
  // Имя раздела — из дерева (его дал бэк как данные); ключ из props — только когда дерева нет
  const prefix = section ? sectionPrefix(section) : selectedKey ?? '';

  // «Проверить сейчас» — единственная кнопка шапки во всей вики (записка Майи
  // notes-wiki-panels-v1, п. 5 и «Что нужно от кода» п. 3): под шапкой панели она стоит
  // в закреплённом слоте (PanelHeaderSlot pinned — виден всегда, в покое его приглушает
  // сам PanelShell), без шапки (шторка 360, старая раскладка без флага) — первой строкой
  // тела во всю ширину, 40 px на мобиле, с подписью под ней. Логика запуска (run, замок
  // busy, поколение запросов) от места не меняется. Иконка и её размер — по макету:
  // 13 в шапке (тот же размер, что у кнопок шапок других панелей — ChatListToolbar.tsx:315),
  // ICON_SIZE.xs в теле (кадр mob-health).
  const runBtn = (inBody: boolean) => (
    <Button
      variant="secondary" size={inBody ? (isMobile ? 'md' : 'sm') : 'sm'}
      fullWidth={inBody} dataTestId="wiki-health-run"
      ariaLabel="Выполнить проверку вики сейчас"
      disabled={busy || !selectedKey}
      leftIcon={<RotateCw size={inBody ? ICON_SIZE.xs : 13} strokeWidth={ICON_STROKE} />}
      onClick={run}
    >
      Проверить сейчас
    </Button>
  );
  const runPlacement = hasHeader
    ? <PanelHeaderSlot pinned>{runBtn(false)}</PanelHeaderSlot>
    : (
      <div data-testid="wiki-health-run-line" style={{ display: 'flex', flexDirection: 'column', gap: SP.xs, padding: `0 ${SP.md}px` }}>
        {runBtn(true)}
        <span style={{ fontSize: FS.xs, color: C.textMuted }}>Проверяет всю вики, займёт около минуты</span>
      </div>
    );

  if (!selectedKey) {
    return (
      <div>
        {runPlacement}
        <EmptyState
          icon={<HeartPulse size={ICON_SIZE.xl} strokeWidth={ICON_STROKE} />}
          title="Раздел не выбран"
          subtitle={<span style={{ maxWidth: 360 }}>«Здоровье» показывает отчёт проверки выбранного раздела. Выберите раздел в списке слева или кнопкой «Раздел ▸».</span>}
        />
      </div>
    );
  }

  const groups = report ? groupsOf(report.findings) : [];
  const promotes = groups.find(g => g.kind === 'promote');
  const plain = groups.filter(g => g.kind !== 'promote');
  const checked = checkedText(report?.checkedAt ?? null);
  const nothingFound = !!report && report.findings.length === 0 && !report.failure;
  const unchecked = report?.unchecked ?? 0;
  // Шапка «Находки (N)» и число строк пишет один код: расхождение — обрезанный или
  // повреждённый файл, и «N находок» в шапке не должно молча стать M строк на экране
  const totalMismatch = !!report && report.total !== null && report.total !== report.findings.length;
  // Счётчики и «динамика» имеют смысл только при находках: у отказа прогона и пустого отчёта
  // сетка из нулей — шум
  const hasFindings = !!report && report.findings.length > 0;

  // Действие на строке «дрейфа» (F5c): только у находки с путём страницы и только под обоими
  // флагами; прочие виды действия не имеют. Кнопка выключена на время своего запроса (тестовый
  // помощник click() disabled не кликает — повторный клик не пройдёт и до замка в ref)
  const adoptForFinding = (kind: HealthKind, f: HealthFinding): ReactNode => {
    const page = f.page;
    if (!canAdoptMine || kind !== 'drift' || !page) return undefined;
    return (
      <Button
        variant="primary" size={isMobile ? 'md' : 'xs'} dataTestId="wiki-health-adopt-mine"
        title="Изменённые места станут вашими (✎ вы); сам текст страницы при этом не меняется"
        disabled={!!mineUi && mineUi.id === `${selectedKey}\n${page}` && mineUi.busy}
        onClick={() => setMineAsk(page)}
      >
        {ADOPT_MINE.label}
      </Button>
    );
  };

  return (
    <div ref={panelRef} style={{ display: 'flex', flexDirection: 'column', gap: SP.sm, paddingBottom: SP.md }}>
      {runPlacement}
      <div style={{ padding: `${SP.sm}px ${SP.md}px 0` }}>
        <span style={{ display: 'inline-flex', alignItems: 'center', gap: SP.xs, fontSize: FS.xs, color: C.textMuted, fontFamily: FONT.mono }}>
          <HeartPulse size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
          <span style={{ wordBreak: 'break-all' }}>
            {checked ? `проверка: ${checked}` : 'проверки ещё не было'} · {prefix}
          </span>
        </span>
      </div>

      {actionError ? (
        <div style={{ padding: `0 ${SP.md}px` }}>
          <Notice
            tone="warning" icon={AlertTriangle}
            title={actionError instanceof WikiError ? healthErrorText(actionError) : 'Не удалось начать проверку'}
          />
        </div>
      ) : null}

      {loadError ? (
        <div style={{ padding: `0 ${SP.md}px` }}>
          <Notice
            tone="danger" icon={AlertTriangle}
            title={loadError instanceof WikiError ? healthErrorText(loadError) : 'Не удалось прочитать здоровье раздела'}
            action={<Button variant="ghost" size="sm" onClick={load}>Повторить</Button>}
          />
        </div>
      ) : null}

      {/* Исход «Принять как мою правку» (F5c): успех и отказ — плашка над отчётом. Отчёт
          под плашкой — последнего прогона линта: строка «дрейфа» в нём остаётся до
          следующей проверки (вердикт R-F5c), поэтому тексты плашки ведут к «Проверить сейчас» */}
      {mineUi?.notice ? (
        <div data-testid="wiki-health-adopt-mine-flash" style={{ padding: `0 ${SP.md}px` }}>
          <Notice
            tone={mineUi.notice.tone}
            icon={mineUi.notice.tone === 'info' ? CheckCircle2 : AlertTriangle}
            title={mineUi.notice.title}
            action={<Button variant="ghost" size="sm" onClick={() => setMineUi(null)}>Закрыть</Button>}
          >
            {mineUi.notice.text}
          </Notice>
        </div>
      ) : null}

      {noReport && (
        <EmptyState
          icon={<HeartPulse size={ICON_SIZE.xl} strokeWidth={ICON_STROKE} />}
          title="Нет данных"
          subtitle={<span style={{ maxWidth: 360 }}>Отчёта по этому разделу ещё нет. «Проверить сейчас» проверяет всю вики: здесь появятся находки раздела и счётчики.</span>}
        />
      )}

      {!report && !loadError && !noReport && <LoadingSkeleton />}

      {report && (
        <>
          {report.failure && (
            <div style={{ padding: `0 ${SP.md}px` }}>
              <Notice tone="danger" icon={AlertTriangle} title="Проверка этого раздела не завершилась">
                {report.failure}
              </Notice>
            </div>
          )}

          {totalMismatch && (
            <div data-testid="wiki-health-mismatch" style={{ padding: `0 ${SP.md}px` }}>
              <Notice tone="warning" icon={AlertTriangle} title="Отчёт не сходится сам с собой">
                {`В шапке отчёта указано находок: ${report!.total}, в списке строк: ${report!.findings.length}. Запустите проверку заново.`}
              </Notice>
            </div>
          )}

          {unchecked > 0 && (
            <div data-testid="wiki-health-unchecked" style={{ padding: `0 ${SP.md}px` }}>
              <Notice tone="info" icon={AlertTriangle} title={`Не проверено страниц без фактов: ${unchecked}`}>
                Находки «нет источников» и «без изменений» эти страницы не видят — отсутствие находок о них ничего не значит.
              </Notice>
            </div>
          )}

          {nothingFound ? (
            <EmptyState
              icon={<HeartPulse size={ICON_SIZE.xl} strokeWidth={ICON_STROKE} />}
              title={unchecked > 0 ? 'Среди проверенных страниц находок нет' : 'Находок нет'}
              subtitle={<span style={{ maxWidth: 360 }}>
                {unchecked > 0
                  ? 'Непроверенные страницы названы выше. Следующая проверка — по кнопке выше.'
                  : 'Последняя проверка ничего не нашла. Следующая — по кнопке выше.'}
              </span>}
            />
          ) : hasFindings && (
            <>
              {/* Счётчики последнего отчёта — по видам, которых он действительно касается.
                  Метрик прогона здесь нет: их отдельной ручкой бэк не отдаёт */}
              <div
                data-testid="wiki-health-counters"
                style={{ display: 'grid', gridTemplateColumns: `repeat(${narrow ? 2 : 4}, minmax(0, 1fr))`, gap: SP.sm, padding: `0 ${SP.md}px` }}
              >
                {plain.map(g => (
                  <div key={g.kind} data-testid={`wiki-health-count-${g.kind}`} style={{ background: C.bgInset, borderRadius: R.md, padding: SP.sm }}>
                    <span style={{ display: 'block', fontSize: FS.xs, color: C.textMuted }}>{g.label}</span>
                    <span style={{ display: 'block', fontFamily: FONT.mono, fontSize: FS.lg, color: C.textHeading }}>{g.items.length}</span>
                  </div>
                ))}
              </div>
              <div style={{ padding: `0 ${SP.md}px`, fontSize: FS.xs, color: C.textMuted }}>
                Динамика метрик — позже: историю замеров прогона бэк ещё не отдаёт. Здесь — счётчики последней проверки.
              </div>

              {plain.map(g => (
                <div key={g.kind} data-testid={`wiki-health-group-${g.kind}`}>
                  <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, padding: `${SP.sm}px ${SP.md}px` }}>
                    <Badge tone={g.tone} size="xs" title={g.title}>{g.label}</Badge>
                    <span style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted }}>{g.items.length}</span>
                  </div>
                  <FindingsGroup
                    items={g.items} testId={g.kind}
                    renderRow={f => <FindingRow f={f} adopt={adoptForFinding(g.kind, f)} />}
                  />
                </div>
              ))}

              {promotes && (
                <div data-testid="wiki-health-promote" style={{ paddingTop: SP.sm }}>
                  <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, padding: `0 ${SP.md}px ${SP.sm}px` }}>
                    <ArrowUpCircle size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
                    <span style={{ fontSize: FS.base, fontWeight: 600, color: C.textHeading }}>Просится наверх</span>
                    {/* метка вида та же, что у остальных групп: человек видит, ЧТО именно просится */}
                    <Badge tone={KIND_META.promote.tone} size="xs" title={KIND_META.promote.title}>{KIND_META.promote.label}</Badge>
                    <span style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted }}>{promotes.items.length}</span>
                  </div>
                  <div style={{ padding: `0 ${SP.md}px ${SP.sm}px` }}>
                    <Notice tone="info" icon={ArrowUpCircle} title="Повышать будет человек и позже">
                      Код предлагает поднять эти страницы в раздел выше. Само повышение — следующей фичей (Ф2), здесь только предложение.
                    </Notice>
                  </div>
                  <FindingsGroup items={promotes.items} testId="promote" renderRow={f => <FindingRow f={f} />} />
                </div>
              )}
            </>
          )}
        </>
      )}

      {/* Подтверждение (F5c) — текст записки notes-wiki-unified-v1 дословно (§«д. Изменили
          снаружи»), кроме «✎ вы» вместо имени макета: сноска человека рисуется знаком «✎ вы»
          (ADR-038 «Авторство…»), так же делает F5b. Диалог открыт до исхода запроса: спиннер
          на кнопке, закрытие — по результату */}
      {mineAsk !== null && (
        <ConfirmDialog
          title={ADOPT_MINE.confirmTitle}
          subtitle={ADOPT_MINE.confirmBody}
          confirmLabel={ADOPT_MINE.confirm}
          onConfirm={() => runAdoptMine(mineAsk)}
          onCancel={() => setMineAsk(null)}
        />
      )}
    </div>
  );
}

// Тексты «Принять как мою правку» (F5c). Подпись и подтверждение — из записки
// notes-wiki-unified-v1 (§«д. Изменили снаружи»), дословно, кроме «✎ вы» вместо имени
// макета (тот же приём, что TRANSFER_TEXT в F5b). Исход и отказы — свои формулировки по
// кодам контракта (ADR-038 «Контракт для фронта», §adopt); тексты фичи ведёт Софья.
const ADOPT_MINE = {
  label: 'Принять как мою правку',
  confirmTitle: 'Принять как мою правку?',
  confirmBody: 'Как мою правку — изменённые места станут вашими (✎ вы). Как есть — текст останется, а у этих мест будет пометка «изменено снаружи».',
  confirm: 'Принять как мою правку',
  doneTitle: 'Принято как ваша правка',
  // Честный исход (вердикт R-F5c): «Здоровье» — отчёт последнего прогона линта, его после
  // принятия бэк не пересчитывает — строка «дрейфа» остаётся в нём до следующей проверки
  doneText: 'Строка «дрейфа» останется в отчёте до следующей проверки — нажмите «Проверить сейчас».',
  unchangedTitle: 'Ничего не изменилось',
  unchangedText: 'Файл уже совпадает с журналом Хранителя: принимать нечего.',
  changedTitle: 'Страницу поправили ещё раз',
  changedText: 'Страница изменилась. Отчёт устарел — нажмите «Проверить сейчас».',
  invalidTitle: 'Не удалось принять правку',
  invalidText: 'Страницу нельзя принять в таком виде.',
  failTitle: 'Не удалось принять правку',
  failText: 'Страница осталась как есть. Проверьте соединение и повторите.',
} as const;
