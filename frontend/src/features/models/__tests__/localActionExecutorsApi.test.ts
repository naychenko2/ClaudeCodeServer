import { describe, it, expect, vi, beforeEach } from 'vitest';

// W7a · Слой данных блока «Исполнители ночи»: putLocalActionExecutors → PUT
// /api/admin/local-actions/{key}/executors (контракт W2b, LocalActionsAdminController.SetExecutors).
// request() мокаем сервером: записывает URL/тело и бросает ошибку с status/body — той же формой,
// что настоящий request() в offline.ts (он сам дописывает '/api' — здесь проверяем URL, который
// ему передаёт modelsApi, как в соседних тестах доменных api).
const { request } = vi.hoisted(() => ({ request: vi.fn() }));
vi.mock('../../../lib/offline', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../lib/offline')>();
  return { ...orig, request };
});

import { modelsApi } from '../api';

type ReqOpts = { method?: string; body?: string };

beforeEach(() => {
  request.mockReset();
});

describe('modelsApi.localActions.putLocalActionExecutors', () => {
  it('метод и адрес: PUT /admin/local-actions/{key}/executors', async () => {
    request.mockResolvedValue({ key: 'wiki-nightly', maxTotal: 0, executors: [] });
    await modelsApi.localActions.putLocalActionExecutors('wiki-nightly', { maxTotal: 0, executors: [] });

    const [url, options] = request.mock.calls[0] as [string, ReqOpts];
    expect(url).toBe('/admin/local-actions/wiki-nightly/executors');
    expect(options.method).toBe('PUT');
  });

  it('тело равно переданному: maxTotal и порядок items (порядок = приоритет) сохранены', async () => {
    // Порядок заведомо не алфавитный и не по parallel: сортировка любого вида его меняет
    const body = {
      maxTotal: 3,
      executors: [
        { engine: 'qwen3.8-27b', parallel: 2 },
        { engine: 'local', parallel: 1 },
        { engine: 'gemma', parallel: 1 },
      ],
    };
    request.mockResolvedValue({ key: 'wiki-nightly', maxTotal: 3, executors: body.executors });
    await modelsApi.localActions.putLocalActionExecutors('wiki-nightly', body);

    const [, options] = request.mock.calls[0] as [string, ReqOpts];
    expect(JSON.parse(options.body!)).toEqual(body);
  });

  it('пустой список — снятие ключа: тело несёт пустой executors', async () => {
    request.mockResolvedValue({ key: 'wiki-nightly', maxTotal: 0, executors: [] });
    await modelsApi.localActions.putLocalActionExecutors('wiki-nightly', { maxTotal: 0, executors: [] });

    const [, options] = request.mock.calls[0] as [string, ReqOpts];
    expect(JSON.parse(options.body!)).toEqual({ maxTotal: 0, executors: [] });
  });

  it('400 с текстами валидатора — ошибка пробросена как есть, не проглочена', async () => {
    request.mockRejectedValue(Object.assign(new Error('HTTP 400'), {
      status: 400,
      body: { errors: ['Исполнитель «gpt-4o» не локальный движок'] },
    }));

    const err = await modelsApi.localActions
      .putLocalActionExecutors('wiki-nightly', { maxTotal: 2, executors: [{ engine: 'gpt-4o', parallel: 1 }] })
      .catch((e) => e);
    expect(err.status).toBe(400);
    expect(err.body.errors).toEqual(['Исполнитель «gpt-4o» не локальный движок']);
  });
});
