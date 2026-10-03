import { describe, expect, it } from 'vitest';
import { actionChips, chipSelectable, CHAT_CHIP_ID, MAX_VIEW_ACTIONS } from '../../../lib/chatContext/actionChips';
import { pickDefaultAction } from '../../../lib/chatContext/actionMemory';
import { OPS } from '../ops';
import { audioState, buildAudioActions, resolveSpeakOp, type AudioActionInput, type AudioRefLike } from './actions';

// Каталог действий звука (ADR-023 §Д2.2, Р1, Р6): четыре состояния основного объекта

const VOICE: AudioRefLike = { kind: 'audio-voice', role: 'voice' };
const SAMPLE: AudioRefLike = { kind: 'audio', role: 'reference' };
const PIECE: AudioRefLike = { kind: 'audio', role: 'piece' };

const base: AudioActionInput = { state: 'song', refs: [], hasSelection: false, stemSets: ['vocals', '4', '6', 'karaoke'] };
const FIXTURES: Record<string, AudioActionInput> = {
  'черновик': { ...base, state: 'draft' },
  'речь': { ...base, state: 'speech' },
  'песня': base,
  'песня с выделением и кусками': { ...base, hasSelection: true, refs: [PIECE, PIECE] },
  'со стемами': { ...base, state: 'stems' },
  'со стемами и кусками': { ...base, state: 'stems', refs: [PIECE, PIECE] },
};

const OP_NAMES = OPS.map(o => o.op);

describe.each(Object.entries(FIXTURES))('каталог звука · %s', (_name, input) => {
  const actions = buildAudioActions(input);

  it('вид отдаёт не больше пяти действий: с «Чатом» хоста чипов не больше шести, «Чат» первым', () => {
    expect(actions.length).toBeLessThanOrEqual(MAX_VIEW_ACTIONS);
    const chips = actionChips(actions);
    expect(chips.length).toBeLessThanOrEqual(6);
    expect(chips[0].id).toBe(CHAT_CHIP_ID);
  });

  it('id уникальны', () => {
    expect(new Set(actions.map(a => a.id)).size).toBe(actions.length);
  });

  it('op каждого run-действия — операция каталога OPS', () => {
    for (const a of actions.filter(x => x.kind === 'run')) expect(OP_NAMES).toContain(a.op);
  });

  it('у объекта человека выбран ровно один чип: первое run без серости; у агента — «Чат»', () => {
    const selectable = actionChips(actions).filter(chipSelectable).map(c => c.id);
    const picked = pickDefaultAction(actions, 'human');
    expect(picked).toBe(actions.find(a => a.kind === 'run' && !a.disabledReason)!.id);
    expect(selectable).toContain(picked);
    expect(pickDefaultAction(actions, 'agent')).toBeNull();
  });
});

describe('каталог звука: состав по состояниям', () => {
  const ids = (i: AudioActionInput) => buildAudioActions(i).map(a => a.id);

  it('состояние читается из версии: стемы сильнее всего, без звука — черновик, без mode — песня', () => {
    expect(audioState({ hasSound: false, hasStems: false, mode: 'voice' })).toBe('draft');
    expect(audioState({ hasSound: true, hasStems: false, mode: 'voice' })).toBe('speech');
    expect(audioState({ hasSound: true, hasStems: false, mode: 'music' })).toBe('song');
    expect(audioState({ hasSound: true, hasStems: false, mode: undefined })).toBe('song');
    expect(audioState({ hasSound: true, hasStems: true, mode: 'voice' })).toBe('stems');
    expect(audioState({ hasSound: false, hasStems: true, mode: null })).toBe('stems');
  });

  it('черновик — «Озвучить», «Песня», «Эффект» (ТО-2 (1)), умолчание «Озвучить»', () => {
    expect(ids(FIXTURES['черновик'])).toEqual(['speak', 'song', 'sfx']);
    expect(buildAudioActions(FIXTURES['черновик'])).toMatchObject([
      { op: 'speak', text: 'required' }, { op: 'song', text: 'required' }, { op: 'sfx', text: 'required' },
    ]);
    expect(pickDefaultAction(buildAudioActions(FIXTURES['черновик']), 'human')).toBe('speak');
  });

  it('речь: озвучить ещё, сменить голос, убрать шум, стемы; умолчание «Озвучить ещё»', () => {
    const a = buildAudioActions(FIXTURES['речь']);
    expect(a.map(x => x.id)).toEqual(['speakMore', 'convert', 'denoise', 'stems']);
    expect(a.map(x => x.op)).toEqual(['speak', 'convertVoice', 'denoise', 'separate']);
    expect(pickDefaultAction(a, 'human')).toBe('speakMore');
  });

  it('песня: перегенерировать кусок, стемы, убрать шум, склеить; умолчание «Стемы» (кусок серый без выделения)', () => {
    const a = buildAudioActions(FIXTURES['песня']);
    expect(a.map(x => x.id)).toEqual(['repaint', 'stems', 'denoise', 'concat']);
    expect(a.find(x => x.id === 'repaint')!.disabledReason).toMatch(/Выделите кусок на волне/);
    expect(pickDefaultAction(a, 'human')).toBe('stems');
  });

  it('песня с выделением: «Перегенерировать кусок» живой и становится умолчанием', () => {
    const a = buildAudioActions(FIXTURES['песня с выделением и кусками']);
    expect(a.find(x => x.id === 'repaint')!.disabledReason).toBeUndefined();
    expect(pickDefaultAction(a, 'human')).toBe('repaint');
  });

  it('со стемами: «Свести» и «Склеить»; умолчание «Свести»', () => {
    const a = buildAudioActions(FIXTURES['со стемами']);
    expect(a.map(x => x.id)).toEqual(['mix', 'concat']);
    expect(a.map(x => x.op)).toEqual(['mixStems', 'concat']);
    expect(pickDefaultAction(a, 'human')).toBe('mix');
  });

  it('«Склеить» серая без двух кусков, с причиной; с двумя — живая', () => {
    const concat = (refs: AudioRefLike[]) => buildAudioActions({ ...base, refs }).find(x => x.id === 'concat')!;
    expect(concat([]).disabledReason).toBe('Добавьте куски через «В контекст»');
    expect(concat([PIECE]).disabledReason).toMatch(/ещё один кусок/);
    expect(concat([PIECE, PIECE]).disabledReason).toBeUndefined();
  });

  it('«Стемы» — вопрос «Набор»: только то, что умеет хоть один поставщик, первое значение предвыбрано', () => {
    const q = buildAudioActions(base).find(x => x.id === 'stems')!.question!;
    expect(q).toMatchObject({ param: 'stemSet', title: 'Набор' });
    expect(q.options.map(o => o.value)).toEqual(['vocals', '4', '6', 'karaoke']);
    const some = buildAudioActions({ ...base, stemSets: ['4', 'karaoke'] }).find(x => x.id === 'stems')!;
    expect(some.question!.options.map(o => o.value)).toEqual(['4', 'karaoke']);
    // Каталога ещё нет — все четыре, действие живое
    expect(buildAudioActions({ ...base, stemSets: null }).find(x => x.id === 'stems')!.question!.options).toHaveLength(4);
    // Ни у кого нет разделения — серое с причиной
    expect(buildAudioActions({ ...base, stemSets: [] }).find(x => x.id === 'stems')!.disabledReason).toMatch(/стемы/);
  });

  it('«Сменить голос» серая, пока нет ни голоса, ни образца', () => {
    const convert = (refs: AudioRefLike[]) => buildAudioActions({ ...base, state: 'speech', refs }).find(x => x.id === 'convert')!;
    expect(convert([]).disabledReason).toMatch(/голос или образец/);
    expect(convert([VOICE]).disabledReason).toBeUndefined();
    expect(convert([SAMPLE]).disabledReason).toBeUndefined();
  });

  it('вне чипов остаются операции только для агента', () => {
    const used = new Set(Object.values(FIXTURES).flatMap(i => buildAudioActions(i).map(a => a.op)));
    for (const agentOnly of ['dialogue', 'designVoice', 'cover', 'outpaint', 'extract', 'lego', 'complete', 'upsample', 'master',
      'transcribe', 'align', 'toMidi', 'normalize', 'trainVoice', 'trim', 'gainFade'] as const) {
      expect(used.has(agentOnly)).toBe(false);
    }
  });
});

describe('резолв «Озвучить» по референсам', () => {
  it('без референсов — speak диктором по умолчанию', () => {
    expect(resolveSpeakOp([])).toBe('speak');
  });

  it('голос из библиотеки — speak с голосом', () => {
    expect(resolveSpeakOp([VOICE])).toBe('speak');
  });

  it('образец — cloneVoice', () => {
    expect(resolveSpeakOp([SAMPLE])).toBe('cloneVoice');
  });

  it('голос сильнее образца: speak не читает образец', () => {
    expect(resolveSpeakOp([SAMPLE, VOICE])).toBe('speak');
  });

  it('чужие роли на выбор не влияют', () => {
    expect(resolveSpeakOp([PIECE])).toBe('speak');
  });

  it('чип «Озвучить» несёт резолвленную операцию', () => {
    const op = (refs: AudioRefLike[]) => buildAudioActions({ ...base, state: 'draft', refs })[0].op;
    expect(op([])).toBe('speak');
    expect(op([SAMPLE])).toBe('cloneVoice');
    expect(op([SAMPLE, VOICE])).toBe('speak');
  });
});
