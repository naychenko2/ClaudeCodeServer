// Стор «← назад» (featureBackNav): откуда пришли в ключевой чат, в памяти вкладки.
// Чистая логика (без DOM): запись/сброс/чтение по текущему чату.
import { describe, expect, it, beforeEach } from 'vitest';
import { recordBackNav, clearBackNav, getBackNav, backNavFrom, normalizeBackTarget, __test } from './featureBackNav';

beforeEach(() => __test.reset());

describe('featureBackNav', () => {
  it('recordBackNav: записывает from→to; backNavFrom(to) = from, backNavFrom(другой) = null', () => {
    recordBackNav('chat-exec', 'chat-key');
    expect(getBackNav()).toEqual({ from: 'chat-exec', to: { kind: 'chat', id: 'chat-key' } });
    expect(backNavFrom('chat-key')).toBe('chat-exec');
    expect(backNavFrom('other')).toBeNull();
    expect(backNavFrom(null)).toBeNull();
  });

  it('recordBackNav(from, from) — не переход: указатель не ставится', () => {
    recordBackNav('chat-a', 'chat-a');
    expect(getBackNav()).toBeNull();
    expect(backNavFrom('chat-a')).toBeNull();
  });

  it('recordBackNav с пустым from/to — не записывается', () => {
    recordBackNav('', 'chat-key');
    expect(getBackNav()).toBeNull();
    recordBackNav('chat-exec', '');
    expect(getBackNav()).toBeNull();
  });

  it('clearBackNav: сбрасывает; повторный clear — идемпотентен', () => {
    recordBackNav('chat-exec', 'chat-key');
    clearBackNav();
    expect(getBackNav()).toBeNull();
    expect(backNavFrom('chat-key')).toBeNull();
    clearBackNav(); // не падает
    expect(getBackNav()).toBeNull();
  });

  it('смена проекта: clearBackNav сбрасывает указатель (не перескакивает через границу проекта)', () => {
    // Указатель из старого проекта (чат → фича) записан; WorkspacePage при перемонтировании
    // (смена проекта, key={project.id}) зовёт clearBackNav, чтобы он не применился к новому.
    recordBackNav('chatA', { kind: 'feature', id: 'f1' });
    expect(getBackNav(), 'указатель не записан').not.toBeNull();
    clearBackNav();
    expect(getBackNav(), 'указатель из старого проекта не сброшен').toBeNull();
  });

  it('recordBackNav поверх прежнего — заменяет указатель (один «← назад»)', () => {
    recordBackNav('chat-a', 'chat-key');
    recordBackNav('chat-b', 'chat-key');
    expect(getBackNav()).toEqual({ from: 'chat-b', to: { kind: 'chat', id: 'chat-key' } });
    expect(backNavFrom('chat-key')).toBe('chat-b');
  });

  it('backNavFrom: только когда to === текущий чат (иначе null, даже если from совпадает)', () => {
    recordBackNav('chat-exec', 'chat-key');
    // from = 'chat-exec', но мы в 'chat-exec' (это from, не to) → null.
    expect(backNavFrom('chat-exec')).toBeNull();
  });
});

describe('featureBackNav: цель перехода (chat | feature) и нормализация старой строки', () => {
  beforeEach(() => __test.reset());

  it('normalizeBackTarget: старая строка (id чата) → { kind: chat, id }', () => {
    expect(normalizeBackTarget('chat-key')).toEqual({ kind: 'chat', id: 'chat-key' });
  });

  it('normalizeBackTarget: объект { kind, id } — как есть (feature)', () => {
    expect(normalizeBackTarget({ kind: 'feature', id: 'f1' })).toEqual({ kind: 'feature', id: 'f1' });
  });

  it('recordBackNav с целем-фичей: to = { kind: feature, id }; backNavFrom(чата) = null', () => {
    recordBackNav('chat-exec', { kind: 'feature', id: 'f1' });
    expect(getBackNav()).toEqual({ from: 'chat-exec', to: { kind: 'feature', id: 'f1' } });
    // Страница фичи — не чат: «← назад» по chat-id не совпадает (backNavFrom ищет to-чат).
    expect(backNavFrom('f1')).toBeNull();
    expect(backNavFrom('chat-exec')).toBeNull();
  });

  it('«чат → тот же чат» (to.kind=chat, id=from) — не переход', () => {
    recordBackNav('chat-a', 'chat-a');
    expect(getBackNav()).toBeNull();
  });

  it('«чат → фича с тем же id» — переход (разные поверхности, guard не срабатывает)', () => {
    recordBackNav('f1', { kind: 'feature', id: 'f1' });
    expect(getBackNav()).toEqual({ from: 'f1', to: { kind: 'feature', id: 'f1' } });
  });
});
