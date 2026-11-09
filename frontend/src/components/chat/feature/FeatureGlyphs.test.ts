// Глиф queue (макет feature-queue-1, «Тег и глиф»): круг r 7 как у hold + три полосы.
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import { FeatureGlyph } from './FeatureGlyphs';

describe('FeatureGlyph queue', () => {
  const html = renderToStaticMarkup(createElement(FeatureGlyph, { kind: 'queue', size: 14 }));

  it('viewBox 16×16, размер из пропа, круг r 7 с заливкой bgInset и обводкой textMuted 1', () => {
    expect(html).toContain('viewBox="0 0 16 16"');
    expect(html).toContain('width="14"');
    expect(html).toContain('<circle cx="8" cy="8" r="7" fill="var(--c-bg-inset)" stroke="var(--c-text-muted)" stroke-width="1"></circle>');
  });

  it('три полосы 6.8×1.6 rx 0.8: верхняя textSecondary, две нижние textMuted', () => {
    const bars = [...html.matchAll(/<rect x="4.6" y="([\d.]+)" width="6.8" height="1.6" rx="0.8" fill="([^"]+)"/g)]
      .map(m => [m[1], m[2]]);
    expect(bars).toEqual([
      ['4.4', 'var(--c-text-secondary)'],
      ['7.2', 'var(--c-text-muted)'],
      ['10', 'var(--c-text-muted)'],
    ]);
  });
});
