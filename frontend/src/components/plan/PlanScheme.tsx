// Разворот плана схемой (часть B фичи «Визуальный разворот плана», docs/plans/visual-plan.md).
// Три уровня:
//   • «Суть» (без прокрутки): жанр, суть одной фразой, ряд чисел, блок «Требует вашего внимания»
//     (только блоки с непустыми флагами — каждый кликабелен и ведёт в свой блок), границы
//     плана чипами.
//   • «Карта»: блоки по строке с ветвлением по DependsOn.
//   • «Блок»: заголовок блока, флаги пилюлями и ИСХОДНЫЙ markdown раздела через sectionOf(h)
//     из useHeadings.
// Крошки сверху, возврат с любой глубины. Из «Сути» прыжок сразу в блок, минуя карту.
//
// ЖЕЛЕЗНОЕ ПРАВИЛО: пункт внимания без живой пары (anchor, anchorIndex) в useHeadings
// не рисуется. Причина — это не дубликат серверной проверки: сервер сверял anchor с
// ИСХОДНЫМ markdown плана, а мы ищем заголовок в ОТРЕНДЕРЕННОМ DOM. Тексты могут
// разойтись из-за inline-разметки внутри заголовка (см. эпиграф в schemeLogic.ts).
// Тогда пункт, ведущий «в раздел», не имел бы цели — это ровно та гладкая сводка,
// от которой мы защищаемся. Поэтому пункт тихо исчезает, а не остаётся неактивной ссылкой.
//
// Замечания из схемы НЕ создаются — это навигация. Замечания остаются в PlanRemarks
// у текста; пункт внимания уводит в «Блок» с исходным разделом, а «оставить
// замечание к разделу» — клик по маркеру у заголовка в тексте.
//
// Чистая логика (resolveHeading, headingHasDuplicates, sliceSection, stripInlineMarkdown)
// — в schemeLogic.ts, чтобы её можно было прогнать юнит-тестами без браузера.

import { useMemo, useState, type RefObject } from 'react';
import { AlertTriangle, ArrowLeft, ArrowRight, ChevronRight, ShieldOff } from 'lucide-react';
import type { PlanMap, PlanMapBlock, PlanMapGenre, PlanMapBlockType } from '../../types';
import { useHeadings, type Heading } from '../../hooks/useHeadings';
import { MarkdownContent } from '../chat/MarkdownContent';
import { C, FONT, R, SP, FS } from '../../lib/design';
import { resolveHeading, headingHasDuplicates, sliceSection } from './schemeLogic';

interface Props {
  // Карта (PlanMap) — приходит с бэка. Сборка инициируется родителем; компонент
  // принимает готовый объект и рисует.
  map: PlanMap;
  // Исходный markdown плана — для sectionOf(h) на уровне «Блок»
  planText: string;
  // Контейнер, внутри которого MarkdownContent рендерит план (тот же реф, что у
  // PlanRemarks у текста). Нужен для useHeadings — заголовки берём с реального DOM,
  // иначе scrollToHeading/sectionOf работали бы со «своими» узлами и промахивались.
  contentRef: RefObject<HTMLElement | null>;
}

// Жанр → человекочитаемая подпись на «Сути»
const GENRE_LABEL: Record<PlanMapGenre, string> = {
  feature:   'фича',
  fix:       'починка',
  choice:    'выбор',
  audit:     'аудит',
  framework: 'каркас',
  operation: 'операция',
};

// Тип блока → короткая подпись на пилюле (на карте/блоке). boundary — НЕ показываем
// в карте (это граница, а не шаг), только в блоке «Чего этот план не делает» на «Сути».
const TYPE_LABEL: Record<PlanMapBlockType, string> = {
  step:       'шаг',
  decision:   'развилка',
  fork:       'развилка',
  risk:       'риск',
  criterion:  'критерий',
  boundary:   'граница',
};

// Подпись флага внимания — на «Сути» и на уровне «Блок» в виде пилюли
const FLAG_LABEL: Record<PlanMapBlock['flags'][number], string> = {
  'blocking':       'блокирует',
  'needs-decision': 'нужно решение',
  'expands-scope':  'расширяет рамку',
  'has-cost':       'несёт стоимость',
  'review-fix':     'проверить починку',
};

type View = 'essence' | 'map' | 'block';

export function PlanScheme({ map, planText, contentRef }: Props) {
  const [view, setView] = useState<View>('essence');
  const [selectedBlockId, setSelectedBlockId] = useState<string | null>(null);
  const headings = useHeadings(contentRef, planText);

  // Кеш: id блока → живой Heading. Пересчитываем только при смене headings.
  const resolved = useMemo(() => {
    const m = new Map<string, Heading>();
    for (const b of map.blocks) {
      const h = resolveHeading(b.anchor, b.anchorIndex, headings);
      if (h) m.set(b.id, h);
    }
    return m;
  }, [map.blocks, headings]);

  // Только блоки с непустыми флагами — пункт внимания. Без живой пары в DOM блок
  // выбрасывается (см. эпиграф): пункт, ведущий в никуда, защиты от «согласен не
  // читая» не даёт. Это НЕ повтор серверного потолка «не больше 5 флагов»: потолок
  // режет на сервере и нам сюда больше 5 просто не придёт, фронт только фильтрует
  // уже валидированный список по наличию флагов.
  const attentionBlocks = useMemo(
    () => map.blocks.filter(b => b.flags.length > 0 && resolved.has(b.id)),
    [map.blocks, resolved],
  );

  // Границы плана — блоки с type='boundary' (если модель выделила их). Тоже только
  // с живым якорем, иначе чип «X не делает» указывал бы на несуществующий раздел.
  const boundaryBlocks = useMemo(
    () => map.blocks.filter(b => b.type === 'boundary' && resolved.has(b.id)),
    [map.blocks, resolved],
  );

  // Карта без границ и без внимания — собственно «шаги» для экрана «Карта»
  const mapBlocks = useMemo(
    () => map.blocks.filter(b => b.type !== 'boundary'),
    [map.blocks],
  );

  // Выбранный блок (для экрана «Блок»)
  const selectedBlock = selectedBlockId ? map.blocks.find(b => b.id === selectedBlockId) ?? null : null;
  const selectedHeading = selectedBlockId ? resolved.get(selectedBlockId) ?? null : null;
  const selectedSection = selectedHeading
    ? sliceSection(planText, selectedHeading, headings)
    : null;

  function openBlock(id: string) {
    setSelectedBlockId(id);
    setView('block');
  }

  function backToEssence() { setView('essence'); setSelectedBlockId(null); }
  function backToMap() { setView('map'); setSelectedBlockId(null); }

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.md }}>
      <Breadcrumbs view={view} hasSelected={!!selectedBlock} onEssence={backToEssence} onMap={() => { setView('map'); setSelectedBlockId(null); }} />
      {view === 'essence' && (
        <Essence
          map={map}
          attentionBlocks={attentionBlocks}
          boundaryBlocks={boundaryBlocks}
          onOpenBlock={openBlock}
        />
      )}
      {view === 'map' && (
        <MapView
          blocks={mapBlocks}
          resolved={resolved}
          onOpenBlock={openBlock}
        />
      )}
      {view === 'block' && selectedBlock && (
        <BlockView
          block={selectedBlock}
          heading={selectedHeading}
          section={selectedSection}
          headings={headings}
          onBackToEssence={backToEssence}
          onBackToMap={backToMap}
        />
      )}
    </div>
  );
}

// === Крошки ===
// Из «Сути» можно прыгнуть сразу в «Блок», минуя «Карту», поэтому «Блок» помечен
// отдельно. Последний элемент не кликабелен — мы в нём.
function Breadcrumbs({ view, hasSelected, onEssence, onMap }: {
  view: View;
  hasSelected: boolean;
  onEssence: () => void;
  onMap: () => void;
}) {
  const items: Array<{ label: string; onClick?: () => void; active: boolean }> = [];
  items.push({ label: 'Суть', onClick: view !== 'essence' ? onEssence : undefined, active: view === 'essence' });
  if (view === 'map' || view === 'block') {
    items.push({ label: 'Карта', onClick: view === 'block' ? onMap : undefined, active: view === 'map' });
  }
  if (view === 'block' && hasSelected) {
    items.push({ label: 'Блок', active: true });
  }
  return (
    <div style={{
      display: 'flex', flexWrap: 'wrap', alignItems: 'center', gap: 4,
      fontFamily: FONT.sans, fontSize: FS.sm,
    }}>
      {items.map((it, i) => (
        <span key={it.label} style={{ display: 'inline-flex', alignItems: 'center', gap: 4 }}>
          {i > 0 && <ChevronRight size={12} style={{ color: C.textMuted, flexShrink: 0 }} />}
          {it.onClick ? (
            <button onClick={it.onClick} style={{
              border: 'none', background: 'transparent', cursor: 'pointer',
              padding: '2px 4px', borderRadius: R.sm,
              fontFamily: FONT.sans, fontSize: FS.sm,
              color: C.textSecondary, fontWeight: 600,
            }}>{it.label}</button>
          ) : (
            <span style={{
              padding: '2px 4px',
              color: C.textHeading, fontWeight: 600,
            }}>{it.label}</span>
          )}
        </span>
      ))}
    </div>
  );
}

// === Экран «Суть» ===
function Essence({ map, attentionBlocks, boundaryBlocks, onOpenBlock }: {
  map: PlanMap;
  attentionBlocks: PlanMapBlock[];
  boundaryBlocks: PlanMapBlock[];
  onOpenBlock: (id: string) => void;
}) {
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.md }}>
      {/* Жанр + суть */}
      <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xs }}>
        <span style={{
          alignSelf: 'flex-start',
          padding: '3px 9px', borderRadius: R.pill,
          background: C.bgInset, color: C.textSecondary,
          fontFamily: FONT.sans, fontSize: FS.xs, fontWeight: 700,
          textTransform: 'lowercase',
        }}>
          {GENRE_LABEL[map.genre] || map.genre}
        </span>
        <div style={{
          fontFamily: FONT.serif, fontSize: FS.lg, fontWeight: 700,
          color: C.textHeading, lineHeight: 1.3,
        }}>{map.oneLine}</div>
      </div>

      {/* Числа: «3 шага · 2 файла затрагивается» */}
      {map.numbers.length > 0 && (
        <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.xs }}>
          {map.numbers.map((n, i) => (
            <span key={i} style={{
              display: 'inline-flex', alignItems: 'baseline', gap: 4,
              padding: '4px 10px', borderRadius: R.pill,
              border: `1px solid ${C.border}`, background: C.bgWhite,
              fontFamily: FONT.sans, fontSize: FS.sm,
            }}>
              <strong style={{ color: C.textHeading, fontWeight: 700 }}>{n.value}</strong>
              <span style={{ color: C.textSecondary }}>{n.label}</span>
            </span>
          ))}
        </div>
      )}

      {/* Блок внимания — только если есть пункты с живым якорем */}
      {attentionBlocks.length > 0 && (
        <div style={{
          border: `1px solid ${C.border}`,
          background: C.warningBg, borderRadius: R.lg,
          padding: `${SP.sm}px ${SP.md}px`,
          display: 'flex', flexDirection: 'column', gap: SP.sm,
        }}>
          <div style={{ display: 'flex', alignItems: 'center', gap: 6, fontFamily: FONT.sans, fontSize: FS.sm, color: C.textHeading, fontWeight: 700 }}>
            <AlertTriangle size={14} style={{ color: 'var(--c-warning)', flexShrink: 0 }} />
            Требует вашего внимания · {attentionBlocks.length}
          </div>
          <div style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
            {attentionBlocks.map(b => (
              <button key={b.id} onClick={() => onOpenBlock(b.id)} style={{
                display: 'flex', alignItems: 'center', gap: SP.sm,
                padding: '8px 10px', borderRadius: R.md,
                background: C.bgWhite, border: `1px solid ${C.border}`,
                cursor: 'pointer', fontFamily: FONT.sans,
                textAlign: 'left', width: '100%',
              }}>
                <span style={{ flex: 1, minWidth: 0, fontSize: FS.base, color: C.textHeading, fontWeight: 600 }}>
                  {b.title || b.anchor}
                </span>
                <div style={{ display: 'flex', flexWrap: 'wrap', gap: 4, justifyContent: 'flex-end' }}>
                  {b.flags.map(f => (
                    <span key={f} style={{
                      padding: '2px 7px', borderRadius: R.pill,
                      background: C.bgInset, color: C.textSecondary,
                      fontSize: FS.xs, fontWeight: 600, whiteSpace: 'nowrap',
                    }}>{FLAG_LABEL[f] || f}</span>
                  ))}
                </div>
                <ArrowRight size={14} style={{ color: C.textMuted, flexShrink: 0 }} />
              </button>
            ))}
          </div>
        </div>
      )}

      {/* Границы плана: «Чего этот план не делает» */}
      {boundaryBlocks.length > 0 && (
        <div style={{
          border: `1px solid ${C.border}`,
          background: C.bgWhite, borderRadius: R.lg,
          padding: `${SP.sm}px ${SP.md}px`,
          display: 'flex', flexDirection: 'column', gap: SP.sm,
        }}>
          <div style={{ display: 'flex', alignItems: 'center', gap: 6, fontFamily: FONT.sans, fontSize: FS.sm, color: C.textHeading, fontWeight: 700 }}>
            <ShieldOff size={14} style={{ color: C.textMuted, flexShrink: 0 }} />
            Чего этот план не делает
          </div>
          <div style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
            {boundaryBlocks.map(b => (
              <button key={b.id} onClick={() => onOpenBlock(b.id)} style={{
                display: 'flex', alignItems: 'center', gap: SP.sm,
                padding: '6px 10px', borderRadius: R.md,
                background: C.bgMain, border: `1px solid ${C.border}`,
                cursor: 'pointer', fontFamily: FONT.sans,
                textAlign: 'left', width: '100%',
              }}>
                <span style={{ flex: 1, minWidth: 0, fontSize: FS.sm, color: C.textSecondary }}>
                  {b.title || b.anchor}
                </span>
                <ArrowRight size={12} style={{ color: C.textMuted, flexShrink: 0 }} />
              </button>
            ))}
          </div>
        </div>
      )}
    </div>
  );
}

// === Экран «Карта» ===
// Блоки по строке с зависимостями: если у блока есть dependsOn — слева рендерится
// тонкая метка «после: …». Без графа: волна 1 — линейная схема, специальные формы
// под жанры (схема отказа для fix, матрица причин для audit) — следующая волна.
function MapView({ blocks, resolved, onOpenBlock }: {
  blocks: PlanMapBlock[];
  resolved: Map<string, Heading>;
  onOpenBlock: (id: string) => void;
}) {
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
      {blocks.map((b, i) => {
        const prev = i > 0 ? blocks[i - 1] : null;
        const dependsOnPrevious = prev && b.dependsOn.includes(prev.id);
        const hasLiveAnchor = resolved.has(b.id);
        return (
          <div key={b.id} style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
            {dependsOnPrevious && (
              <div style={{
                width: 2, height: 14, marginLeft: 18,
                background: C.border,
              }} />
            )}
            <button
              onClick={hasLiveAnchor ? () => onOpenBlock(b.id) : undefined}
              disabled={!hasLiveAnchor}
              style={{
                display: 'flex', alignItems: 'flex-start', gap: SP.sm,
                padding: '10px 12px', borderRadius: R.lg,
                background: C.bgWhite, border: `1px solid ${C.border}`,
                cursor: hasLiveAnchor ? 'pointer' : 'default',
                fontFamily: FONT.sans, textAlign: 'left', width: '100%',
                opacity: hasLiveAnchor ? 1 : 0.55,
              }}
            >
              <span style={{
                flexShrink: 0, width: 22, height: 22, borderRadius: R.full,
                background: C.bgInset, color: C.textSecondary,
                display: 'flex', alignItems: 'center', justifyContent: 'center',
                fontSize: FS.xs, fontWeight: 700,
              }}>{i + 1}</span>
              <div style={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', gap: 2 }}>
                <div style={{ display: 'flex', alignItems: 'center', gap: 6, flexWrap: 'wrap' }}>
                  <span style={{
                    fontSize: FS.md, fontWeight: 700, color: C.textHeading,
                  }}>{b.title || b.anchor}</span>
                  <span style={{
                    padding: '1px 7px', borderRadius: R.pill,
                    background: C.bgInset, color: C.textMuted,
                    fontSize: FS.xs, fontWeight: 600, whiteSpace: 'nowrap',
                  }}>{TYPE_LABEL[b.type] || b.type}</span>
                  {b.flags.length > 0 && (
                    <span style={{
                      padding: '1px 7px', borderRadius: R.pill,
                      background: 'var(--c-warning-bg)', color: 'var(--c-warning-text)',
                      fontSize: FS.xs, fontWeight: 600, whiteSpace: 'nowrap',
                    }}>{b.flags.length} {b.flags.length === 1 ? 'флаг' : 'флагов'}</span>
                  )}
                </div>
                <div style={{
                  fontSize: FS.sm, color: C.textSecondary,
                  overflow: 'hidden', display: '-webkit-box',
                  WebkitLineClamp: 1, WebkitBoxOrient: 'vertical',
                }}>{b.anchor}</div>
              </div>
              {hasLiveAnchor && <ArrowRight size={14} style={{ color: C.textMuted, flexShrink: 0, alignSelf: 'center' }} />}
            </button>
          </div>
        );
      })}
    </div>
  );
}

// === Экран «Блок» ===
function BlockView({ block, heading, section, headings, onBackToEssence, onBackToMap }: {
  block: PlanMapBlock;
  heading: Heading | null;
  section: string | null;
  headings: Heading[];
  onBackToEssence: () => void;
  onBackToMap: () => void;
}) {
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.md }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.xs }}>
        <button onClick={onBackToEssence} title="К сути" style={{
          display: 'inline-flex', alignItems: 'center', gap: 4,
          border: 'none', background: 'transparent', cursor: 'pointer',
          fontFamily: FONT.sans, fontSize: FS.sm, color: C.textSecondary, fontWeight: 600,
          padding: '2px 4px',
        }}>
          <ArrowLeft size={12} /> к сути
        </button>
        <span style={{ color: C.textMuted, fontFamily: FONT.sans, fontSize: FS.xs }}>·</span>
        <button onClick={onBackToMap} title="К карте" style={{
          display: 'inline-flex', alignItems: 'center', gap: 4,
          border: 'none', background: 'transparent', cursor: 'pointer',
          fontFamily: FONT.sans, fontSize: FS.sm, color: C.textSecondary, fontWeight: 600,
          padding: '2px 4px',
        }}>
          к карте
        </button>
      </div>

      <div style={{
        background: C.bgWhite, border: `1px solid ${C.border}`,
        borderRadius: R.lg, padding: `${SP.md}px`,
        display: 'flex', flexDirection: 'column', gap: SP.sm,
      }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 6, flexWrap: 'wrap' }}>
          <span style={{
            padding: '2px 8px', borderRadius: R.pill,
            background: C.bgInset, color: C.textSecondary,
            fontFamily: FONT.sans, fontSize: FS.xs, fontWeight: 600,
            textTransform: 'lowercase',
          }}>{TYPE_LABEL[block.type] || block.type}</span>
          {block.flags.map(f => (
            <span key={f} style={{
              padding: '2px 8px', borderRadius: R.pill,
              background: 'var(--c-warning-bg)', color: 'var(--c-warning-text)',
              fontFamily: FONT.sans, fontSize: FS.xs, fontWeight: 600,
              whiteSpace: 'nowrap',
            }}>{FLAG_LABEL[f] || f}</span>
          ))}
        </div>
        <div style={{
          fontFamily: FONT.serif, fontSize: FS.lg, fontWeight: 700,
          color: C.textHeading, lineHeight: 1.3,
        }}>{block.title || block.anchor}</div>
        {heading ? (
          <div style={{ fontSize: FS.sm, color: C.textMuted, fontFamily: FONT.sans }}>
            Раздел плана: <strong style={{ color: C.textSecondary, fontWeight: 600 }}>{block.anchor}</strong>
            {/* Подпись (N-й) у всех одноимённых, если заголовок в плане повторяется —
                то же правило, что в buildPlanFeedback: иначе у замечания из схемы и
                у того же замечания в тексте обратной связи получатся разные адреса. */}
            {headingHasDuplicates(heading.text, headings) && ` (${heading.occurrence + 1}-й)`}
          </div>
        ) : (
          <div style={{ fontSize: FS.sm, color: C.dangerText, fontFamily: FONT.sans }}>
            Раздел плана не найден — текст ниже от ближайшего совпадения по тексту заголовка.
          </div>
        )}
      </div>

      {section !== null ? (
        <div style={{
          background: C.bgWhite, border: `1px solid ${C.border}`,
          borderRadius: R.lg, padding: `${SP.md}px`,
          fontSize: FS.base, color: C.textHeading,
        }}>
          <MarkdownContent text={section || '_(пустой раздел)_'} />
        </div>
      ) : (
        <div style={{
          background: C.bgInset, border: `1px dashed ${C.border}`,
          borderRadius: R.lg, padding: `${SP.md}px`,
          fontSize: FS.sm, color: C.textMuted, fontFamily: FONT.sans,
          textAlign: 'center',
        }}>
          Исходный текст раздела недоступен.
        </div>
      )}
    </div>
  );
}