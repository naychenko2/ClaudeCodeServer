// Вход из «Файлов» в контекст хода (слот context-opener, ADR-023): файл фильма проекта становится основным объектом «video-film».

import { FILM_KIND } from './state';

export const isFilmFile = (path: string) => /\.film$/i.test(path);

// Фильм — сам файл: нити заводить не нужно, ссылка строится по пути (наличие файла проверяет сервер при setPrimary)
export const filmRefOfPath = async (_projectId: string, _sessionId: string, path: string) =>
  ({ kind: FILM_KIND, ref: { filmPath: path } });
