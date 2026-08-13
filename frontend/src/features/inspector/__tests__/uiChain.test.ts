import { describe, it, expect } from 'vitest';
import { buildChatPrompt, buildUiChain, defaultChainIndex, srcFile, type ChainNode } from '../uiChain';

// Фейковые DOM-узлы (утиный тип ChainNode) — vitest в проекте живёт в node-окружении
// без jsdom, конвенция — как chatReadState.test.ts
function node(attrs: Record<string, string>, opts?: {
  tag?: string; text?: string; parent?: ChainNode | null;
}): ChainNode {
  return {
    tagName: (opts?.tag ?? 'div').toUpperCase(),
    parentElement: opts?.parent ?? null,
    textContent: opts?.text ?? '',
    getAttribute: (name: string) => attrs[name] ?? null,
  };
}

describe('buildUiChain: подъём по parentElement со сбором data-cc-src', () => {
  it('собирает цепочку от глубокого к корню, пропуская элементы без атрибута', () => {
    const root = node({ 'data-cc-src': 'frontend/src/App.tsx:100' });
    const page = node({ 'data-cc-src': 'frontend/src/pages/WorkspacePage.tsx:88' }, { parent: root });
    const bare = node({}, { parent: page });   // элемент без атрибута — просто шаг вверх
    const leaf = node({ 'data-cc-src': 'frontend/src/components/Composer.tsx:214' }, { tag: 'button', parent: bare });
    expect(buildUiChain(leaf).map(l => l.src)).toEqual([
      'frontend/src/components/Composer.tsx:214',
      'frontend/src/pages/WorkspacePage.tsx:88',
      'frontend/src/App.tsx:100',
    ]);
  });

  it('дедуплицирует подряд идущие одинаковые src (вложенность одного компонента)', () => {
    const root = node({ 'data-cc-src': 'frontend/src/App.tsx:10' });
    const outer = node({ 'data-cc-src': 'frontend/src/components/Card.tsx:5' }, { parent: root });
    const inner = node({ 'data-cc-src': 'frontend/src/components/Card.tsx:5' }, { parent: outer });
    expect(buildUiChain(inner).map(l => l.src)).toEqual([
      'frontend/src/components/Card.tsx:5',
      'frontend/src/App.tsx:10',
    ]);
  });

  it('одинаковые src НЕ подряд не склеиваются (компонент внутри самого себя через прослойку)', () => {
    const top = node({ 'data-cc-src': 'frontend/src/components/Tree.tsx:7' });
    const mid = node({ 'data-cc-src': 'frontend/src/components/Row.tsx:3' }, { parent: top });
    const leaf = node({ 'data-cc-src': 'frontend/src/components/Tree.tsx:7' }, { parent: mid });
    expect(buildUiChain(leaf).map(l => l.src)).toEqual([
      'frontend/src/components/Tree.tsx:7',
      'frontend/src/components/Row.tsx:3',
      'frontend/src/components/Tree.tsx:7',
    ]);
  });

  it('пропускает собственный UI инспектора (data-cc-inspector)', () => {
    const root = node({ 'data-cc-src': 'frontend/src/App.tsx:10' });
    const own = node({
      'data-cc-inspector': '1',
      'data-cc-src': 'frontend/src/features/inspector/UiInspectorOverlay.tsx:40',
    }, { parent: root });
    const leaf = node({ 'data-cc-src': 'frontend/src/components/Composer.tsx:214' }, { parent: own });
    expect(buildUiChain(leaf).map(l => l.src)).toEqual([
      'frontend/src/components/Composer.tsx:214',
      'frontend/src/App.tsx:10',
    ]);
  });

  it('label: тег + aria-label приоритетнее текста, текст режется до 60 символов', () => {
    const byAria = node({ 'data-cc-src': 'a.tsx:1', 'aria-label': 'Отправить' }, { tag: 'button', text: 'игнор' });
    expect(buildUiChain(byAria)[0].label).toBe('button · Отправить');
    const long = node({ 'data-cc-src': 'b.tsx:2' }, { text: 'x'.repeat(80) });
    expect(buildUiChain(long)[0].label).toBe(`div · ${'x'.repeat(60)}`);
    const empty = node({ 'data-cc-src': 'c.tsx:3' }, { tag: 'span', text: '  ' });
    expect(buildUiChain(empty)[0].label).toBe('span');
  });

  it('пустой вход и цепочка без атрибутов дают пустой список', () => {
    expect(buildUiChain(null)).toEqual([]);
    expect(buildUiChain(node({}, { parent: node({}) }))).toEqual([]);
  });
});

describe('defaultChainIndex: дефолт — самый глубокий src вне components/ui', () => {
  it('пропускает примитивы ui-кита', () => {
    const levels = [
      { src: 'frontend/src/components/ui/Button.tsx:62', label: 'button' },
      { src: 'frontend/src/components/Composer.tsx:214', label: 'div' },
      { src: 'frontend/src/App.tsx:100', label: 'div' },
    ];
    expect(defaultChainIndex(levels)).toBe(1);
  });

  it('вся цепочка из примитивов — берёт самый глубокий', () => {
    const levels = [
      { src: 'frontend/src/components/ui/Modal.tsx:90', label: 'div' },
      { src: 'frontend/src/components/ui/Button.tsx:62', label: 'button' },
    ];
    expect(defaultChainIndex(levels)).toBe(0);
  });
});

describe('srcFile: путь для frontmatter file: без номера строки', () => {
  it('срезает завершающее :line и не трогает путь без него', () => {
    expect(srcFile('frontend/src/components/Composer.tsx:214')).toBe('frontend/src/components/Composer.tsx');
    expect(srcFile('frontend/src/App.tsx')).toBe('frontend/src/App.tsx');
  });
});

describe('buildChatPrompt: затравка чата про элемент', () => {
  // Цепочка — от глубокого к корню, как её отдаёт buildUiChain
  const chain = [
    { src: 'frontend/src/components/Composer.tsx:214', label: 'button · Отправить' },
    { src: 'frontend/src/pages/WorkspacePage.tsx:88', label: 'div' },
  ];

  it('контекст (уровень, цепочка от корня к глубокому, экран) + комментарий через пустую строку', () => {
    expect(buildChatPrompt(chain[0], chain, '#/project/p1', '  Почему кнопка серая?  ')).toBe([
      'Про элемент интерфейса: frontend/src/components/Composer.tsx:214',
      'Цепочка: frontend/src/pages/WorkspacePage.tsx:88 > frontend/src/components/Composer.tsx:214',
      'Экран: #/project/p1',
      '',
      'Почему кнопка серая?',
    ].join('\n'));
  });

  it('уровень может быть любым из цепочки, не только глубоким', () => {
    expect(buildChatPrompt(chain[1], chain, '#/home', 'вся панель')).toContain(
      'Про элемент интерфейса: frontend/src/pages/WorkspacePage.tsx:88');
  });

  it('пустой/пробельный комментарий — только контекст, без висячих пустых строк', () => {
    expect(buildChatPrompt(chain[0], chain, '#/home', '   ')).toBe([
      'Про элемент интерфейса: frontend/src/components/Composer.tsx:214',
      'Цепочка: frontend/src/pages/WorkspacePage.tsx:88 > frontend/src/components/Composer.tsx:214',
      'Экран: #/home',
    ].join('\n'));
  });
});
