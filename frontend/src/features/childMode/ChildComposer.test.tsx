// KM-F8: детский композер — только нужные ребёнку контролы. Скрытое проверяем по узлам
// (aria-label/title/data-атрибуты взрослых контролов), видимое — по подписям.

import { describe, it, expect, vi } from 'vitest';
import { act, createElement } from 'react';
import { createRoot } from 'react-dom/client';
import { parseHTML } from 'linkedom';

const { window: win, document: doc } = parseHTML('<!doctype html><html><body><div id="root"></div></body></html>');
Object.assign(win, {
  matchMedia: () => ({
    matches: false, addEventListener: () => {}, removeEventListener: () => {},
    addListener: () => {}, removeListener: () => {},
  }),
});
vi.stubGlobal('window', win);
vi.stubGlobal('document', doc);
vi.stubGlobal('navigator', { language: 'ru-RU', onLine: true, userAgent: 'node' });
vi.stubGlobal('HTMLElement', win.HTMLElement);
vi.stubGlobal('Node', win.Node);
vi.stubGlobal('Event', win.Event);
(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

const { ChildComposer } = await import('./ChildComposer');

describe('ChildComposer', () => {
  it('есть «+», отправка и «Читать ответы вслух»; модели, effort, режима, микрофона и «/» нет', () => {
    const root = createRoot(doc.getElementById('root')!);
    act(() => {
      root.render(createElement(ChildComposer, {
        sessionId: 's1', onSend: () => {}, onStop: () => {}, onAttach: () => {}, isGenerating: false,
        attachments: ['.cc-attachments/s1/a.jpg'], onRemoveAttachment: () => {}, onToggleVoiceMode: () => {},
      }));
    });
    const labels = Array.from(doc.querySelectorAll('[aria-label],[title]'))
      .map(el => `${el.getAttribute('aria-label') ?? ''}|${el.getAttribute('title') ?? ''}`).join('\n');

    expect(labels).toContain('Прикрепить файл');
    expect(labels).toContain('Отправить');
    expect(labels).toContain('Читать ответы вслух');
    expect(doc.querySelector('textarea')).not.toBeNull();
    expect(doc.body.textContent).toContain('a.jpg');

    const hidden = /модел|effort|усили|режим|микрофон|голосов|скилл|Выбрать скилл|персон|собеседник/i;
    expect(labels).not.toMatch(hidden);
    expect(doc.querySelectorAll('[data-composer-actions],[data-composer-input]').length).toBe(0);
    root.unmount();
  });
});
