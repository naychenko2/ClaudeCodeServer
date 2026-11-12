// Тесты плашки «Нужно от тебя / Что мешает» (FS-FY): три вида по ask.source,
// порядок строк, кнопки по видам, отсутствие кнопки без onAnswer, подстрока только
// при personaId, мобильная кнопка (100 % ширины, minHeight 40) и ровно один вызов
// onAnswer по клику. Окружение node: разметка — renderToStaticMarkup, клик — fakeDom.

import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { FeatureAskBlock, ASK_LABEL } from './FeatureAskBlock';
import { mount, installFakeDom, uninstallFakeDom, findNodeByAttr, reactProps } from '../chat/__tests__/fakeDom';
import type { FeatureAsk } from '../../lib/featurePage';

const NAME_OF = (id: string): string => ({ 'p-alex': 'Александр', 'p-denis': 'Денис' })[id] ?? id;

// since — «25 мин назад» от момента ренера: давность в компоненте считается от
// Date.now() (useNow), тест детерминирован по целым минутам.
const ago = (minutes: number): string => new Date(Date.now() - minutes * 60_000).toISOString();

function ask(over: Partial<FeatureAsk> = {}): FeatureAsk {
  return { text: 'Нужен ответ по объёму работ', source: 'wait', since: ago(25), personaId: 'p-alex', ...over };
}

const html = (a: FeatureAsk | null, isMobile = false, onAnswer?: () => void): string =>
  renderToStaticMarkup(createElement(FeatureAskBlock, { ask: a, nameOf: NAME_OF, isMobile, onAnswer }));

// Кнопка в разметке — нативный <button> (implicit role=button): их и считаем.
const buttonCount = (h: string): number => (h.match(/<button/g) ?? []).length;

describe('FeatureAskBlock', () => {
  it('ask = null: спокойный вид — «От тебя», «Ничего не нужно — работа идёт», 0 кнопок', () => {
    const h = html(null);
    expect(h).toContain('От тебя');
    expect(h).toContain('Ничего не нужно — работа идёт');
    expect(h).not.toContain(ASK_LABEL.need);
    expect(h).not.toContain(ASK_LABEL.blocker);
    expect(buttonCount(h)).toBe(0);
    expect(h).toContain('data-tone="calm"');
  });

  it('source = wait: «Нужно от тебя», фраза, «Александр ждёт 25 мин», «Ответить Александру»', () => {
    const h = html(ask(), false, () => {});
    expect(h).toContain(ASK_LABEL.need);
    expect(h).toContain('Нужен ответ по объёму работ');
    expect(h).toContain('Александр ждёт 25 мин');
    expect(h).toContain('Ответить Александру');
    expect(h).toContain('data-tone="warning"');
  });

  it('source = merge: «Нужно от тебя» и «Решить по мержу»', () => {
    const h = html(ask({ source: 'merge', text: 'Фича ждёт решения по мержу' }), false, () => {});
    expect(h).toContain(ASK_LABEL.need);
    expect(h).toContain('Решить по мержу');
    expect(h).not.toContain(ASK_LABEL.blocker);
    expect(h).toContain('data-tone="warning"');
  });

  it('source = stopped: «Что мешает», «Денис стоит 40 мин», «Открыть чат Дениса»', () => {
    const h = html(ask({ source: 'stopped', text: 'Денис не стартует: движок недоступен', since: ago(40), personaId: 'p-denis' }), false, () => {});
    expect(h).toContain(ASK_LABEL.blocker);
    expect(h).toContain('Денис не стартует: движок недоступен');
    expect(h).toContain('Денис стоит 40 мин');
    expect(h).toContain('Открыть чат Дениса');
    expect(h).not.toContain(ASK_LABEL.need);
    expect(h).toContain('data-tone="danger"');
  });

  it('без onAnswer при wait: 0 элементов role=button (кнопки нет вовсе)', () => {
    const h = html(ask());
    expect(buttonCount(h)).toBe(0);
    // текст и подстрока остаются
    expect(h).toContain('Нужен ответ по объёму работ');
    expect(h).toContain('Александр ждёт 25 мин');
  });

  it('нет personaId: подстроки нет (имени нет в разметке); у wait кнопка «Открыть чат»', () => {
    const h = html(ask({ personaId: null }), false, () => {});
    // Точнее: имя не встречается нигде — ни в подстроке, ни в кнопке
    // («ждёт» по всей разметке ловило бы и законный текст фразы с этим словом).
    expect(h).not.toContain('Александр');
    expect(h).toContain('Открыть чат');
    expect(h).not.toContain('Ответить');
  });

  it('нет since: подстрока ровно «Александр ждёт», без «null»/«undefined»', () => {
    const h = html(ask({ since: null }), false, () => {});
    // Точный текст подстроки целиком: она обрывается сразу после «ждёт» —
    // «Александр ждёт null» (снятый guard давности) сюда не попадает.
    expect(h).toContain('Александр ждёт</div>');
    expect(h).not.toContain('null');
    expect(h).not.toContain('undefined');
  });

  it('мобила: кнопка на 100 % ширины и min-height 40; десктоп — без этих свойств', () => {
    const mob = html(ask(), true, () => {});
    expect(mob).toContain('width:100%');
    expect(mob).toContain('min-height:40px');
    const desk = html(ask(), false, () => {});
    expect(desk).not.toContain('width:100%');
    expect(desk).not.toContain('min-height:40px');
  });

  it('ASK_LABEL — общий словарь: ровно «Нужно от тебя» и «Что мешает»', () => {
    expect(ASK_LABEL.need).toBe('Нужно от тебя');
    expect(ASK_LABEL.blocker).toBe('Что мешает');
  });
});

describe('FeatureAskBlock: клик', () => {
  beforeEach(() => { installFakeDom(); });
  afterEach(() => { uninstallFakeDom(); });

  it('клик кнопки — ровно один вызов onAnswer', () => {
    const onAnswer = vi.fn();
    const { container } = mount(createElement(FeatureAskBlock, { ask: ask(), nameOf: NAME_OF, isMobile: false, onAnswer }));
    const btn = findNodeByAttr(container, 'data-testid', 'feature-ask-answer');
    expect(btn).not.toBeNull();
    (reactProps(btn!)!.onClick as (e: unknown) => void)({});
    expect(onAnswer).toHaveBeenCalledTimes(1);
  });
});
