import { describe, expect, it } from 'vitest';
import { dedupeVariants } from './useJobStatus';

describe('dedupeVariants', () => {
  it('схлопывает дубли и сортирует по возрастанию', () => {
    expect(dedupeVariants([0, 0, 1])).toEqual([0, 1]);
    expect(dedupeVariants([2, 1, 2, 0])).toEqual([0, 1, 2]);
    expect(dedupeVariants([3, 3, 3])).toEqual([3]);
    expect(dedupeVariants([])).toEqual([]);
  });
});
