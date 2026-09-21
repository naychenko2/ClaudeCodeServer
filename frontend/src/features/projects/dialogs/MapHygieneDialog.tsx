import { useEffect, useMemo, useState } from 'react';
import type {
  MapHygieneApplyFailure, MapHygieneApplyFailureReason,
  MapHygieneReport, MapHygieneSuggestion, MapHygieneSeverity,
} from '../../../types';
import { C, FS, FONT, MODAL_W, R, SP } from '../../../lib/design';
import { Badge, Button, Checkbox, Modal, WaitingIndicator } from '../../../components/ui';
import { api } from '../../../lib/api';

// Модалка «Уборка карты проекта» с фактами сканера и (опционально) формулировками
// модели.
//
// Структура отчёта:
//  • Шапка — динамический подзаголовок «CLAUDE.md · N строк · N КБ · ≈N токенов…» и
//    три метки Badge: длинные секции (warning), мёртвые ссылки (danger), ориентир
//    Anthropic (neutral). Процент «N % от нормы» НЕ показываем — большой проект
//    объективно больше, и приговор в процентах обесценивает отчёт (план, P9).
//  • Тело — один список, две озаглавленные группы (НЕ табы):
//      «Правки в один клик · N» — предложения с готовым apply (механические правки).
//      «Работа для чата · N» — остальные (вынос секции, переписать путь и т.п.).
//    Табы прячут половину картины и дают ложное «я прибрался», когда закрыт только
//    механический таб — а весь вес файла (длинные секции) сидит во второй группе.
//  • У неприменимых предложений (`apply: null`) чекбокса нет вовсе — галочка, которая
//    ничего не делает, врёт сильнее любой подписи. Счётчик «Применить отмеченное · N»
//    считает только применимое.
//  • Раскладка строки внутри группы: факт сканера основным, формулировка модели
//    (modelSays) — вторичным. Это не вкусовое: модель не читала карту целиком, её
//    суждение — догадка по метаданным; поменяешь местами — человек снесёт живой
//    раздел, поверив красивой фразе.
//  • Подвал — «Ещё в проекте»: вложенные карты справочно, без плашек и без действий.
//    Вложенная карта — не дефект, а правильный приём прогрессивного раскрытия.
//
// Состояния:
//  • review идёт — WaitingIndicator на месте группы «Работа для чата», факты уже видны
//  • review отказал — плашка с modelNote + «Повторить», без глухого «не удалось»
//  • apply частичный — панель с applied/failed по строкам, причины на человеческом
//    языке; шапка и группы перерисованы из scan того же ответа, второй запрос не нужен
//  • 409 — плашка «файл изменился после проверки · Проверить заново», общая для обоих
//    путей (review и apply)
interface Props {
  projectId: string;
  report: MapHygieneReport;
  /** Перезагрузка отчёта через эндпоинт scan — родительская секция уже умеет. */
  onReloaded?: () => Promise<unknown> | void;
  /** Получен свежий отчёт (после review/apply) — родитель обновит сводку в аккордеоне. */
  onReport?: (r: MapHygieneReport) => void;
  onClose: () => void;
}

export function MapHygieneDialog({ projectId, report, onClose, onReloaded, onReport }: Props) {
  // Отчёт лежит в локальном state: после review/apply он обновляется из ответа сервера.
  // Инициализируется из props один раз
  const [current, setCurrent] = useState<MapHygieneReport>(report);

  // Чекбоксы — по умолчанию сняты: предотмеченная галочка это не «явное утверждение
  // человека», а дефолт, который прокликивают (план Р10.1)
  const [selected, setSelected] = useState<Set<string>>(() => new Set());

  // Синхронизируем current с props.report по baseSha. Это нужно для двух сценариев:
  //  • «Проверить заново» после 409 — родитель пересканирует, baseSha прыгает, диалог
  //    подхватывает свежий отчёт без переоткрытия
  //  • apply записал файл, но родитель ещё не успел обновить state через onReport —
  //    следующий рендер принесёт новый report из props, и current синхронизируется
  // Сбрасываем выбор: id привязаны к якорям прежнего снимка текста, и отметить что-то
  // после записи — значит отметить не то
  useEffect(() => {
    setCurrent(report);
    setSelected(new Set());
  }, [report.baseSha]);

  const [reviewing, setReviewing] = useState(false);
  const [applying, setApplying] = useState(false);
  // Баннер ошибки. stale отдельно от прочих — у него своё действие «Проверить заново»
  const [banner, setBanner] = useState<{ kind: 'stale' | 'other'; msg: string } | null>(null);
  // Результат последнего apply — applied[] и failed[] для построчной раскладки
  const [lastApply, setLastApply] = useState<{
    applied: string[]; failed: MapHygieneApplyFailure[];
  } | null>(null);

  const { applicable, work } = useMemo(() => {
    const a: MapHygieneSuggestion[] = [];
    const w: MapHygieneSuggestion[] = [];
    for (const s of current.suggestions) (s.apply ? a : w).push(s);
    return { applicable: a, work: w };
  }, [current]);

  const toggle = (id: string) => {
    setSelected(prev => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id); else next.add(id);
      return next;
    });
  };
  const selectAll = () => setSelected(new Set(applicable.map(s => s.id)));
  const clearAll = () => setSelected(new Set());

  // Модель уже разбирала карту — признак «можно вызвать review ещё раз». По modelNote
  // тоже считаем: даже если ни одно суждение не прошло, серверный note показывает, что
  // ход был
  const modelReviewed = current.suggestions.some(s => s.modelSays !== null)
    || current.modelNote != null;

  // Текст ошибки с бэка. err.body?.error — у ProjectMap-ручек там строка;
  // подстраховка — обычный Error.message
  const errorText = (e: unknown): string => {
    const err = e as { body?: { error?: string }; message?: string; status?: number };
    return err?.body?.error || err?.message || 'Не удалось выполнить запрос';
  };

  // 409 — особый случай: файл изменился между сканом и запросом, шапку перерисует
  // «Проверить заново», и до этого момента ничего не делаем (выбранные id могут
  // указывать на старые строки)
  const isStale = (e: unknown): boolean =>
    (e as { status?: number } | null)?.status === 409;

  const runReview = async () => {
    if (reviewing || applying) return;
    if (current.baseSha == null) {
      setBanner({ kind: 'other', msg: 'Сначала отсканируйте карту заново' });
      return;
    }
    setBanner(null);
    setLastApply(null);
    setReviewing(true);
    try {
      const next = await api.projects.mapHygiene.review(projectId, current.baseSha);
      setCurrent(next);
      onReport?.(next);
    } catch (e) {
      if (isStale(e)) setBanner({ kind: 'stale', msg: 'файл изменился' });
      else setBanner({ kind: 'other', msg: errorText(e) });
    } finally {
      setReviewing(false);
    }
  };

  const runApply = async () => {
    if (reviewing || applying) return;
    const ids = Array.from(selected);
    if (ids.length === 0 || current.baseSha == null) return;
    setBanner(null);
    setApplying(true);
    try {
      const r = await api.projects.mapHygiene.apply(projectId, current.baseSha, ids);
      setLastApply({ applied: r.applied, failed: r.failed });
      // Отчёт из apply приходит свежим — шапка и группы перерисовываются без второго
      // запроса (Р10.4). Сбрасываем выделение: id уже применённых ушли в прошлое, а
      // выделение привязано к id, не к содержимому
      setCurrent(r.scan);
      onReport?.(r.scan);
      setSelected(new Set());
    } catch (e) {
      if (isStale(e)) setBanner({ kind: 'stale', msg: 'файл изменился' });
      else setBanner({ kind: 'other', msg: errorText(e) });
    } finally {
      setApplying(false);
    }
  };

  const subtitle = (
    <span>
      CLAUDE.md · {current.lines.toLocaleString('ru')} строк ·{' '}
      {Math.round(current.bytes / 1024).toLocaleString('ru')} КБ ·{' '}
      ≈{current.approxTokens.toLocaleString('ru')} токенов в каждой сессии
    </span>
  );

  return (
    <Modal
      title="Уборка карты проекта"
      subtitle={subtitle}
      width={MODAL_W.wide}
      cardStyle={{ height: 'calc(100vh - 32px)' }}
      onClose={onClose}
      footer={
        <Footer
          applicableCount={applicable.length}
          selectedCount={selected.size}
          onSelectAll={selectAll}
          onClearAll={clearAll}
          onApply={runApply}
          applying={applying}
          onClose={onClose}
        />
      }
    >
      {!current.exists ? (
        <EmptyState>Карты проекта пока нет. Ассистент создаст её при первом знакомстве с проектом.</EmptyState>
      ) : current.suggestions.length === 0 ? (
        <EmptyState>Карта в порядке</EmptyState>
      ) : (
        <>
          <HeaderBadges report={current} />
          {banner?.kind === 'stale' && (
            <StaleBanner onRescan={() => {
              setBanner(null);
              setLastApply(null);
              void onReloaded?.();
            }} />
          )}
          {banner?.kind === 'other' && (
            <ErrorBanner msg={banner.msg} onDismiss={() => setBanner(null)} />
          )}
          {current.modelNote && (
            <ModelNoteBanner
              msg={current.modelNote}
              onRetry={runReview}
              retrying={reviewing}
            />
          )}
          {lastApply && (
            <ApplyResultPanel applied={lastApply.applied} failed={lastApply.failed} />
          )}
          <Group
            title="Правки в один клик"
            count={applicable.length}
            empty="Здесь появятся готовые механические правки"
          >
            {applicable.map(s => (
              <SuggestionRow
                key={s.id}
                suggestion={s}
                selected={selected.has(s.id)}
                onToggle={() => toggle(s.id)}
              />
            ))}
          </Group>
          <Group
            title="Работа для чата"
            count={work.length}
            rightSlot={
              reviewing ? (
                <WaitingIndicator awaitingResponse hint="Модель думает" />
              ) : (
                <Button variant="ghost" size="xs" onClick={runReview}>
                  {modelReviewed ? 'Переразобрать' : 'Разобрать моделью'}
                </Button>
              )
            }
          >
            {reviewing ? null : work.map(s => (
              <SuggestionRow key={s.id} suggestion={s} />
            ))}
          </Group>
          <MoreMapsPanel report={current} />
        </>
      )}
    </Modal>
  );
}

function Group({ title, count, empty, rightSlot, children }: {
  title: string;
  count: number;
  empty?: string;
  rightSlot?: React.ReactNode;
  children: React.ReactNode;
}) {
  return (
    <section>
      <div style={{
        display: 'flex', alignItems: 'center', justifyContent: 'space-between',
        gap: SP.sm, paddingBottom: SP.xs,
      }}>
        <h3 style={{
          fontFamily: 'inherit',
          fontSize: FS.sm, fontWeight: 600, margin: 0,
          color: C.textSecondary, textTransform: 'uppercase', letterSpacing: '0.05em',
        }}>
          {title} · {count}
        </h3>
        {rightSlot}
      </div>
      {count === 0 && empty ? (
        <div style={{ fontSize: FS.base, color: C.textMuted, padding: `${SP.sm}px 0` }}>{empty}</div>
      ) : (
        <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xs }}>{children}</div>
      )}
    </section>
  );
}

function HeaderBadges({ report }: { report: MapHygieneReport }) {
  const over = report.budget.overBudget;
  return (
    <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.sm }}>
      <Badge tone="warning">
        {report.longSectionCount} {pluralRu(report.longSectionCount, 'секция длиннее', 'секции длиннее', 'секций длиннее')} {report.budget.recommendedLines} строк
      </Badge>
      <Badge tone="danger">
        {report.deadLinkCount} {pluralRu(report.deadLinkCount, 'мёртвая ссылка', 'мёртвые ссылки', 'мёртвых ссылок')}
      </Badge>
      <Badge tone="neutral">
        Ориентир: до {report.budget.recommendedLines} строк{over ? ' · превышен' : ''}
      </Badge>
    </div>
  );
}

// Плашка «Файл изменился после проверки» — единая для review и apply. Появляется,
// когда сервер вернул 409 (staleBaseSha). Кнопка «Проверить заново» зовёт scan
function StaleBanner({ onRescan }: { onRescan: () => void }) {
  return (
    <div style={{
      marginTop: SP.sm, padding: SP.sm,
      borderRadius: R.md,
      background: C.warningBg, color: C.warningText,
      fontSize: FS.sm, lineHeight: 1.5,
      display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap',
    }}>
      <span style={{ flex: 1, minWidth: 200 }}>
        Файл изменился после проверки — отметки могут указывать на старые строки.
      </span>
      <Button variant="ghost" size="sm" onClick={onRescan}>Проверить заново</Button>
    </div>
  );
}

// Прочие ошибки: показываем настоящую причину (текст с бэка) и кнопку «Закрыть плашку».
// Глухое «не удалось» ничего не лечит — человек не починит «модель для разбора не настроена»
function ErrorBanner({ msg, onDismiss }: { msg: string; onDismiss: () => void }) {
  return (
    <div style={{
      marginTop: SP.sm, padding: SP.sm,
      borderRadius: R.md,
      background: C.dangerBg, color: C.dangerText,
      fontSize: FS.sm, lineHeight: 1.5,
      display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap',
    }}>
      <span style={{ flex: 1, minWidth: 200 }}>{msg}</span>
      <Button variant="ghost" size="sm" onClick={onDismiss}>Закрыть</Button>
    </div>
  );
}

// Плашка «модель отказала» — показывает серверный modelNote дословно. По нему человек
// понимает, что чинить: настроить модель, дать доступ к сети или просто повторить.
// Кнопка «Повторить» запускает review ещё раз
function ModelNoteBanner({ msg, onRetry, retrying }: {
  msg: string; onRetry: () => void; retrying: boolean;
}) {
  return (
    <div style={{
      marginTop: SP.sm, padding: SP.sm,
      borderRadius: R.md,
      background: C.warningBg, color: C.warningText,
      fontSize: FS.sm, lineHeight: 1.5,
      display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap',
    }}>
      <span style={{ flex: 1, minWidth: 200 }}>
        Модель не дала формулировок: <strong>{msg}</strong>
      </span>
      <Button variant="ghost" size="sm" onClick={onRetry} loading={retrying}>Повторить</Button>
    </div>
  );
}

// Раскладка строки «применили N, не вышло M» + построчный список причин по failed[].
// applied скрывается, когда пуст (плашка по делу не появляется); failed показывается
// всегда — это то, ради чего человек открыл панель
function ApplyResultPanel({ applied, failed }: {
  applied: string[]; failed: MapHygieneApplyFailure[];
}) {
  const appliedCount = applied.length;
  const failedCount = failed.length;
  if (appliedCount === 0 && failedCount === 0) return null;
  return (
    <div style={{
      marginTop: SP.sm, padding: SP.sm,
      borderRadius: R.md,
      background: failedCount > 0 ? C.warningBg : C.successBg,
      color: failedCount > 0 ? C.warningText : C.successText,
      fontSize: FS.sm, lineHeight: 1.5,
    }}>
      <div style={{ fontWeight: 600, marginBottom: failedCount > 0 ? SP.xs : 0 }}>
        {appliedCount > 0 && (
          <span>Применили {appliedCount} {pluralRu(appliedCount, 'правку', 'правки', 'правок')}.</span>
        )}
        {failedCount > 0 && (
          <span style={{ marginLeft: appliedCount > 0 ? SP.sm : 0 }}>
            Не вышло: {failedCount} {pluralRu(failedCount, 'правка', 'правки', 'правок')}.
          </span>
        )}
      </div>
      {failedCount > 0 && (
        <ul style={{ margin: 0, paddingLeft: SP.lg }}>
          {failed.map((f, i) => (
            <li key={`${f.id}-${i}`} style={{ fontSize: FS.sm }}>
              <code style={{ fontFamily: FONT.mono, fontSize: FS.xs }}>{f.id.slice(0, 8)}</code>
              {' — '}
              {failureReasonText(f.reason)}
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

function failureReasonText(r: MapHygieneApplyFailureReason): string {
  switch (r) {
    case 'anchorNotFound': return 'якорь не найден в карте (правку уже внесли или текст переписали)';
    case 'ambiguousAnchor': return 'якорь встречается несколько раз — куда писать решает человек';
    case 'unknownId': return 'предложения с таким id нет в свежем скане';
    case 'notApplicable': return 'механической правки у этого предложения нет по его виду';
  }
}

function SuggestionRow({ suggestion, selected, onToggle }: {
  suggestion: MapHygieneSuggestion;
  selected?: boolean;
  onToggle?: () => void;
}) {
  return (
    <div style={{
      display: 'flex', alignItems: 'flex-start', gap: SP.sm,
      padding: SP.sm, borderRadius: R.md,
      border: `1px solid ${C.borderLight}`,
      background: C.bgWhite,
    }}>
      {onToggle ? (
        <Checkbox checked={!!selected} onChange={() => onToggle()} ariaLabel={suggestion.fact} />
      ) : (
        <span aria-hidden style={{ width: 40, height: 40, flexShrink: 0 }} />
      )}
      <div style={{ flex: 1, minWidth: 0 }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap' }}>
          <Badge size="xs" tone={badgeTone(suggestion.severity)}>{severityLabel(suggestion.severity)}</Badge>
          {suggestion.anchor.heading && (
            <span style={{ fontSize: FS.sm, color: C.textMuted }}>«{suggestion.anchor.heading}» · строка {suggestion.anchor.line}</span>
          )}
          {!suggestion.anchor.heading && suggestion.anchor.line > 0 && (
            <span style={{ fontSize: FS.sm, color: C.textMuted }}>строка {suggestion.anchor.line}</span>
          )}
          {suggestion.savingLines > 0 && (
            <span style={{ fontSize: FS.sm, color: C.textMuted }}>· {suggestion.savingLines} {pluralRu(suggestion.savingLines, 'строка', 'строки', 'строк')}</span>
          )}
        </div>
        <div style={{ fontSize: FS.base, color: C.textPrimary, lineHeight: 1.45, marginTop: 2 }}>
          {suggestion.fact}
        </div>
        {suggestion.modelSays && (
          <div style={{ fontSize: FS.sm, color: C.textSecondary, lineHeight: 1.5, marginTop: 2, fontStyle: 'italic' }}>
            {suggestion.modelSays}
          </div>
        )}
        {suggestion.apply && (
          <DiffBeforeAfter before={suggestion.apply.before} after={suggestion.apply.after} />
        )}
      </div>
    </div>
  );
}

function DiffBeforeAfter({ before, after }: { before: string; after: string }) {
  return (
    <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.xs, marginTop: 6 }}>
      <span style={{
        fontFamily: FONT.mono, fontSize: FS.xs,
        padding: '2px 6px', borderRadius: R.sm,
        background: C.diffRemBg, color: C.diffRemText, maxWidth: '100%',
        overflow: 'hidden', textOverflow: 'ellipsis',
      }} title={before}>{before}</span>
      <span style={{ fontSize: FS.xs, color: C.textMuted, alignSelf: 'center' }}>→</span>
      <span style={{
        fontFamily: FONT.mono, fontSize: FS.xs,
        padding: '2px 6px', borderRadius: R.sm,
        background: C.diffAddBg, color: C.diffAddText, maxWidth: '100%',
        overflow: 'hidden', textOverflow: 'ellipsis',
      }} title={after}>{after}</span>
    </div>
  );
}

function MoreMapsPanel({ report }: { report: MapHygieneReport }) {
  const items: { path: string; lines: number }[] = [];
  if (report.secondMap) items.push({ path: report.secondMap.path, lines: report.secondMap.lines });
  if (report.localMap) items.push({ path: report.localMap.path, lines: report.localMap.lines });
  for (const m of report.nestedMaps) items.push({ path: m.path, lines: m.lines });
  if (items.length === 0) return null;
  return (
    <section>
      <h3 style={{
        fontFamily: 'inherit',
        fontSize: FS.sm, fontWeight: 600,
        color: C.textSecondary, textTransform: 'uppercase', letterSpacing: '0.05em',
        margin: 0, paddingBottom: SP.xs,
      }}>
        Ещё в проекте
      </h3>
      <ul style={{ margin: 0, paddingLeft: SP.lg, fontSize: FS.base, color: C.textSecondary, lineHeight: 1.5 }}>
        {items.map(m => (
          <li key={m.path} style={{ fontFamily: 'JetBrains Mono, monospace', fontSize: FS.sm }}>
            {m.path} · {m.lines} {pluralRu(m.lines, 'строка', 'строки', 'строк')}
          </li>
        ))}
      </ul>
    </section>
  );
}

function Footer({ applicableCount, selectedCount, onSelectAll, onClearAll, onApply, applying, onClose }: {
  applicableCount: number;
  selectedCount: number;
  onSelectAll: () => void;
  onClearAll: () => void;
  onApply: () => void;
  applying: boolean;
  onClose: () => void;
}) {
  // Кнопка активна, когда есть отмеченные и не идёт прямо сейчас apply. Дисейблится
  // с подсказкой, чтобы не молча
  const canApply = selectedCount > 0 && !applying;
  return (
    <div style={{
      display: 'flex', alignItems: 'center', gap: SP.md, flexWrap: 'wrap',
      width: '100%',
    }}>
      <div style={{ display: 'flex', gap: SP.sm, flex: 1, minWidth: 200 }}>
        {applicableCount > 0 && selectedCount === 0 && (
          <Button variant="ghost" size="sm" onClick={onSelectAll}>Отметить все</Button>
        )}
        {applicableCount > 0 && selectedCount > 0 && (
          <Button variant="ghost" size="sm" onClick={onClearAll}>Снять отметки</Button>
        )}
      </div>
      <Button variant="ghost" size="md" onClick={onClose}>Закрыть</Button>
      <Button
        variant="primary"
        size="md"
        disabled={!canApply}
        title={selectedCount === 0 ? 'Отметьте хотя бы одну механическую правку' : undefined}
        loading={applying}
        onClick={onApply}
      >
        Применить отмеченное · {selectedCount}
      </Button>
    </div>
  );
}

function EmptyState({ children }: { children: React.ReactNode }) {
  return (
    <div style={{
      padding: `${SP.lg}px ${SP.md}px`,
      fontSize: FS.md, color: C.textSecondary, lineHeight: 1.5, textAlign: 'center',
    }}>
      {children}
    </div>
  );
}

function badgeTone(s: MapHygieneSeverity): 'danger' | 'warning' | 'neutral' {
  if (s === 'high') return 'danger';
  if (s === 'medium') return 'warning';
  return 'neutral';
}

function severityLabel(s: MapHygieneSeverity): string {
  if (s === 'high') return 'важно';
  if (s === 'medium') return 'стоит посмотреть';
  return 'мелочь';
}

function pluralRu(n: number, one: string, few: string, many: string): string {
  const mod10 = n % 10;
  const mod100 = n % 100;
  if (mod10 === 1 && mod100 !== 11) return one;
  if (mod10 >= 2 && mod10 <= 4 && (mod100 < 10 || mod100 >= 20)) return few;
  return many;
}
