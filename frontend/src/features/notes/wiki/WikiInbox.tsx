// «Входящие» (S7a, макет llm-wiki-v2 §4): подвкладка вики, где только то, где нужен человек,
// — пакеты со статусом proposed. Операции с решением политики auto здесь не показываются
// вовсе: их решает код, не человек (контракт S4a: через REST auto не решается, 409
// wiki_pkg_auto). Неспорное и применённое — «Журнал» (S7b), не этот экран.
//
// Данные: GET /api/wiki/packages?inbox=true (стор не задействован — список живёт в компоненте,
// он не читается другими экранами). Порядок запросов — тот же инвариант, что у стора S5a:
// ответ кладётся, только если его поколение последнее (урок S5b/S5e); смена экрана или
// закрытие пакета поднимает новое поколение, поздний ответ прежней загрузки его не затрёт.
//
// Клик по строке открывает экран пакета (WikiPackageView) на данных строки: список уже несёт
// полный пакет (DTO List включает операции). Закрытие перечитывает список: решённый пакет
// мог уйти из «Входящих», и строка-«пропosed» не должна остаться в списке.

import { useCallback, useEffect, useRef, useState } from 'react';
import { AlertTriangle, Inbox } from 'lucide-react';
import {
  C, FONT, FS, R, SP, useIsMobile,
  WikiError, listWikiPackages, useWikiTree,
  type WikiPackageDto, type WikiPackageOperationDto,
} from 'aihome_shell/kit';
import { Button } from '../../../components/ui/Button';
import { EmptyState } from '../../../components/ui/EmptyState';
import { Notice } from '../../../components/ui/Notice';
import { ICON_SIZE, ICON_STROKE } from '../../../components/ui/icons';
import { sectionPrefix, wikiErrorText } from './WikiTab';
import { ORIGIN_META } from './wikiLabels';
import { WikiPackageView } from './WikiPackageView';

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
  }
  return wikiErrorText(e);
}

// Спорная операция пакета: решение человека ждёт только их (decision=inbox, ещё не решена)
export function pendingContested(p: WikiPackageDto): WikiPackageOperationDto[] {
  return p.operations.filter(o => o.decision === 'inbox' && !o.humanDecision);
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
  // первую, остальные — на экране пакета
  const first = pendingContested(pkg)[0];
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
        {first?.reason && (
          <span style={{ display: 'block', fontSize: FS.sm, marginTop: 2 }}>
            <b style={{ color: C.textMuted, fontWeight: 600 }}>Почему здесь:</b>{' '}
            <span style={{ color: C.textPrimary }}>{first.reason}</span>
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

// ─── Подвкладка ───

export function WikiInbox() {
  const [data, setData] = useState<{ packages: WikiPackageDto[]; invalidCount: number } | null>(null);
  const [loadError, setLoadError] = useState<unknown>(null);
  const [opened, setOpened] = useState<WikiPackageDto | null>(null);
  // Поколение загрузок (урок S5b/S5e, образец — openSeq в WikiTab): только последний
  // запрос пишет список; поздний ответ прежней загрузки его не затрёт
  const loadSeq = useRef(0);

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

  // Закрытие пакета перечитывает список: решённый пакет мог выйти из «Входящих»
  const closePackage = useCallback(() => {
    setOpened(null);
    load();
  }, [load]);

  if (opened) return <WikiPackageView pkg={opened} onBack={closePackage} />;

  if (loadError) {
    const text = loadError instanceof WikiError ? pkgErrorText(loadError) : 'Не удалось прочитать «Входящие»';
    return (
      <div data-testid="wiki-inbox-error" style={{ padding: SP.sm }}>
        <Notice tone="danger" icon={AlertTriangle} title={text} action={<Button variant="ghost" size="sm" onClick={load}>Повторить</Button>} />
      </div>
    );
  }
  if (!data) return <LoadingSkeleton />;

  const empty = data.packages.length === 0;
  return (
    <div style={{ padding: `0 0 ${SP.md}px` }}>
      <div style={{ padding: `${SP.sm}px ${SP.md}px 0` }}>
        <Notice tone="info" icon={Inbox} title="Здесь только то, где нужен человек">
          Неспорное применилось само — смотреть в «Журнале».
        </Notice>
      </div>
      {data.invalidCount > 0 && (
        <div style={{ padding: `${SP.sm}px ${SP.md}px 0` }}>
          <Notice tone="warning" icon={AlertTriangle} title={`Нечитаемых файлов пакета: ${data.invalidCount}`}>
            Они видны как сломанные и в решение не берутся.
          </Notice>
        </div>
      )}
      {empty ? (
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
      ) : (
        <>
          {data.packages.map(p => (
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
