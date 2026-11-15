import { beforeEach, describe, expect, it } from 'vitest';
import { __resetVoicesScope, enterVoicesScope, personalFilesPath, personalSoundFiles, voicesScopeBase } from './scope';

const file = (path: string, isDirectory = false) => ({ name: path.split('/').pop()!, path, isDirectory });

beforeEach(() => __resetVoicesScope());

describe('звук из «Личного»', () => {
  it('берёт только звуковые файлы, папки и прочее отбрасывает, порядок по пути', () => {
    const got = personalSoundFiles([
      file('b.MP3'), file('notes.txt'), file('audio', true), file('audio/a.wav'), file('video/clip.mp4'), file('c.m4a'),
    ]);
    expect(got.map(f => f.path)).toEqual(['audio/a.wav', 'b.MP3']);
  });

  it('путь личных файлов чата кодирует id', () => {
    expect(personalFilesPath('s/1')).toBe('/personal/chats/s%2F1/files');
  });
});

describe('база голосов личной области', () => {
  it('явный sessionId не зависит от порядка вызовов', () => {
    expect(voicesScopeBase('personal', 'chat-a')).toBe('/audio-editor/chats/chat-a');
    enterVoicesScope('personal', 'chat-b');
    expect(voicesScopeBase('personal', 'chat-a')).toBe('/audio-editor/chats/chat-a');
    expect(voicesScopeBase('personal')).toBe('/audio-editor/chats/chat-b');
  });

  it('без чата личная область отказывает, проектная — по проекту', () => {
    expect(() => voicesScopeBase('personal')).toThrow();
    expect(voicesScopeBase('p1', 'ignored')).toBe('/projects/p1/audio-editor');
  });
});
