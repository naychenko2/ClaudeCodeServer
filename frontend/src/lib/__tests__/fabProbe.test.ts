import { describe, it, expect } from 'vitest';
import { ignoresControlIn, type ProbeNode, type ProbeStyle } from '../ai/fabProbe';

// Какие контролы под кандидатом места круглешка AI не в счёт. DOM-окружения у vitest в
// проекте нет — цепочку предков собираем из макетов узлов со своими стилями
type Node = ProbeNode & { style: Partial<ProbeStyle> };
const node = (parent: Node | null, style: Partial<ProbeStyle> = {}, size?: { scroll: number; client: number }): Node => ({
  parentElement: parent, style, scrollHeight: size?.scroll ?? 0, clientHeight: size?.client ?? 0,
});
const styleOf = (n: Node): ProbeStyle => ({ opacity: '1', position: 'static', overflowY: 'visible', ...n.style });
const ignores = (el: Node, root: Node) => ignoresControlIn(el, styleOf, root);

describe('fabProbe: какие контролы кнопке не мешают', () => {
  const body = node(null);
  const scroller = (overflow = true) => node(body, { overflowY: 'auto' }, { scroll: overflow ? 1000 : 200, client: 200 });

  it('строка прокручиваемого списка — не в счёт, её отлистывают', () => {
    expect(ignores(node(node(scroller())), body)).toBe(true);
  });

  it('контейнер не переполнен — контрол прибит, в счёт', () => {
    expect(ignores(node(scroller(false)), body)).toBe(false);
  });

  it('липкий футер шторки внутри скроллера — прибит, в счёт', () => {
    const footer = node(scroller(), { position: 'sticky' });
    expect(ignores(node(footer), body)).toBe(false);
  });

  it('fixed-контрол внутри скроллера — прибит, в счёт', () => {
    expect(ignores(node(scroller(), { position: 'fixed' }), body)).toBe(false);
  });

  it('прозрачный контрол или прозрачный предок (действия строки по наведению) — не в счёт', () => {
    expect(ignores(node(body, { opacity: '0' }), body)).toBe(true);
    expect(ignores(node(node(body, { opacity: '0' })), body)).toBe(true);
    // Прозрачный предок выше скроллера тоже прячет контрол (модалка гаснет)
    const fading = node(body, { opacity: '0' });
    const sc = node(fading, { overflowY: 'auto' }, { scroll: 1000, client: 200 });
    expect(ignores(node(sc, { position: 'sticky' }), body)).toBe(true);
  });

  it('видимый контрол вне скроллеров — в счёт', () => {
    expect(ignores(node(node(body)), body)).toBe(false);
  });
});
