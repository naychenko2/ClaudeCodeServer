import { useMemo, useState } from 'react';
import type { MapHygieneReport, MapHygieneSuggestion, MapHygieneSeverity } from '../../../types';
import { C, FS, FONT, MODAL_W, R, SP } from '../../../lib/design';
import { Badge, Button, Checkbox, Modal } from '../../../components/ui';

// Модалка «Уборка карты проекта» с фактами сканера.
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
//    ничего не делает, врёт сильнее любой подписи. Счётчик «Применить отмеченное ·
//    N» считает только применимое.
//  • Подвал — «Ещё в проекте»: вложенные карты справочно, без плашек и без действий.
//    Вложенная карта — не дефект, а правильный приём прогрессивного раскрытия.
//
// Состояние модели (формулировки, думает / отказала) и само применение правок —
// волны 2 и 4. В этой волне модель не дёргается, поэтому группа 1 пуста до ответа
// review, а действие apply — заглушка.
interface Props {
  report: MapHygieneReport;
  /** Перезагрузка отчёта через эндпоинт scan — родительская секция уже умеет. */
  onReloaded?: () => Promise<unknown> | void;
  onClose: () => void;
}

export function MapHygieneDialog({ report, onClose, onReloaded }: Props) {
  // Чекбоксы — по умолчанию сняты: предотмеченная галочка это не «явное утверждение
  // человека», а дефолт, который прокликивают (план Р10.1)
  const [selected, setSelected] = useState<Set<string>>(() => new Set());

  // apply есть только у dead-link с единственным кандидатом И уникальным якорем
  // (контракт записи Р10а). В этой волне scan приходит с apply: null у всех —
  // заполнение на review (волна 2). Группа «Правки в один клик» уже создана
  // структурно, чтобы место под неё было при появлении формулировок
  void onReloaded; // зарезервировано под «Проверить заново» (плашка + кнопка — волна 4)
  const { applicable, work } = useMemo(() => {
    const a: MapHygieneSuggestion[] = [];
    const w: MapHygieneSuggestion[] = [];
    for (const s of report.suggestions) (s.apply ? a : w).push(s);
    return { applicable: a, work: w };
  }, [report]);

  const toggle = (id: string) => {
    setSelected(prev => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id); else next.add(id);
      return next;
    });
  };
  const selectAll = () => setSelected(new Set(applicable.map(s => s.id)));
  const clearAll = () => setSelected(new Set());

  const subtitle = (
    <span>
      CLAUDE.md · {report.lines.toLocaleString('ru')} строк ·{' '}
      {Math.round(report.bytes / 1024).toLocaleString('ru')} КБ ·{' '}
      ≈{report.approxTokens.toLocaleString('ru')} токенов в каждой сессии
    </span>
  );

  return (
    <Modal
      title="Уборка карты проекта"
      subtitle={subtitle}
      width={MODAL_W.wide}
      // Высота фиксированная — содержимое сильно разное (от одной секции до 257 строк),
      // прыгающая карточка мешала бы сравнивать. Низ с действиями прижат, середина
      // скроллится — это работа самого Modal через cc-modal-content (см. styles index.css)
      cardStyle={{ height: 'calc(100vh - 32px)' }}
      onClose={onClose}
      footer={
        <Footer
          applicableCount={applicable.length}
          selectedCount={selected.size}
          onSelectAll={selectAll}
          onClearAll={clearAll}
          // apply — волна 4 (POST .../apply). В этой волне только отметки сохраняются,
          // кнопка disabled — человек видит, что выбор не потерян между состояниями
          onApply={() => {}}
          onClose={onClose}
        />
      }
    >
      {!report.exists ? (
        <EmptyState>Карты проекта пока нет. Ассистент создаст её при первом знакомстве с проектом.</EmptyState>
      ) : report.suggestions.length === 0 ? (
        <EmptyState>Карта в порядке</EmptyState>
      ) : (
        <>
          <HeaderBadges report={report} />
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
          <Group title="Работа для чата" count={work.length}>
            {work.map(s => (
              <SuggestionRow key={s.id} suggestion={s} />
            ))}
          </Group>
          <MoreMapsPanel report={report} />
        </>
      )}
    </Modal>
  );
}

function Group({ title, count, empty, children }: {
  title: string;
  count: number;
  empty?: string;
  children: React.ReactNode;
}) {
  return (
    <section>
      <h3 style={{
        fontFamily: 'inherit',
        fontSize: FS.sm, fontWeight: 600,
        color: C.textSecondary, textTransform: 'uppercase', letterSpacing: '0.05em',
        margin: 0, paddingBottom: SP.xs,
      }}>
        {title} · {count}
      </h3>
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

function SuggestionRow({ suggestion, selected, onToggle }: {
  suggestion: MapHygieneSuggestion;
  selected?: boolean;
  onToggle?: () => void;
}) {
  // Факт — основной текст (план Р8: «поменять местами = человек снесёт живой раздел,
  // поверив красивой фразе»). ModelSays — вторичный, выделен курсивом цветом secondary;
  // сейчас modelSays всегда null (модель не зовётся в этой волне), но место оставлено
  // под волну 2
  return (
    <div style={{
      display: 'flex', alignItems: 'flex-start', gap: SP.sm,
      padding: SP.sm, borderRadius: R.md,
      border: `1px solid ${C.borderLight}`,
      background: C.bgWhite,
    }}>
      {/* Чекбокс — только у применимых. У остальных (apply: null) чекбокса нет вовсе —
          галочка, которая ничего не делает, врёт сильнее любой подписи */}
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
        {/* Якорь замены (apply.before/after) показываем как «было → стало» на
            токенах C.diffRemBg/C.diffAddBg. DiffView сюда не лезет — у него нет ни
            потолка высоты, ни виртуализации (план Р9) */}
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
  // Вложенные карты справочно — без плашек и без действий (план, Р4). Вложенная
  // карта — правильный приём прогрессивного раскрытия, и плашка серьёзности
  // отправила бы человека чинить здоровое
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

function Footer({ applicableCount, selectedCount, onSelectAll, onClearAll, onApply, onClose }: {
  applicableCount: number;
  selectedCount: number;
  onSelectAll: () => void;
  onClearAll: () => void;
  onApply: () => void;
  onClose: () => void;
}) {
  // «Применить отмеченное · N» считает только применимое (apply !== null). В этой
  // волне POST ещё не реализован — кнопка disabled с подсказкой. Отметить все /
  // снять — действующие кнопки, потому что отметки должны сохраняться между
  // состояниями и человек должен иметь возможность снять
  const canApply = selectedCount > 0;
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
        title={canApply ? 'Появятся в следующей версии' : 'Отметьте хотя бы одну механическую правку'}
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
