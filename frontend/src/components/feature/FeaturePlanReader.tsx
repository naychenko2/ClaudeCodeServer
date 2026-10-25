// Читалка документа фичи (волна 3, вкладка «Планы и макеты»).
//
// Шапка: метка роли, заголовок, автор (имя персоны + «задача» — NavTargetButton),
// время сдачи. Тело: markdown через ТОТ ЖЕ рендер, что в ленте чата (MarkdownContent —
// свой не заводить: таблицы со своей прокруткой, код, mermaid — как в ленте).
//
// Текст — по клику: GET /api/tasks/{id} → resultMarkdown; кэш на время жизни страницы
// (модульный: переживает перемонтирование, лента/страница перерисовываются).
//
// Состояния: hasText=false → «Текста пока нет»; loading (скелет); error + «Повторить»;
// ok → markdown. Чипы «Макеты в тексте» — по plan.mockupPaths (тап → карточка макета).
//
// Длинный документ (6 и больше `##`): «Содержание · N» — на 1280 раскрыто, на 360 свёрнуто;
// каждый `##`-раздел сворачивается отдельно, «Свернуть все / Развернуть все» в панели
// оглавления. Короткий документ — сплошной текст без свёртки (байт в байт).
//
// Ссылки между документами фичи (W4-LINK-F): [[wikilinks]] и относительные пути внутри
// дерева фичи — кликабельны, открывают цель одним кликом. [[Заголовок]] резолвится по
// спискам документов фичи, путь — через резолв featureId (W4-5B2), не своей логикой:
// цель читается тем же эндпоинтом файлов. Внешние http-ссылки — как раньше (ReaderLinkWrap
// в общем рендере). Битые ссылки не роняют читалку: открытие само показывает «не найдено».

import { useEffect, useMemo, useRef, useState } from 'react';
import { AlertTriangle, FileText, RotateCw, Scissors, MessageCircle, ChevronDown, type LucideIcon } from 'lucide-react';
import { C, R, FS, FONT, SP } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';
import { Button } from '../ui/Button';
import { MarkdownContent } from '../chat/MarkdownContent';
import { FeatureLinkContext } from '../chat/contexts';
import { FILE_LIKE_MENTION } from '../../lib/projectFileIndex';
import { getPersonaById } from '../../lib/personas';
import { openTaskInSection } from '../../lib/tasks';
import { NavTargetButton, NavTargetPair } from './NavTargetButton';
import { EMPTY_NAV_MARKS, type NavMarks } from '../../lib/featureNavMarks';
import { hhmm, dateOnly } from '../chat/feature/featureTime';
import { api } from '../../lib/api';
import type { FeaturePlan, FeatureMockup } from '../../types/featurePlans';

// === Роль документа: подпись + цвет значка (макет §токены) ===
// план — planLight/planText; разрез — infoBg/info; консультация — bgSelected/textSecondary.
export const PLAN_ROLE_LABEL: Record<FeaturePlan['role'], string> = {
  plan: 'план',
  cut: 'разрез',
  consult: 'консультация',
};
export const PLAN_ROLE_STYLE: Record<FeaturePlan['role'], { bg: string; fg: string; Icon: LucideIcon }> = {
  plan: { bg: C.planLight, fg: C.planText, Icon: FileText },
  cut: { bg: C.infoBg, fg: C.info, Icon: Scissors },
  consult: { bg: C.bgSelected, fg: C.textSecondary, Icon: MessageCircle },
};

// Кэш текста документа на время жизни страницы: (taskId, completedAt) → состояние.
// Модульный — переживает перемонтирование (лента/страница перерисовываются на каждом
// обновлении). Ключ включает completedAt: перезаписанный план (task обновлён) меняет
// completedAt → старый текст не показывается, идёт повторный запрос.
type TextState =
  | { status: 'loading' }
  | { status: 'error' }
  | { status: 'ok'; text: string };
const _textCache = new Map<string, TextState>();
const textCacheKey = (p: FeaturePlan) => `${p.taskId}|${p.completedAt ?? ''}`;

// Разбор длинного документа: преамбула + `##`-секции. Один сканер и для «Содержание»,
// и для свёртки: строки внутри ```- или ~~~-забора — не заголовки, `##` в код-блоке
// раздел не начинает. Только уровень 2, без `###`.
export interface PlanSection {
  // Текст строки заголовка как в исходном markdown (`## Заголовок`) — её отдаём общему рендеру.
  head: string;
  // Заголовок без `##` — подпись пункта «Содержание».
  title: string;
  // Тело раздела (без строки заголовка) — общий рендер отдельным вызовом.
  body: string;
}

function splitDoc(text: string): { preamble: string; sections: PlanSection[] } {
  const lines = text.split('\n');
  const heads: number[] = [];
  // Открытый забор: закрывается только той же разновидности и не короче открывающего
  // (CommonMark). Строка ``` внутри ~~~-забора — просто текст, а не смена состояния.
  let fence: string | null = null;
  let fenceLen = 0;
  for (let i = 0; i < lines.length; i++) {
    // Строка-забор меняет состояние, но остаётся в теле раздела: её рендерит общий рендер.
    // Отступ забора — до 3 пробелов (таб = 4: это уже indented code). Закрывает забор только
    // строка из одного забора и пробелов: ```js внутри ```-забора — содержимое кода.
    const m = /^ {0,3}(`{3,}|~{3,})(.*)$/.exec(lines[i]);
    if (m) {
      const run = m[1];
      if (fence === null) { fence = run[0]; fenceLen = run.length; }
      else if (run[0] === fence && run.length >= fenceLen && m[2].trim() === '') { fence = null; }
      continue;
    }
    if (!fence && /^##\s+/.test(lines[i])) heads.push(i);
  }
  const sections = heads.map((start, k) => {
    const block = lines.slice(start, k + 1 < heads.length ? heads[k + 1] : lines.length);
    const m = /^##\s+(.+?)\s*$/.exec(block[0]);
    return { head: block[0], title: headingToPlain(m ? m[1] : block[0]), body: block.slice(1).join('\n').trim() };
  });
  return { preamble: lines.slice(0, heads[0] ?? 0).join('\n').trim(), sections };
}

// Подпись раздела: только инлайн-разметка. Маркеры списка в начале не снимаем — иначе
// «1. Введение» теряет номер и подпись расходится с видимым заголовком.
function headingToPlain(text: string): string {
  return text
    .replace(/<\/?[A-Za-z][^>]*>/g, '')
    .replace(/!\[([^\]]*)\]\([^)]*\)/g, '$1')
    .replace(/\[([^\]]*)\]\([^)]*\)/g, '$1')
    .replace(/`+/g, '')
    .replace(/\*\*|__|~~/g, '')
    .replace(/\*([^*\n]+)\*/g, '$1')
    .replace(/(^|[^\p{L}\p{N}_])_([^_\n]+)_(?=[^\p{L}\p{N}_]|$)/gu, '$1$2')
    .replace(/\s+/g, ' ')
    .trim();
}

// W4-LINK-F: [[X]] в тексте документа — ссылка между документами фичи. Совпал
// заголовок другого документа → featuredoc: (откроется в этой же читалке); похоже на
// путь внутри дерева фичи → featurelink: (читается через резолв featureId, W4-5B2).
// Всё остальное остаётся текстом — битая ссылка ничего не роняет. Внутри код-заборов
// не трогаем: там цитата, а не ссылка (тот же сканер заборов, что у splitDoc).
const normTitle = (s: string) => s.toLowerCase().trim();
// Похоже на путь внутри дерева фичи: с разделителем или с расширением файла.
const isTreePath = (s: string) => s.includes('/') || FILE_LIKE_MENTION.test(s);

function linkifyFeatureRefs(text: string, docs: FeaturePlan[]): string {
  const byTitle = new Map<string, string>();
  for (const d of docs) byTitle.set(normTitle(d.title), d.taskId);
  const lines = text.split('\n');
  let fence: string | null = null;
  let fenceLen = 0;
  for (let i = 0; i < lines.length; i++) {
    const m = /^ {0,3}(`{3,}|~{3,})(.*)$/.exec(lines[i]);
    if (m) {
      const run = m[1];
      if (fence === null) { fence = run[0]; fenceLen = run.length; }
      else if (run[0] === fence && run.length >= fenceLen && m[2].trim() === '') fence = null;
      continue;
    }
    if (fence) continue;
    lines[i] = lines[i].replace(/\[\[([^\]|]+)(?:\|([^\]]+))?\]\]/g, (full, target, label) => {
      const t = String(target).trim();
      if (!t) return full;
      const id = byTitle.get(normTitle(t));
      if (id) return `[${label ?? target}](featuredoc:${encodeURIComponent(id)})`;
      if (isTreePath(t)) return `[${label ?? target}](featurelink:${encodeURIComponent(t)})`;
      return full;
    });
  }
  return lines.join('\n');
}

// Значок роли: 24×24, R 7, цвет по роли (макет §токены).
export function RoleGlyph({ role }: { role: FeaturePlan['role'] }) {
  const { bg, fg, Icon } = PLAN_ROLE_STYLE[role];
  return (
    <span
      data-testid="plan-role-glyph"
      aria-label={PLAN_ROLE_LABEL[role]}
      style={{
        width: 24, height: 24, borderRadius: 7, flexShrink: 0,
        background: bg, color: fg,
        display: 'inline-flex', alignItems: 'center', justifyContent: 'center',
      }}
    >
      <Icon size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
    </span>
  );
}

// Служебные строки markdown (```fence```, `inline`) — не заголовки.
export interface FeaturePlanReaderProps {
  // Документ (план/разрез/консультация).
  plan: FeaturePlan;
  // Все макеты фичи — для чипов «Макеты в тексте» (сопоставление по mockupPaths).
  mockups: FeatureMockup[];
  isMobile?: boolean;
  // Тап по чипу «Макеты в тексте» → карточка макета в группе «Макеты».
  onOpenMockup?: (path: string) => void;
  // Открыть задачу документа (автор → «задача»).
  onOpenTask?: (taskId: string) => void;
  // Пометка «задача закрыта» для задачи документа (из данных страницы).
  marks?: NavMarks;
  // Фича документа (W4-LINK-F): резолв featureId для чтения файлов дерева фичи (W4-5B2)
  // и проект — область запроса. Без них ссылки между документами не оживают.
  featureId?: string | null;
  projectId?: string | null;
  // Другие документы фичи — для резолва [[wikilinks]] по заголовку.
  documents?: FeaturePlan[];
  // Открыть другой документ фичи в этой же читалке.
  onOpenDocument?: (taskId: string) => void;
}

export function FeaturePlanReader({ plan, mockups, isMobile = false, onOpenMockup, onOpenTask, marks = EMPTY_NAV_MARKS, featureId = null, projectId = null, documents = [], onOpenDocument }: FeaturePlanReaderProps) {
  const [state, setState] = useState<TextState>(() => _textCache.get(textCacheKey(plan)) ?? { status: 'loading' });
  const [reload, setReload] = useState(0);
  // Открытый по ссылке целевой документ (W4-LINK-F): текст читается через резолв
  // featureId (W4-5B2), при отказе — корень проекта (после мержа worktree нет, документ
  // в main). Оба отказа — честный «документа нет», читалка не роняется.
  const [openDoc, setOpenDoc] = useState<{ path: string; status: 'loading' | 'error' | 'ok'; text?: string; isBinary?: boolean } | null>(null);
  // «Содержание · N» — раскрыто на 1280, свёрнуто на 360.
  const [tocOpen, setTocOpen] = useState(!isMobile);

  // Обвязка общего рендера (W4-LINK-F): распознавание ссылок — то же, что в чате
  // (FileLink + resolveFileMention в MarkdownContent), расширенное схемами читалки.
  // Без featureId/projectId — null: поведение ссылок как раньше.
  const linkCtx = useMemo(() => {
    if (!featureId || !projectId) return null;
    const openFile = (path: string) => {
      setOpenDoc({ path, status: 'loading' });
      const read = (fid?: string) => api.files.getContent(projectId, path, fid);
      read(featureId)
        .then(r => setOpenDoc({ path, status: 'ok', text: r.content ?? '', isBinary: r.isBinary }))
        .catch(() => read()
          .then(r => setOpenDoc({ path, status: 'ok', text: r.content ?? '', isBinary: r.isBinary }))
          .catch(() => setOpenDoc({ path, status: 'error' })));
    };
    return { featureId, projectId, openFile, openDocument: (id: string) => onOpenDocument?.(id) };
  }, [featureId, projectId, onOpenDocument]);
  // Свёрнутые `##`-разделы длинного документа: по умолчанию пустое — все раскрыты.
  const [collapsed, setCollapsed] = useState<Set<number>>(() => new Set());
  // Корень тела — для скролла к `##`-секции по пункту «Содержание» (под липкие вкладки).
  const bodyRef = useRef<HTMLDivElement>(null);
  // Пункт «Содержание» → раскрыть соответствующую `##`-секцию и докрутить её (макет).
  // Привязка по data-атрибуту секции, а не по индексу `h2` в DOM: заголовок внутри
  // цитаты или списка рендерится как h2, но сканер его не считает — индексы разъехались бы.
  // Скролл — в эффекте после коммита DOM (дефект приёмки): позиция, посчитанная по
  // свёрнутому разделу, после раскрытия «уезжает» вниз и раздел остаётся в середине.
  const [pendingScroll, setPendingScroll] = useState<number | null>(null);
  const scrollToSection = (i: number) => {
    setCollapsed(s => {
      if (!s.has(i)) return s;
      const next = new Set(s);
      next.delete(i);
      return next;
    });
    setPendingScroll(i);
  };

  // Позиция считается уже по раскрытому разделу: эффект выполняется после фиксации DOM.
  useEffect(() => {
    if (pendingScroll === null) return;
    setPendingScroll(null);
    const root = bodyRef.current;
    if (!root) return;
    const target = root.querySelector(`[data-plan-section="${pendingScroll}"]`) as HTMLElement | undefined;
    if (!target) return;
    // Компенсация липкой шапки/вкладок: секция встаёт под ними, а не под верх.
    target.style.scrollMarginTop = '72px';
    target.scrollIntoView({ behavior: 'smooth', block: 'start' });
  }, [pendingScroll]);

  const toggleSection = (i: number, expanded: boolean) => setCollapsed(s => {
    const next = new Set(s);
    if (expanded) next.add(i); else next.delete(i);
    return next;
  });

  // Текст по клику: GET /api/tasks/{id} → resultMarkdown. Кэш на время жизни страницы.
  useEffect(() => {
    if (!plan.hasText) return; // «Текста пока нет» — запрос не идёт
    // Новый документ (или повтор) — свёртка начинается заново: все разделы раскрыты.
    setCollapsed(new Set());
    // Открытый по ссылке документ относится к прежнему тексту — с новым он не показан.
    setOpenDoc(null);
    const key = textCacheKey(plan);
    // Повтор (reload > 0): сбросить кэш (в т.ч. ошибку), чтобы тянуть заново.
    if (reload > 0) _textCache.delete(key);
    const cached = _textCache.get(key);
    if (cached && cached.status !== 'loading') { setState(cached); return; }
    setState({ status: 'loading' });
    let cancelled = false;
    void api.tasks.get(plan.taskId)
      .then(t => {
        const s: TextState = { status: 'ok', text: t.resultMarkdown ?? '' };
        _textCache.set(key, s);
        if (!cancelled) setState(s);
      })
      .catch(() => {
        const s: TextState = { status: 'error' };
        _textCache.set(key, s);
        if (!cancelled) setState(s);
      });
    return () => { cancelled = true; };
  }, [plan.taskId, plan.completedAt, plan.hasText, reload]);

  const persona = plan.authorPersonaId ? getPersonaById(plan.authorPersonaId) : undefined;
  const authorName = persona?.name ?? (plan.authorPersonaId ? plan.authorPersonaId : null);
  const time = plan.completedAt ? `${dateOnly(plan.completedAt)} · ${hhmm(plan.completedAt)}` : null;

  // «Макеты в тексте»: mockupPaths → макеты (по path или по файлам).
  const mentioned = mockups.filter(m =>
    plan.mockupPaths.some(p => p === m.path || m.files.some(f => f.path === p))
  );

  // Ссылки между документами оживляются только при резолве фичи (linkCtx): без него
  // текст рендерится как раньше — [[…]] остаётся текстом. Текст один раз: и сканер
  // разделов, и рендер видят обработанный текст (короткий документ рендерится целиком).
  const docText = state.status === 'ok' ? (linkCtx ? linkifyFeatureRefs(state.text, documents) : state.text) : '';
  const doc = state.status === 'ok' ? splitDoc(docText) : null;
  const h2 = doc ? doc.sections.map(s => s.title) : [];
  const longDoc = h2.length >= 6;
  // Кнопки свёртки: не показываем обе активными, когда делать нечего.
  const allCollapsed = longDoc && doc!.sections.every((_, i) => collapsed.has(i));
  const allExpanded = !longDoc || collapsed.size === 0;

  return (
    <FeatureLinkContext.Provider value={linkCtx}>
    <div data-testid="plan-reader" style={{ display: 'flex', flexDirection: 'column', gap: SP.md, fontFamily: FONT.sans }}>
      {/* Шапка: роль · заголовок · автор · время */}
      <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xs }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm }}>
          <RoleGlyph role={plan.role} />
          <span
            data-testid="plan-reader-role"
            style={{ fontSize: FS.xs, fontWeight: 600, color: PLAN_ROLE_STYLE[plan.role].fg, background: PLAN_ROLE_STYLE[plan.role].bg, borderRadius: R.sm, padding: '0 6px' }}
          >
            {PLAN_ROLE_LABEL[plan.role]}
          </span>
        </div>
        <h1 data-testid="plan-reader-title" style={{ fontFamily: FONT.serif, fontSize: isMobile ? 18 : 20, fontWeight: 700, color: C.textHeading, letterSpacing: '-0.01em', margin: 0, lineHeight: 1.25 }}>
          {plan.title}
        </h1>
        <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap' }}>
          {authorName && (
            <span style={{ fontSize: FS.sm, color: C.textSecondary, display: 'inline-flex', alignItems: 'center', gap: 4 }}>
              {authorName}
            </span>
          )}
          {plan.taskId && (
            <NavTargetButton
              kind="task"
              taskId={plan.taskId}
              taskClosed={marks.taskClosed(plan.taskId)}
              onOpen={() => (onOpenTask ? onOpenTask(plan.taskId) : openTaskInSection({ id: plan.taskId, projectId: projectId ?? undefined }))}
              title="Открыть задачу документа"
              isMobile={isMobile}
            />
          )}
          {time && (
            <span style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted, whiteSpace: 'nowrap' }}>{time}</span>
          )}
        </div>
      </div>

      <hr style={{ border: 'none', borderTop: `1px solid ${C.borderLight}`, margin: 0 }} />

      {/* «Макеты в тексте»: чипы по mockupPaths (тап → карточка макета). */}
      {mentioned.length > 0 && (
        <div data-testid="plan-reader-mockups" style={{ display: 'flex', alignItems: 'center', gap: SP.xs, flexWrap: 'wrap' }}>
          <span style={{ fontSize: FS.xs, color: C.textMuted, marginRight: 2 }}>Макеты в тексте:</span>
          {mentioned.map(m => (
            <button
              key={m.path}
              type="button"
              data-testid="plan-reader-mockup-chip"
              onClick={() => onOpenMockup?.(m.path)}
              style={{
                display: 'inline-flex', alignItems: 'center', gap: 4,
                height: isMobile ? 40 : 26, padding: '0 8px',
                border: `1px solid ${C.border}`, background: C.bgWhite, borderRadius: R.sm,
                fontFamily: FONT.mono, fontSize: 12, color: C.accent, cursor: 'pointer',
              }}
            >
              {m.title}
            </button>
          ))}
        </div>
      )}

      {/* «Содержание · N» — длинный документ (6+ `##`): 1280 раскрыто, 360 свёрнуто.
          При открытом по ссылке целевом документе оглавание текущего не показывается:
          скролл к разделам закрытого текста не имеет цели. */}
      {longDoc && !openDoc && (
        <div data-testid="plan-reader-toc" style={{ border: `1px solid ${C.borderLight}`, borderRadius: R.md, background: C.bgCard }}>
          <button
            type="button"
            aria-expanded={tocOpen}
            onClick={() => setTocOpen(v => !v)}
            style={{
              width: '100%', display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: SP.sm,
              height: isMobile ? 44 : 32, padding: `0 ${SP.md}px`, border: 'none', background: 'transparent',
              cursor: 'pointer', fontFamily: FONT.sans, fontSize: FS.sm, fontWeight: 600, color: C.textHeading,
            }}
          >
            <span>Содержание · {h2.length}</span>
            <ChevronDown size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} style={{ transform: tocOpen ? 'rotate(180deg)' : 'none', transition: 'transform 0.15s' }} />
          </button>
          {/* Свёртка разделов: видны и при закрытом оглавлении (на 360 оно по умолчанию свёрнуто).
              «Свернуть все» неактивна, когда всё уже свёрнуто, «Развернуть все» — когда всё раскрыто. */}
          <div style={{ display: 'flex', gap: SP.xs, flexWrap: 'wrap', padding: `0 ${SP.md}px ${SP.sm}px` }}>
            <Button
              size={isMobile ? 'md' : 'xs'}
              variant="ghost"
              dataTestId="plan-collapse-all"
              disabled={allCollapsed}
              onClick={() => setCollapsed(new Set(doc!.sections.map((_, i) => i)))}
            >
              Свернуть все
            </Button>
            <Button
              size={isMobile ? 'md' : 'xs'}
              variant="ghost"
              dataTestId="plan-expand-all"
              disabled={allExpanded}
              onClick={() => setCollapsed(new Set())}
            >
              Развернуть все
            </Button>
          </div>
          {tocOpen && (
            <ol style={{ margin: 0, padding: `${SP.xs}px ${SP.md}px ${SP.sm}px`, listStyle: 'none', display: 'grid', gridTemplateColumns: isMobile ? '1fr' : '1fr 1fr', gap: `${SP.xs}px ${SP.md}px` }}>
              {h2.map((t, i) => (
                <li key={i}>
                  <button
                    type="button"
                    data-testid="plan-reader-toc-item"
                    onClick={() => scrollToSection(i)}
                    title={t}
                    style={{
                      width: '100%', textAlign: 'left', border: 'none', background: 'transparent', cursor: 'pointer',
                      // Тач-цель на 360 — не меньше 40 px по обеим сторонам (высота). На десктопе
                      // строки остаются компактными: двухколоночная сетка от высоты не разъезжается.
                      padding: isMobile ? '0 8px' : 0, minHeight: isMobile ? 40 : 0,
                      fontFamily: FONT.sans, fontSize: FS.sm, color: C.accent,
                      overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
                    }}
                  >
                    {t}
                  </button>
                </li>
              ))}
            </ol>
          )}
        </div>
      )}

      {/* Тело: состояния */}
      {!plan.hasText ? (
        <div data-testid="plan-reader-no-text" style={{ display: 'flex', flexDirection: 'column', gap: SP.sm, padding: `${SP.md}px 0` }}>
          <span style={{ fontSize: FS.sm, color: C.textMuted }}>Текста пока нет</span>
          {plan.taskId && (
            <NavTargetPair
              chatId={null}
              showNoChat={false}
              taskId={plan.taskId}
              taskClosed={marks.taskClosed(plan.taskId)}
              onOpenTask={() => (onOpenTask ? onOpenTask(plan.taskId) : openTaskInSection({ id: plan.taskId, projectId: projectId ?? undefined }))}
              taskTitle="Открыть задачу документа"
              isMobile={isMobile}
            />
          )}
        </div>
      ) : state.status === 'loading' ? (
        <div data-testid="plan-reader-loading" style={{ display: 'flex', flexDirection: 'column', gap: 8, padding: `${SP.md}px 0` }}>
          {[70, 92, 84, 60].map((w, i) => (
            <div key={i} style={{ height: 12, width: `${w}%`, background: C.bgSelected, borderRadius: R.sm }} />
          ))}
        </div>
      ) : state.status === 'error' ? (
        <div data-testid="plan-reader-error" style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap', padding: `${SP.md}px 0` }}>
          <span style={{ display: 'inline-flex', alignItems: 'center', gap: 6, color: C.dangerText, fontSize: FS.sm }}>
            <AlertTriangle size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} /> Не удалось загрузить текст
          </span>
          <Button size="xs" variant="ghost" dataTestId="plan-retry" onClick={() => setReload(n => n + 1)}>
            <RotateCw size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} /> Повторить
          </Button>
          {plan.taskId && (
            <NavTargetButton
              kind="task"
              taskId={plan.taskId}
              taskClosed={marks.taskClosed(plan.taskId)}
              onOpen={() => (onOpenTask ? onOpenTask(plan.taskId) : openTaskInSection({ id: plan.taskId, projectId: projectId ?? undefined }))}
              title="Открыть задачу документа"
              isMobile={isMobile}
            />
          )}
        </div>
      ) : (
        <>
          {openDoc ? (
            /* Открытый по ссылке целевой документ (W4-LINK-F): тот же общий рендер,
               возврат — кнопкой, состояние загрузки/отказа честное. */
            <div data-testid="plan-open-doc" style={{ border: `1px solid ${C.border}`, borderRadius: R.md, background: C.bgCard, padding: `${SP.md}px ${SP.md}px` }}>
              <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, marginBottom: SP.sm }}>
                <span style={{ display: 'inline-flex', alignItems: 'center', gap: 6, color: C.textHeading, fontSize: FS.sm, fontFamily: FONT.mono, flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                  <FileText size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
                  {openDoc.path}
                </span>
                <Button size={isMobile ? 'md' : 'xs'} variant="ghost" dataTestId="plan-open-doc-close" onClick={() => setOpenDoc(null)}>
                  К текущему документу
                </Button>
              </div>
              {openDoc.status === 'loading' ? (
                <div data-testid="plan-open-doc-loading" style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
                  {[70, 92, 84, 60].map((w, i) => (
                    <div key={i} style={{ height: 12, width: `${w}%`, background: C.bgSelected, borderRadius: R.sm }} />
                  ))}
                </div>
              ) : openDoc.status === 'error' ? (
                <span data-testid="plan-open-doc-missing" style={{ fontSize: FS.sm, color: C.textMuted }}>Документа нет — он не найден ни в дереве фичи, ни в проекте</span>
              ) : openDoc.isBinary ? (
                <span data-testid="plan-open-doc-binary" style={{ fontSize: FS.sm, color: C.textMuted }}>Файл не текстовый — открыть его можно в просмотре файлов.</span>
              ) : (
                <div data-testid="plan-open-doc-body" style={{ fontSize: FS.base, lineHeight: 1.6, color: C.textPrimary }}>
                  <MarkdownContent text={openDoc.text ?? ''} />
                </div>
              )}
            </div>
          ) : (
            <div ref={bodyRef} data-testid="plan-reader-body" style={{ fontSize: FS.base, lineHeight: 1.6, color: C.textPrimary }}>
            {!longDoc ? (
              // Короткий документ — сплошной текст, без свёртки (как раньше).
              <MarkdownContent text={docText} />
            ) : (
              // Длинный: преамбула и каждый `##`-раздел — отдельным вызовом общего рендера.
              // Свёрнутый раздел отдаёт рендеру только строку заголовка: тело скрыто.
              <>
                {doc!.preamble && <MarkdownContent text={doc!.preamble} />}
                {doc!.sections.map((sec, i) => {
                  const expanded = !collapsed.has(i);
                  return (
                    <div key={i} data-testid="plan-section" data-plan-section={i}>
                      {/* Заголовок не внутри button (button допускает только phrasing-контент):
                          h2 остаётся заголовком, переключатель — отдельная кнопка-шевелон рядом. */}
                      {/* Свёртка — только кнопкой-шевелоном: тап по тексту заголовка раздел не сворачивает
                          (известный компромисс, он ломал выделение, ссылки и двойной клик — не возвращать). */}
                      <div
                        data-testid="plan-section-head"
                        style={{ display: 'flex', alignItems: 'center', gap: SP.sm, textAlign: 'left' }}
                      >
                        <div style={{ flex: 1, minWidth: 0 }}>
                          <MarkdownContent text={sec.head} />
                        </div>
                        <button
                          type="button"
                          data-testid="plan-section-toggle"
                          aria-expanded={expanded}
                          aria-label={`${expanded ? 'Свернуть' : 'Развернуть'} раздел ${sec.title}`}
                          title={expanded ? 'Свернуть раздел' : 'Развернуть раздел'}
                          onClick={() => toggleSection(i, expanded)}
                          style={{
                            width: isMobile ? 40 : 34, minHeight: isMobile ? 40 : 26, flexShrink: 0,
                            border: 'none', background: 'transparent', cursor: 'pointer',
                            display: 'inline-flex', alignItems: 'center', justifyContent: 'center',
                            color: C.textSecondary,
                          }}
                        >
                          <ChevronDown size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} style={{ transform: expanded ? 'rotate(180deg)' : 'none', transition: 'transform 0.15s' }} />
                        </button>
                      </div>
                      {expanded && sec.body && <MarkdownContent text={sec.body} />}
                    </div>
                  );
                })}
              </>
            )}
            </div>
          )}
          {/* Подвал: текст — итог задачи, здесь только чтение (макет). */}
          <div data-testid="plan-reader-footer" style={{ borderTop: `1px solid ${C.borderLight}`, marginTop: SP.md, paddingTop: SP.sm, fontSize: FS.xs, color: C.textMuted, lineHeight: 1.45 }}>
            Текст — итог задачи. Здесь только чтение; правка — в задаче.
          </div>
        </>
      )}
    </div>
    </FeatureLinkContext.Provider>
  );
}
