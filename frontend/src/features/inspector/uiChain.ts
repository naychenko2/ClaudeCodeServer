// Цепочка исходников для UI-инспектора: подъём от кликнутого элемента к корню DOM
// с сбором data-cc-src (проставляет scripts/babel-cc-src.mjs). Чистая логика на
// утином типе узла — тестируется в node-окружении vitest без jsdom на фейковых
// объектах (конвенция — как chatReadState.test.ts).

// Минимальный контракт DOM-узла: ровно то, что нужно для подъёма
export interface ChainNode {
  tagName: string;
  parentElement: ChainNode | null;
  getAttribute(name: string): string | null;
  textContent?: string | null;
}

export interface ChainLevel {
  src: string;    // «frontend/src/…/X.tsx:214» — путь от корня репы + строка
  label: string;  // человекочитаемая метка: тег + aria-label/текст
}

const LABEL_MAX = 60;

function labelOf(node: ChainNode): string {
  const tag = node.tagName.toLowerCase();
  const text = (node.getAttribute('aria-label') || node.textContent || '')
    .trim().replace(/\s+/g, ' ');
  return text ? `${tag} · ${text.slice(0, LABEL_MAX)}` : tag;
}

// От глубокого (кликнутый элемент) к корню. Дедуп подряд идущих одинаковых src —
// вложенные элементы одного компонента дают одну запись (метка — самого глубокого).
// Собственный UI инспектора (data-cc-inspector) пропускаем.
export function buildUiChain(start: ChainNode | null): ChainLevel[] {
  const levels: ChainLevel[] = [];
  for (let node = start; node; node = node.parentElement) {
    if (node.getAttribute('data-cc-inspector') != null) continue;
    const src = node.getAttribute('data-cc-src');
    if (!src) continue;
    if (levels.length > 0 && levels[levels.length - 1].src === src) continue;
    levels.push({ src, label: labelOf(node) });
  }
  return levels;
}

// Дефолтный уровень формы: самый глубокий src ВНЕ примитивов components/ui —
// клик по кнопке должен указывать на место использования, а не на Button.tsx.
// Вся цепочка из примитивов — берём самый глубокий как есть.
export function defaultChainIndex(levels: ChainLevel[]): number {
  const i = levels.findIndex(l => !l.src.includes('src/components/ui/'));
  return i >= 0 ? i : 0;
}

// Путь для frontmatter file: — без «:строка», иначе бэкенд посчитает файл отсутствующим
export function srcFile(src: string): string {
  return src.replace(/:\d+$/, '');
}

// Затравка «в чат» про элемент: контекст (выбранный уровень, цепочка от корня к глубокому —
// как ui_chain заметки, экран) + комментарий пользователя через пустую строку (если есть).
// Чистая функция под юнит-тест в node-окружении.
export function buildChatPrompt(
  level: ChainLevel, chain: ChainLevel[], route: string, comment: string,
): string {
  const chainLine = [...chain].reverse().map(l => l.src).join(' > ');
  const lines = [
    `Про элемент интерфейса: ${level.src}`,
    `Цепочка: ${chainLine}`,
    `Экран: ${route}`,
  ];
  const c = comment.trim();
  if (c) lines.push('', c);
  return lines.join('\n');
}
