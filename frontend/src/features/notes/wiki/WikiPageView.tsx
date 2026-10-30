// Страница вики (только чтение): макет llm-wiki-v2.html, экраны «Страница вики» и «Хроника
// чата». Страница только для чтения — ни одного контрола правки текста; единственное
// действие — «Принять как есть» при дрейфе (решение человека о файле, ADR-035, решение 6):
// только при drift из ответа бэка, блокируется на время запроса, ошибка называется (не молча).
//
// Данные — страница из стора (lib/wiki: GET /api/wiki/page отдаёт {pageKey, content,
// drift?, manifestState?} — контракт S2f плюс доработка S5b): экран читает слот последней
// прочитанной страницы, поэтому перечитывание после adopt видно здесь без локального
// состояния. Дрейф — поле ответа (его считает бэк сверкой файла с манифестом), а не шапка
// status: шапку пишет модель, она не сигнал (ревью S5b M1). Разбор здесь — зеркало бэкового
// WikiPage.Parse
// (WikiPage.cs:68): шапка → '# заголовок' → '## секции' → '[^sN]: [[цель]] — «цитата»'.
// Фронт-разбор терпимый: страница с неразобранной шапкой или секциями остаётся видимой
// (тот же принцип, что у каталога, lib/wiki.ts:40) — строгость бэка это его гейт записи,
// не витрина. Рендер сносок в общем MarkdownViewer — S5e, здесь его не ждём: маркер-
// надстрочник с цитатой в title и блок «Источники» из определений сносок.
//
// Открытие страницы — из каталога (WikiTab, одна точка подключения); открытие по ссылкам
// внутри страницы не в этом шаге. Сирота: имени и даты удаления проекта бэк не отдаёт
// (хвост S2f) — плашка общая, как в каталоге (WikiTab.OrphanNotice).

import { useMemo, useState } from 'react';
import type { ReactNode } from 'react';
import { AlertTriangle, ChevronLeft, Lock, Quote, ScrollText } from 'lucide-react';
import { C, FONT, FS, R, SP } from '../../../lib/design';
import { Badge } from '../../../components/ui/Badge';
import { Button } from '../../../components/ui/Button';
import { Notice } from '../../../components/ui/Notice';
import { ICON_SIZE, ICON_STROKE } from '../../../components/ui/icons';
import { useIsMobile } from '../../../lib/breakpoints';
import { WikiError, adoptWikiPage, useWikiPage, type WikiSectionDto } from '../../../lib/wiki';
import { PAGE_STATUS, PAGE_TYPE, sectionPrefix, wikiErrorText } from './WikiTab';

// ─── Разбор страницы (зеркало бэкового WikiPage.Parse, терпимый) ───

const TITLE_RX = /^# (.+)$/;
const SECTION_RX = /^## (.+)$/;
// Определение сноски: [^id]: [[цель]] — «цитата» (зеркало бэковой WikiPage.cs:37;
// скобка в классе экранирована: в JS `[^]` — «любой символ», не отрицание)
const FN_DEF_RX = /^\[\^([^\]\r\n]+)\]: (.+?) — «(.*)»$/;
// Ссылка сноски и ссылка вики внутри строки: первая группа — id сноски, вторая — цель [[…]]
const INLINE_RX = /\[\^([^\]\r\n]+)\]|\[\[([^\]\r\n]+)\]\]/g;

export interface ParsedWikiPage {
  type: string | null;
  kind: string | null;
  status: string | null;
  summary: string | null;
  updated: string | null;
  aliases: string[];
  title: string | null;
  sections: { name: string; lines: string[] }[];
  footnotes: { id: string; target: string; quote: string }[];
  // Строки вне секций — для бэка это ошибка формата, для витрины — текст страницы
  strayLines: string[];
}

// [a, b] — та же списочная форма, что у бэка (WikiFrontmatter.ParseList); прочее — пусто
function parseListValue(v: string): string[] {
  if (!v.startsWith('[') || !v.endsWith(']')) return [];
  const inner = v.slice(1, -1).trim();
  return inner ? inner.split(', ').map(s => s.trim()).filter(Boolean) : [];
}

export function parseWikiPage(text: string): ParsedWikiPage {
  const lines = text.replace(/\r\n/g, '\n').replace(/\r/g, '\n').split('\n');
  const page: ParsedWikiPage = {
    type: null, kind: null, status: null, summary: null, updated: null, aliases: [],
    title: null, sections: [], footnotes: [], strayLines: [],
  };
  let i = 0;
  // Шапка: только известные ключи (type/kind/status/summary/updated/aliases);
  // неизвестные и кривые строки пропускаем — витрине они не нужны
  if (lines[0] === '---') {
    const close = lines.indexOf('---', 1);
    if (close > 0) {
      for (let j = 1; j < close; j++) {
        const sep = lines[j].indexOf(': ');
        if (sep <= 0) continue;
        const key = lines[j].slice(0, sep);
        const value = lines[j].slice(sep + 2);
        if (key === 'type') page.type = value;
        else if (key === 'kind') page.kind = value;
        else if (key === 'status') page.status = value;
        else if (key === 'summary') page.summary = value;
        else if (key === 'updated') page.updated = value;
        else if (key === 'aliases') page.aliases = parseListValue(value);
      }
      i = close + 1;
    }
  }
  let current: { name: string; lines: string[] } | null = null;
  for (; i < lines.length; i++) {
    const line = lines[i];
    if (!line) continue;
    const fn = FN_DEF_RX.exec(line);
    if (fn) {
      const raw = fn[2].trim();
      page.footnotes.push({
        id: fn[1],
        target: raw.startsWith('[[') && raw.endsWith(']]') ? raw.slice(2, -2) : raw,
        quote: fn[3],
      });
      continue;
    }
    const t = TITLE_RX.exec(line);
    if (t && !page.title) { page.title = t[1]; continue; }
    const s = SECTION_RX.exec(line);
    if (s) { current = { name: s[1], lines: [] }; page.sections.push(current); continue; }
    if (current) current.lines.push(line);
    else page.strayLines.push(line);
  }
  return page;
}

// Дата шапки — ISO yyyy-MM-dd (тот формат, что в фикстурах бэка); в UI — Д.М.ГГГГ, как в
// макете. Неразобранное — как написано (не выдумываем дату)
function fmtDate(s: string): string {
  const m = /^(\d{4})-(\d{2})-(\d{2})/.exec(s);
  return m ? `${m[3]}.${m[2]}.${m[1]}` : s;
}

// Строка содержания: [^sN] → надстрочник-маркер с цитатой в title; [[X]] → текст без
// скобок (открытие по ссылке — не этот шаг). Полный markdown-рендер — S5e
function renderLine(line: string, quotes: Map<string, string>, keyBase: string): ReactNode[] {
  const parts: ReactNode[] = [];
  let last = 0;
  let k = 0;
  INLINE_RX.lastIndex = 0;
  let m: RegExpExecArray | null;
  while ((m = INLINE_RX.exec(line))) {
    if (m.index > last) parts.push(line.slice(last, m.index));
    if (m[1] !== undefined) {
      parts.push(
        <sup key={`${keyBase}-fn${k++}`} title={quotes.get(m[1]) ?? m[1]} style={{ color: C.accent, fontSize: FS.xs, fontWeight: 600, whiteSpace: 'nowrap' }}>
          {m[1]}
        </sup>,
      );
    } else {
      parts.push(m[2]);
    }
    last = m.index + m[0].length;
  }
  if (last < line.length) parts.push(line.slice(last));
  return parts;
}

// ─── Куски страницы ───

// Пункт строки: маркер списка («- ») за собой не повторяем
const BULLET_RX = /^[-*]\s+/;

const EMPTY_HINT: Record<string, string> = {
  Противоречия: 'Открытых противоречий нет.',
  История: 'Ничего не замещено.',
};

function SectionBlock({ name, lines, quotes }: {
  name: string;
  lines: string[];
  quotes: Map<string, string>;
}) {
  return (
    <section data-testid="wiki-section" data-section-name={name} style={{ marginTop: SP.lg }}>
      <div style={{ display: 'flex', alignItems: 'baseline', gap: SP.sm }}>
        <h3 style={{ margin: 0, fontFamily: FONT.serif, fontSize: FS.lg, fontWeight: 700, color: C.textHeading }}>{name}</h3>
        {lines.length > 0 && <span style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted }}>{lines.length}</span>}
      </div>
      {lines.length ? (
        <ul style={{ margin: `${SP.sm}px 0 0`, paddingLeft: SP.lg, display: 'flex', flexDirection: 'column', gap: SP.sm }}>
          {lines.map((ln, i) => (
            <li key={i} style={{ fontSize: FS.md, color: C.textPrimary, lineHeight: 1.5 }}>
              {renderLine(ln.replace(BULLET_RX, ''), quotes, `s${i}`)}
            </li>
          ))}
        </ul>
      ) : (
        <div style={{ marginTop: SP.xs, fontSize: FS.sm, color: C.textMuted }}>
          {EMPTY_HINT[name] ?? 'Пока ничего нет.'}
        </div>
      )}
    </section>
  );
}

// «Источники» — блок из определений сносок; отдельно в файле он не хранится (макет)
function SourcesBlock({ footnotes }: { footnotes: ParsedWikiPage['footnotes'] }) {
  return (
    <section data-testid="wiki-sources" style={{ marginTop: SP.lg }}>
      <div style={{ display: 'flex', alignItems: 'baseline', gap: SP.sm }}>
        <h3 style={{ margin: 0, fontFamily: FONT.serif, fontSize: FS.lg, fontWeight: 700, color: C.textHeading }}>Источники</h3>
        <span style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted }}>{footnotes.length}</span>
      </div>
      <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm, marginTop: SP.sm }}>
        {footnotes.map(f => (
          <div key={f.id} style={{ display: 'flex', gap: SP.sm, padding: SP.sm, border: `1px solid ${C.border}`, borderRadius: R.md, background: C.bgCard }}>
            <span style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted, minWidth: 28, flexShrink: 0 }}>{f.id}</span>
            <div style={{ minWidth: 0, flex: 1 }}>
              <div style={{ fontSize: FS.base, fontWeight: 500, color: C.textHeading, overflowWrap: 'break-word' }}>{f.target}</div>
              <div style={{ fontFamily: FONT.serif, fontSize: FS.md, color: C.textSecondary, marginTop: 2 }}>«{f.quote}»</div>
            </div>
          </div>
        ))}
      </div>
      <div style={{ marginTop: SP.xs, fontSize: FS.xs, color: C.textMuted }}>
        Блок собирается из сносок — отдельно в файле он не хранится.
      </div>
    </section>
  );
}

// ─── Страница ───

interface Props {
  section: WikiSectionDto;
  pageKey: string;
  onBack: () => void;
}

export function WikiPageView({ section, pageKey, onBack }: Props) {
  const isMobile = useIsMobile();
  // Страницу и её дрейф читает стор (ревью S5b M2): после adopt он перечитывает страницу,
  // и экран показывает новый ответ бэка, а не локальную копию прежнего
  const dto = useWikiPage(section.key, pageKey);
  const content = dto?.content ?? '';
  const page = useMemo(() => parseWikiPage(content), [content]);
  const [adoptBusy, setAdoptBusy] = useState(false);
  const [adoptError, setAdoptError] = useState<unknown>(null);

  // Дрейф — поле ответа (его считает бэк сверкой файла с манифестом). Шапка status: drift
  // — текст модели, не сигнал: плашка и кнопка по ней не поднимаются (ревью S5b M1)
  const isChronicle = page.type === 'chronicle';
  const st = page.status ? PAGE_STATUS[page.status] : undefined;
  const drift = dto?.drift === true;
  const quotes = new Map(page.footnotes.map(f => [f.id, f.quote]));

  // «О чём» — не секция-заголовок, а строка-описание под заголовком (макет «Хроника»)
  const aboutSection = isChronicle ? page.sections.find(s => s.name === 'О чём') : undefined;
  const bodySections = isChronicle ? page.sections.filter(s => s.name !== 'О чём') : page.sections;
  const aboutText = aboutSection?.lines.map(l => l.replace(BULLET_RX, '')).join(' ') || page.summary;

  // Страницы в сторе нет — её ещё читают: родитель держит свой спиннер, здесь рендерить нечего
  if (!dto) return null;

  const runAdopt = () => {
    setAdoptBusy(true);
    setAdoptError(null);
    // После принятого adopt стор перечитывает страницу (lib/wiki) — плашка и кнопка уходят
    // по drift=false в новом ответе; локального флага «принято» больше нет (ревью S5b M2).
    // Отказ по-прежнему называется, кнопка блокируется на время запроса.
    void adoptWikiPage(section.key, pageKey)
      .catch(e => setAdoptError(e))
      .finally(() => setAdoptBusy(false));
  };

  return (
    <div data-testid="wiki-page" style={{ padding: SP.sm, maxWidth: 760 }}>
      {/* «Назад» — к каталогу раздела. На мобиле — тач-цель ≥ 40 px (урок ревью S5c/S5d) */}
      <Button
        variant="ghost"
        size={isMobile ? 'md' : 'sm'}
        dataTestId="wiki-page-back"
        ariaLabel="Вернуться к каталогу раздела"
        leftIcon={<ChevronLeft size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
        onClick={onBack}
      >
        Назад
      </Button>

      {section.orphan && (
        <Notice tone="warning" icon={AlertTriangle} title="Проект удалён" style={{ marginTop: SP.md }}>
          Раздел скрыт от всех чатов, агентов и персон. Страницы остались: их можно читать.
          Удалить раздел — из каталога, кнопкой «Назад».
        </Notice>
      )}

      {drift && (
        <Notice
          tone="warning"
          icon={AlertTriangle}
          title="Расходится с журналом Хранителя"
          style={{ marginTop: SP.md }}
          action={
            <Button
              variant="secondary"
              size={isMobile ? 'md' : 'sm'}
              dataTestId="wiki-adopt-btn"
              title="Хранитель запомнит текущий файл как верный и запишет в журнал «ручная правка вне Хранителя»"
              loading={adoptBusy}
              onClick={runAdopt}
            >
              Принять как есть
            </Button>
          }
        >
          Файл изменён мимо Хранителя. Пока вы не решите, пакеты по этой странице не
          применяются — ни сами, ни из «Входящих».
        </Notice>
      )}

      {adoptError ? (
        <div data-testid="wiki-adopt-error" style={{ marginTop: SP.sm }}>
          <Notice
            tone="danger"
            icon={AlertTriangle}
            title={adoptError instanceof WikiError ? wikiErrorText(adoptError) : 'Не удалось принять страницу'}
            action={<Button variant="ghost" size="sm" onClick={() => setAdoptError(null)}>Закрыть</Button>}
          >
            Страница осталась как есть — ничего не изменено.
          </Notice>
        </div>
      ) : null}

      {/* Кикер: префикс раздела (тот же форматтер, что у каталога) + тип + статус */}
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.xs, flexWrap: 'wrap', marginTop: SP.md }}>
        <span style={{ fontFamily: FONT.mono, fontSize: FS.sm, color: C.textMuted }}>
          {sectionPrefix(section)}{isChronicle ? ' / Хроники' : ''} /
        </span>
        {isChronicle ? (
          <>
            <ScrollText size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ color: C.textSecondary }} />
            <span style={{ fontSize: FS.sm, color: C.textSecondary }}>хроника · чат</span>
          </>
        ) : (
          <span style={{ fontSize: FS.sm, color: C.textSecondary }}>
            {PAGE_TYPE[page.type ?? ''] ?? 'Страница'}{page.kind ? ` · ${page.kind}` : ''}
          </span>
        )}
        {st && <Badge tone={st.tone} size="xs" title={st.title}>{st.text}</Badge>}
      </div>

      <h2 style={{ margin: `${SP.xs}px 0 0`, fontFamily: FONT.serif, fontSize: FS.xl, fontWeight: 500, color: C.textHeading, lineHeight: 1.25, overflowWrap: 'break-word' }}>
        {page.title ?? pageKey}
      </h2>

      {isChronicle && aboutText ? (
        <p style={{ margin: `${SP.xs}px 0 0`, fontSize: FS.md, color: C.textSecondary, lineHeight: 1.5 }}>
          <b style={{ color: C.textHeading, fontWeight: 600 }}>О чём:</b> {aboutText}
        </p>
      ) : !isChronicle && page.summary ? (
        <p style={{ margin: `${SP.xs}px 0 0`, fontSize: FS.md, color: C.textSecondary, lineHeight: 1.5 }}>
          {page.summary}
        </p>
      ) : null}

      <div style={{ display: 'flex', gap: SP.md, flexWrap: 'wrap', marginTop: SP.sm, fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted }}>
        <span style={{ display: 'inline-flex', alignItems: 'center', gap: 4 }}>
          <Quote size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
          {page.footnotes.length} ист.
        </span>
        {page.updated && <span>обновлено {fmtDate(page.updated)}</span>}
        {page.aliases.length > 0 && <span>aliases: {page.aliases.join(', ')}</span>}
      </div>

      {/* Плашка «только чтение» — тихая строка, не Notice (макет): правки идут через Хранителя */}
      <div data-testid="wiki-readonly" style={{ display: 'flex', gap: SP.sm, alignItems: 'flex-start', marginTop: SP.md, padding: `${SP.sm}px ${SP.md}px`, background: C.bgInset, borderRadius: R.md, fontSize: FS.sm, color: C.textSecondary, lineHeight: 1.45 }}>
        <Lock size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} aria-hidden style={{ flexShrink: 0, marginTop: 2, color: C.textMuted }} />
        <span>
          {isChronicle ? (
            <>
              <b style={{ color: C.textHeading }}>Хроника пишется сама</b> каждую ночь, когда в чате
              появилось новое. Неверное — откатите чат целиком: чат получит пометку «Не разбирать».
            </>
          ) : (
            <>
              <b style={{ color: C.textHeading }}>Только чтение</b> — вики ведёт Хранитель: правки
              идут через него. Неверное утверждение? Откатите его пакет в «Журнале» или весь
              источник по сноске.
            </>
          )}
        </span>
      </div>

      {page.strayLines.length > 0 && (
        <div style={{ marginTop: SP.md, fontSize: FS.md, color: C.textPrimary, lineHeight: 1.5, whiteSpace: 'pre-wrap' }}>
          {page.strayLines.join('\n')}
        </div>
      )}

      {bodySections.map(s => (
        <SectionBlock key={s.name} name={s.name} lines={s.lines} quotes={quotes} />
      ))}

      {page.footnotes.length > 0 && <SourcesBlock footnotes={page.footnotes} />}
    </div>
  );
}
