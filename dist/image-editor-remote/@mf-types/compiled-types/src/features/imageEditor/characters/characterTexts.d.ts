export declare const CHARACTER_PHOTOS_NOTICE = "\u0424\u043E\u0442\u043E \u0431\u0443\u0434\u0443\u0442 \u043E\u0442\u043F\u0440\u0430\u0432\u043B\u044F\u0442\u044C\u0441\u044F \u0432\u044B\u0431\u0440\u0430\u043D\u043D\u043E\u043C\u0443 \u0441\u0435\u0440\u0432\u0438\u0441\u0443 \u0440\u0438\u0441\u043E\u0432\u0430\u043D\u0438\u044F \u043F\u0440\u0438 \u043A\u0430\u0436\u0434\u043E\u0439 \u0433\u0435\u043D\u0435\u0440\u0430\u0446\u0438\u0438. \u0423 Higgsfield \u043E\u043D\u0438 \u043F\u043E\u043F\u0430\u0434\u0443\u0442 \u0432 \u043E\u0431\u0449\u0438\u0439 \u0430\u043A\u043A\u0430\u0443\u043D\u0442 \u0430\u0434\u043C\u0438\u043D\u0438\u0441\u0442\u0440\u0430\u0442\u043E\u0440\u0430. \u0417\u0430\u0433\u0440\u0443\u0436\u0430\u0439\u0442\u0435 \u0444\u043E\u0442\u043E \u0447\u0443\u0436\u0438\u0445 \u043B\u044E\u0434\u0435\u0439 \u0442\u043E\u043B\u044C\u043A\u043E \u0441 \u0438\u0445 \u0441\u043E\u0433\u043B\u0430\u0441\u0438\u044F";
export declare const LIST_STATUS: {
    readonly emptySubtitle: "Персонаж — это 3–10 фото лица. Обучите его — и сходство на картинках станет заметно точнее.";
    readonly noPhotos: "Нет фото";
    readonly recognized: (n: number) => string;
    readonly running: "Обучается";
    readonly queued: "В очереди";
    readonly trained: "Обучен";
    readonly failed: "Не получилось обучить";
};
export declare const SAY: {
    readonly ready: "Узнаётся по фото. Обучите — и сходство на картинках станет заметно точнее.";
    readonly needTrain: (k: number) => string;
    readonly running: "Учится быть похожим. Можно закрыть панель — обучение идёт само.";
    readonly queued: "Ждёт своей очереди на видеокарте. Можно закрыть панель — обучение начнётся само.";
    readonly trained: (title: string) => string;
    readonly trainedOwn: "Рисуется по своей обученной версии:";
    readonly failed: "Что-то пошло не так на видеокарте. Ваши фото не потерялись — можно попробовать ещё раз.";
    readonly failedLost: "Обучение прервалось: видеокарта его потеряла. Фото на месте — можно начать заново.";
    readonly failedAllRejected: "Ни одно фото не подошло: на них не нашли лицо. Загрузите другие.";
};
export declare const METER: {
    readonly title: "Насколько похож";
    readonly stops: {
        readonly recognized: "Узнаётся";
        readonly canTrain: "Можно обучать";
        readonly similar: "Заметно похож";
        readonly queued: "В очереди";
        readonly failed: "Не получилось";
    };
    readonly badgeDone: "Обучен";
    readonly badgeFailed: "Ошибка";
    readonly running: (p?: number | null) => string;
    readonly facePhotos: (n: number) => string;
    readonly trainPhotos: (n: number) => string;
    readonly trainNeed: (n: number, min: number) => string;
    readonly afterTrain: "после обучения";
    readonly queuedNote: "ждёт видеокарту";
    readonly eta: (t: string) => string;
    readonly trainedOn: (d: string) => string;
    readonly own: "своя версия";
    readonly retry: "попробуйте снова";
};
export declare const HERO: {
    readonly train: "Обучить персонажа";
    readonly addTrainPhotos: "Добавить фото для обучения";
    readonly toChatContext: "В контекст чата";
    readonly replaceTrainPhotos: "Заменить фото для обучения";
    readonly retry: "Попробовать снова";
    readonly cancel: "Отменить обучение";
    readonly trainMeta: "На видеокарте AI Home · около 2 часов";
    readonly trainNeedMeta: (n: number) => string;
    readonly noChat: "Откройте чат, чтобы положить персонажа в контекст";
    readonly fixTrigger: "Слово-метка не подходит — исправьте его в «Тонкой настройке».";
};
export declare const CHIPS: {
    readonly photos: "Фото";
    readonly versions: "Версии";
    readonly expert: "Тонкая настройка";
    readonly photosNeed: "Нужны фото для обучения";
};
export declare const MENU: {
    readonly nameDesc: "Имя и описание";
    readonly photos: "Фото";
    readonly versions: "Версии";
    readonly expert: "Тонкая настройка";
    readonly remove: "Удалить персонажа";
};
export declare const SHEET_NAME_MAX = 24;
export declare const SHEET: {
    readonly photos: (name: string) => string;
};
export declare const FACES: {
    readonly title: "Лицо для узнавания";
    readonly counter: (n: number) => string;
    readonly hint: "Сервис рисования получает эти фото с каждой картинкой. Первое — главное.";
    readonly hintLabels: "Метки ракурса и эмоции помогают взять фото под сюжет — нажмите на фото, чтобы поправить.";
    readonly consentFirst: "Сначала отметьте согласие ниже";
    readonly consentLabel: "Понимаю, у людей на фото есть согласие";
    readonly minReached: "У персонажа должно остаться хотя бы 3 фото";
    readonly add: "Ещё фото";
    readonly upload: "Загрузить";
    readonly remove: "Убрать фото";
};
export declare const TRAIN_PHOTOS: {
    readonly title: "Для обучения";
    readonly hint: "Нужны только для обучения на видеокарте AI Home — в сервисы рисования не уходят. 20–50 фото одного человека: лицо хорошо видно, разные ракурсы, свет и фон.";
};
export declare const EXPERT: {
    readonly title: "Тонкая настройка";
    readonly triggerLabel: "Слово-метка";
    readonly strengthLabel: "Сила сходства";
    readonly strengthHint: "По умолчанию 0,8. Слишком большая сила искажает картинку.";
    readonly ownLabel: "Своя обученная версия";
    readonly ownNone: "Не выбрана — по обучению AI Home";
    readonly ownHint: "Файл из ComfyUI. Если выбрана, персонаж рисуется по ней, а не по версиям обучения.";
    readonly restart: "Обучить заново";
};
export declare const VERSIONS_OWN: {
    readonly title: "Сейчас подключена своя обученная версия";
};
export declare const REMOVE_PHOTO: {
    readonly title: "Убрать фото?";
    readonly body: "Фото удалится из папки персонажа. Если это главное, главным станет следующее.";
    readonly confirm: "Убрать";
    readonly cancel: "Оставить";
};
export declare const CREATE: {
    readonly title: "Новый персонаж";
    readonly subtitle: "Загрузите 3–10 фото лица";
    readonly hint: "Первое станет главным. JPG, PNG или WebP";
    readonly mainTip: "Для главного подойдёт фото анфас на уровне глаз, лицо крупно, ровный фон.";
    readonly addDescription: "＋ Добавить описание";
    readonly submit: "Создать персонажа";
};
