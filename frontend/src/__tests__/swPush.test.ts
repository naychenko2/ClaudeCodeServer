import { describe, it, expect } from 'vitest';
import { notificationTargetUrl } from '../swPush';

// S7: url уведомления пишет и модель через MCP — клик открывает только свой origin
describe('notificationTargetUrl', () => {
  it.each([
    'https://evil.example/phish',
    '//evil.example',
    'javascript:alert(1)',
    '\\\\evil.example',
    '/\\evil.example',
    '/\t/evil.example',
    '#//evil.example',
    'data:text/html,<script>1</script>',
  ])('чужой адрес %s открывает корень приложения', (url) => {
    expect(notificationTargetUrl({ url }, 'https://host.example')).toBe('https://host.example/#');
  });

  it('путь приложения и свой абсолютный адрес открываются как есть', () => {
    const origin = 'https://host.example/';
    expect(notificationTargetUrl({ url: '/chats/42' }, origin)).toBe('https://host.example/#chats/42');
    expect(notificationTargetUrl({ url: '#/chats/7' }, origin)).toBe('https://host.example/#/chats/7');
    expect(notificationTargetUrl({ url: 'https://host.example/#/notes/1' }, origin))
      .toBe('https://host.example/#/notes/1');
  });

  it('без url — корень приложения', () => {
    expect(notificationTargetUrl(undefined, 'https://host.example')).toBe('https://host.example/#');
  });
});
