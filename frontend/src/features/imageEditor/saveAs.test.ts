import { describe, expect, it } from 'vitest';
import type { FileEntry } from '../../types';
import { checkKey, defaultStem, foldersTarget, nameStem, personalImageFolders, projectFolders, saveBlocked, shownFolders, suggestionStem, type SaveCheckState } from './saveAs';

const entry = (path: string, isDirectory = false): FileEntry =>
  ({ name: path.split('/').pop() ?? path, path, isDirectory, modified: '', isModified: false });

describe('«Сохранить как…»: имя', () => {
  it('вписанное расширение срезается, а не удваивается', () => {
    expect(nameStem('hero.png')).toBe('hero');
    expect(nameStem(' hero.v3.PNG ')).toBe('hero.v3');
    expect(nameStem('photo.jpeg')).toBe('photo');
    expect(nameStem('hero.v2')).toBe('hero.v2');
  });

  it('по умолчанию — следующая версия исходника без расширения', () => {
    expect(defaultStem('hero.png')).toBe('hero.v2');
    expect(defaultStem('hero.v2.png')).toBe('hero.v3');
  });

  it('подсказка сервера подставляется именем без папки и расширения', () => {
    expect(suggestionStem('images/generated/hero.v3.png')).toBe('hero.v3');
  });
});

describe('«Сохранить как…»: кнопка', () => {
  const key = checkKey('images', 'hero.v2', 'png');
  const free: SaveCheckState = { key, result: { path: 'images/hero.v2.png', taken: false, suggestion: null }, error: null };
  const taken: SaveCheckState = { key, result: { path: 'images/hero.v2.png', taken: true, suggestion: 'images/hero.v3.png' }, error: null };

  it('свободное проверенное имя — можно сохранять', () => {
    expect(saveBlocked('hero.v2', key, free)).toBe(false);
  });

  it('занятое имя блокирует кнопку', () => {
    expect(saveBlocked('hero.v2', key, taken)).toBe(true);
  });

  it('пустое, ещё не проверенное или отвергнутое имя блокирует кнопку', () => {
    expect(saveBlocked('', key, free)).toBe(true);
    expect(saveBlocked('hero.v2', key, null)).toBe(true);
    expect(saveBlocked('hero.v2', checkKey('images/blog', 'hero.v2', 'png'), free)).toBe(true);
    expect(saveBlocked('hero.v2', key, { key, result: null, error: 'Путь вне папки проекта' })).toBe(true);
  });

  it('смена формата требует новой проверки: у WebP своё расширение', () => {
    expect(saveBlocked('hero.v2', checkKey('images', 'hero.v2', 'webp'), free)).toBe(true);
  });
});

describe('«Сохранить как…»: папки', () => {
  // Ф-1 «Медиа в личных чатах»: личная область просит дерево личных файлов, а не проекта
  it('личная область — псевдо-id личных файлов чата, проект — свой id', () => {
    expect(foldersTarget('personal', 'chat-1')).toBe('personal-files:chat-1');
    expect(foldersTarget('p1', 's3')).toBe('p1');
  });

  it('текущая папка в списке, даже если дерево её не отдало', () => {
    const list = shownFolders([{ path: 'images', files: 2 }], 'images/blog');
    expect(list.map(f => f.path)).toEqual(['images/blog', 'images']);
    expect(list[0]).toEqual({ path: 'images/blog', files: 0 });
  });

  it('папка, отданная деревом, не дублируется', () => {
    const tree = [{ path: 'images', files: 2 }];
    expect(shownFolders(tree, 'images')).toEqual(tree);
  });

  it('дерево ещё не пришло — список из одной текущей папки', () => {
    expect(shownFolders(null, 'images')).toEqual([{ path: 'images', files: 0 }]);
  });

  it('корень первым, число файлов прямо в папке', () => {
    const list = projectFolders([
      entry('README.md'), entry('images', true), entry('images/hero.png'), entry('images/logo.png'),
      entry('images/blog', true), entry('images/generated', true), entry('images/generated/hero.v2.png'),
    ]);
    expect(list).toEqual([
      { path: '', files: 1 },
      { path: 'images', files: 2 },
      { path: 'images/blog', files: 0 },
      { path: 'images/generated', files: 1 },
    ]);
  });
});

describe('«Сохранить как…»: папки личной области ⊆ допуску бэка', () => {
  // Бэк (PersonalMediaFolders.Resolve) принимает только images и images/**: остальное — 400,
  // а корень молча маппит в images/. Диалог не должен предлагать то, что сервер отвергнет.
  it('из дерева личной области остаются только images и её подпапки', () => {
    const tree = projectFolders([
      entry('notes.txt'), entry('audio', true), entry('audio/song.mp3'),
      entry('video', true), entry('video/clip.mp4'),
      entry('images', true), entry('images/hero.png'), entry('images/hero.v2.png'),
      entry('images/blog', true), entry('images/blog/post.png'),
    ]);
    expect(tree.map(f => f.path)).toEqual(['', 'audio', 'images', 'images/blog', 'video']);
    expect(personalImageFolders(tree).map(f => f.path)).toEqual(['images', 'images/blog']);
  });

  it('корень «Личного» не предлагается: бэк маппит пустой путь в images/, подпись лжёт', () => {
    const tree = projectFolders([entry('notes.txt'), entry('images', true), entry('images/hero.png')]);
    expect(personalImageFolders(tree).map(f => f.path)).toEqual(['images']);
  });

  it('похожая по имени папка не проходит: images-archive — не подпапка images/', () => {
    const tree = projectFolders([entry('images-archive', true), entry('images-archive/a.png')]);
    expect(personalImageFolders(tree)).toEqual([]);
  });

  it('файлы в images/ считаются в своей папке, фильтр счёт не теряет', () => {
    const tree = projectFolders([entry('images', true), entry('images/hero.png'), entry('images/blog', true)]);
    expect(personalImageFolders(tree)).toEqual([
      { path: 'images', files: 1 },
      { path: 'images/blog', files: 0 },
    ]);
  });

  // Х-9 (дефект 44718bbf) убрал виртуальную «Заметки» из личного корня на источнике
  // (PersonalFilesController.cs:115 зовёт List с withNotesRoot: false). Диалог не должен
  // предлагать «Заметки» даже если такая папка вернётся в листинге: список ⊆ images/**.
  it('папка notes/ в личном корне не предлагается — ни виртуальная, ни физическая', () => {
    const tree = projectFolders([
      entry('notes', true), entry('notes/idea.md'),
      entry('images', true), entry('images/hero.png'),
    ]);
    expect(tree.map(f => f.path)).toEqual(['', 'images', 'notes']);
    const shown = shownFolders(personalImageFolders(tree), 'images');
    expect(shown.map(f => f.path)).toEqual(['images']);
  });

  // «Личное пусто» — достижимое состояние (пункт 3 возврата QA-2): пустой корень не даёт
  // ни «Заметок», ни корня; диалог предлагает только медиальную папку сохранения images/
  it('пустой личный корень: список — предлагаемая images/, ничего больше', () => {
    const shown = shownFolders(personalImageFolders(projectFolders([])), 'images');
    expect(shown).toEqual([{ path: 'images', files: 0 }]);
  });
});
