// Тесты стора «где смотрят видео»: главный инвариант — центральный остров занимает
// РОВНО ОДИН обитатель, кадр или каталог. Живого кадра в продукте тоже один: панель
// снимает свой плеер по этому же стору, и разъехавшееся состояние дало бы два звука.
import { describe, it, expect, beforeEach } from 'vitest';
import {
  getVideoCenter, getVideoPicker, getVideoStage, setVideoPicker, setVideoStage, closeVideoCenter,
  setVideoCenterBlocked, getVideoCenterBlocked, setPanelChannel, getPanelChannel,
  clampRect, FLOAT_MIN_W, FLOAT_HEADER_H,
  setVideoSlot, getVideoSlots, videoFramePlace,
  getVideoPlayerState, setVideoPlayerState,
} from '../videoStage';
import type { VideoChannel } from '../../types';

const ch = (id: string): VideoChannel => ({
  id, provider: 'smotrim', title: `Канал ${id}`, embeddable: true,
  embedUrl: `https://example.test/${id}`,
});

// Стор — модульный синглтон: между тестами возвращаем его в исходное состояние
beforeEach(() => {
  setVideoCenterBlocked(false);
  setVideoPicker(false);
  setVideoStage(null);
  setPanelChannel(null);
  setVideoSlot('panel', null);
  setVideoSlot('center', null);
  setVideoPlayerState(false, false);
});

// Плеер один на продукт (живой кадр всегда один), и его кнопки живут в сторе:
// пауза у эфира — снятие кадра, у ролика — команда; смена канала чистит состояние,
// иначе новый канал приезжал бы с чужой паузой.
describe('состояние плеера', () => {
  it('пауза и тишина читаются и пишутся', () => {
    setVideoPlayerState(true, true);
    expect(getVideoPlayerState()).toEqual({ paused: true, muted: true });

    setVideoPlayerState(false, true);
    expect(getVideoPlayerState()).toEqual({ paused: false, muted: true });
  });

  it('смена канала панели сбрасывает паузу и тишину', () => {
    setPanelChannel(ch('a'));
    setVideoPlayerState(true, true);
    setPanelChannel(ch('b'));
    expect(getVideoPlayerState()).toEqual({ paused: false, muted: false });
  });

  it('тот же канал в другом режиме состояние не трогает', () => {
    setPanelChannel(ch('a'));
    setVideoPlayerState(true, false);
    setVideoStage(ch('a'), 'center');
    expect(getVideoPlayerState()).toEqual({ paused: true, muted: false });
  });

  it('смена канала развёрнутого кадра тоже сбрасывает', () => {
    setVideoStage(ch('a'), 'center');
    setVideoPlayerState(true, true);
    setVideoStage(ch('b'), 'center');
    expect(getVideoPlayerState()).toEqual({ paused: false, muted: false });
  });

  it('возврат кадра в панель сохраняет состояние — канал тот же', () => {
    setVideoStage(ch('a'), 'float');
    setVideoPlayerState(true, false);
    setVideoStage(null);
    expect(getVideoPlayerState()).toEqual({ paused: true, muted: false });
  });
});

// Центр, занятый файлом или задачей. Дыра, ради которой это тестируется: раньше
// запрет жил эффектом страницы по признаку «центр свободен», и центр, занятый
// ЗАРАНЕЕ, признака не менял — кадр, отправленный туда, пропадал совсем: панель
// снимала свой плеер, а рисовать его было уже некому.
describe('занятый центр', () => {
  it('кадр в занятый центр не уходит и остаётся в панели', () => {
    setVideoCenterBlocked(true);
    setVideoStage(ch('a'), 'center');

    expect(getVideoStage()).toBeNull();
    expect(getVideoCenter()).toBeNull();
  });

  it('каталог в занятый центр не открывается', () => {
    setVideoCenterBlocked(true);
    setVideoPicker(true);
    expect(getVideoPicker()).toBe(false);
  });

  it('занятие центра выселяет и кадр, и каталог', () => {
    setVideoStage(ch('a'), 'center');
    setVideoCenterBlocked(true);
    expect(getVideoStage()).toBeNull();

    setVideoCenterBlocked(false);
    setVideoPicker(true);
    setVideoCenterBlocked(true);
    expect(getVideoPicker()).toBe(false);
  });

  it('плавающее окно занятому центру не мешает и остаётся живым', () => {
    setVideoStage(ch('a'), 'float');
    setVideoCenterBlocked(true);

    expect(getVideoStage()?.mode).toBe('float');
    expect(getVideoCenterBlocked()).toBe(true);
  });
});

describe('обитатель центра', () => {
  it('пустой стор — центр свободен', () => {
    expect(getVideoCenter()).toBeNull();
  });

  it('кадр в центре виден как player, в окне — не виден вовсе', () => {
    setVideoStage(ch('a'), 'center');
    expect(getVideoCenter()).toBe('player');

    setVideoStage(ch('a'), 'float');
    expect(getVideoCenter()).toBeNull();
    expect(getVideoStage()?.mode).toBe('float');
  });

  it('каталог вытесняет кадр из центра, возвращая его в панель', () => {
    setVideoStage(ch('a'), 'center');
    setVideoPicker(true);

    expect(getVideoCenter()).toBe('picker');
    // Именно в панель, а не в окно: кадр продолжит идти сбоку, пока выбирают следующий
    expect(getVideoStage()).toBeNull();
  });

  it('каталог НЕ трогает плавающее окно — оно не в центре', () => {
    setVideoStage(ch('a'), 'float');
    setVideoPicker(true);

    expect(getVideoCenter()).toBe('picker');
    expect(getVideoStage()?.mode).toBe('float');
  });

  it('разворот кадра в центре закрывает каталог', () => {
    setVideoPicker(true);
    setVideoStage(ch('b'), 'center');

    expect(getVideoPicker()).toBe(false);
    expect(getVideoCenter()).toBe('player');
  });

  it('уход кадра в окно каталог не открывает обратно', () => {
    setVideoPicker(true);
    setVideoStage(ch('b'), 'center');
    setVideoStage(ch('b'), 'float');

    expect(getVideoCenter()).toBeNull();
  });

  it('закрытие центра снимает обоих обитателей', () => {
    setVideoStage(ch('a'), 'center');
    closeVideoCenter();
    expect(getVideoCenter()).toBeNull();
    expect(getVideoStage()).toBeNull();

    setVideoPicker(true);
    closeVideoCenter();
    expect(getVideoPicker()).toBe(false);
  });

  it('закрытие центра оставляет плавающее окно в покое', () => {
    setVideoStage(ch('a'), 'float');
    closeVideoCenter();
    expect(getVideoStage()?.mode).toBe('float');
  });
});

// Панель — место фонового просмотра, и канал для неё выбирают в каталоге: тот стоит
// в центре и до состояния панели иначе не дотянулся бы.
describe('канал боковой панели', () => {
  it('канал уходит в панель, не занимая центр', () => {
    setPanelChannel(ch('a'));
    expect(getPanelChannel()?.id).toBe('a');
    expect(getVideoCenter()).toBeNull();
  });

  it('выбор канала для панели возвращает туда же развёрнутый кадр', () => {
    setVideoStage(ch('a'), 'center');
    setPanelChannel(ch('b'));

    expect(getVideoStage()).toBeNull();
    expect(getPanelChannel()?.id).toBe('b');
  });

  it('«вернуть кадр в панель» реально кладёт его в панель, а не гасит', () => {
    setVideoStage(ch('a'), 'center');
    setVideoStage(null);
    expect(getPanelChannel()?.id).toBe('a');

    setVideoStage(ch('c'), 'float');
    setVideoStage(null);
    expect(getPanelChannel()?.id).toBe('c');
  });

  it('крестик центра и приход каталога тоже возвращают кадр в панель', () => {
    setVideoStage(ch('a'), 'center');
    closeVideoCenter();
    expect(getPanelChannel()?.id).toBe('a');

    setVideoStage(ch('b'), 'center');
    setVideoPicker(true);
    expect(getPanelChannel()?.id).toBe('b');
  });

  it('занятие центра файлом не гасит эфир, а уводит его в панель', () => {
    setVideoStage(ch('a'), 'center');
    setVideoCenterBlocked(true);
    expect(getPanelChannel()?.id).toBe('a');
  });
});

describe('геометрия плавающего окна', () => {
  it('высота считается из ширины по 16:9 плюс шапка', () => {
    const r = clampRect({ x: 10, y: 10, w: 320, h: 999 });
    expect(r.h).toBe(Math.round((r.w * 9) / 16) + FLOAT_HEADER_H);
  });

  it('уже минимума окно не становится', () => {
    expect(clampRect({ x: 0, y: 0, w: 10, h: 10 }).w).toBe(FLOAT_MIN_W);
  });

  it('окно за краем экрана возвращается кромкой внутрь', () => {
    const r = clampRect({ x: 99999, y: 99999, w: 400, h: 300 });
    // Без окна браузера стор меряет по запасному экрану 1280×800. Важен сам факт
    // возврата: кромка обязана остаться в пределах, иначе окно нечем поймать мышью
    expect(r.x).toBeLessThan(1280);
    expect(r.y).toBeLessThan(800);
  });
});

// Место живого кадра. Кадр рисует ОДИН оверлей над страницами (иначе эфир умирал бы
// при смене проекта вместе со страницей), и он обязан однозначно понимать, куда
// встать — и когда сняться совсем.
describe('место живого кадра', () => {
  it('канал панели держит кадр в панели', () => {
    expect(videoFramePlace(null, ch('a'))).toBe('panel');
  });

  it('развёрнутый кадр уводит место в центр', () => {
    expect(videoFramePlace({ channel: ch('a'), mode: 'center' }, ch('a'))).toBe('center');
  });

  it('плавающее окно рисует кадр само — оверлею места нет', () => {
    // Иначе получилось бы два живых iframe одного эфира: окно и оверлей
    expect(videoFramePlace({ channel: ch('a'), mode: 'float' }, ch('a'))).toBeNull();
  });

  it('без канала места нет', () => {
    expect(videoFramePlace(null, null)).toBeNull();
  });
});

describe('слоты под кадр', () => {
  const slot = (x: number) => ({ frame: { x, y: 0, w: 320, h: 180 }, clip: { x, y: 0, w: 320, h: 200 } });

  it('панель и центр держат свои прямоугольники независимо', () => {
    setVideoSlot('panel', slot(10));
    setVideoSlot('center', slot(500));

    expect(getVideoSlots().panel?.frame.x).toBe(10);
    expect(getVideoSlots().center?.frame.x).toBe(500);
  });

  it('снятие слота освобождает только своё место', () => {
    setVideoSlot('panel', slot(10));
    setVideoSlot('center', slot(500));
    setVideoSlot('panel', null);

    expect(getVideoSlots().panel).toBeNull();
    expect(getVideoSlots().center).not.toBeNull();
  });

  it('равная геометрия не считается изменением', () => {
    // Петля измерения зовёт стор на каждом кадре: публикуй он одинаковое, оверлей
    // перерисовывался бы шестьдесят раз в секунду впустую
    setVideoSlot('panel', slot(10));
    const before = getVideoSlots();
    setVideoSlot('panel', slot(10));

    expect(getVideoSlots()).toBe(before);
  });
});
