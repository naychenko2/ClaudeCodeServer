// Тексты новой панели персонажа — спецификация docs/mockups/character-panel-final-spec.md §10.1–10.7.
// Метки фото (§10.5) и согласие с метками лежат в фиче умного подбора, здесь их нет.
// Тексты, что уже есть в loraTrainTexts.ts, берутся оттуда по имени.

import { TRAIN_BADGE, TRAIN_TEXT } from './loraTrainTexts';

// Дословно из постановки — текст сверяет приёмка
export const CHARACTER_PHOTOS_NOTICE =
  'Фото будут отправляться выбранному сервису рисования при каждой генерации. У Higgsfield они попадут в общий аккаунт администратора. Загружайте фото чужих людей только с их согласия';

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

// Реплика карточки под шкалой
export const SAY = {
  ready: 'Узнаётся по фото. Обучите — и сходство на картинках станет заметно точнее.',
  needTrain: (k: number) => `Узнаётся по фото. Чтобы обучить, нужно ещё ${k} фото для обучения.`,
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
