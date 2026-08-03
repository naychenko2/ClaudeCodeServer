// Тексты ошибок ридера — готовы к вставке (Софья), см. постановку задачи и ADR-005 §6.
// Тон нейтральный, без danger-заливки: часть сайтов не читается — это норма, а не поломка.
import type { ReaderErrorCode } from '../../../types';

export type ReaderErrorAction = 'retry' | 'browser' | 'close';

export interface ReaderErrorCopy {
  reason: string;
  actions: ReaderErrorAction[];   // порядок = порядок кнопок, главная всегда первой
}

export const READER_ERROR_COPY: Record<ReaderErrorCode, ReaderErrorCopy> = {
  'invalid-url':      { reason: 'Это не похоже на адрес страницы.', actions: ['close'] },
  'local-address':    { reason: 'Адрес ведёт в вашу локальную сеть — такие страницы сервер не открывает.', actions: ['browser'] },
  'dns-failed':       { reason: 'Такой сайт не нашёлся — возможно, в адресе опечатка.', actions: ['browser'] },
  'unreachable':      { reason: 'Сайт не отвечает.', actions: ['retry', 'browser'] },
  'tls-invalid':      { reason: 'У сайта неисправен сертификат безопасности.', actions: ['browser'] },
  'timeout':          { reason: 'Сайт отвечал слишком долго.', actions: ['retry', 'browser'] },
  'auth-required':    { reason: 'Страница требует входа, а сервер открывает её без ваших логинов.', actions: ['browser'] },
  'blocked-by-site':  { reason: 'Сайт не пустил: он отличает чтение роботом от обычного посещения.', actions: ['browser'] },
  'not-found':        { reason: 'Страница не найдена или закрыта.', actions: ['browser'] },
  'server-error':     { reason: 'Ошибка на стороне сайта.', actions: ['retry', 'browser'] },
  'too-many-redirects': { reason: 'Сайт слишком долго перекидывал с адреса на адрес.', actions: ['browser'] },
  'not-a-page':       { reason: 'По ссылке файл, а не страница.', actions: ['browser'] },
  pdf:                { reason: 'Это PDF — браузер покажет его лучше.', actions: ['browser'] },
  'too-large':        { reason: 'Страница слишком большая, чтобы показать её рядом.', actions: ['browser'] },
  'not-readable':     { reason: 'На странице не нашлось текста статьи — она собирается уже в браузере.', actions: ['browser'] },
};
