// Шторка «Имя и описание»: без имени сохранить нельзя (confirmDisabled = !canSaveName), запрос уходит без фото.
import { describe, expect, it, vi } from 'vitest';
import { canSaveName, saveNameDescription } from './CharacterNameSheet';
import type { ImageEditCharacter } from '../api';

const char = { slug: 'anya', name: 'Аня', description: 'каре', local: null } as unknown as ImageEditCharacter;

describe('canSaveName', () => {
  it('пустое и из пробелов — нельзя', () => {
    expect(canSaveName('')).toBe(false);
    expect(canSaveName('   ')).toBe(false);
  });
  it('с именем — можно', () => expect(canSaveName('Аня')).toBe(true));
});

describe('saveNameDescription', () => {
  it('шлёт обрезанные имя и описание без фото и закрывает шторку', async () => {
    const updated = { ...char, name: 'Оля' } as ImageEditCharacter;
    const api = { updateCharacter: vi.fn().mockResolvedValue(updated) };
    const onChanged = vi.fn(), onClose = vi.fn();
    await saveNameDescription({
      api: api as never, projectId: 'p', character: char, name: ' Оля ', description: '  ', onChanged, onClose,
    });
    expect(api.updateCharacter).toHaveBeenCalledWith('p', 'anya', { name: 'Оля', description: undefined, photos: [] });
    expect(onChanged).toHaveBeenCalledWith(updated);
    expect(onClose).toHaveBeenCalledOnce();
  });
  it('при ошибке не закрывает шторку и пробрасывает исключение', async () => {
    const api = { updateCharacter: vi.fn().mockRejectedValue(new Error('boom')) };
    const onClose = vi.fn();
    await expect(saveNameDescription({
      api: api as never, projectId: 'p', character: char, name: 'Оля', description: '', onChanged: vi.fn(), onClose,
    })).rejects.toThrow('boom');
    expect(onClose).not.toHaveBeenCalled();
  });
});
