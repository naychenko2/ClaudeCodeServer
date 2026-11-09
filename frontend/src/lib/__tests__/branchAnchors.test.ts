// Тесты якорей кнопки «Ветвление» (lib/branchAnchors).
//
// Дефект (дефектная карточка 034a96c3, профиль CDP): структура якорей держала на
// каждом элементе ленты объект с anchorText — срезом текста сообщения. В V8 такой
// срез удерживает ИСХОДНУЮ строку целиком, а useMemo пересобирал структуру на каждую
// стрим-дельту при невиртуализированной ленте. В часовом замере это был прирост живой
// кучи под местом вызова (+11 МБ за час) и пики JSHeapTotal до 325 МБ.
import { describe, it, expect } from 'vitest';
import type { ChatItem } from '../../types';
import {
  buildBranchAnchors, branchAnchorText, BRANCH_TURN, BRANCH_BEFORE_PROMPT,
} from '../branchAnchors';

const user = (text: string): ChatItem => ({ kind: 'user_message', text } as unknown as ChatItem);
const text = (t: string): ChatItem => ({ kind: 'text', text: t } as unknown as ChatItem);

describe('buildBranchAnchors', () => {
  it('пустая лента — ни одного якоря', () => {
    const a = buildBranchAnchors([]);
    expect(a.userIdx.length).toBe(0);
  });

  it('элементы выше первого user_message якоря не имеют', () => {
    const a = buildBranchAnchors([text('до'), text('ещё до'), user('вопрос')]);
    expect(a.userIdx[0]).toBe(-1);
    expect(a.userIdx[1]).toBe(-1);
    expect(a.userIdx[2]).toBe(0);
    expect(a.include[0]).toBe(0);
  });

  it('user_message — свой якорь с видом beforePrompt', () => {
    const a = buildBranchAnchors([user('первый'), user('второй')]);
    expect(Array.from(a.userIdx)).toEqual([0, 1]);
    expect(Array.from(a.include)).toEqual([BRANCH_BEFORE_PROMPT, BRANCH_BEFORE_PROMPT]);
  });

  it('ответ ассистента цепляется к ПОСЛЕДНЕГО user_message выше', () => {
    const a = buildBranchAnchors([user('первый'), text('ответ'), user('второй'), text('ещё'), text('хвост')]);
    expect(Array.from(a.userIdx)).toEqual([0, 0, 1, 1, 1]);
    expect(Array.from(a.include)).toEqual([
      BRANCH_BEFORE_PROMPT, BRANCH_TURN, BRANCH_BEFORE_PROMPT, BRANCH_TURN, BRANCH_TURN,
    ]);
  });

  it('структура не хранит ни одной строки — только два типизированных массива', () => {
    const items = Array.from({ length: 100 }, (_, i) => user(`вопрос ${i}`));
    const a = buildBranchAnchors(items);

    expect(a.userIdx).toBeInstanceOf(Int32Array);
    expect(a.include).toBeInstanceOf(Uint8Array);
    // Никаких третьих полей: прежняя форма держала anchorText на каждый элемент
    expect(Object.keys(a).sort()).toEqual(['include', 'userIdx']);
    expect(Array.from(a.userIdx).every(v => typeof v === 'number')).toBe(true);
    // 5 байт на элемент ленты вместо объекта с текстовым срезом
    expect(a.userIdx.byteLength + a.include.byteLength).toBe(items.length * 5);
  });
});

describe('branchAnchorText', () => {
  it('отдаёт первые 120 символов нужного user_message', () => {
    const long = 'я'.repeat(500);
    const items = [user('первый'), user(long)];
    expect(branchAnchorText(items, 0)).toBe('первый');
    expect(branchAnchorText(items, 1)).toHaveLength(120);
  });

  it('номер считается среди user_message, а не среди элементов ленты', () => {
    const items = [text('ответ'), user('вопрос')];
    expect(branchAnchorText(items, 0)).toBe('вопрос');
  });

  it('вне диапазона — пустая строка (кнопки у такого элемента нет)', () => {
    expect(branchAnchorText([user('вопрос')], 5)).toBe('');
    expect(branchAnchorText([], 0)).toBe('');
  });
});