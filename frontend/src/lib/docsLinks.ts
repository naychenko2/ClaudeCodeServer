// Разбор ссылок и якорей документации проекта (панель «Документы»).
//
// Модуль намеренно чистый (без React и DOM): это единственная часть фронтовой логики
// панели, которую можно покрыть тестами — vitest здесь гоняется в окружении node.
//
// Слагификация ПОВТОРЯЕТ серверную (Services/Docs/DocsIndexService.Slugify) — это контракт
// между бэком и панелью. Бэк считает слаг от текста заголовка, очищенного от markdown,
// фронт — от textContent узла, где разметки уже нет; при расхождении алгоритмов переход
// по «foo.md#раздел» перестал бы находить цель.

export type DocLinkKind = 'doc' | 'repo' | 'external';

export interface ResolvedDocLink {
  kind: DocLinkKind;
  // Для doc/repo — путь от корня проекта с прямыми слэшами; для external — исходный URL
  target: string;
  anchor: string | null;
}

// Текст без markdown-разметки: картинки и ссылки схлопываются в подпись, выделение снимается
export function stripMarkdown(text: string): string {
  return text
    .replace(/!\[([^\]]*)\]\([^)]*\)/g, '$1')
    .replace(/\[([^\]]*)\]\([^)]*\)/g, '$1')
    .replace(/`/g, '')
    .replace(/\*\*|__|\*|_|~~/g, '')
    .trim();
}

// Слаг якоря: нижний регистр, разделители в дефис, прочая пунктуация отброшена.
// Буквы любых алфавитов сохраняются — заголовки в проекте русские.
export function slugify(headingText: string): string {
  const cleaned = stripMarkdown(headingText).toLowerCase();
  let out = '';
  for (const ch of cleaned) {
    if (/[\p{L}\p{N}]/u.test(ch)) out += ch;
    else if (ch === ' ' || ch === '\t' || ch === '-' || ch === '_' || ch === '.' || ch === '/') out += '-';
  }
  return out.replace(/-{2,}/g, '-').replace(/^-+|-+$/g, '');
}

// «foo.md#раздел» → ['foo.md', 'раздел']; якорь нормализуется тем же слагом, потому что
// в доках его пишут и словами, и готовым слагом.
// Декодирование обязательно: remark отдаёт href уже процент-энкодленным, и кириллический
// якорь приезжает как «%D1%81%D1%80%D0%BE%D0%BA». Без decode слаг превращался в мусор,
// заголовок по нему не находился, и переход по ссылке молча открывал документ с начала.
export function splitAnchor(target: string): [string, string | null] {
  const i = target.indexOf('#');
  if (i < 0) return [target, null];
  const raw = target.slice(i + 1);
  let decoded: string;
  try { decoded = decodeURIComponent(raw); }
  catch { decoded = raw; }   // битая %-последовательность — слагифицируем как есть
  const anchor = slugify(decoded);
  return [target.slice(0, i), anchor.length === 0 ? null : anchor];
}

export function isExternal(target: string): boolean {
  return /^(https?:\/\/|mailto:|\/\/)/i.test(target);
}

// Путь ссылки относительно документа-источника → путь от корня проекта.
// null — ссылка уводит выше корня проекта.
export function resolveRelative(fromDoc: string, target: string): string | null {
  let decoded: string;
  try { decoded = decodeURIComponent(target.replace(/\\/g, '/')); }
  catch { decoded = target.replace(/\\/g, '/'); }   // битая %-последовательность — берём как есть

  const slash = fromDoc.lastIndexOf('/');
  const baseDir = slash < 0 ? '' : fromDoc.slice(0, slash);
  const combined = decoded.startsWith('/')
    ? decoded.replace(/^\/+/, '')
    : baseDir ? `${baseDir}/${decoded}` : decoded;

  const segments: string[] = [];
  for (const seg of combined.split('/')) {
    if (seg === '' || seg === '.') continue;
    if (seg === '..') {
      if (segments.length === 0) return null;   // выше корня проекта
      segments.pop();
      continue;
    }
    segments.push(seg);
  }
  return segments.length === 0 ? null : segments.join('/');
}

// Картинка документа → путь файла в проекте. null — грузить как есть: внешние адреса,
// data:/blob: и всё, что уводит выше корня. Общий для панели и центральной области:
// один и тот же README рендерится в обоих, и расходиться им незачем.
export function resolveDocImage(fromDoc: string, src: string): string | null {
  if (!src || /^(https?:|data:|blob:|\/\/)/i.test(src)) return null;
  return resolveRelative(fromDoc, src);
}

// Куда ведёт ссылка, кликнутая в превью документа. knownDocs — пути документов области
// (нижним регистром): по ним отличаем переход внутри панели от открытия файла в центре.
export function resolveDocLink(
  fromDoc: string, href: string, knownDocs: ReadonlySet<string>,
): ResolvedDocLink | null {
  if (!href) return null;
  if (isExternal(href)) return { kind: 'external', target: href, anchor: null };

  const [path, anchor] = splitAnchor(href);
  // Ссылка-якорь внутри текущего документа
  if (path === '') return anchor ? { kind: 'doc', target: fromDoc, anchor } : null;

  const target = resolveRelative(fromDoc, path);
  if (target === null) return null;
  return {
    kind: knownDocs.has(target.toLowerCase()) ? 'doc' : 'repo',
    target,
    anchor,
  };
}

// Раздел документа для цитаты в чат: от заголовка со слагом slug до следующего
// заголовка того же или более высокого уровня. Режем ИСХОДНЫЙ markdown, а не текст из
// DOM: иначе в цитату уедет содержимое без кодоблоков, таблиц и списков.
export function sliceSection(markdown: string, slug: string): string | null {
  const lines = markdown.split('\n');
  let start = -1;
  let level = 0;
  let inFence = false;

  for (let i = 0; i < lines.length; i++) {
    const line = lines[i].replace(/\r$/, '');
    if (/^ {0,3}(`{3,}|~{3,})/.test(line)) { inFence = !inFence; continue; }
    if (inFence) continue;

    const m = /^ {0,3}(#{1,6})\s+(.+?)\s*#*\s*$/.exec(line);
    if (!m) continue;

    if (start < 0) {
      if (slugify(m[2]) !== slug) continue;
      start = i;
      level = m[1].length;
      continue;
    }
    if (m[1].length <= level) return lines.slice(start, i).join('\n').trim();
  }

  return start < 0 ? null : lines.slice(start).join('\n').trim();
}
