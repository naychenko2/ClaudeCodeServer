import { describe, it, expect } from 'vitest';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
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

  // Юникод-слеши и percent-encoding: парсер URL их в «/» не превращает, поэтому путь
  // остаётся внутри hash своего приложения, а всё прочее — корень приложения
  it.each([
    ['/∕evil', 'https://host.example/#∕evil'],
    ['/／evil', 'https://host.example/#／evil'],
    ['／／evil', 'https://host.example/#'],
    ['%2F%2Fevil', 'https://host.example/#'],
    ['/%5Cevil', 'https://host.example/#%5Cevil'],
  ])('обфусцированный адрес %s не уводит со своего origin', (url, expected) => {
    const target = notificationTargetUrl({ url }, 'https://host.example');
    expect(target).toBe(expected);
    expect(new URL(target).origin).toBe('https://host.example');
  });
});

// Тост уведомления не навигирует сам: запасной window.location.assign(t.url) обходил
// notificationTargetUrl. Навигация — только через обязательный onNavigate (S7)
describe('NotificationToasts', () => {
  const src = readFileSync(fileURLToPath(new URL('../components/NotificationToasts.tsx', import.meta.url)), 'utf8');

  it('не открывает url уведомления в обход проверки origin', () => {
    expect(src).not.toMatch(/location\.(assign|replace|href)|window\.open/);
  });

  it('onNavigate обязателен', () => {
    expect(src).toMatch(/\{\s*onNavigate\s*\}:\s*\{\s*onNavigate:\s*\(/);
  });
});
