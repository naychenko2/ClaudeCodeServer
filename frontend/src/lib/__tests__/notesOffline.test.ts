import { describe, it, expect, vi, beforeEach } from 'vitest';
import type { NoteSummary } from '../../types';

// Мокаем idb-обёртку: overlayNotesList читает записи только через noteContentAll.
const { noteContentAll } = vi.hoisted(() => ({ noteContentAll: vi.fn() }));
vi.mock('../idb', () => ({
  noteContentAll,
  // остальные экспорты idb в тестируемых путях не вызываются — заглушки
  idbGet: vi.fn(), idbSet: vi.fn(),
  noteContentGet: vi.fn(), noteContentPut: vi.fn(), noteContentDelete: vi.fn(),
  notesOutboxAll: vi.fn(), notesOutboxPut: vi.fn(), notesOutboxDelete: vi.fn(),
}));

import { mergeNoteOps, sliceFragment, overlayNotesList, type NotesOutboxOp, type NoteRecord } from '../notesOffline';

function op(kind: NotesOutboxOp['kind'], payload: NotesOutboxOp['payload']): NotesOutboxOp {
  return { opId: 1, localKey: 'k1', kind, payload, attempts: 0 };
}

describe('mergeNoteOps — коалесинг офлайн-очереди заметок', () => {
  it('create + update → один create со слитым payload', () => {
    const r = mergeNoteOps(op('create', { title: 'A', content: 'старое', source: 'personal' }), 'update', { content: 'новое' });
    expect(r).not.toBe('drop');
    if (r === 'drop') return;
    expect(r.kind).toBe('create');
    expect(r.payload.title).toBe('A');
    expect(r.payload.content).toBe('новое');
    expect(r.payload.source).toBe('personal');
  });

  it('create + delete → drop (заметка не дошла до сервера)', () => {
    expect(mergeNoteOps(op('create', { title: 'A' }), 'delete', {})).toBe('drop');
  });

  it('update + update → слияние, позднее перекрывает', () => {
    const r = mergeNoteOps(op('update', { content: 'v1' }), 'update', { content: 'v2' });
    if (r === 'drop') throw new Error('не drop');
    expect(r.kind).toBe('update');
    expect(r.payload.content).toBe('v2');
  });

  it('update + delete → delete', () => {
    const r = mergeNoteOps(op('update', { content: 'x' }), 'delete', {});
    if (r === 'drop') throw new Error('не drop');
    expect(r.kind).toBe('delete');
  });

  it('delete терминален — последующий update не меняет', () => {
    const r = mergeNoteOps(op('delete', {}), 'update', { content: 'z' });
    if (r === 'drop') throw new Error('не drop');
    expect(r.kind).toBe('delete');
  });

  it('assignDefined не затирает существующее undefined-полем', () => {
    const r = mergeNoteOps(op('create', { title: 'A', content: 'тело' }), 'update', { content: undefined });
    if (r === 'drop') throw new Error('не drop');
    expect(r.payload.content).toBe('тело');   // undefined не перезаписал
  });
});

describe('sliceFragment — фрагмент по якорю-заголовку', () => {
  const doc = [
    '# Введение', 'вступление',
    '## Раздел A', 'тело A', '### Подраздел', 'тело подраздела',
    '## Раздел B', 'тело B',
  ].join('\n');

  it('возвращает секцию от заголовка до следующего того же/высшего уровня', () => {
    const frag = sliceFragment(doc, 'Раздел A');
    expect(frag).toContain('## Раздел A');
    expect(frag).toContain('тело A');
    expect(frag).toContain('### Подраздел');   // глубже — входит
    expect(frag).not.toContain('Раздел B');     // следующий раздел того же уровня — не входит
  });

  it('якорь не найден → весь контент', () => {
    expect(sliceFragment(doc, 'Такого нет')).toBe(doc);
  });

  it('регистронезависимый матч заголовка', () => {
    expect(sliceFragment(doc, 'раздел b')).toContain('тело B');
  });
});

describe('overlayNotesList — оверлей офлайн-создания/удаления поверх серверного списка', () => {
  beforeEach(() => noteContentAll.mockReset());

  const serverList: NoteSummary[] = [
    { id: 'srv-1', title: 'Серверная 1', source: 'personal', sourceLabel: 'Личный', path: 'a.md', tags: [], createdAt: '', updatedAt: '' },
    { id: 'srv-2', title: 'Серверная 2', source: 'personal', sourceLabel: 'Личный', path: 'b.md', tags: [], createdAt: '', updatedAt: '' },
  ];

  function rec(p: Partial<NoteRecord>): NoteRecord {
    return {
      localKey: 'lk', serverId: null, source: 'personal', path: 'x.md', title: 'X', content: '', tags: [],
      baseUpdatedAt: null, dirty: false, createdOffline: false, deletedOffline: false, localUpdatedAt: 0, ...p,
    };
  }

  it('нет записей → серверный список без изменений', async () => {
    noteContentAll.mockResolvedValue([]);
    expect(await overlayNotesList(serverList)).toEqual(serverList);
  });

  it('добавляет созданную офлайн (serverId=null) в начало', async () => {
    noteContentAll.mockResolvedValue([rec({ localKey: 'lk-new', createdOffline: true, dirty: true, title: 'Новая офлайн' })]);
    const res = await overlayNotesList(serverList);
    expect(res).toHaveLength(3);
    expect(res[0].id).toBe('lk-new');
    expect(res[0].title).toBe('Новая офлайн');
  });

  it('убирает удалённую офлайн (по serverId) из списка', async () => {
    noteContentAll.mockResolvedValue([rec({ localKey: 'lk-2', serverId: 'srv-2', deletedOffline: true })]);
    const res = await overlayNotesList(serverList);
    expect(res.map(n => n.id)).toEqual(['srv-1']);
  });

  it('уже синхронизированная запись (serverId есть, не dirty) список не меняет', async () => {
    noteContentAll.mockResolvedValue([rec({ localKey: 'lk-1', serverId: 'srv-1' })]);
    const res = await overlayNotesList(serverList);
    expect(res.map(n => n.id)).toEqual(['srv-1', 'srv-2']);
  });
});
