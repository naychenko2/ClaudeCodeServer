// Единый источник «центр занят другим окном» (автоскрытие страницы фичи, дефект 20.10).
//
// Страница фичи (FeatureCenter) — СОСЕД центра: при открытой она прячет центр
// (чат/файл/…) display:none. Чтобы страница уступала центр ЛЮБОМУ другому окну,
// «центр занят» вычисляется ОДНИМ предикатом, а не перечислением состояний,
// которое расходится с реальностью (уже расходилось: 3 major — видео-центр,
// доска на мобиле, командный центр).
//
// Новый центральный режим добавляется ОДИМ полем в CenterOccupancy — и попадает
// в автоскрытие страницы (WorkspacePage) и в centerFree/видео-центр
// (DesktopWorkspace) сразу, а не в россыпь обработчиков.
//
// Базовый чат НЕ «другое окно»: его выбор закрывает страницу отдельно
// (closeFeaturePage в handleSelectSession). Страница фичи — тоже не «занятость»
// (она и есть то, что прячет центр).

export interface CenterOccupancy {
  // Файл (openFile)
  file: boolean;
  // Просмотр коммита (openCommitSha)
  commit: boolean;
  // Карточка задачи (selectedTaskId)
  task: boolean;
  // Документ «Граф»/«Архитектура» (centerDoc)
  graph: boolean;
  // Ридер ссылок (reader.state.open)
  reader: boolean;
  // Студия персоны / создание (selectedPersonaId || personaCreating)
  persona: boolean;
  // Командный центр (teamCenterOpen)
  teamCenter: boolean;
  // Доска задач (видимое состояние: на мобиле — только в режиме «Задачи»)
  board: boolean;
  // Превью dev-сервиса (ccActivePreview)
  preview: boolean;
  // Терминал в центре — только на мобиле (leftTab 'tools'); на десктопе в рельсе
  terminalMobile: boolean;
  // Видео-центр (useVideoCenter): кадр или каталог в центральном острове
  videoCenter: boolean;
}

// «В центре что-то кроме базового чата/страницы фичи»: любое окно заняло центр.
export function centerOccupiedByOther(o: CenterOccupancy): boolean {
  return !!(
    o.file || o.commit || o.task || o.graph || o.reader
    || o.persona || o.teamCenter || o.board || o.preview
    || o.terminalMobile || o.videoCenter
  );
}

// Отпечаток занятости центра: для каждого ЗАНЯТОГО окна — «тип:идентичность».
// Идентичность (путь файла / id задачи / sha коммита / url ридера / id персоны/превью/
// терминала / тип документа/видео-центра) отличает СМЕНУ окна того же типа («файл → другой
// файл», «задача → другая задача», «коммит → другой коммит») от простого «окно открыто»:
// набор типов (старый отпечаток) при такой смене не менялся → автоскрытие страницы не
// срабатывало (проба Глеба, MAJOR). Окна без пер-экземплярной идентичности (teamCenter/board)
// дают пустую строку: для них отпечаток меняется только при открытии/закрытии (булев).
// Порядок стабильный (Object.keys по порядку объявления) — сравнение строк детерминировано.
export function centerFingerprint(
  occupancy: CenterOccupancy,
  identity: Record<keyof CenterOccupancy, string>,
): string {
  return (Object.keys(occupancy) as (keyof CenterOccupancy)[])
    .filter(k => occupancy[k])
    .map(k => `${k}:${identity[k] ?? ''}`)
    .join(',');
}

// Перечень полей CenterOccupancy как данные (для теста полноты). `satisfies Record<…, true>`
// заставляет компилятор требовать КАЖДОЕ поле интерфейса: добавил окно в CenterOccupancy,
// забыл здесь — tsc краснеет; тест полноты берёт ключи отсюда и проверяет покрытие.
export const CENTER_OCCUPANCY_KEYS = Object.keys({
  file: true, commit: true, task: true, graph: true, reader: true,
  persona: true, teamCenter: true, board: true, preview: true,
  terminalMobile: true, videoCenter: true,
} satisfies Record<keyof CenterOccupancy, true>) as (keyof CenterOccupancy)[];
