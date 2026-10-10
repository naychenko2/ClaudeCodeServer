import { describe, expect, it } from 'vitest';
import { loraDisplayName, loraLabels, ownLoraLabel, labelOrShortName } from './loraName';

const at = (d: number, h = 12, m = 0) => new Date(2026, 9, d, h, m).toISOString();
const A = 'ccs/a300cc8f/andreyk/andreyk_qwen21_v1_000001750.safetensors';
const B = 'ccs/a300cc8f/andreyk2/andreyk_qwen21_v1_000001750.safetensors';

describe('loraDisplayName', () => {
  it('срезает префикс и расширение', () => {
    expect(loraDisplayName(A)).toBe('andreyk_qwen21_v1_000001750');
    expect(loraDisplayName('manual.safetensors')).toBe('manual');
  });
});

describe('loraLabels', () => {
  it('имя и дата', () => {
    expect(loraLabels([A], { [A]: at(10) }).get(A)).toBe('andreyk_qwen21_v1_000001750 · 10 октября');
  });
  it('без даты — только имя', () => {
    expect(loraLabels([A]).get(A)).toBe('andreyk_qwen21_v1_000001750');
  });
  it('одинаковое имя в разные дни различается датой', () => {
    const l = loraLabels([A, B], { [A]: at(10), [B]: at(11) });
    expect(l.get(A)).toContain('10 октября');
    expect(l.get(B)).toContain('11 октября');
  });
  it('одна дата — добавляется время', () => {
    const l = loraLabels([A, B], { [A]: at(10, 9, 5), [B]: at(10, 14, 32) });
    expect(l.get(A)).toBe('andreyk_qwen21_v1_000001750 · 10 октября, 09:05');
    expect(l.get(B)).toBe('andreyk_qwen21_v1_000001750 · 10 октября, 14:32');
  });
  it('ручные без даты различаются папкой', () => {
    const l = loraLabels(['x/one/face.safetensors', 'x/two/face.safetensors']);
    expect(l.get('x/one/face.safetensors')).toBe('face · one');
    expect(l.get('x/two/face.safetensors')).toBe('face · two');
  });
  it('одинаковая папка — путь внутри префикса', () => {
    const p = 'ccs/o1/same/face.safetensors', q = 'ccs/o2/same/face.safetensors';
    const l = loraLabels([p, q]);
    expect(l.get(p)).not.toBe(l.get(q));
  });
  it('все подписи уникальны', () => {
    const names = [A, B, 'face.safetensors', 'a/face.safetensors', 'b/face.safetensors'];
    expect(new Set(loraLabels(names).values()).size).toBe(names.length);
  });
});

describe('ownLoraLabel', () => {
  const list = (items: string[], trained?: Record<string, string>) => ({ available: true, items, trained });
  it('тёзка из другой папки различается так же, как в селекте', () => {
    const items = ['face.safetensors', 'other/face.safetensors'];
    const label = ownLoraLabel('other/face.safetensors', undefined, list(items));
    expect(label).toBe(loraLabels(items).get('other/face.safetensors'));
    expect(label).toBe('face · other');
  });
  it('обученные тёзки одной даты — со временем', () => {
    const t = { [A]: at(10, 9, 5), [B]: at(10, 14, 32) };
    expect(ownLoraLabel(B, t[B], list([A, B], t))).toBe('andreyk_qwen21_v1_000001750 · 10 октября, 14:32');
  });
  it('дата обучения из own подставляется, когда в trained записи нет', () => {
    expect(ownLoraLabel(A, at(10), list([A]))).toBe('andreyk_qwen21_v1_000001750 · 10 октября');
  });
  it('own нет в списке (ручная LoRA, пропала из ComfyUI) — подпись всё равно есть и тёзки различаются', () => {
    const gone = 'other/face.safetensors';
    expect(ownLoraLabel(gone, undefined, list(['face.safetensors']))).toBe('face · other');
    expect(ownLoraLabel(gone, undefined, list([]))).toBe('face');
  });
  it('список недоступен — одно имя без пути', () => {
    expect(ownLoraLabel('other/face.safetensors', undefined, { available: false, items: [] })).toBe('face');
    expect(ownLoraLabel('other/face.safetensors', undefined, null)).toBe('face');
  });
});

describe('сокращение имени в селекте', () => {
  it('режет имя, дата и время остаются', () => {
    const l = loraLabels([A, B], { [A]: at(10, 9, 5), [B]: at(10, 14, 32) }, 12);
    expect(l.get(B)).toBe('andrey…01750 · 10 октября, 14:32');
  });
  it('короткое имя не трогает', () => {
    expect(loraLabels(['face.safetensors'], undefined, 12).get('face.safetensors')).toBe('face');
  });
  it('совпавшие после сокращения остаются полными', () => {
    const a = 'aaaaaaaaaaaaaaaaaa_x.safetensors', b = 'aaaaaaaaaaaaaaaaaa_y.safetensors';
    const l = loraLabels([a, b], undefined, 12);
    expect(l.get(a)).not.toBe(l.get(b));
  });
});

describe('labelOrShortName', () => {
  it('без подписи (ComfyUI недоступен) — короткое имя без ccs/ и .safetensors', () => {
    const label = labelOrShortName(loraLabels([]), 'ccs/75b93/two/face.safetensors');
    expect(label).toBe('face');
    expect(label).not.toContain('ccs/');
    expect(label).not.toContain('.safetensors');
  });
  it('подпись из набора приоритетнее', () => {
    expect(labelOrShortName(new Map([[A, 'метка']]), A)).toBe('метка');
  });
});
