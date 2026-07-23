import { describe, it, expect } from 'vitest';
import {
  parseWidgetInput, buildWidgetSrcDoc, clampWidgetHeight,
  WIDGET_MIN_HEIGHT, WIDGET_MAX_HEIGHT, WIDGET_MAX_HEIGHT_MOBILE, WIDGET_HEIGHT_MSG,
} from '../widgetHtml';
import { isWidgetShow } from '../../components/chat/WidgetView';

// --- parseWidgetInput: defensive-разбор input вызова widget_show ---

describe('parseWidgetInput', () => {
  it('валидный input разбирается целиком', () => {
    const r = parseWidgetInput({ html: '<b>hi</b>', title: 'Дашборд', height: 400 });
    expect(r).toEqual({ html: '<b>hi</b>', title: 'Дашборд', height: 400 });
  });

  it('мусор вместо объекта → пустые значения', () => {
    expect(parseWidgetInput(null)).toEqual({ html: '', title: '', height: null });
    expect(parseWidgetInput('строка')).toEqual({ html: '', title: '', height: null });
    expect(parseWidgetInput(42)).toEqual({ html: '', title: '', height: null });
  });

  it('нестроковый html и title игнорируются', () => {
    const r = parseWidgetInput({ html: 123, title: { a: 1 }, height: 'big' });
    expect(r).toEqual({ html: '', title: '', height: null });
  });

  it('height клампится в допустимые пределы', () => {
    expect(parseWidgetInput({ html: 'x', height: 10 }).height).toBe(WIDGET_MIN_HEIGHT);
    expect(parseWidgetInput({ html: 'x', height: 5000 }).height).toBe(WIDGET_MAX_HEIGHT);
    expect(parseWidgetInput({ html: 'x', height: NaN }).height).toBeNull();
  });
});

// --- clampWidgetHeight: пределы десктопа и мобилы ---

describe('clampWidgetHeight', () => {
  it('мобильный предел ниже десктопного', () => {
    expect(clampWidgetHeight(9999)).toBe(WIDGET_MAX_HEIGHT);
    expect(clampWidgetHeight(9999, true)).toBe(WIDGET_MAX_HEIGHT_MOBILE);
    expect(clampWidgetHeight(0)).toBe(WIDGET_MIN_HEIGHT);
  });
});

// --- buildWidgetSrcDoc: CSP, тема, авто-высота, вставка html ---

describe('buildWidgetSrcDoc', () => {
  it('содержит строгую CSP-мету (без сети) и form-action', () => {
    const doc = buildWidgetSrcDoc('<b>w</b>', 'light');
    expect(doc).toContain('Content-Security-Policy');
    expect(doc).toContain("default-src 'none'");
    expect(doc).toContain("form-action 'none'");
  });

  it('вставляет html модели в body', () => {
    expect(buildWidgetSrcDoc('<b>маркер</b>', 'light')).toContain('<b>маркер</b>');
  });

  it('темы дают разные CSS-переменные и color-scheme', () => {
    const light = buildWidgetSrcDoc('x', 'light');
    const dark = buildWidgetSrcDoc('x', 'dark');
    expect(light).toContain('color-scheme:light');
    expect(dark).toContain('color-scheme:dark');
    expect(light).not.toEqual(dark);
    // Обещанные модели переменные присутствуют
    for (const v of ['--cc-bg', '--cc-text', '--cc-accent', '--cc-border', '--cc-muted']) {
      expect(light).toContain(v);
      expect(dark).toContain(v);
    }
  });

  it('содержит скрипт авто-высоты с postMessage-типом', () => {
    expect(buildWidgetSrcDoc('x', 'light')).toContain(WIDGET_HEIGHT_MSG);
  });
});

// --- isWidgetShow: детект инструмента по суффиксу ---

describe('isWidgetShow', () => {
  it('распознаёт mcp__widgets__widget_show (и без учёта регистра/ключа сервера)', () => {
    expect(isWidgetShow('mcp__widgets__widget_show')).toBe(true);
    expect(isWidgetShow('MCP__WIDGETS__WIDGET_SHOW')).toBe(true);
    expect(isWidgetShow('mcp__other__widget_show')).toBe(true);
  });

  it('не срабатывает на прочие инструменты', () => {
    expect(isWidgetShow('Bash')).toBe(false);
    expect(isWidgetShow('mcp__widgets__something')).toBe(false);
    expect(isWidgetShow('widget_show_extra')).toBe(false);
  });
});
