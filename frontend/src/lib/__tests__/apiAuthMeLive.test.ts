import { describe, it, expect, vi } from 'vitest';

// api.ts берёт request из offline.ts — мокаем и проверяем, что live доезжает до запроса.
const { request } = vi.hoisted(() => ({ request: vi.fn(async () => ({})) }));
vi.mock('../offline', () => ({ request, readStoredToken: vi.fn(() => 'tok') }));

import { api } from '../api';

describe('api.auth.me', () => {
  it('live пробрасывается в request: на входе ответ из IDB-кэша не нужен', async () => {
    await api.auth.me({ live: true });
    expect(request).toHaveBeenCalledWith('/auth/me', { live: true });
  });
});
