// «Чем» у «Стемов» зависит от набора дорожек (макет composer-actions-v1, сценарий 5): «Набор: 4» — «Авто» сам
// становится HTDemucs, а не первой моделью каталога.
import { beforeEach, describe, expect, it } from 'vitest';
import type { AudioCatalog, AudioModelInfo } from '../api';
import { __resetExecutorState, executorModel } from './executors';

const stem = (id: string, label: string, set: string): AudioModelInfo => ({
  id, label, caps: { ops: ['separate'], languages: [], voiceKinds: [], producesFiles: ['audio'], license: { label: 'MIT', kind: 'permissive' }, priceUnit: 'free', stemSet: set },
} as unknown as AudioModelInfo);
const catalog: AudioCatalog = {
  autoModelId: 'auto', maxCount: 4,
  providers: [{
    key: 'local', label: 'Локальные модели', priceUnit: 'free', available: true, reason: null,
    models: [stem('bs', 'BS-RoFormer', 'vocals'), stem('ht4', 'HTDemucs · 4 стема', '4'), stem('ht6', 'HTDemucs · 6 стемов', '6')],
  }],
} as unknown as AudioCatalog;

const model = (stemSet: string | null) =>
  executorModel({ sessionId: 's1', op: 'separate', catalog, personal: false, notify: () => {}, stemSet });
const auto = (stemSet: string | null) => model(stemSet)!.rows.find(r => r.id === 'auto')!.sub ?? '';

beforeEach(() => __resetExecutorState());

describe('исполнители «Стемов» по набору', () => {
  it('без набора «Авто» — первая модель каталога, в списке все три', () => {
    expect(auto(null)).toContain('BS-RoFormer');
    expect(model(null)!.rows.filter(r => r.id !== 'auto')).toHaveLength(3);
  });

  it('набор «4» — «Авто» стал HTDemucs, лишних моделей в списке нет', () => {
    expect(auto('4')).toContain('HTDemucs · 4 стема');
    expect(auto('4')).not.toContain('BS-RoFormer');
    expect(model('4')!.rows.filter(r => r.id !== 'auto').map(r => r.name)).toEqual(['HTDemucs · 4 стема']);
  });

  it('смена набора отдаёт новую модель, а не кэш прошлого', () => {
    expect(model('4')).not.toBe(model('6'));
    expect(model('4')).toBe(model('4'));
  });

  it('набор не относится к другим операциям', () => {
    const m = executorModel({ sessionId: 's1', op: 'denoise', catalog, personal: false, notify: () => {}, stemSet: '4' });
    expect(m?.rows[0].sub).not.toContain('HTDemucs');
  });
});
