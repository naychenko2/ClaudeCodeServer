import { describe, expect, it } from 'vitest';
import { compactStack, genPanelInZone, GEN_STACK_W } from './genPanelPlacement';

describe('панели генерации в планшетной зоне', () => {
  it('800–1019: «Контекст» колонкой в потоке, обычная панель — ящиком поверх', () => {
    expect(compactStack(800, 340, ['chatContext'])).toEqual({ inline: true, width: GEN_STACK_W });
    expect(compactStack(900, 340, ['files']).inline).toBe(false);
    expect(compactStack(900, 340, ['chatContext', 'files']).inline).toBe(false);
  });

  it('с 1020 любая панель в потоке своей шириной', () => {
    expect(compactStack(1024, 340, ['chatContext'])).toEqual({ inline: true, width: 340 });
    expect(compactStack(1024, 340, ['files'])).toEqual({ inline: true, width: 340 });
  });

  it('ниже 800 панели генерации в зоне нет — её рисует шторка', () => {
    expect(compactStack(799, 340, ['chatContext']).inline).toBe(false);
    expect(genPanelInZone('chatContext', true, 799)).toBe(false);
    expect(genPanelInZone('chatContext', true, 800)).toBe(true);
    expect(genPanelInZone('files', true, 700)).toBe(true);
    expect(genPanelInZone('chatContext', false, 700)).toBe(true);
  });
});
