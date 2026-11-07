// UserAvatar: цвет — детерминированный по userId, инициал — из displayName/username,
// «бывший участник» — нейтральный вид (bgInset/textMuted). Среда — node, без window.
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import { C, GROUP_COLORS } from '../../../lib/design';
import { UserAvatar, userAvatarColor } from '../UserAvatar';

const render = (props: Record<string, unknown>) =>
  renderToStaticMarkup(createElement(UserAvatar, props));

describe('UserAvatar', () => {
  it('один userId — один тон на 1000 повторов, тон из существующей палитры', () => {
    const tones = new Set<string>();
    for (let i = 0; i < 1000; i++) tones.add(userAvatarColor('user-42'));
    expect(tones.size).toBe(1);
    const tone = [...tones][0];
    expect(GROUP_COLORS).toContain(tone);
    // и рендер несёт ровно этот тон, а не пересчитанный заново
    expect(render({ userId: 'user-42', displayName: 'Мария' })).toContain(`background:${tone}`);
  });

  it('разные id распределяются по нескольким тонам (хеш не константа)', () => {
    const tones = new Set<string>();
    for (let i = 0; i < 100; i++) tones.add(userAvatarColor(`user-${i}`));
    expect(tones.size).toBeGreaterThan(1);
  });

  it('бывший участник — bgInset/textMuted, цвет id не несёт', () => {
    const html = render({ userId: 'user-42', displayName: 'Мария', gone: true });
    expect(html).toContain(`background:${C.bgInset}`);
    expect(html).toContain(`color:${C.textMuted}`);
    expect(html).not.toContain(userAvatarColor('user-42'));
  });

  it('инициал — первая буква displayName, иначе username, иначе «·»; в верхнем регистре', () => {
    expect(render({ userId: 'u', displayName: 'мария иванов' })).toContain('>М</span>');
    expect(render({ userId: 'u', displayName: '  ', username: 'misha' })).toContain('>M</span>');
    expect(render({ userId: 'u' })).toContain('>·</span>');
  });

  it('aria-label — имя участника', () => {
    expect(render({ userId: 'u', displayName: 'Мария Иванова' })).toContain('aria-label="Мария Иванова"');
    expect(render({ userId: 'u', username: 'misha' })).toContain('aria-label="misha"');
  });

  it('размеры 18/20/28/34, по умолчанию 20', () => {
    for (const size of [18, 20, 28, 34] as const) {
      const html = render({ userId: 'u', displayName: 'Мария', size });
      expect(html).toContain(`width:${size}px`);
      expect(html).toContain(`height:${size}px`);
    }
    expect(render({ userId: 'u', displayName: 'Мария' })).toContain('width:20px');
  });
});
