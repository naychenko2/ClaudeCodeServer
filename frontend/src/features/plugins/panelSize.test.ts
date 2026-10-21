// Размер панели — в БАЙТАХ (TextEncoder), не в символах (минор Глеба, 5а).
// 100 000 русских символов ≈ 200 КиБ байт (2 байта/символ в UTF-8), но .length = 100 000
// (символы). Потолок MAX_PANEL_FILE_BYTES = 256 КиБ: .length не срабатывал (100 000 < 262 144),
// TextEncoder — срабатывает (200 000 < 262 144 — проходит; 262 145+ — отказ).
//
// Мутация (вернуть .length) → красный: 100 000 русских символов проходят по .length,
// но по TextEncoder — 200 000 байт (в пределах потолка).

import { describe, it, expect } from 'vitest';
import { MAX_PANEL_FILE_BYTES } from './bridge';

// Та же логика, что в PluginFrame.tsx / DraftCard.tsx: new TextEncoder().encode(html).length
function panelSizeBytes(html: string): number {
  return new TextEncoder().encode(html).length;
}

describe('Размер панели в байтах (TextEncoder, 5а)', () => {
  it('100 000 русских символов ≈ 200 КиБ байт — проходят (потолок 256 КиБ)', () => {
    // Русский символ в UTF-8 — 2 байта. 100 000 × 2 = 200 000 байт ≈ 195 КиБ < 256 КиБ.
    const html = 'а'.repeat(100_000);
    const bytes = panelSizeBytes(html);
    expect(bytes).toBe(200_000);
    expect(bytes).toBeLessThan(MAX_PANEL_FILE_BYTES);
    // .length (символы) = 100 000 — тоже < 256 КиБ, но по другой причине
    expect(html.length).toBe(100_000);
  });

  it('131 072 русских символа = 262 144 байта = ровно 256 КиБ — проходят', () => {
    const html = 'а'.repeat(131_072);
    const bytes = panelSizeBytes(html);
    expect(bytes).toBe(262_144);
    expect(bytes).toBe(MAX_PANEL_FILE_BYTES);
    expect(bytes > MAX_PANEL_FILE_BYTES).toBe(false);
  });

  it('131 073 русских символа = 262 146 байт > 256 КиБ — отказ «Панель слишком большая»', () => {
    const html = 'а'.repeat(131_073);
    const bytes = panelSizeBytes(html);
    expect(bytes).toBe(262_146);
    expect(bytes > MAX_PANEL_FILE_BYTES).toBe(true);
  });

  it('100 000 ASCII-символов = 100 000 байт (1 байт/символ) — проходят', () => {
    const html = 'a'.repeat(100_000);
    const bytes = panelSizeBytes(html);
    expect(bytes).toBe(100_000);
    expect(bytes).toBeLessThan(MAX_PANEL_FILE_BYTES);
  });
});
