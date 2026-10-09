import { beforeEach, describe, expect, it, vi } from 'vitest';

const { request } = vi.hoisted(() => ({ request: vi.fn(async () => ({})) }));
vi.mock('aihome_shell/kit', async orig => ({ ...(await orig<object>()), request }));

import { loraTrainApi } from './api';

beforeEach(() => { request.mockClear(); });

describe('занятость карты на уровне инстанса (ADR-026 §7.4)', () => {
  it('active: GET без проекта', async () => {
    await loraTrainApi.active();
    expect(request).toHaveBeenCalledWith('/image-editor/lora-train/active', { live: true });
  });
  it('cancelActive: DELETE той же ручки', async () => {
    await loraTrainApi.cancelActive();
    expect(request).toHaveBeenCalledWith('/image-editor/lora-train/active', { method: 'DELETE' });
  });
});
