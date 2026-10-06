import { describe, expect, it } from 'vitest';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { splitWatchdogPreview } from '../watchdogPreview';
import { ChatPreviewLine } from '../../components/ChatPreviewLine';

describe('splitWatchdogPreview', () => {
  it('сообщение сторожа — без эмодзи', () => {
    expect(splitWatchdogPreview('⏰ Сторож «Билд»: условие выполнено.'))
      .toEqual({ watchdog: true, text: 'Сторож «Билд»: условие выполнено.' });
  });
  it('с селектором вариации U+FE0F после ⏰ — тоже сторож', () => {
    expect(splitWatchdogPreview('⏰️ Сторож «.NET 10 SDK»: вернулся'))
      .toEqual({ watchdog: true, text: 'Сторож «.NET 10 SDK»: вернулся' });
  });
  it.each([
    'Привет ⏰ Сторож «X»',
    '⏰ напомни завтра',
    '',
  ])('«%s» — не сторож, текст как есть', text => {
    expect(splitWatchdogPreview(text)).toEqual({ watchdog: false, text });
  });
});

describe('ChatPreviewLine', () => {
  const render = (text: string) => renderToStaticMarkup(createElement(ChatPreviewLine, { text }));
  it('сторож — иконка будильника вместо цветного эмодзи', () => {
    const html = render('⏰ Сторож «Билд»: условие выполнено.');
    expect(html).toContain('lucide-alarm-clock');
    expect(html).not.toContain('⏰');
    expect(html).toContain('Сторож «Билд»: условие выполнено.');
  });
  it('обычное сообщение — как было', () => {
    expect(render('давай и задно скачай игру')).toBe('давай и задно скачай игру');
  });
});
