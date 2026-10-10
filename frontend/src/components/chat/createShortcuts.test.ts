// Входы «＋» и пустой ленты от `create` видов контекста (ADR-023, шаг 2к-2).
import { describe, expect, it, vi } from 'vitest';

vi.stubGlobal('window', Object.assign(new EventTarget(), {
  innerWidth: 1440, innerHeight: 900,
  matchMedia: () => ({ matches: false, addEventListener: () => {}, removeEventListener: () => {} }),
}));

const { createShortcuts, shortcutOverflowItems } = await import('./composerShortcuts');
type Kinds = Parameters<typeof createShortcuts>[0];

const run = vi.fn();
const image = { name: 'image', order: 1, action: { kinds: ['image'], create: { title: 'Картинка', hint: 'черновик', icon: null, run } } } as unknown as Kinds[number];
const audio = { name: 'audio', action: { kinds: ['audio'] } } as unknown as Kinds[number];
const o = { projectId: 'p', sessionId: 's1', isMobile: false };

describe('createShortcuts', () => {
  it('вид с create даёт ярлык, вид без create — нет; запуск получает контекст чата', () => {
    const list = createShortcuts([audio, image], o);
    expect(list.map(s => s.key)).toEqual(['create:image']);
    expect(list[0]).toMatchObject({ title: 'Картинка', hint: 'черновик' });
    list[0].onSelect();
    expect(run).toHaveBeenCalledWith({ projectId: 'p', sessionId: 's1', isMobile: false });
  });

  it('без чата ярлыков нет: черновику некуда лечь', () => {
    expect(createShortcuts([image], { ...o, sessionId: null })).toEqual([]);
  });
});

describe('shortcutOverflowItems', () => {
  const list = createShortcuts([image], o);

  it('«＋» ушла с полосы — ярлыки строками «⋯», клик заводит объект', () => {
    const items = shortcutOverflowItems(list, false);
    expect(items).toMatchObject([{ key: 'create:image', label: 'Картинка', sublabel: 'черновик' }]);
    run.mockClear();
    items[0].onClick();
    expect(run).toHaveBeenCalledTimes(1);
  });

  it('«＋» стоит в ряду — в «⋯» не дублируем: ярлыки живут в её меню', () => {
    expect(shortcutOverflowItems(list, true)).toEqual([]);
  });
});
