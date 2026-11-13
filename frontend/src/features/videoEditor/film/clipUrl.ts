// URL клипа фильма для <video>: файл проекта по ручке файлов. У личной области такой ручки нет
// (файлы «Личного» наружу отдаёт только версия сцены), поэтому превью строки — без видео
import { api } from 'aihome_shell/kit';
import { isPersonalScope } from '../scope';

export const filmClipUrl = (scope: string, file: string): string | null =>
  isPersonalScope(scope) ? null : api.files.fileUrl(scope, file);
