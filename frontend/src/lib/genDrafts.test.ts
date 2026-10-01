import { beforeEach, describe, expect, it } from 'vitest';
import { __resetGenDrafts, clearGenDraft, getGenDraftText, hasGenDraft, noteGenDraft, setGenDraftText } from './genDrafts';
import { modeDraftText, nextModeDraft, nextPrefill, type ModeDraftState, type PrefillState } from './composerModes';

beforeEach(() => { __resetGenDrafts(); });

// Поле ввода режима, как его ведёт Composer: смена элемента → подмена черновика →
// затравка; каждая правка пишется черновиком своего элемента
function field() {
  let text = '';
  let draft: ModeDraftState = { key: null };
  let prefill: PrefillState = { key: null, auto: null };
  let key: string | null = null;
  return {
    select(next: string, seed: string | null = null) {
      const d = nextModeDraft(draft, next, text, prefill.auto, getGenDraftText);
      draft = d.state;
      const r = nextPrefill(prefill, { key: next, text: seed }, d.field);
      prefill = r.state;
      text = r.field;
      key = next;
    },
    type(t: string) { text = t; setGenDraftText(key, modeDraftText(text, prefill.auto)); },
    get text() { return text; },
  };
}

describe('правило 3: черновик переживает переключение', () => {
  it('промпт, набранный под картинкой A, возвращается при возврате к ней; у B — своё', () => {
    const f = field();
    f.select('images:A', 'старый промпт A');
    expect(f.text).toBe('старый промпт A');
    f.type('сделай закат');
    expect(hasGenDraft('images:A')).toBe(true);
    f.select('images:B', 'промпт B');
    expect(f.text).toBe('промпт B');
    expect(hasGenDraft('images:B')).toBe(false);
    f.select('images:A', 'старый промпт A');
    expect(f.text).toBe('сделай закат');
  });

  it('нетронутая затравка — не черновик: пометки нет, смена элемента её заменяет', () => {
    const f = field();
    f.select('images:A', 'промпт A');
    f.type('промпт A');
    expect(hasGenDraft('images:A')).toBe(false);
    f.select('images:B', null);
    expect(f.text).toBe('');
  });

  it('без затравки (звук): набранное уходит с элементом, у другого поле пустое', () => {
    const f = field();
    f.select('sound:A');
    f.type('Привет, мир');
    f.select('sound:B');
    expect(f.text).toBe('');
    f.select('sound:A');
    expect(f.text).toBe('Привет, мир');
  });

  it('запуск забирает черновик — и текст, и правки полей панели', () => {
    noteGenDraft('sound:A');
    setGenDraftText('sound:A', 'текст');
    clearGenDraft('sound:A');
    expect(hasGenDraft('sound:A')).toBe(false);
    expect(getGenDraftText('sound:A')).toBeNull();
  });

  it('правка поля панели ставит пометку и без текста; пустой текст её не снимает', () => {
    noteGenDraft('sound:A');
    setGenDraftText('sound:A', '');
    expect(hasGenDraft('sound:A')).toBe(true);
    expect(getGenDraftText('sound:A')).toBeNull();
  });
});
