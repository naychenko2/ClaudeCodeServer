import { describe, it, expect } from 'vitest';
import { readHidden } from '../useActionVisibility';
import { migrateCardWikiHidden, migrateRingPillHidden } from '../../lib/chatActions';

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

describe('migrateCardWikiHidden — перевод набора плитки под ключ «Вики»', () => {
  it('ключа нет — он дописывается в конец', () => {
    expect(migrateCardWikiHidden(['pin'])).toEqual(['pin', 'wiki']);
  });

  it('ключ уже в наборе — набор как есть', () => {
    expect(migrateCardWikiHidden(['pin', 'wiki'])).toEqual(['pin', 'wiki']);
  });

  it('пустой набор — только «Вики»', () => {
    expect(migrateCardWikiHidden([])).toEqual(['wiki']);
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
    const s = memStore({ cc_composer_hidden: '["cost"]' });
    expect(readHidden('composer', s)).toEqual(['cost']);
  });
});

describe('readHidden — одноразовое скрытие кнопки «Тема» в плитке', () => {
  const CARD = 'cc_chat_card_hidden';
  const CARD_MARKER = 'cc_chat_card_hidden_subject_v1';
  const WIKI_MARKER = 'cc_chat_card_hidden_wiki_v1';

  it('старый набор без subject — тема уходит в меню и записывается', () => {
    // Маркер «Вики» зафиксирован в исходном состоянии: здесь проверяется только
    // перевод «Темы», совместный прогон обеих миграций — ниже, отдельным тестом
    const s = memStore({ [CARD]: '["rename"]', [WIKI_MARKER]: '1' });
    expect(readHidden('chat-card', s)).toEqual(['rename', 'subject']);
    expect(s.raw.get(CARD)).toBe('["rename","subject"]');
  });

  it('перевод одноразовый: показанная глазиком тема остаётся в ряду', () => {
    const s = memStore({ [CARD]: '[]' });
    readHidden('chat-card', s);
    s.setItem(CARD, '[]');                          // человек вернул тему глазиком
    expect(readHidden('chat-card', s)).toEqual([]);
  });

  it('без сохранённого набора — дефолт, маркер ставится', () => {
    const s = memStore();
    expect(readHidden('chat-card', s)).toBeNull();
    expect(s.raw.get(CARD_MARKER)).toBe('1');
  });
});

describe('readHidden — одноразовое скрытие кнопки «Вики» в плитке', () => {
  const CARD = 'cc_chat_card_hidden';
  const SUBJECT_MARKER = 'cc_chat_card_hidden_subject_v1';
  const WIKI_MARKER = 'cc_chat_card_hidden_wiki_v1';

  it('сохранённый набор без «Вики» — ключ дописывается, маркер ставится', () => {
    const s = memStore({ [CARD]: '["pin"]', [SUBJECT_MARKER]: '1' });
    expect(readHidden('chat-card', s)).toEqual(['pin', 'wiki']);
    expect(s.raw.get(CARD)).toBe('["pin","wiki"]');
    expect(s.raw.get(WIKI_MARKER)).toBe('1');
  });

  it('перевод одноразовый: возвращённая глазиком «Вики» остаётся в ряду', () => {
    const s = memStore({ [CARD]: '["pin"]', [SUBJECT_MARKER]: '1' });
    readHidden('chat-card', s);                     // дописал «Вики», поставил маркер
    s.setItem(CARD, '["pin"]');                     // человек вернул «Вики» глазиком
    expect(readHidden('chat-card', s)).toEqual(['pin']);
  });

  it('обе миграции плитки за одно чтение: и «Тема», и «Вики»', () => {
    const s = memStore({ [CARD]: '["pin"]' });
    expect(readHidden('chat-card', s)).toEqual(['pin', 'subject', 'wiki']);
    expect(s.raw.get(CARD)).toBe('["pin","subject","wiki"]');
    expect(s.raw.get(SUBJECT_MARKER)).toBe('1');
    expect(s.raw.get(WIKI_MARKER)).toBe('1');
  });

  it('отказ хранилища не роняет чтение', () => {
    const s = {
      getItem: () => { throw new Error('приватный режим'); },
      setItem: () => { throw new Error('приватный режим'); },
    };
    expect(readHidden('chat-card', s)).toBeNull();
  });

  it('набор с обоими ключами не переписывается, но оба маркера ставятся', () => {
    const writes: string[] = [];
    const s = memStore({ [CARD]: '["subject","wiki"]' });
    const spy = { ...s, setItem: (k: string, v: string) => { writes.push(k); s.setItem(k, v); } };
    expect(readHidden('chat-card', spy)).toEqual(['subject', 'wiki']);
    expect(writes).toEqual([SUBJECT_MARKER, WIKI_MARKER]);
  });

  it('битый набор: оба маркера всё равно ставятся', () => {
    const s = memStore({ [CARD]: '{oops' });
    expect(readHidden('chat-card', s)).toBeNull();
    expect(s.raw.get(SUBJECT_MARKER)).toBe('1');
    expect(s.raw.get(WIKI_MARKER)).toBe('1');
  });

  // «Вики» на этих поверхностях и так на виду — их наборы не мигрируем
  it('шапка и стена под «Вики» не мигрируются', () => {
    const s = memStore({ cc_chat_header_hidden: '["wiki"]', cc_chat_wall_hidden: '["wiki"]' });
    expect(readHidden('chat-header', s)).toEqual(['wiki']);
    expect(readHidden('chat-wall', s)).toEqual(['wiki']);
  });
});
