import { describe, it, expect } from 'vitest';
import { markdownToPlain } from '../markdownPlain';

describe('markdownToPlain', () => {
  it('снимает заголовки ATX', () => {
    expect(markdownToPlain('## Задача\nПроверить кламп')).toBe('Задача Проверить кламп');
  });

  it('снимает жирное и курсив', () => {
    expect(markdownToPlain('**Важно**: сделать *быстро* и __аккуратно__'))
      .toBe('Важно: сделать быстро и аккуратно');
  });

  it('не режет snake_case подчёркиваниями', () => {
    expect(markdownToPlain('поле tool_use и _акцент_')).toBe('поле tool_use и акцент');
  });

  it('снимает маркеры списков', () => {
    expect(markdownToPlain('- первый\n- второй\n1. третий')).toBe('первый второй третий');
  });

  it('оставляет от ссылки подпись', () => {
    expect(markdownToPlain('см. [гайд](docs/design/guidelines.md) и ![схема](a.png)'))
      .toBe('см. гайд и схема');
  });

  it('разворачивает код-фенс в текст', () => {
    expect(markdownToPlain('Пример:\n```ts\nconst a = 1;\n```\nвсё')).toBe('Пример: const a = 1; всё');
  });

  it('снимает цитаты и линейки', () => {
    expect(markdownToPlain('> цитата\n\n---\n\nтекст')).toBe('цитата текст');
  });

  it('схлопывает многострочный текст в одну строку без разметки', () => {
    const md = '# Вопрос\n\nНужно **проверить**:\n\n- `MarkdownContent`\n- кламп\n\nСпасибо.';
    expect(markdownToPlain(md)).toBe('Вопрос Нужно проверить: MarkdownContent кламп Спасибо.');
  });

  it('пустой ввод — пустая строка', () => {
    expect(markdownToPlain('')).toBe('');
  });
});
