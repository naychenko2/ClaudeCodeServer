import { beforeEach, describe, expect, it, vi } from 'vitest';

const { request } = vi.hoisted(() => ({ request: vi.fn(async () => ({})) }));
vi.mock('aihome_shell/kit', async orig => ({ ...(await orig<object>()), request }));

import { imageEditorApi } from './api';

beforeEach(() => { request.mockClear(); });

async function sentForm(description: string | undefined): Promise<FormData> {
  await imageEditorApi().updateCharacter('p1', 'anya', { name: 'Аня', description, photos: [] });
  return (request.mock.calls[0] as unknown as [string, { body: FormData }])[1].body;
}

describe('форма персонажа', () => {
  it('пустое описание уходит пустым полем — это «стереть»', async () => {
    expect((await sentForm('')).get('description')).toBe('');
  });
  it('неуказанное описание в форму не попадает — это «не менять»', async () => {
    expect((await sentForm(undefined)).has('description')).toBe(false);
  });
});
