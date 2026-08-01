import { useEffect, useState } from 'react';
import { ChevronRight, ChevronDown, Check, Copy, EyeOff, Sparkles, Snowflake, RefreshCw } from 'lucide-react';
import { Modal } from '../../components/ui/Modal';
import { Button } from '../../components/ui/Button';
import { SegmentedControl } from '../../components/ui/Segmented';
import { api } from '../../lib/api';
import { C, FS, SP, R, FONT } from '../../lib/design';
import type { PromptSnapshot, PromptSection, CliSkill } from '../../types';

// Шторка «какой промпт ушёл»: посекционно то, что CCS собрал и передал claude CLI на этом
// ходу, плюс доступная часть слоя самого CLI и разбор «что лишнее» по кнопке.
// Текст грузится по REST — по SignalR ходит только id снимка.

interface Props {
  sessionId: string;
  snapshotId: string;
  // Размер контекста последнего запроса хода (result.contextTokens) — для сравнения
  // «наши секции против всего, что реально ушло». Считает ChatPanel по ленте.
  // null — ход не дошёл до ответа модели (например, упал на аутентификации)
  contextTokens?: number | null;
  // Факт по кэшу промптов из usage хода: сколько токенов взято из кэша и сколько
  // в него записано. Единственные точные числа про кэш, что у нас есть
  turnCache?: { read: number; creation: number } | null;
  onClose: () => void;
}

// Заголовок строки-раздела: серая подпись над блоком
function SectionLabel({ children }: { children: React.ReactNode }) {
  return (
    <div style={{ fontSize: FS.xs, color: C.textMuted, margin: `${SP.md}px 0 ${SP.sm}px` }}>
      {children}
    </div>
  );
}

// Доли рисуем ОДНИМ цветом: разноцветица читается как «категории разного смысла»,
// а здесь смысл один — вес куска. Отличаются только насыщенностью, по убыванию веса;
// связь «сегмент ↔ строка» даёт наведение, а не цвет.
const shareOpacity = (i: number, count: number) =>
  count <= 1 ? 1 : 1 - (i / (count - 1)) * 0.6;

// Оценка веса в токенах. Точное число знает только модель, у нас есть лишь символы:
// для смеси русского и английского ~3 символа на токен. Показываем как «≈» и только
// рядом с точными числами из usage — чтобы прикидка не выдавала себя за факт.
const approxTokens = (chars: number) => Math.round(chars / 3);

// Сворачивающийся блок с произвольным содержимым — для того, что нужно редко
// (аргументы запуска CLI: длинный список путей и флагов, в глаза лезть не должен)
function Collapsible({ title, hint, children }: {
  title: string; hint?: string; children: React.ReactNode;
}) {
  const [open, setOpen] = useState(false);
  const Icon = open ? ChevronDown : ChevronRight;
  return (
    <div style={{ border: `1px solid ${C.border}`, borderRadius: R.md, overflow: 'hidden' }}>
      <button
        onClick={() => setOpen(o => !o)}
        style={{
          display: 'flex', alignItems: 'center', gap: SP.sm, width: '100%',
          padding: `9px ${SP.md}px`, background: 'none', border: 'none', cursor: 'pointer',
          fontFamily: FONT.sans, fontSize: FS.base, color: C.textPrimary, textAlign: 'left',
        }}>
        <Icon size={15} color={C.textMuted} style={{ flexShrink: 0 }} />
        <span style={{ flex: 1, minWidth: 0 }}>{title}</span>
        {hint && (
          <span style={{ color: C.textMuted, fontSize: FS.sm, whiteSpace: 'nowrap' }}>{hint}</span>
        )}
      </button>
      {open && <div style={{ padding: `0 ${SP.md}px ${SP.md}px` }}>{children}</div>}
    </div>
  );
}

// Инструменты хода, разложенные по владельцам. Именно они — главный едок контекста:
// CLI отдаёт модели ОПИСАНИЕ каждого инструмента, а нам наружу — только имена, поэтому
// вес показываем числом штук, а не токенами (врать точной цифрой нельзя).
function ToolsRow({ tools, servers }: {
  tools: string[]; servers?: { name: string; status: string }[];
}) {
  const [open, setOpen] = useState(false);
  const Icon = open ? ChevronDown : ChevronRight;

  // mcp__tasks__tasks_create → «tasks»; всё остальное — встроенные инструменты CLI
  const groups = new Map<string, string[]>();
  for (const t of tools) {
    const m = /^mcp__([^_]+(?:_[^_]+)*?)__/.exec(t);
    const owner = m ? m[1] : 'встроенные CLI';
    (groups.get(owner) ?? groups.set(owner, []).get(owner)!).push(t);
  }
  const sorted = [...groups.entries()].sort((a, b) => b[1].length - a[1].length);

  return (
    <div style={{ borderBottom: `1px solid ${C.borderLight}` }}>
      <button onClick={() => setOpen(o => !o)}
        style={{
          display: 'flex', alignItems: 'center', gap: SP.sm, width: '100%',
          padding: `9px ${SP.md}px`, background: 'none', border: 'none', cursor: 'pointer',
          fontFamily: FONT.sans, fontSize: FS.base, color: C.textPrimary, textAlign: 'left',
        }}>
        <Icon size={15} color={C.textMuted} style={{ flexShrink: 0 }} />
        <span style={{ flex: 1, minWidth: 0 }}>Инструменты модели</span>
        <span style={{ color: C.textMuted, fontSize: FS.sm, fontVariantNumeric: 'tabular-nums' }}>
          {tools.length}
        </span>
      </button>
      {open && (
        <div style={{ padding: `0 ${SP.md}px ${SP.md}px 34px` }}>
          {sorted.map(([owner, list]) => (
            <div key={owner} style={{ marginBottom: SP.sm }}>
              <div style={{
                display: 'flex', alignItems: 'baseline', gap: SP.sm,
                fontSize: FS.sm, color: C.textPrimary, marginBottom: SP.xxs,
              }}>
                <span style={{ flex: 1, minWidth: 0 }}>
                  {owner}
                  {servers?.some(s => s.name === owner && s.status !== 'connected') && (
                    <span style={{ color: C.warningText }}> · {
                      servers.find(s => s.name === owner)?.status}</span>
                  )}
                </span>
                <span style={{ color: C.textMuted, fontVariantNumeric: 'tabular-nums' }}>
                  {list.length} шт. · {Math.round(list.length * 100 / tools.length)}%
                </span>
              </div>
              <div style={{
                fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted, lineHeight: 1.6,
                wordBreak: 'break-all',
              }}>
                {list.map(t => t.replace(/^mcp__[^_]+(?:_[^_]+)*?__/, '')).join(', ')}
              </div>
            </div>
          ))}
          <div style={{ fontSize: FS.xs, color: C.textMuted, lineHeight: 1.5 }}>
            Каждый инструмент уходит модели вместе с описанием — их текст CLI наружу
            не отдаёт, поэтому вес показан числом, а не токенами.
          </div>
        </div>
      )}
    </div>
  );
}

// Каталог скиллов: имя + описание (их CLI кладёт модели списком)
function SkillsRow({ skills }: { skills: CliSkill[] }) {
  const [open, setOpen] = useState(false);
  const Icon = open ? ChevronDown : ChevronRight;
  return (
    <div style={{ borderBottom: `1px solid ${C.borderLight}` }}>
      <button onClick={() => setOpen(o => !o)}
        style={{
          display: 'flex', alignItems: 'center', gap: SP.sm, width: '100%',
          padding: `9px ${SP.md}px`, background: 'none', border: 'none', cursor: 'pointer',
          fontFamily: FONT.sans, fontSize: FS.base, color: C.textPrimary, textAlign: 'left',
        }}>
        <Icon size={15} color={C.textMuted} style={{ flexShrink: 0 }} />
        <span style={{ flex: 1, minWidth: 0 }}>Скиллы в каталоге</span>
        <span style={{ color: C.textMuted, fontSize: FS.sm, fontVariantNumeric: 'tabular-nums' }}>
          {skills.length}
        </span>
      </button>
      {open && (
        <div style={{ padding: `0 ${SP.md}px ${SP.md}px 34px` }}>
          {skills.map(s => (
            <div key={`${s.source}:${s.name}`} style={{ marginBottom: SP.xs, fontSize: FS.sm }}>
              <span style={{ color: C.textPrimary }}>{s.name}</span>
              {s.description && (
                <span style={{ color: C.textMuted }}>
                  {' — '}
                  {s.description.length > 120 ? s.description.slice(0, 120) + '…' : s.description}
                </span>
              )}
            </div>
          ))}
        </div>
      )}
    </div>
  );
}

// Строка «метка — значение» без раскрытия: то, у чего нет текста (счётчики слоя CLI)
function MetaRow({ label, value }: { label: string; value: string }) {
  return (
    <div style={{
      display: 'flex', alignItems: 'center', gap: SP.sm, padding: `9px ${SP.md}px`,
      borderBottom: `1px solid ${C.borderLight}`, fontSize: FS.base, color: C.textPrimary,
    }}>
      <span style={{ flex: 1, minWidth: 0 }}>{label}</span>
      <span style={{
        color: C.textMuted, fontSize: FS.sm, whiteSpace: 'nowrap',
        fontVariantNumeric: 'tabular-nums',
      }}>
        {value}
      </span>
    </div>
  );
}

// Полоса заполнения: сегменты по долям, как в индикаторе окна контекста.
// hovered — ключ подсвеченной строки: сегмент и строка подсвечиваются вместе,
// поэтому и без цветовой кодировки видно, где что.
function ShareBar({ parts, hovered, onHover }: {
  parts: { key: string; size: number; opacity: number }[];
  hovered?: string | null;
  onHover?: (key: string | null) => void;
}) {
  const total = Math.max(1, parts.reduce((s, p) => s + p.size, 0));
  return (
    <div style={{
      display: 'flex', height: 10, borderRadius: R.sm, overflow: 'hidden',
      background: C.track, gap: 1,
    }}>
      {parts.filter(p => p.size > 0).map(p => (
        <div key={p.key}
          onMouseEnter={() => onHover?.(p.key)}
          onMouseLeave={() => onHover?.(null)}
          title={p.key}
          style={{
            width: `${p.size * 100 / total}%`,
            background: hovered && hovered !== p.key ? C.border : C.accent,
            opacity: hovered === p.key ? 1 : p.opacity,
            cursor: onHover ? 'pointer' : 'default',
            transition: 'background 120ms, opacity 120ms',
          }} />
      ))}
    </div>
  );
}

// Раскрывающаяся секция промпта: заголовок, размер, доля, текст.
// Наведение связано с полосой долей через общий hovered-ключ.
// loadText — ленивый догруз (файлы слоя CLI приходят без текста, только с размером).
function SectionRow({ section, share, hovered, onHover, loadText }: {
  section: PromptSection; share?: number;
  hovered?: string | null;
  onHover?: (key: string | null) => void;
  loadText?: (key: string) => Promise<string>;
}) {
  const [open, setOpen] = useState(false);
  const [lazyText, setLazyText] = useState<string | null>(null);
  const [loadFailed, setLoadFailed] = useState(false);
  const Icon = open ? ChevronDown : ChevronRight;
  const isHot = hovered === section.key;
  const size = section.text.length || section.size || 0;
  const text = section.text || lazyText;

  const toggle = () => {
    const next = !open;
    setOpen(next);
    // Текст тянем один раз и только когда строку реально раскрыли
    if (next && !section.text && lazyText === null && loadText) {
      loadText(section.key)
        .then(setLazyText)
        .catch(() => setLoadFailed(true));
    }
  };
  return (
    <div style={{ borderBottom: `1px solid ${C.borderLight}` }}
      onMouseEnter={() => onHover?.(section.key)}
      onMouseLeave={() => onHover?.(null)}>
      <button
        onClick={toggle}
        style={{
          display: 'flex', alignItems: 'center', gap: SP.sm, width: '100%',
          padding: `9px ${SP.md}px`, border: 'none', cursor: 'pointer',
          background: isHot ? C.bgSelected : 'none',
          transition: 'background 120ms',
          fontFamily: FONT.sans, fontSize: FS.base, color: C.textPrimary, textAlign: 'left',
        }}>
        <Icon size={15} color={C.textMuted} style={{ flexShrink: 0 }} />
        <span style={{ flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis' }}>
          {section.title}
        </span>
        {/* Кэшируемость — иконкой у КАЖДОЙ строки, а не только у изменчивых: иначе
            в чате без recall и персоны непонятно, есть ли признак вообще.
            Снежинка — кусок стабилен и живёт в кэшируемом префиксе; стрелки — пересчитан
            под этот ход и ломает кэш с себя и дальше. Что попало в кэш фактически,
            знает только API (точные цифры — в шапке, из usage) */}
        <span style={{ display: 'inline-flex', flexShrink: 0 }}
          title={section.stable === false
            ? 'Пересчитывается под текст хода — кэш префикса с этого места не переиспользуется'
            : 'Одинакова от хода к ходу — попадает в кэшируемый префикс'}>
          {section.stable === false
            ? <RefreshCw size={12} color={C.warningText} />
            : <Snowflake size={12} color={C.textMuted} />}
        </span>
        {/* Персона размазана по пяти секциям — метим их, чтобы её цена была видна */}
        {section.group === 'persona' && (
          <span title="Часть слоя персоны"
            style={{
              flexShrink: 0, fontSize: FS.xs, color: C.planText, background: C.planLight,
              borderRadius: R.sm, padding: '1px 6px', whiteSpace: 'nowrap',
            }}>
            персона
          </span>
        )}
        <span style={{
          color: C.textMuted, fontSize: FS.sm, whiteSpace: 'nowrap',
          fontVariantNumeric: 'tabular-nums',
        }}>
          {size.toLocaleString('ru')}
          {share !== undefined && ` · ${share}%`}
        </span>
      </button>
      {open && (
        <pre style={{
          margin: 0, padding: `0 ${SP.md}px ${SP.md}px 34px`, maxHeight: 280, overflow: 'auto',
          fontFamily: FONT.mono, fontSize: FS.sm, lineHeight: 1.5, color: C.textSecondary,
          whiteSpace: 'pre-wrap', wordBreak: 'break-word',
        }}>
          {text ?? (loadFailed ? 'Не удалось загрузить текст' : 'Загружаю…')}
        </pre>
      )}
    </div>
  );
}

export function PromptSnapshotDialog({ sessionId, snapshotId, contextTokens, turnCache, onClose }: Props) {
  const [snapshot, setSnapshot] = useState<PromptSnapshot | null>(null);
  // 'loading' | 'ready' | 'gone' (снимок вытеснен ретеншном) | 'error'
  const [state, setState] = useState<'loading' | 'ready' | 'gone' | 'error'>('loading');
  const [copied, setCopied] = useState(false);
  const [includeText, setIncludeText] = useState(false);
  const [analysis, setAnalysis] = useState<string | null>(null);
  const [analyzing, setAnalyzing] = useState(false);
  const [analysisError, setAnalysisError] = useState<string | null>(null);
  // Открыть снимок старта прогона вместо унаследованного (кнопка на плашке)
  const [shownId, setShownId] = useState(snapshotId);
  // Ключ секции под курсором: связывает сегмент полосы со строкой списка
  const [hovered, setHovered] = useState<string | null>(null);
  // Вид: 'source' — по источнику (как это устроено), 'all' — единый список всех кусков
  // по весу (на что уходит контекст). Разные вопросы — разные разрезы
  const [view, setView] = useState<'source' | 'all'>('source');

  useEffect(() => {
    let alive = true;
    setState('loading');
    setAnalysis(null);
    api.sessions.promptSnapshot(sessionId, shownId)
      .then(s => { if (alive) { setSnapshot(s); setState('ready'); } })
      .catch((e: unknown) => {
        if (!alive) return;
        // 404 — штатный случай: снимок вытеснен окном последних 50 ходов чата
        const gone = e instanceof Error && e.message.includes('404');
        setState(gone ? 'gone' : 'error');
      });
    return () => { alive = false; };
  }, [sessionId, shownId]);

  const systemSections = snapshot?.sections.filter(s => s.kind === 'system') ?? [];
  const turnSection = snapshot?.sections.find(s => s.kind === 'turn');
  const totalChars = systemSections.reduce((sum, s) => sum + s.text.length, 0);
  // Ровно то, что ушло в --append-system-prompt: текст хода сюда не входит.
  // Порядок — ИСХОДНЫЙ, как в промпте: копируем то, что реально видела модель
  const fullPrompt = systemSections.map(s => s.text).join('\n\n');
  // А показываем от самых жирных: вопрос «на что уходит контекст» важнее порядка склейки
  const bySize = [...systemSections].sort((a, b) => b.text.length - a.text.length);

  // Вид «всё вместе»: один список без деления на источники — системные секции, текст хода
  // и файлы слоя CLI в общем зачёте. Отвечает на вопрос «кто съел контекст», тогда как
  // вид «по источнику» отвечает на «как это устроено и что чем отправлено»
  const sizeOf = (s: PromptSection) => s.text.length || s.size || 0;
  const allParts = [
    ...systemSections,
    ...(turnSection ? [turnSection] : []),
    ...(snapshot?.cliLayer?.files ?? []),
  ].sort((a, b) => sizeOf(b) - sizeOf(a));
  const allTotal = allParts.reduce((sum, s) => sum + sizeOf(s), 0);

  // Во сколько обходятся подсистемы. Персона отдельным вопросом: её слой размазан по
  // пяти секциям (контракт, память, привязки, упоминания, подсказка про инструменты),
  // и по одной строке цену не понять
  const GROUP_TITLES: Record<string, string> = {
    persona: 'Персона',
    mcp: 'Подсказки MCP',
    project: 'Проект',
    recall: 'Recall заметок',
    turn: 'Текст хода',
    cli: 'Файлы CLI',
    misc: 'Прочее',
  };
  const byGroup = [...allParts.reduce((acc, s) => {
    const g = s.kind === 'cli-file' ? 'cli' : (s.group ?? 'misc');
    acc.set(g, (acc.get(g) ?? 0) + sizeOf(s));
    return acc;
  }, new Map<string, number>())].sort((a, b) => b[1] - a[1]);
  const personaChars = byGroup.find(([g]) => g === 'persona')?.[1] ?? 0;

  const copyAll = () => {
    navigator.clipboard?.writeText(fullPrompt)
      .then(() => { setCopied(true); setTimeout(() => setCopied(false), 1500); })
      .catch(() => {});
  };

  // Догруз текста файла слоя CLI — только когда его строку раскрыли (см. SectionRow)
  const loadFileText = (key: string) =>
    api.sessions.promptSnapshotFile(sessionId, shownId, key).then(f => f.text);

  const analyze = () => {
    if (analyzing) return;
    setAnalyzing(true);
    setAnalysisError(null);
    api.sessions.analyzePrompt(sessionId, shownId, includeText)
      .then(r => setAnalysis(r.analysis))
      .catch(() => setAnalysisError('Не удалось разобрать'))
      .finally(() => setAnalyzing(false));
  };

  const cli = snapshot?.cliLayer;

  return (
    <Modal width={620} title="Что ушло модели на этом ходу" onClose={onClose}
      subtitle={snapshot ? [snapshot.model, snapshot.mode].filter(Boolean).join(' · ') : undefined}>

      {state === 'loading' && (
        <div style={{ padding: SP.lg, color: C.textMuted, fontSize: FS.base }}>Загружаю…</div>
      )}

      {state === 'gone' && (
        <div style={{ padding: SP.lg, color: C.textSecondary, fontSize: FS.base, lineHeight: 1.6 }}>
          Снимок вытеснен: хранятся последние 50 ходов чата.
        </div>
      )}

      {state === 'error' && (
        <div style={{ padding: SP.lg, color: C.dangerText, fontSize: FS.base }}>
          Не удалось загрузить снимок промпта.
        </div>
      )}

      {state === 'ready' && snapshot && (
        <div>
          {/* Применён ли собранный промпт к этому ходу — главное, в чём UI не имеет права врать */}
          <div style={{
            display: 'flex', alignItems: 'flex-start', gap: SP.sm, padding: `9px ${SP.md}px`,
            borderRadius: R.md, fontSize: FS.base, lineHeight: 1.5,
            background: snapshot.applied ? C.successBg : C.bgInset,
            color: snapshot.applied ? C.successText : C.textSecondary,
          }}>
            {snapshot.applied
              ? <Check size={16} style={{ flexShrink: 0, marginTop: 1 }} />
              : <EyeOff size={16} style={{ flexShrink: 0, marginTop: 1 }} />}
            <span>
              {snapshot.applied
                ? 'Применён этим ходом — процесс стартовал с этим промптом.'
                : 'Унаследован: ход доигрывался в живом процессе, и этот промпт модели не уходил. '}
              {!snapshot.applied && (snapshot.inheritedFromId
                ? (
                  <button onClick={() => setShownId(snapshot.inheritedFromId!)}
                    style={{
                      background: 'none', border: 'none', padding: 0, cursor: 'pointer',
                      color: C.accent, fontSize: FS.base, fontFamily: 'inherit',
                    }}>
                    Открыть действующий снимок
                  </button>
                )
                : 'Снимок старта прогона недоступен.')}
            </span>
          </div>

          {/* Окно контекста хода: сколько в нём занял наш промпт, а сколько — невидимый
              слой CLI и история. Наша часть — прикидка по символам, поэтому «≈» */}
          {typeof contextTokens === 'number' && contextTokens > 0 && (
            <div style={{ marginTop: SP.md }}>
              <div style={{
                display: 'flex', alignItems: 'baseline', justifyContent: 'space-between',
                marginBottom: SP.xs, fontSize: FS.base, color: C.textPrimary,
              }}>
                <span>Контекст запроса</span>
                <span style={{ color: C.textMuted, fontVariantNumeric: 'tabular-nums' }}>
                  {contextTokens.toLocaleString('ru')} токенов
                </span>
              </div>
              <ShareBar parts={[
                { key: 'ccs', size: Math.min(approxTokens(totalChars), contextTokens), opacity: 1 },
                { key: 'cli', size: Math.max(0, contextTokens - approxTokens(totalChars)), opacity: 0.18 },
              ]} />
              <div style={{
                display: 'flex', gap: SP.md, marginTop: SP.xs,
                fontSize: FS.xs, color: C.textMuted,
              }}>
                <span>
                  <span style={{ ...dotStyle, background: C.accent }} />
                  промпт CCS ≈ {approxTokens(totalChars).toLocaleString('ru')} (
                  {Math.min(100, Math.round(approxTokens(totalChars) * 100 / contextTokens))}%)
                </span>
                <span>
                  <span style={{ ...dotStyle, background: C.border }} />
                  слой CLI и история — остальное
                </span>
              </div>
              {/* Единственные точные числа про кэш, что у нас есть: сам факт из usage хода */}
              {turnCache && (turnCache.read > 0 || turnCache.creation > 0) && (
                <div style={{ marginTop: SP.xs, fontSize: FS.xs, color: C.textMuted }}>
                  Кэш промптов: взято {turnCache.read.toLocaleString('ru')}, записано{' '}
                  {turnCache.creation.toLocaleString('ru')} токенов. Кэш экономит деньги,
                  но место в окне контекста занимает всё равно.
                </div>
              )}
            </div>
          )}

          {/* Два разреза одних и тех же данных: «как устроено» и «кто съел контекст» */}
          <div style={{ marginTop: SP.md }}>
            <SegmentedControl value={view} onChange={setView} options={[
              { value: 'source', label: 'По источнику' },
              { value: 'all', label: 'Всё вместе' },
            ]} />
          </div>

          {view === 'all' && (
            <>
              <SectionLabel>По подсистемам</SectionLabel>
              <div style={{ border: `1px solid ${C.border}`, borderRadius: R.md, overflow: 'hidden' }}>
                {byGroup.map(([g, chars]) => (
                  <MetaRow key={g} label={GROUP_TITLES[g] ?? g}
                    value={`${chars.toLocaleString('ru')} · ${
                      allTotal > 0 ? Math.round(chars * 100 / allTotal) : 0}%`} />
                ))}
              </div>
              {personaChars > 0 && (
                <div style={{ marginTop: SP.sm, fontSize: FS.sm, color: C.textSecondary, lineHeight: 1.5 }}>
                  Персона стоит {personaChars.toLocaleString('ru')} символов
                  {' '}(≈ {approxTokens(personaChars).toLocaleString('ru')} токенов) на каждом ходу —
                  контракт, память, привязки и подсказки про её инструменты вместе.
                </div>
              )}

              <SectionLabel>
                Все куски по весу · {allTotal.toLocaleString('ru')} символов
              </SectionLabel>
              <div style={{ marginBottom: SP.sm }}>
                <ShareBar hovered={hovered} onHover={setHovered}
                  parts={allParts.map((s, i) => ({
                    key: s.key, size: sizeOf(s), opacity: shareOpacity(i, allParts.length),
                  }))} />
              </div>
              <div style={{ border: `1px solid ${C.border}`, borderRadius: R.md, overflow: 'hidden' }}>
                {allParts.map(s => (
                  <SectionRow key={s.key} section={s} hovered={hovered} onHover={setHovered}
                    loadText={s.kind === 'cli-file' ? loadFileText : undefined}
                    share={allTotal > 0 ? Math.round(sizeOf(s) * 100 / allTotal) : 0} />
                ))}
              </div>
              <div style={{ marginTop: SP.sm, fontSize: FS.xs, color: C.textMuted, lineHeight: 1.5 }}>
                Описания {cli?.tools?.length ?? 0} инструментов сюда не входят — их текст CLI
                наружу не отдаёт, хотя модели они уходят вместе со всем остальным.
              </div>
            </>
          )}

          {view === 'source' && (<>
          <SectionLabel>
            Системный промпт · {totalChars.toLocaleString('ru')} символов
            {' '}(≈ {approxTokens(totalChars).toLocaleString('ru')} токенов)
          </SectionLabel>
          <div style={{ marginBottom: SP.sm }}>
            <ShareBar hovered={hovered} onHover={setHovered}
              parts={bySize.map((s, i) => ({
                key: s.key, size: s.text.length, opacity: shareOpacity(i, bySize.length),
              }))} />
          </div>
          <div style={{ border: `1px solid ${C.border}`, borderRadius: R.md, overflow: 'hidden' }}>
            {bySize.map(s => (
              <SectionRow key={s.key} section={s} hovered={hovered} onHover={setHovered}
                share={totalChars > 0 ? Math.round(s.text.length * 100 / totalChars) : 0} />
            ))}
          </div>

          {turnSection && (
            <>
              <SectionLabel>Текст хода, ушедший CLI — это не системный промпт</SectionLabel>
              <div style={{ border: `1px solid ${C.border}`, borderRadius: R.md, overflow: 'hidden' }}>
                <SectionRow section={turnSection} />
              </div>
            </>
          )}

          <SectionLabel>Запуск CLI</SectionLabel>
          <Collapsible title="Аргументы запуска" hint={`${snapshot.cliArgs.length} шт.`}>
            <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.xs }}>
              {snapshot.mcpServers.length > 0 && (
                <span style={chipStyle}>mcp: {snapshot.mcpServers.join(', ')}</span>
              )}
              {snapshot.cliArgs.map((a, i) => <span key={i} style={chipStyle}>{a}</span>)}
            </div>
          </Collapsible>

          {/* Слой claude CLI: то, что он подмешивает поверх нашего промпта */}
          <SectionLabel>Слой claude CLI</SectionLabel>
          {/* Сервер отдаёт незаполненные поля как null, поэтому проверяем тип, а не
              !== undefined: у хода, упавшего до старта процесса, их попросту нет */}
          <div style={{ border: `1px solid ${C.border}`, borderRadius: R.md, overflow: 'hidden' }}>
            {cli?.tools && cli.tools.length > 0 && (
              <ToolsRow tools={cli.tools} servers={cli.mcpServers ?? undefined} />
            )}
            {cli?.skills && cli.skills.length > 0 && <SkillsRow skills={cli.skills} />}
            {typeof cli?.transcriptBytes === 'number' && (
              <MetaRow label="История разговора (--resume)"
                value={`${Math.round(cli.transcriptBytes / 1024).toLocaleString('ru')} КБ${
                  typeof cli.transcriptMessages === 'number' ? ` · ${cli.transcriptMessages} сообщ.` : ''}`} />
            )}
            {cli?.files?.map(f => (
              <SectionRow key={f.key} section={f} loadText={loadFileText} />
            ))}
          </div>

          <div style={{
            marginTop: SP.sm, padding: `${SP.sm}px ${SP.md}px`, borderRadius: R.md,
            background: C.warningBg, color: C.warningText, fontSize: FS.sm, lineHeight: 1.55,
          }}>
            Файлы CLAUDE.md — наша реконструкция: импорты раскрыты нами, цепочка родительских
            CLAUDE.md и импорты вида @~/… не собираются. Текст встроенного промпта Anthropic
            и описания инструментов CLI наружу не отдаёт вовсе.
          </div>
          </>)}

          {/* Разбор промпта моделью. По умолчанию наружу уходят только метаданные секций */}
          <SectionLabel>Разбор промпта</SectionLabel>
          <label style={{
            display: 'flex', alignItems: 'flex-start', gap: SP.sm, fontSize: FS.sm,
            color: C.textSecondary, lineHeight: 1.5, cursor: 'pointer', marginBottom: SP.sm,
          }}>
            <input type="checkbox" checked={includeText} style={{ marginTop: 2 }}
              onChange={e => setIncludeText(e.target.checked)} />
            <span>
              Приложить фрагменты текста секций. Без галочки исполнителю уходят только размеры
              и заголовки — recall заметок и память персоны машину не покидают.
            </span>
          </label>

          {analysis && (
            <div style={{
              padding: SP.md, borderRadius: R.md, background: C.bgInset,
              fontSize: FS.base, color: C.textPrimary, lineHeight: 1.6, whiteSpace: 'pre-wrap',
            }}>
              {analysis}
            </div>
          )}
          {analysisError && (
            <div style={{ color: C.dangerText, fontSize: FS.sm }}>{analysisError}</div>
          )}

          <div style={{ display: 'flex', justifyContent: 'flex-end', gap: SP.sm, marginTop: SP.lg }}>
            <Button variant="ghost" size="sm" onClick={analyze} loading={analyzing}
              leftIcon={<Sparkles size={14} />}>
              {analyzing ? 'Разбираю…' : 'Проанализировать'}
            </Button>
            <Button variant="ghost" size="sm" onClick={copyAll}
              leftIcon={copied ? <Check size={14} color={C.success} /> : <Copy size={14} />}>
              {copied ? 'Скопировано' : 'Скопировать промпт'}
            </Button>
          </div>
        </div>
      )}
    </Modal>
  );
}

// Точка-метка легенды под полосой
const dotStyle: React.CSSProperties = {
  display: 'inline-block', width: 8, height: 8, borderRadius: 2,
  marginRight: SP.xs, verticalAlign: 'baseline',
};

const chipStyle: React.CSSProperties = {
  fontFamily: FONT.mono, fontSize: FS.sm, background: C.bgInset,
  borderRadius: R.sm, padding: '3px 8px', color: C.textSecondary,
  wordBreak: 'break-all',
};
