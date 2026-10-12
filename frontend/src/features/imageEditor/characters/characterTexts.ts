// Тексты новой панели персонажа — спецификация docs/mockups/character-panel-final-spec.md §10.1–10.7.
// Метки фото (§10.5) и согласие с метками лежат в фиче умного подбора, здесь их нет.
// Тексты, что уже есть в loraTrainTexts.ts, берутся оттуда по имени.

import { TRAIN_BADGE, TRAIN_TEXT } from './loraTrainTexts';

// Дословно из постановки — текст сверяет приёмка
export const CHARACTER_PHOTOS_NOTICE =
  'Фото будут отправляться выбранному сервису рисования при каждой генерации. У Higgsfield они попадут в общий аккаунт администратора. Загружайте фото чужих людей только с их согласия';

// Согласие при включённом character-smart-photos (ADR-027 §10): фото ещё и на разметку модели
export const CHARACTER_PHOTOS_NOTICE_LABELS =
  'Фото лица уходят выбранному сервису рисования при каждой генерации, а при загрузке — ещё и модели, которая размечает ракурс и эмоцию. У Higgsfield они попадут в общий аккаунт администратора. Загружайте фото чужих людей только с их согласия';

// Список: статусы строк и пустое состояние
export const LIST_STATUS = {
  emptySubtitle: 'Персонаж — это 3–10 фото лица. Обучите его — и сходство на картинках станет заметно точнее.',
  noPhotos: 'Нет фото',
  recognized: (n: number) => `Узнаётся · ${n} фото`,
  running: 'Обучается',
  queued: 'В очереди',
  trained: 'Обучен',
  failed: 'Не получилось обучить',
} as const;

// Реплика карточки под шкалой. Части, а не одна строка: «Обучите» и «{k} фото» выделяются жирным (C.textHeading)
export type SayPart = { text: string; bold?: boolean };
export const SAY = {
  ready: (): SayPart[] => [
    { text: 'Узнаётся по фото. ' },
    { text: 'Обучите', bold: true },
    { text: ' — и сходство на картинках станет заметно точнее.' },
  ],
  // Обучение недоступно (off/noLocal) — без призыва, только первая часть
  readyNoTrain: (): SayPart[] => [
    { text: 'Узнаётся по фото.' },
  ],
  needTrain: (k: number): SayPart[] => [
    { text: 'Узнаётся по фото. Чтобы обучить, нужно ещё ' },
    { text: `${k} фото`, bold: true },
    { text: ' для обучения.' },
  ],
  running: 'Учится быть похожим. Можно закрыть панель — обучение идёт само.',
  queued: 'Ждёт своей очереди на видеокарте. Можно закрыть панель — обучение начнётся само.',
  trained: (title: string) => `Заметно похож. Рисуется по версии «${title}».`,
  trainedOwn: 'Рисуется по своей обученной версии:',
  failed: TRAIN_TEXT.failedBody,
  failedLost: TRAIN_TEXT.failedLost,
  failedAllRejected: TRAIN_TEXT.failedAllRejected,
} as const;

// Шкала «Насколько похож»
export const METER = {
  title: 'Насколько похож',
  stops: {
    recognized: 'Узнаётся',
    canTrain: 'Можно обучать',
    similar: 'Заметно похож',
    queued: 'В очереди',
    failed: 'Не получилось',
  },
  badgeDone: TRAIN_BADGE.done,
  badgeFailed: TRAIN_BADGE.failed,
  running: (p?: number | null) => (p == null ? 'Обучается' : `Обучается · ${p}%`),
  facePhotos: (n: number) => `${n} фото лица`,
  trainPhotos: (n: number) => `${n} фото для обучения`,
  trainNeed: (n: number, min: number) => `${n} из ${min} для обучения`,
  afterTrain: 'после обучения',
  queuedNote: 'ждёт видеокарту',
  eta: (t: string) => `осталось ~${t}`,
  trainedOn: (d: string) => `обучен ${d}`,
  own: 'своя версия',
  retry: 'попробуйте снова',
} as const;

// Главная кнопка и мета под ней
export const HERO = {
  train: TRAIN_TEXT.title,
  addTrainPhotos: 'Добавить фото для обучения',
  toChatContext: 'В контекст чата',
  replaceTrainPhotos: 'Заменить фото для обучения',
  retry: TRAIN_TEXT.retry,
  cancel: TRAIN_TEXT.cancel,
  trainMeta: 'На видеокарте AI Home · около 2 часов',
  trainNeedMeta: (n: number) => `Нужно минимум 20 · сейчас ${n}`,
  noChat: 'Откройте чат, чтобы положить персонажа в контекст',
  fixTrigger: 'Слово-метка не подходит — исправьте его в «Тонкой настройке».',
} as const;

// Чипы-шторки
export const CHIPS = {
  photos: 'Фото',
  versions: 'Версии',
  expert: 'Тонкая настройка',
  photosNeed: 'Нужны фото для обучения',
} as const;

// Меню «⋯»
export const MENU = {
  nameDesc: 'Имя и описание',
  photos: 'Фото',
  versions: 'Версии',
  expert: 'Тонкая настройка',
  remove: 'Удалить персонажа',
} as const;

// Заголовки шторок. Склонения не делаем; длинное имя (больше 24 символов) заменяем общим заголовком
export const SHEET_NAME_MAX = 24;
export const SHEET = {
  photos: (name: string) => (name.length > SHEET_NAME_MAX ? 'Фото персонажа' : `Фото ${name}`),
} as const;

// Шторка «Фото»: лицо для узнавания
export const FACES = {
  title: 'Лицо для узнавания',
  counter: (n: number) => `${n} из 10`,
  hint: 'Сервис рисования получает эти фото с каждой картинкой. Первое — главное.',
  hintLabels: 'Метки ракурса и эмоции помогают взять фото под сюжет — нажмите на фото, чтобы поправить.',
  consentFirst: 'Сначала отметьте согласие ниже',
  consentLabel: 'Понимаю, у людей на фото есть согласие',
  minReached: 'У персонажа должно остаться хотя бы 3 фото',
  add: 'Ещё фото',
  upload: 'Загрузить',
  remove: 'Убрать фото',
} as const;

// Метки фото (ADR-027 §1, спека character-panel-final-spec §10.5)
export const LABELS = {
  // Поля редактора
  angle: 'Ракурс',
  pitch: 'Наклон головы',
  emotion: 'Эмоция',
  close: 'Закрыть метки',
  // Словари значений (ключ — значение из ADR-027)
  angleOptions: [
    { value: 'front', label: 'Анфас' },
    { value: 'three-quarter', label: 'Вполоборота' },
    { value: 'profile', label: 'Профиль' },
  ] as const,
  pitchOptions: [
    { value: 'level', label: 'Прямо' },
    { value: 'up', label: 'Вверх' },
    { value: 'down', label: 'Вниз' },
  ] as const,
  emotionOptions: [
    { value: 'neutral', label: 'Спокойное' },
    { value: 'smile', label: 'Улыбка' },
    { value: 'laugh', label: 'Смех' },
    { value: 'serious', label: 'Серьёзное' },
    { value: 'sad', label: 'Грустное' },
    { value: 'surprised', label: 'Удивлённое' },
  ] as const,
  // Подпись под фото: эмоция — со строчной («Анфас · спокойное»)
  captionAngle: { front: 'Анфас', 'three-quarter': 'Вполоборота', profile: 'Профиль' } as Record<string, string>,
  captionEmotion: { neutral: 'спокойное', smile: 'улыбка', laugh: 'смех', serious: 'серьёзное', sad: 'грустное', surprised: 'удивлённое' } as Record<string, string>,
  // Состояния
  pending: 'Размечаем…',
  none: 'Без меток',
  // Источник разметки
  srcAuto: 'Разметила модель. Ошиблась — поправьте, ваша правка останется.',
  srcHuman: 'Поправлено вами — авторазметка эту метку не тронет.',
  srcPending: 'Модель ещё размечает это фото.',
  srcNone: 'Модель не разметила это фото. Выберите метки сами.',
  saveFailed: 'Не получилось сохранить метку. Попробуйте ещё раз.',
} as const;

// Шторка «Фото»: для обучения
export const TRAIN_PHOTOS = {
  title: 'Для обучения',
  hint: 'Нужны только для обучения на видеокарте AI Home — в сервисы рисования не уходят. 20–50 фото одного человека: лицо хорошо видно, разные ракурсы, свет и фон.',
} as const;

// Тонкая настройка
export const EXPERT = {
  title: 'Тонкая настройка',
  triggerLabel: 'Слово-метка',
  strengthLabel: 'Сила сходства',
  strengthHint: 'По умолчанию 0,8. Слишком большая сила искажает картинку.',
  ownLabel: 'Своя обученная версия',
  ownNone: 'Не выбрана — по обучению AI Home',
  ownHint: 'Файл из ComfyUI. Если выбрана, персонаж рисуется по ней, а не по версиям обучения.',
  restart: TRAIN_TEXT.restart,
} as const;

export const VERSIONS_OWN = {
  title: 'Сейчас подключена своя обученная версия',
} as const;

// Подтверждение «Убрать фото?»
export const REMOVE_PHOTO = {
  title: 'Убрать фото?',
  body: 'Фото удалится из папки персонажа. Если это главное, главным станет следующее.',
  confirm: 'Убрать',
  cancel: 'Оставить',
} as const;

// Создание
export const CREATE = {
  title: 'Новый персонаж',
  subtitle: 'Загрузите 3–10 фото лица',
  hint: 'Первое станет главным. JPG, PNG или WebP',
  mainTip: 'Для главного подойдёт фото анфас на уровне глаз, лицо крупно, ровный фон.',
  addDescription: '＋ Добавить описание',
  submit: 'Создать персонажа',
} as const;
