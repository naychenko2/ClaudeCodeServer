// Навигация в «Модели и расход»: диплинк #/models (уведомление об обновлении claude CLI)
// и «Собрать цепочку…». Контракт:
//   - #/models и /models (форма из web-push) → оверлей поверх home, раздел не меняется;
//   - хосты модалки только подсматривают пендинг (hasPendingOpen), съедает его модалка
//     (consumeOpenRequest) — иначе вкладка терялась бы до маунта модалки;
//   - requestNewPreset по-прежнему ведёт на «Модели» и просит черновик.
import { describe, expect, it } from 'vitest';
import { parseHash } from '../nav';
import {
  consumeDraftRequest, consumeOpenRequest, hasPendingOpen, requestNewPreset, requestOpenModelsSpend,
  subscribeModelProvidersNav,
} from '../modelProvidersNav';

describe('parseHash: #/models', () => {
  it('#/models — оверлей «Модели и расход» поверх home', () => {
    expect(parseHash('#/models')).toEqual({ screen: 'home', modelsSpend: true });
  });

  it('/#models — форма URL после service worker', () => {
    expect(parseHash('#models')).toEqual({ screen: 'home', modelsSpend: true });
  });
});

describe('modelProvidersNav', () => {
  it('hasPendingOpen не съедает запрос, consumeOpenRequest отдаёт вкладку один раз', () => {
    requestOpenModelsSpend('quotas');
    expect(hasPendingOpen()).toBe(true);
    expect(hasPendingOpen()).toBe(true);
    expect(consumeOpenRequest()).toBe('quotas');
    expect(consumeOpenRequest()).toBeNull();
    expect(hasPendingOpen()).toBe(false);
  });

  it('requestNewPreset — вкладка «Модели» и черновик, как раньше', () => {
    requestNewPreset();
    expect(consumeOpenRequest()).toBe('slots');
    expect(consumeDraftRequest()).toBe(true);
    expect(consumeDraftRequest()).toBe(false);
  });

  it('запрос будит подписчиков', () => {
    let calls = 0;
    const off = subscribeModelProvidersNav(() => { calls++; });
    requestOpenModelsSpend('quotas');
    off();
    requestOpenModelsSpend('quotas');
    expect(calls).toBe(1);
    consumeOpenRequest();
  });
});
