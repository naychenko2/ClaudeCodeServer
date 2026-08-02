import { useEffect, useRef, useState } from 'react';
import { ChevronRight, ChevronDown, Check, EyeOff, Sparkles, Database, CircleDollarSign,
  UserRound } from 'lucide-react';
import { Modal } from '../../components/ui/Modal';
import { Button } from '../../components/ui/Button';
import { SegmentedControl } from '../../components/ui/Segmented';
import { MarkdownContent } from '../../components/chat/MarkdownContent';
import { api } from '../../lib/api';
import { useModelLabel } from '../../lib/models';
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
        <span style={{ flex: 1, minWidth: 0 }}>Инструменты, которые она может вызвать</span>
        {/* Состав от сообщения к сообщению не меняется — значит идёт из кэша */}
        <span style={MARKS_COL}><CacheMark stable /></span>
        <span style={SIZE_COL}>{tools.length} шт.</span>
      </button>
      {open && (
        <div style={{ padding: `0 ${SP.md}px ${SP.md}px 34px` }}>
          {/* Оговорка первой строкой: иначе непонятно, почему у самой жирной части
              нет веса, а есть только количество */}
          <div style={{ fontSize: FS.xs, color: C.textMuted, lineHeight: 1.5, marginBottom: SP.sm }}>
            Каждый инструмент модель получает вместе с описанием — это и есть главный
            расход места. Сами описания CLI не показывает, поэтому веса у них нет.
          </div>
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
        </div>
      )}
    </div>
  );
}

// Вес каталога навыков: имя + описание каждого. В отличие от инструментов, их текст
// у нас на руках, поэтому размер считаем, а не гадаем
const skillsChars = (skills: CliSkill[]) =>
  skills.reduce((sum, s) => sum + s.name.length + (s.description?.length ?? 0), 0);

// Каталог навыков: имя + описание (их CLI кладёт модели списком)
function SkillsRow({ skills, share, hovered, onHover, onLeave }: {
  skills: CliSkill[]; share?: number;
  hovered?: boolean; onHover?: () => void; onLeave?: () => void;
}) {
  const [open, setOpen] = useState(false);
  const Icon = open ? ChevronDown : ChevronRight;
  const chars = skillsChars(skills);
  return (
    <div style={{
      borderBottom: `1px solid ${C.borderLight}`,
      background: hovered ? C.bgSelected : 'transparent', transition: 'background 120ms',
    }} onMouseEnter={onHover} onMouseLeave={onLeave}>
      <button onClick={() => setOpen(o => !o)}
        style={{
          display: 'flex', alignItems: 'center', gap: SP.sm, width: '100%',
          padding: `9px ${SP.md}px`, background: 'none', border: 'none', cursor: 'pointer',
          fontFamily: FONT.sans, fontSize: FS.base, color: C.textPrimary, textAlign: 'left',
        }}>
        <Icon size={15} color={C.textMuted} style={{ flexShrink: 0 }} />
        <span style={{ flex: 1, minWidth: 0 }}>Навыки, о которых она знает</span>
        <span style={MARKS_COL}><CacheMark stable /></span>
        <span style={SIZE_COL}>
          {chars.toLocaleString('ru')}
          {share !== undefined && ` · ${share}%`}
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

// Ключ для связки «сегмент полосы ↔ строка списка». Полос в шторке несколько, а
// состояние наведения одно, поэтому ключ несёт имя своей полосы: без этого наведение
// на файл в блоке claude подсвечивало бы одноимённый сегмент в другом блоке.
const barKey = (bar: string, key: string) => `${bar}:${key}`;

// Колонка значков и колонка чисел — одной ширины во всех строках списка: так значки
// стоят друг под другом, а не разъезжаются от длины числа
const MARKS_COL: React.CSSProperties = {
  display: 'inline-flex', alignItems: 'center', justifyContent: 'flex-end',
  gap: SP.xs, width: 30, flexShrink: 0,
};
const SIZE_COL: React.CSSProperties = {
  color: C.textMuted, fontSize: FS.sm, whiteSpace: 'nowrap',
  fontVariantNumeric: 'tabular-nums', textAlign: 'right',
  minWidth: 88, flexShrink: 0,
};

// Значок «платим каждый раз / берётся из кэша» — одинаковый у всех строк, чтобы взгляд
// не искал исключения. Монетка именно потому, что это про деньги, а не про технику
function CacheMark({ stable }: { stable: boolean }) {
  return (
    <span style={{ display: 'inline-flex', flexShrink: 0 }}
      title={stable
        ? 'Один и тот же каждый раз — берётся из кэша'
        : 'Собирается заново под каждое сообщение — платите за него каждый раз'}>
      {stable
        ? <Database size={12} color={C.textMuted} />
        : <CircleDollarSign size={12} color={C.textMuted} />}
    </span>
  );
}

// Строка «метка — значение» без раскрытия: то, у чего нет текста (счётчики слоя CLI)
function MetaRow({ label, value, icon, hovered, onHover, onLeave }: {
  label: string; value: string; icon?: React.ReactNode;
  hovered?: boolean; onHover?: () => void; onLeave?: () => void;
}) {
  return (
    <div
      onMouseEnter={onHover}
      onMouseLeave={onLeave}
      style={{
        display: 'flex', alignItems: 'center', gap: SP.sm, padding: `9px ${SP.md}px`,
        borderBottom: `1px solid ${C.borderLight}`, fontSize: FS.base, color: C.textPrimary,
        background: hovered ? C.bgSelected : 'transparent', transition: 'background 120ms',
      }}>
      {icon}
      <span style={{ flex: 1, minWidth: 0 }}>{label}</span>
      <span style={SIZE_COL}>{value}</span>
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
// Наведение связано с полосой долей через общий hovered-ключ. Ключ ОБЯЗАН нести префикс
// своей полосы (см. barKey): состояние одно на всю шторку, и без префикса наведение
// в одном блоке подсвечивало бы сегмент в другом — ключи там совпадают.
// loadText — ленивый догруз (файлы слоя CLI приходят без текста, только с размером).
function SectionRow({ section, share, hoverKey, hovered, onHover, loadText }: {
  section: PromptSection; share?: number;
  hoverKey?: string;
  hovered?: string | null;
  onHover?: (key: string | null) => void;
  loadText?: (key: string) => Promise<string>;
}) {
  const [open, setOpen] = useState(false);
  const [lazyText, setLazyText] = useState<string | null>(null);
  const [loadFailed, setLoadFailed] = useState(false);
  const Icon = open ? ChevronDown : ChevronRight;
  const myKey = hoverKey ?? section.key;
  const isHot = hovered === myKey;
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
      onMouseEnter={() => onHover?.(myKey)}
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
        {/* Колонки фиксированной ширины: иначе значки прыгали бы по горизонтали вслед
            за длиной чисел, и глазу не за что зацепиться */}
        <span style={MARKS_COL}>
          {/* Персона размазана по пяти кускам — метим их, чтобы её цена была видна */}
          {section.group === 'persona' && (
            <span style={{ display: 'inline-flex' }} title="Часть персоны">
              <UserRound size={12} color={C.textMuted} />
            </span>
          )}
          {/* Монетка — кусок собирается заново под это сообщение, за него платите каждый
              раз; база — он один и тот же, модель берёт из кэша */}
          <CacheMark stable={section.stable !== false} />
        </span>
        <span style={SIZE_COL}>
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
  const [includeText, setIncludeText] = useState(false);
  const [analysis, setAnalysis] = useState<string | null>(null);
  const [analyzing, setAnalyzing] = useState(false);
  const [analysisError, setAnalysisError] = useState<string | null>(null);
  // Блок разбора живёт в самом низу списка секций — после ответа подводим к нему сами
  const analysisRef = useRef<HTMLDivElement>(null);
  // Открыть снимок старта прогона вместо унаследованного (кнопка на плашке)
  const [shownId, setShownId] = useState(snapshotId);
  // Ключ секции под курсором: связывает сегмент полосы со строкой списка
  const [hovered, setHovered] = useState<string | null>(null);
  // Вид: 'source' — откуда что взялось, 'all' — все куски одним списком по весу,
  // 'fresh' — только то, что собирается заново каждый раз (за это платим всегда)
  const [view, setView] = useState<'source' | 'all' | 'fresh'>('source');

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
  // Показываем от самых жирных: вопрос «на что уходит контекст» важнее порядка склейки

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
    mcp: 'Как пользоваться инструментами',
    project: 'Про этот проект',
    recall: 'Подтянутые заметки',
    turn: 'Ваше сообщение',
    cli: 'Файлы CLAUDE.md',
    misc: 'Прочее',
  };
  const byGroup = [...allParts.reduce((acc, s) => {
    const g = s.kind === 'cli-file' ? 'cli' : (s.group ?? 'misc');
    acc.set(g, (acc.get(g) ?? 0) + sizeOf(s));
    return acc;
  }, new Map<string, number>())].sort((a, b) => b[1] - a[1]);

  // Куски, которые собираются заново под каждое сообщение: их модель получает целиком
  // каждый раз, кэш тут не помогает
  const freshParts = allParts.filter(s => s.stable === false);
  const freshTotal = freshParts.reduce((sum, s) => sum + sizeOf(s), 0);

  // Верхняя полоса повторяет три блока ниже: сообщение, инструкции приложения и всё,
  // что добавил CLI. Первые два считаем по своим символам, третий — остатком: описания
  // инструментов внутри него нам неизвестны, и вычитание честнее выдуманного числа
  const turnTokens = Math.min(approxTokens(turnSection ? sizeOf(turnSection) : 0), contextTokens ?? 0);
  const ownTokens = Math.min(approxTokens(totalChars), Math.max(0, (contextTokens ?? 0) - turnTokens));
  const cliTokens = Math.max(0, (contextTokens ?? 0) - turnTokens - ownTokens);

  // Догруз текста файла слоя CLI — только когда его строку раскрыли (см. SectionRow)
  const loadFileText = (key: string) =>
    api.sessions.promptSnapshotFile(sessionId, shownId, key).then(f => f.text);

  const analyze = () => {
    if (analyzing) return;
    setAnalyzing(true);
    setAnalysisError(null);
    api.sessions.analyzePrompt(sessionId, shownId, includeText)
      .then(r => setAnalysis(r.analysis))
      // Показываем настоящую причину: чаще всего это «модель для разбора не настроена»
      // или протухший вход в claude — по глухому «не удалось» такое не починишь
      .catch((e: unknown) => setAnalysisError(
        e instanceof Error && e.message ? e.message : 'Не удалось разобрать'))
      .finally(() => setAnalyzing(false));
  };

  // Разбор занимает до полутора минут, а его результат оказывается ниже всего списка —
  // без подводки человек смотрит на неизменившийся экран и думает, что ничего не вышло
  useEffect(() => {
    if (analysis || analysisError) {
      analysisRef.current?.scrollIntoView({ behavior: 'smooth', block: 'start' });
    }
  }, [analysis, analysisError]);

  const cli = snapshot?.cliLayer;
  // Пустая модель у снимка = «по умолчанию»; хук сам подставит подпись из каталога
  const modelLabel = useModelLabel(snapshot?.model);

  // Доли внутри блока claude. Считаем по тому, что реально измеримо: файлы CLAUDE.md,
  // каталог навыков и вес прошлой переписки. Описания инструментов сюда не входят —
  // их текста у нас нет, и подставлять выдуманное число нельзя
  const cliParts = [
    ...(cli?.files ?? []).map(f => ({ key: f.key, size: sizeOf(f) })),
    ...(cli?.skills?.length ? [{ key: 'skills', size: skillsChars(cli.skills) }] : []),
    ...(typeof cli?.transcriptBytes === 'number'
      ? [{ key: 'transcript', size: cli.transcriptBytes }] : []),
  ].sort((a, b) => b.size - a.size);
  const cliTotal = cliParts.reduce((sum, p) => sum + p.size, 0);

  // Строки блока CLI в том же порядке, что и сегменты полосы — по убыванию веса.
  // Инструменты сюда не входят: их вес неизвестен, они рендерятся отдельно в конце
  const cliShare = (size: number) => (cliTotal > 0 ? Math.round(size * 100 / cliTotal) : 0);
  const cliRows: { key: string; size: number; render: () => React.ReactNode }[] = [
    ...(cli?.files ?? []).map(f => ({
      key: f.key,
      size: sizeOf(f),
      render: () => (
        <SectionRow key={f.key} section={f} loadText={loadFileText}
          hoverKey={barKey('cli', f.key)} hovered={hovered} onHover={setHovered}
          share={cliShare(sizeOf(f))} />
      ),
    })),
    ...(cli?.skills?.length ? [{
      key: 'skills',
      size: skillsChars(cli.skills),
      render: () => (
        <SkillsRow key="skills" skills={cli.skills!}
          hovered={hovered === barKey('cli', 'skills')}
          onHover={() => setHovered(barKey('cli', 'skills'))}
          onLeave={() => setHovered(null)}
          share={cliShare(skillsChars(cli.skills!))} />
      ),
    }] : []),
    ...(typeof cli?.transcriptBytes === 'number' ? [{
      key: 'transcript',
      size: cli.transcriptBytes,
      render: () => (
        <MetaRow key="transcript" label="Прошлая переписка в этом чате"
          hovered={hovered === barKey('cli', 'transcript')}
          onHover={() => setHovered(barKey('cli', 'transcript'))}
          onLeave={() => setHovered(null)}
          value={`${Math.round(cli.transcriptBytes! / 1024).toLocaleString('ru')} КБ · ${
            cliShare(cli.transcriptBytes!)}%`} />
      ),
    }] : []),
  ].sort((a, b) => b.size - a.size);

  return (
    <Modal width={620} title="Что модель знала, когда отвечала" onClose={onClose}
      // Подпись модели — та же, что под постом (id → человеческое имя): иначе в ленте
      // «Opus 5», а в шапке шторки сырой claude-opus-5, и это выглядит как разные модели
      subtitle={snapshot ? [modelLabel, snapshot.mode].filter(Boolean).join(' · ') : undefined}
      // Высота фиксированная: содержимое сильно разное от хода к ходу, и прыгающая
      // карточка мешала бы сравнивать. Низ с действиями прижат, середина скроллится
      cardStyle={{ height: 'calc(100vh - 32px)' }}
      footer={state === 'ready' ? (
        <div style={{
          display: 'flex', alignItems: 'center', gap: SP.md, width: '100%', flexWrap: 'wrap',
        }}>
          <label style={{
            display: 'flex', alignItems: 'center', gap: SP.sm, fontSize: FS.sm,
            color: C.textSecondary, lineHeight: 1.4, cursor: 'pointer', flex: 1, minWidth: 220,
          }}>
            <input type="checkbox" checked={includeText} style={{ flexShrink: 0 }}
              onChange={e => setIncludeText(e.target.checked)} />
            <span>Показать модели сам текст, а не только размеры</span>
          </label>
          <Button variant="ghost" size="sm" onClick={analyze} loading={analyzing}
            leftIcon={<Sparkles size={14} />}>
            {analyzing ? 'Смотрю…' : 'Что тут лишнее?'}
          </Button>
        </div>
      ) : undefined}>

      {state === 'loading' && (
        <div style={{ padding: SP.lg, color: C.textMuted, fontSize: FS.base }}>Загружаю…</div>
      )}

      {state === 'gone' && (
        <div style={{ padding: SP.lg, color: C.textSecondary, fontSize: FS.base, lineHeight: 1.6 }}>
          Не сохранилось: храним последние 50 сообщений в чате, это было раньше.
        </div>
      )}

      {state === 'error' && (
        <div style={{ padding: SP.lg, color: C.dangerText, fontSize: FS.base }}>
          Не удалось загрузить.
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
                ? 'Это то, что модель получила на этом сообщении.'
                : 'Модель этого не видела: сообщение ушло в уже работающий процесс, а он читает то, что получил при запуске. '}
              {!snapshot.applied && (snapshot.inheritedFromId
                ? (
                  <button onClick={() => setShownId(snapshot.inheritedFromId!)}
                    style={{
                      background: 'none', border: 'none', padding: 0, cursor: 'pointer',
                      color: C.accent, fontSize: FS.base, fontFamily: 'inherit',
                    }}>
                    Показать, что она видела на самом деле
                  </button>
                )
                : 'Что она видела на самом деле — уже не сохранилось.')}
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
                <span>Всего получила модель</span>
                <span style={{ color: C.textMuted, fontVariantNumeric: 'tabular-nums' }}>
                  {contextTokens.toLocaleString('ru')} токенов
                </span>
              </div>
              {/* Ровно три части — те же, что блоками ниже */}
              <ShareBar parts={[
                { key: 'turn', size: turnTokens, opacity: 1 },
                { key: 'ccs', size: ownTokens, opacity: 0.55 },
                { key: 'cli', size: cliTokens, opacity: 0.18 },
              ]} />
              <div style={{
                display: 'flex', gap: SP.md, marginTop: SP.xs, flexWrap: 'wrap',
                fontSize: FS.xs, color: C.textMuted,
              }}>
                <span>
                  <span style={{ ...dotStyle, background: C.accent }} />
                  ваше сообщение ≈ {turnTokens.toLocaleString('ru')} (
                  {Math.round(turnTokens * 100 / contextTokens)}%)
                </span>
                <span>
                  <span style={{ ...dotStyle, background: C.accentMuted }} />
                  инструкции приложения ≈ {ownTokens.toLocaleString('ru')} (
                  {Math.round(ownTokens * 100 / contextTokens)}%)
                </span>
                <span>
                  <span style={{ ...dotStyle, background: C.border }} />
                  добавил claude CLI ≈ {cliTokens.toLocaleString('ru')} (
                  {Math.round(cliTokens * 100 / contextTokens)}%)
                </span>
              </div>
              {/* Точные числа про кэш есть только здесь — из ответа API. Формулируем
                  по-разному: «взято 0, записано N» человеку ничего не говорит */}
              {turnCache && (turnCache.read > 0 || turnCache.creation > 0) && (
                <div style={{ marginTop: SP.xs, fontSize: FS.xs, color: C.textMuted, lineHeight: 1.5 }}>
                  {turnCache.read > 0 ? (
                    <>
                      Скидка за повтор: {turnCache.read.toLocaleString('ru')} токенов модель
                      уже видела в прошлых сообщениях и посчитала дешевле
                      {turnCache.creation > 0 &&
                        `, ещё ${turnCache.creation.toLocaleString('ru')} запомнила на будущее`}.
                    </>
                  ) : (
                    <>
                      Скидки за повтор не было: {turnCache.creation.toLocaleString('ru')} токенов
                      модель запомнила только сейчас. Так бывает на первом сообщении, после
                      паузы (запомненное живёт минуты) или когда начало промпта изменилось —
                      что именно случилось, из ответа модели не видно.
                    </>
                  )}
                </div>
              )}
            </div>
          )}

          {/* Три взгляда на одни данные: откуда взялось, что весит больше всего,
              и за что платим каждый раз */}
          <div style={{ marginTop: SP.md }}>
            <SegmentedControl value={view} onChange={setView} options={[
              { value: 'source', label: 'Откуда взялось' },
              { value: 'all', label: 'Что весит больше' },
              { value: 'fresh', label: 'Без кэша' },
            ]} />
          </div>

          {view === 'fresh' && (
            <>
              <SectionLabel>
                Собирается заново для каждого сообщения · {freshTotal.toLocaleString('ru')} символов
                {allTotal > 0 && ` (${Math.round(freshTotal * 100 / allTotal)}% от всего)`}
              </SectionLabel>
              {freshParts.length === 0 ? (
                <div style={{
                  padding: `${SP.sm}px ${SP.md}px`, borderRadius: R.md, background: C.bgInset,
                  fontSize: FS.base, color: C.textSecondary, lineHeight: 1.5,
                }}>
                  Здесь всё одинаково от сообщения к сообщению — модель берёт это из кэша.
                </div>
              ) : (
                <>
                  <div style={{ marginBottom: SP.sm }}>
                    <ShareBar hovered={hovered} onHover={setHovered}
                      parts={freshParts.map((s, i) => ({
                        key: barKey('fresh', s.key), size: sizeOf(s),
                        opacity: shareOpacity(i, freshParts.length),
                      }))} />
                  </div>
                  <div style={{ border: `1px solid ${C.border}`, borderRadius: R.md, overflow: 'hidden' }}>
                    {freshParts.map(s => (
                      <SectionRow key={s.key} section={s} hoverKey={barKey('fresh', s.key)}
                        hovered={hovered} onHover={setHovered}
                        loadText={s.kind === 'cli-file' ? loadFileText : undefined}
                        share={freshTotal > 0 ? Math.round(sizeOf(s) * 100 / freshTotal) : 0} />
                    ))}
                  </div>
                </>
              )}
              <div style={{ marginTop: SP.sm, fontSize: FS.xs, color: C.textMuted, lineHeight: 1.5 }}>
                Всё остальное — те же слова, что и в прошлый раз: модель узнаёт их и берёт
                из кэша дешевле. Место в своей памяти они всё равно занимают.
              </div>
            </>
          )}

          {view === 'all' && (
            <>
              <SectionLabel>На что уходит место</SectionLabel>
              <div style={{ marginBottom: SP.sm }}>
                <ShareBar hovered={hovered} onHover={setHovered}
                  parts={byGroup.map(([g, chars], i) => ({
                    key: barKey('grp', g), size: chars, opacity: shareOpacity(i, byGroup.length),
                  }))} />
              </div>
              <div style={{ border: `1px solid ${C.border}`, borderRadius: R.md, overflow: 'hidden' }}>
                {byGroup.map(([g, chars]) => (
                  <MetaRow key={g} label={GROUP_TITLES[g] ?? g}
                    hovered={hovered === barKey('grp', g)}
                    onHover={() => setHovered(barKey('grp', g))}
                    onLeave={() => setHovered(null)}
                    value={`${chars.toLocaleString('ru')} · ${
                      allTotal > 0 ? Math.round(chars * 100 / allTotal) : 0}%`} />
                ))}
              </div>

              <SectionLabel>
                Всё по кускам · {allTotal.toLocaleString('ru')} символов
              </SectionLabel>
              <div style={{ marginBottom: SP.sm }}>
                <ShareBar hovered={hovered} onHover={setHovered}
                  parts={allParts.map((s, i) => ({
                    key: barKey('all', s.key), size: sizeOf(s),
                    opacity: shareOpacity(i, allParts.length),
                  }))} />
              </div>
              <div style={{ border: `1px solid ${C.border}`, borderRadius: R.md, overflow: 'hidden' }}>
                {allParts.map(s => (
                  <SectionRow key={s.key} section={s} hoverKey={barKey('all', s.key)}
                    hovered={hovered} onHover={setHovered}
                    loadText={s.kind === 'cli-file' ? loadFileText : undefined}
                    share={allTotal > 0 ? Math.round(sizeOf(s) * 100 / allTotal) : 0} />
                ))}
              </div>
              <div style={{ marginTop: SP.sm, fontSize: FS.xs, color: C.textMuted, lineHeight: 1.5 }}>
                Сюда не входят описания {cli?.tools?.length ?? 0} инструментов: модель их
                получает, а нам CLI их текст не показывает.
              </div>
            </>
          )}

          {view === 'source' && (<>
          {/* Сначала то, что человек написал сам: от него отталкивается всё остальное */}
          {turnSection && (
            <>
              <SectionLabel>Ваше сообщение — с добавками, которых нет в ленте</SectionLabel>
              <div style={{ border: `1px solid ${C.border}`, borderRadius: R.md, overflow: 'hidden' }}>
                <SectionRow section={turnSection} />
              </div>
            </>
          )}

          <SectionLabel>
            Инструкции от приложения · {totalChars.toLocaleString('ru')} символов
            {' '}(≈ {approxTokens(totalChars).toLocaleString('ru')} токенов)
          </SectionLabel>
          <div style={{ marginBottom: SP.sm }}>
            <ShareBar hovered={hovered} onHover={setHovered}
              parts={bySize.map((s, i) => ({
                key: barKey('sys', s.key), size: s.text.length,
                opacity: shareOpacity(i, bySize.length),
              }))} />
          </div>
          <div style={{ border: `1px solid ${C.border}`, borderRadius: R.md, overflow: 'hidden' }}>
            {bySize.map(s => (
              <SectionRow key={s.key} section={s} hoverKey={barKey('sys', s.key)}
                hovered={hovered} onHover={setHovered}
                share={totalChars > 0 ? Math.round(s.text.length * 100 / totalChars) : 0} />
            ))}
          </div>

          {/* То, что добавляет сам claude поверх наших инструкций */}
          {/* Именно «claude CLI», а не имя ассистента: этот слой добавляет обёртка,
              через которую идут ВСЕ провайдеры — и DeepSeek, и GLM запускаются тем же
              claude CLI, и файлы у них те же CLAUDE.md */}
          <SectionLabel>
            Что добавил claude CLI · {cliTotal.toLocaleString('ru')} символов
          </SectionLabel>
          {cliTotal > 0 && (
            <div style={{ marginBottom: SP.sm }}>
              <ShareBar hovered={hovered} onHover={setHovered}
                parts={cliParts.map((p, i) => ({
                  key: barKey('cli', p.key), size: p.size,
                  opacity: shareOpacity(i, cliParts.length),
                }))} />
            </div>
          )}
          {/* Сервер отдаёт незаполненные поля как null, поэтому проверяем тип, а не
              !== undefined: у хода, упавшего до старта процесса, их попросту нет */}
          <div style={{ border: `1px solid ${C.border}`, borderRadius: R.md, overflow: 'hidden' }}>
            {/* Строки — по убыванию веса, как сегменты полосы. Инструменты всегда
                последние: у них веса нет, и в отсортированном списке им нет места */}
            {cliRows.map(r => r.render())}
            {cli?.tools && cli.tools.length > 0 && (
              <ToolsRow tools={cli.tools} servers={cli.mcpServers ?? undefined} />
            )}
          </div>

          </>)}

          {/* Разбор промпта моделью. По умолчанию наружу уходят только метаданные секций.
              Якорь автоскролла: ответ приходит в самый низ длинного списка, и без него
              человек не видит, что кнопка вообще сработала */}
          <div ref={analysisRef}>
            {analysis && (<>
              <SectionLabel>Что модель думает про этот промпт</SectionLabel>
              <div style={{
                padding: SP.md, borderRadius: R.md, background: C.bgInset,
                fontSize: FS.base, color: C.textPrimary, lineHeight: 1.6,
              }}>
                {/* Модель отвечает markdown-списком — рендерим тем же компонентом, что
                    и ленту чата, иначе на экране сырые звёздочки и решётки */}
                <MarkdownContent text={analysis} />
              </div>
            </>)}
            {analysisError && (
              <div style={{ color: C.dangerText, fontSize: FS.sm }}>{analysisError}</div>
            )}
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

