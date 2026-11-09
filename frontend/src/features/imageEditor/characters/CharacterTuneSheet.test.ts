// Шторка «Тонкая настройка»: отмена откатывает, сохранение пишет LoRA полным путём, подписи без служебного пути.
import { describe, expect, it, vi } from 'vitest';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { CharacterLoraSection } from './CharacterLoraSection';
import { cancelTune, saveTune } from './CharacterTuneSheet';
import type { ImageEditCharacter } from '../api';

const FULL = 'ccs/u1/andrey/andrey_v1.safetensors';
const list = { available: true, items: [FULL, 'other.safetensors'], trained: { [FULL]: '2026-10-03T14:20:00Z' } };
const char = { slug: 'andrey', name: 'Андрей', description: 'd', local: null } as unknown as ImageEditCharacter;

const section = (current: object | null) => renderToStaticMarkup(createElement(CharacterLoraSection, {
  list, current: current as never, slug: 'andrey', onChange: () => {},
}));

describe('cancelTune', () => {
  it('возвращает слово-метку и закрывает, без сохранения', () => {
    const set = vi.fn(), close = vi.fn();
    cancelTune('old_word', set, close);
    expect(set).toHaveBeenCalledWith('old_word');
    expect(close).toHaveBeenCalledOnce();
  });
  it('null возвращает умолчательное слово', () => {
    const set = vi.fn();
    cancelTune(null, set, () => {});
    expect(set).toHaveBeenCalledWith(null);
  });
});

describe('saveTune', () => {
  it('пишет LoRA полным путём и закрывает', async () => {
    const api = { updateCharacter: vi.fn().mockResolvedValue(char) };
    const onClose = vi.fn(), onChanged = vi.fn();
    await saveTune({ api: api as never, projectId: 'p', character: char, lora: { name: FULL, trigger: 'w', strength: 0.8 }, onChanged, onClose });
    expect(api.updateCharacter).toHaveBeenCalledWith('p', 'andrey', expect.objectContaining({ lora: { name: FULL, trigger: 'w', strength: 0.8 } }));
    expect(onChanged).toHaveBeenCalledWith(char);
    expect(onClose).toHaveBeenCalledOnce();
  });
  it('LoRA не трогали — запроса нет', async () => {
    const api = { updateCharacter: vi.fn() };
    const onClose = vi.fn();
    await saveTune({ api: api as never, projectId: 'p', character: char, lora: undefined, onChanged: () => {}, onClose });
    expect(api.updateCharacter).not.toHaveBeenCalled();
    expect(onClose).toHaveBeenCalledOnce();
  });
  it('ошибка: шторка не закрывается', async () => {
    const api = { updateCharacter: vi.fn().mockRejectedValue(new Error('boom')) };
    const onClose = vi.fn();
    await expect(saveTune({ api: api as never, projectId: 'p', character: char, lora: 'remove', onChanged: () => {}, onClose })).rejects.toThrow('boom');
    expect(onClose).not.toHaveBeenCalled();
  });
});

describe('CharacterLoraSection в шторке', () => {
  const cur = { lora: FULL, trigger: 'andrey_w', strength: 0.8 };
  it('слово-триггер своей версии скрыто без своей версии, видно с ней', () => {
    expect(section(null)).not.toContain('Слово-триггер своей версии');
    expect(section(cur)).toContain('Слово-триггер своей версии');
  });
  it('тексты шторки и папка', () => {
    const h = section(null);
    expect(h).toContain('Сила сходства');
    expect(h).toContain('Своя обученная версия');
    expect(h).toContain('Не выбрана — по обучению AI Home');
    expect(h).toContain('Папка: characters/');
    expect(h).not.toContain('Файл LoRA');
  });
  it('путь ccs/… нигде не показан, дата видна', () => {
    const h = section(cur);
    // value у option — полный путь (так и надо), смотрим только видимое: атрибуты вырезаем
    expect(h).toContain(`value="${FULL}"`);
    expect(h.replace(/<[^>]+>/g, '|')).not.toContain('ccs/');
    expect(h).toContain('andrey_v1');
    expect(h).toContain('3 октября');
  });
});
