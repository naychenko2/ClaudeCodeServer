import { describe, it, expect } from 'vitest';
import { readHidden } from '../useActionVisibility';
import { migrateRingPillHidden } from '../../lib/chatActions';

// Хранилище в памяти вместо localStorage (окружение тестов — node)
function memStore(init: Record<string, string> = {}) {
  const m = new Map(Object.entries(init));
  return {
    getItem: (k: string) => m.get(k) ?? null,
    setItem: (k: string, v: string) => { m.set(k, v); },
    raw: m,
  };
}

const HEADER = 'cc_chat_header_hidden';
const MARKER = 'cc_chat_header_hidden_ring_v1';

describe('migrateRingPillHidden — перевод видимости шапки на кольцевую пилюлю', () => {
  it('["cost"]: прятали только стоимость — пилюля возвращается, контекст виден', () => {
    expect(migrateRingPillHidden(['cost'])).toEqual([]);
  });

  it('["context"]: сирота вычищается, пилюля видна', () => {
    expect(migrateRingPillHidden(['context'])).toEqual([]);
  });

  it('оба скрыты — пилюля остаётся скрытой, прочие ключи не трогаются', () => {
    expect(migrateRingPillHidden(['rename', 'context', 'cost', 'fal'])).toEqual(['rename', 'cost', 'fal']);
  });
});

describe('readHidden — одноразовый перевод при чтении шапки', () => {
  it('["cost"] переводится и записывается обратно', () => {
    const s = memStore({ [HEADER]: '["cost","fal"]' });
    expect(readHidden('chat-header', s)).toEqual(['fal']);
    expect(s.raw.get(HEADER)).toBe('["fal"]');
  });

  it('["context"] — сирота вычищается из хранилища', () => {
    const s = memStore({ [HEADER]: '["context"]' });
    expect(readHidden('chat-header', s)).toEqual([]);
    expect(s.raw.get(HEADER)).toBe('[]');
  });

  it('перевод одноразовый: спрятанная после него пилюля остаётся спрятанной', () => {
    const s = memStore({ [HEADER]: '["context"]' });
    readHidden('chat-header', s);
    s.setItem(HEADER, '["cost"]');                 // человек спрятал пилюлю глазиком
    expect(readHidden('chat-header', s)).toEqual(['cost']);
  });

  it('без сохранённого набора — дефолт, и будущий ["cost"] уже не переводится', () => {
    const s = memStore();
    expect(readHidden('chat-header', s)).toBeNull();
    s.setItem(HEADER, '["cost"]');
    expect(readHidden('chat-header', s)).toEqual(['cost']);
  });

  it('битое значение не роняет чтение', () => {
    const s = memStore({ [HEADER]: '{oops' });
    expect(readHidden('chat-header', s)).toBeNull();
  });

  it('оба ключа: пилюля остаётся скрытой, сирота context вычищается из хранилища', () => {
    const s = memStore({ [HEADER]: '["context","cost","fal"]' });
    expect(readHidden('chat-header', s)).toEqual(['cost', 'fal']);
    expect(s.raw.get(HEADER)).toBe('["cost","fal"]');
    expect(s.raw.get(MARKER)).toBe('1');
  });

  it('уже стоящий маркер: сохранённый ["cost"] не переводится', () => {
    const s = memStore({ [HEADER]: '["cost"]', [MARKER]: '1' });
    expect(readHidden('chat-header', s)).toEqual(['cost']);
    expect(s.raw.get(HEADER)).toBe('["cost"]');
  });

  it('набор без context/cost не переписывается, но маркер ставится', () => {
    const writes: string[] = [];
    const s = memStore({ [HEADER]: '["fal"]' });
    const spy = { ...s, setItem: (k: string, v: string) => { writes.push(k); s.setItem(k, v); } };
    expect(readHidden('chat-header', spy)).toEqual(['fal']);
    expect(writes).toEqual([MARKER]);
  });

  it('битое значение: маркер всё равно ставится, а починенный потом ["cost"] не переводится', () => {
    const s = memStore({ [HEADER]: '{oops' });
    expect(readHidden('chat-header', s)).toBeNull();
    expect(s.raw.get(MARKER)).toBe('1');
    s.setItem(HEADER, '["cost"]');
    expect(readHidden('chat-header', s)).toEqual(['cost']);
  });

  it('другие поверхности не переводятся', () => {
    const s = memStore({ cc_chat_card_hidden: '["cost"]' });
    expect(readHidden('chat-card', s)).toEqual(['cost']);
  });
});
