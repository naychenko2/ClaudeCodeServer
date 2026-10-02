// Русские подписи инструментов для ленты чата. Живут в lib, а не в компоненте карточки:
// ими же подписывается живой прогресс (toolTiming) — «сейчас Команда», а не сырое «Bash»

// Русские названия инструментов для ленты чата
const TOOL_LABELS: Record<string, string> = {
  read: 'Чтение', edit: 'Правка', write: 'Запись', multiedit: 'Правки',
  notebookedit: 'Правка ноутбука', bash: 'Команда', bashoutput: 'Вывод команды',
  glob: 'Поиск файлов', grep: 'Поиск', ls: 'Список', task: 'Субагент', agent: 'Субагент',
  websearch: 'Веб-поиск', webfetch: 'Загрузка страницы', skill: 'Навык',
  todowrite: 'План задач', exitplanmode: 'План', toolsearch: 'Поиск инструментов',
  taskcreate: 'Задача', taskupdate: 'Задача', tasklist: 'Список задач', taskget: 'Задача',
  killshell: 'Остановка команды',
};
// Русские подписи MCP-инструментов по полному имени (mcp__server__tool) —
// без них лента показывала бы сырое «glif · compose_project»
const MCP_TOOL_LABELS: Record<string, string> = {
  mcp__glif__compose_project: 'Генерация медиа glif',
  mcp__glif__get_job_status: 'Статус генерации glif',
  mcp__glif__view_media: 'Показ медиа glif',
  mcp__glif__upload_file: 'Загрузка файла glif',
  mcp__glif__get_project: 'Проекты glif',
  mcp__glif__list_projects: 'Проекты glif',
  mcp__glif__list_user_skills: 'Скиллы glif',
  mcp__glif__get_user_skill: 'Скиллы glif',
  mcp__glif__whoami: 'Аккаунт glif',
  mcp__tests__run_tests: 'Тесты',
  // Локальная генерация на своей GPU (MCP local-media): сырое «local-media · local_jobs_wait»
  // на мобиле съедало всю шапку
  'mcp__local-media__local_generate_image': 'Локальная картинка',
  'mcp__local-media__local_edit_image': 'Локальная правка картинки',
  'mcp__local-media__local_face_detail': 'Доводка лиц',
  'mcp__local-media__local_text_to_video': 'Локальное видео по тексту',
  'mcp__local-media__local_image_to_video': 'Локальное видео из картинки',
  'mcp__local-media__local_reference_to_video': 'Локальное видео по референсам',
  'mcp__local-media__local_video_upscale': 'Апскейл видео',
  'mcp__local-media__local_video_inpaint': 'Правка видео',
  'mcp__local-media__local_job_status': 'Статус локальной генерации',
  'mcp__local-media__local_jobs_wait': 'Локальная генерация',
  'mcp__local-media__local_models': 'Локальные модели',
};

// Ожидание локальной генерации (local_jobs_wait): описание в шапке — сколько задач ждём
export const LOCAL_JOBS_WAIT_TOOL = 'mcp__local-media__local_jobs_wait';
export function localJobsWaitArg(input: unknown): string {
  const ids = (input as { job_ids?: unknown } | null)?.job_ids;
  const n = Array.isArray(ids) ? ids.length : 0;
  if (n <= 0) return 'ожидание результата';
  const m10 = n % 10, m100 = n % 100;
  const word = m10 === 1 && m100 !== 11 ? 'задачи' : 'задач';
  return `ожидание ${n} ${word}`;
}
// Имя инструмента для показа: MCP — по карте, иначе «server · tool»; известные — по-русски, прочее — как есть
export function toolLabel(name: string): string {
  if (name.startsWith('mcp__')) return MCP_TOOL_LABELS[name] ?? name.slice(5).replace(/__/g, ' · ');
  return TOOL_LABELS[name.toLowerCase()] ?? name;
}

// Прогон тестов (run_tests): вид прогона виден в шапке карточки — «Тесты · vitest»
export const RUN_TESTS_TOOL = 'mcp__tests__run_tests';
const TEST_KIND_LABELS: Record<string, string> = { dotnet: 'dotnet', vitest: 'vitest', playwright: 'Playwright' };

// Вид прогона из аргумента kind; нет его — dotnet (как на сервере)
export function testRunKindLabel(input: unknown): string {
  const kind = (input as { kind?: unknown } | null)?.kind;
  return typeof kind === 'string' ? TEST_KIND_LABELS[kind.toLowerCase()] ?? kind : 'dotnet';
}

// Подпись шапки карточки: у прогона тестов — с видом, у прочих — обычное имя
export function toolCardLabel(name: string, input: unknown): string {
  return name === RUN_TESTS_TOOL ? `${toolLabel(name)} · ${testRunKindLabel(input)}` : toolLabel(name);
}

// Описание прогона тестов в шапке: цель, файлы и фильтр через « · »
export function testRunArg(input: unknown): string {
  const inp = (input ?? {}) as { target?: unknown; filter?: unknown; files?: unknown };
  const parts: string[] = [];
  if (typeof inp.target === 'string' && inp.target) parts.push(inp.target);
  if (Array.isArray(inp.files)) {
    const files = inp.files.filter((f): f is string => typeof f === 'string');
    if (files.length === 1) parts.push(files[0]);
    else if (files.length > 1) parts.push(`${files.length} ${filesWord(files.length)}`);
  }
  if (typeof inp.filter === 'string' && inp.filter) parts.push(inp.filter);
  return parts.join(' · ');
}

// Склонение слова «файл»
function filesWord(n: number): string {
  const m10 = n % 10, m100 = n % 100;
  if (m10 === 1 && m100 !== 11) return 'файл';
  if (m10 >= 2 && m10 <= 4 && (m100 < 10 || m100 >= 20)) return 'файла';
  return 'файлов';
}

// Склонение слова «действие»
export function toolWord(n: number): string {
  const m10 = n % 10, m100 = n % 100;
  if (m10 === 1 && m100 !== 11) return 'действие';
  if (m10 >= 2 && m10 <= 4 && (m100 < 10 || m100 >= 20)) return 'действия';
  return 'действий';
}
