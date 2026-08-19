import { describe, it, expect, beforeEach } from 'vitest';
import { talkMark, talkDiagDump, talkDiagResetCycles } from '../talkDiag';

// Тайминги круга разговора: без них улучшать задержку «замолчал → услышал ответ»
// нечем — в потоке событий интервалы глазами не читаются.
describe('talkMark — тайминги круга разговора', () => {
  beforeEach(() => talkDiagResetCycles());

  it('круг открывается концом речи, метки попадают в сводку дампа', () => {
    talkMark('speech-end');
    talkMark('send');
    talkMark('turn-start');
    talkMark('first-audio');

    const dump = talkDiagDump();
    expect(dump).toContain('тайминги кругов');
    expect(dump).toContain('первый звук');
    expect(dump).toContain('кругов со звуком: 1');
  });

  it('метки без открытого круга игнорируются', () => {
    talkMark('first-audio'); // озвучка вне петли разговора
    expect(talkDiagDump()).not.toContain('тайминги кругов');
  });

  it('повторная метка круг не смазывает — первая побеждает', () => {
    talkMark('speech-end');
    talkMark('first-audio');
    talkMark('first-audio');

    const rows = talkDiagDump().split('\n').filter(l => l.startsWith('# '));
    expect(rows).toHaveLength(1);
  });

  it('новый конец речи открывает следующий круг', () => {
    talkMark('speech-end');
    talkMark('first-audio');
    talkMark('speech-end');
    talkMark('first-audio');

    expect(talkDiagDump()).toContain('кругов со звуком: 2');
  });
});
