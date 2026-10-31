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

import { useCallback, useEffect, useRef, useState } from 'react';
import { AlertTriangle, ArrowUpCircle, HeartPulse, RefreshCw } from 'lucide-react';
import { C, FONT, FS, R, SP } from '../../../lib/design';
import { Badge, type BadgeTone } from '../../../components/ui/Badge';
import { Button } from '../../../components/ui/Button';
import { EmptyState } from '../../../components/ui/EmptyState';
import { Notice } from '../../../components/ui/Notice';
import { ICON_SIZE, ICON_STROKE } from '../../../components/ui/icons';
import { useIsMobile } from '../../../lib/breakpoints';
import { WikiError, getWikiHealth, runWikiLint, useWikiTree } from '../../../lib/wiki';
import { sectionPrefix, wikiErrorText } from './wikiLabels';

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
const KIND_BY_LABEL: Record<string, HealthKind> = Object.fromEntries(
  (Object.keys(KIND_META) as Exclude<HealthKind, 'unknown'>[]).map(k => [KIND_META[k].label, k]),
);

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
  const kind = KIND_BY_LABEL[kindLabel] ?? 'unknown';
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
  const out: HealthReport = { checkedAt: null, total: null, findings: [], failure: null };
  let where: 'none' | 'findings' | 'failure' = 'none';
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

// ─── Строки ───

function FindingRow({ f }: { f: HealthFinding }) {
  const meta = f.kind === 'unknown' ? null : KIND_META[f.kind];
  return (
    <div
      data-testid="wiki-health-row"
      style={{
        display: 'flex', gap: SP.sm, alignItems: 'baseline', flexWrap: 'wrap',
        padding: `${SP.sm}px ${SP.md}px`, borderTop: `1px solid ${C.borderLight}`,
      }}
    >
      {f.page && (
        <span style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted, wordBreak: 'break-all' }}>
          {f.page}
        </span>
      )}
      {f.message && <span style={{ fontSize: FS.base, color: C.textPrimary, minWidth: 0, flex: '1 1 200px' }}>{f.message}</span>}
      {f.related.length > 0 && (
        <span style={{ fontSize: FS.sm, color: C.textMuted }}>
          связанные:{' '}{f.related.map((r, i) => (
            <span key={i} style={{ fontFamily: FONT.mono, wordBreak: 'break-all' }}>{i ? ', ' : ''}{r}</span>
          ))}
        </span>
      )}
      {f.key && (
        <span style={{ fontFamily: FONT.mono, fontSize: FS.sm, color: C.textMuted, wordBreak: 'break-all' }}>
          ключ: {f.key}
        </span>
      )}
      {f.count !== null && (
        <span style={{ fontFamily: FONT.mono, fontSize: FS.sm, color: C.textMuted }}>счёт: {f.count}</span>
      )}
      {meta?.title && !f.message && <span style={{ fontSize: FS.sm, color: C.textMuted }}>{meta.title}</span>}
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
  const tree = useWikiTree();
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
    setLoadError(null);
    setNoReport(false);
    if (!selectedKey) { setReport(null); return; }
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
      try {
        await runWikiLint();
        if (seq.current !== mine) return;
        const d = await getWikiHealth(selectedKey);
        if (seq.current === mine) { setNoReport(false); setReport(parseHealth(d.content)); }
      } catch (e) {
        if (seq.current === mine) setActionError(e);
      } finally {
        setBusy(false);
      }
    })();
  }, [busy, selectedKey]);

  const section = selectedKey ? tree?.sections.find(s => s.key === selectedKey) ?? null : null;
  // Имя раздела — из дерева (его дал бэк как данные); ключ из props — только когда дерева нет
  const prefix = section ? sectionPrefix(section) : selectedKey ?? '';

  const runButton = (
    <Button
      variant="secondary" size="sm" dataTestId="wiki-health-run"
      ariaLabel="Выполнить проверку вики сейчас"
      disabled={busy || !selectedKey}
      leftIcon={<RefreshCw size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
      onClick={run}
    >
      Проверить сейчас
    </Button>
  );

  if (!selectedKey) {
    return (
      <div>
        <div style={{ padding: `${SP.sm}px ${SP.md}px` }}>{runButton}</div>
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

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm, paddingBottom: SP.md }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap', padding: `${SP.sm}px ${SP.md}px 0` }}>
        <span style={{ flex: '1 1 200px', minWidth: 0, display: 'flex', alignItems: 'center', gap: SP.xs, fontSize: FS.xs, color: C.textMuted, fontFamily: FONT.mono }}>
          <HeartPulse size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
          <span style={{ wordBreak: 'break-all' }}>
            {checked ? `проверка: ${checked}` : 'проверки ещё не было'} · {prefix}
          </span>
        </span>
        {runButton}
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

      {noReport && (
        <EmptyState
          icon={<HeartPulse size={ICON_SIZE.xl} strokeWidth={ICON_STROKE} />}
          title="Нет данных"
          subtitle={<span style={{ maxWidth: 360 }}>Этот раздел ещё не проверялся. Нажмите «Проверить сейчас» — здесь появятся находки и счётчики.</span>}
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

          {nothingFound ? (
            <EmptyState
              icon={<HeartPulse size={ICON_SIZE.xl} strokeWidth={ICON_STROKE} />}
              title="Находок нет"
              subtitle={<span style={{ maxWidth: 360 }}>Последняя проверка ничего не нашла. Следующая — по кнопке выше.</span>}
            />
          ) : (
            <>
              {/* Счётчики последнего отчёта — по видам, которых он действительно касается.
                  Метрик прогона здесь нет: их отдельной ручкой бэк не отдаёт */}
              <div
                data-testid="wiki-health-counters"
                style={{ display: 'grid', gridTemplateColumns: `repeat(${isMobile ? 2 : 4}, minmax(0, 1fr))`, gap: SP.sm, padding: `0 ${SP.md}px` }}
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
                  <div>
                    {g.items.map((f, i) => <div key={i}><FindingRow f={f} /></div>)}
                  </div>
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
                  <div>
                    {promotes.items.map((f, i) => <div key={i}><FindingRow f={f} /></div>)}
                  </div>
                </div>
              )}
            </>
          )}
        </>
      )}
    </div>
  );
}
