// Ход выкатки прода (ADR-010) для карточки в ленте чата: разбор журнала
// GET /api/deploy/status в то, что показывает DeployProgressCard.
//
// Здесь только чистые функции и константы — опрос, тики времени и разметка живут в
// компоненте. Прогноз времени считается ЗДЕСЬ из истории прошлых выкаток: сервер ничего
// подобного не отдаёт и не должен (журнал — шов с внешним агентом, см. DeployState.cs).
//
// Инвариант карты шагов: агент выкатки обновляется отдельно от сервера, поэтому НЕЗНАКОМЫЙ
// шаг не исчезает — он попадает в группу предыдущего известного и рисуется сырым именем.
// Верхние четыре строки карточки ведутся по полю phase, а не угадываются по именам шагов.

import type { DeployJournalRecord, DeployJournalStep } from './api';

// Фазы журнала (DeployPhases на бэке)
export type DeployPhase =
  | 'queued' | 'building' | 'switching' | 'verifying'
  | 'succeeded' | 'rolled_back' | 'failed';

// Группа шагов = фаза, в которой они идут. Их четыре — это и есть верхние строки карточки.
export const GROUP_ORDER = ['queued', 'building', 'switching', 'verifying'] as const;
export type DeployGroup = typeof GROUP_ORDER[number];

// Подписи фаз: title — нейтральная (в title-атрибуте), done/run — состояния строки,
// short — мобильная раскладка, где на длинную формулировку нет ширины
export const GROUPS: Record<DeployGroup, { title: string; done: string; run: string; short: string }> = {
  queued: { title: 'Заявка принята', done: 'Заявка принята', run: 'Жду агента', short: 'Заявка принята' },
  building: { title: 'Собираю новую версию', done: 'Собрал новую версию', run: 'Собираю новую версию', short: 'Собираю' },
  switching: { title: 'Переключаю на новую версию', done: 'Переключил на новую версию', run: 'Переключаю на новую версию', short: 'Переключаю' },
  verifying: { title: 'Проверяю, что прод отвечает', done: 'Прод отвечает', run: 'Проверяю, что прод отвечает', short: 'Проверяю прод' },
};

// Карта известных шагов агента: сырое имя → человеческая подпись и группа.
// Шага здесь нет — строка всё равно рисуется, сырым именем (см. buildStepRows).
export const STEP_LABELS: Record<string, { ru: string; group: DeployGroup }> = {
  'frontend': { ru: 'фронтенд', group: 'building' },
  'staging-clean': { ru: 'чищу staging', group: 'building' },
  'publish-backend': { ru: 'бэкенд', group: 'building' },
  'publish-conpty': { ru: 'консольный хост', group: 'building' },
  'publish-tray': { ru: 'трей', group: 'building' },
  'frontend-copy': { ru: 'кладу фронтенд в сборку', group: 'building' },
  'mcp': { ru: 'MCP-серверы', group: 'building' },
  'mcp-dify': { ru: 'MCP Dify', group: 'building' },
  'workflows': { ru: 'воркфлоу', group: 'building' },
  'sandbox-image': { ru: 'образ песочницы', group: 'building' },
  'data-backup': { ru: 'резервная копия данных', group: 'switching' },
  'stop': { ru: 'останавливаю прод', group: 'switching' },
  'snapshot': { ru: 'снимок текущего релиза', group: 'switching' },
  'swap': { ru: 'подменяю сборку', group: 'switching' },
  'sandbox-container': { ru: 'пересоздаю песочницу', group: 'switching' },
  'start': { ru: 'запускаю прод', group: 'switching' },
  'health': { ru: 'проверка здоровья', group: 'verifying' },
};

// С какого шага сервер мёртв: от него и до конца переключения связи нет.
const DEAD_FROM_STEP = 'stop';

// Регистр нормализуем, как в outcomeState и mapStepStatus: фазу пишет ВНЕШНИЙ агент, и
// 'Succeeded' без result иначе подвесил бы карточку на «идёт» вместо итога.
export function isTerminalPhase(phase: string | null | undefined): boolean {
  const s = (phase ?? '').toLowerCase();
  return s === 'succeeded' || s === 'rolled_back' || s === 'failed';
}

export function phaseGroup(phase: string | null | undefined): DeployGroup {
  return (GROUP_ORDER as readonly string[]).includes(phase ?? '') ? (phase as DeployGroup) : 'building';
}

// === Времена ===
// Журнал пишут ДВА процесса (сервер — UtcNow с 'Z', агент — своей рукой), поэтому любая
// длительность проверяется на вменяемость: кривая метка времени не должна давать «идёт
// 14:32:07» или отрицательные секунды.
const DAY_MS = 24 * 60 * 60 * 1000;

export function sane(ms: number | null | undefined): number | null {
  if (typeof ms !== 'number' || !Number.isFinite(ms) || ms < 0 || ms > DAY_MS) return null;
  return ms;
}

export function parseTime(iso: string | null | undefined): number | null {
  if (!iso) return null;
  const t = Date.parse(iso);
  return Number.isFinite(t) ? t : null;
}

// «41 с» / «2 мин 8 с» — длительность шага и фазы
export function durLabel(ms: number | null | undefined): string {
  const v = sane(ms);
  if (v === null) return '';
  const s = Math.round(v / 1000);
  if (s < 60) return `${s} с`;
  const m = Math.floor(s / 60);
  const r = s % 60;
  return r ? `${m} мин ${r} с` : `${m} мин`;
}

// «3:07» — секундомер идущего процесса; от часа и выше «1:04:09». Часы нужны потому, что
// в мёртвом окне счётчик тикает по локальному таймеру и при зависшем агенте уезжает за час,
// а «93:17» читается как ошибка вёрстки, а не как полтора часа.
export function clockLabel(ms: number): string {
  const s = Math.max(0, Math.round(ms / 1000));
  const h = Math.floor(s / 3600);
  const m = Math.floor(s / 60) % 60;
  const ss = String(s % 60).padStart(2, '0');
  return h > 0 ? `${h}:${String(m).padStart(2, '0')}:${ss}` : `${m}:${ss}`;
}

// «≈ 3 мин» — сколько осталось по прошлым выкаткам
export function etaLabel(ms: number): string {
  if (ms <= 45_000) return 'меньше минуты';
  return `≈ ${Math.round(ms / 60_000)} мин`;
}

// === Прогноз по истории ===

export interface DeployPlan {
  // Ожидаемая последовательность шагов с весами (медиана по успешным выкаткам)
  steps: { name: string; ms: number }[];
  totalMs: number;
  // Сколько обычно длится мёртвое окно: от шага stop до конца переключения
  deadMs: number;
}

// Сколько прошлых успешных выкаток берём на прогноз. Больше — устойчивее к выбросу,
// но и дольше тянется память о машине, которая с тех пор стала быстрее.
const PLAN_SAMPLE = 5;

// Типичная выкатка — это медиана, а не среднее: одна аномально долгая (например, с
// пересборкой образа песочницы) утянула бы прогноз вверх и врала бы человеку.
function median(values: number[]): number {
  const v = [...values].sort((a, b) => a - b);
  const mid = v.length >> 1;
  return Math.round(v.length % 2 === 1 ? v[mid] : (v[mid - 1] + v[mid]) / 2);
}

// Плана нет — истории нет: карточка тогда не показывает ни бара, ни прогноза.
// Пустой прогноз честнее выдуманного.
export function buildPlan(history: DeployJournalRecord[] | null | undefined): DeployPlan | null {
  const ok = (history ?? [])
    .filter(r => r.result?.status === 'succeeded' && (r.steps?.length ?? 0) > 0)
    .slice(0, PLAN_SAMPLE);
  if (ok.length === 0) return null;

  const acc = new Map<string, number[]>();
  for (const rec of ok) {
    for (const s of rec.steps ?? []) {
      const ms = sane(s.ms);
      if (ms === null) continue;
      const cur = acc.get(s.name);
      if (cur) cur.push(ms); else acc.set(s.name, [ms]);
    }
  }
  // Порядок шагов — по самой свежей успешной выкатке: она ближе всего к тому,
  // что агент сделает сейчас
  const steps = (ok[0].steps ?? []).map(s => {
    const a = acc.get(s.name);
    return { name: s.name, ms: a && a.length > 0 ? median(a) : (sane(s.ms) ?? 0) };
  });
  const totalMs = steps.reduce((a, s) => a + s.ms, 0);
  if (totalMs <= 0) return null;

  // Мёртвое окно: от stop до конца switching. Шага stop в плане нет (агент переименовал) —
  // берём всю фазу переключения: лучше грубая оценка, чем никакой
  const groupOf = (name: string) => STEP_LABELS[name]?.group;
  const stopAt = steps.findIndex(s => s.name === DEAD_FROM_STEP);
  const deadMs = steps
    .filter((s, i) => (stopAt >= 0 ? i >= stopAt : true) && groupOf(s.name) === 'switching')
    .reduce((a, s) => a + s.ms, 0);

  return { steps, totalMs, deadMs };
}

// === Шаги записи ===

export type StepStatus = 'done' | 'fail' | 'run' | 'wait';

export interface StepRow {
  name: string;                 // сырое имя шага — по нему человек ищет шаг в логе агента
  label: string | null;         // null = шаг незнаком, рисуем сырым именем
  group: DeployGroup;
  status: StepStatus;
  ms: number | null;
}

// Статусы пишет агент, и его словарь может расшириться: всё неопознанное считаем
// пройденным (шаг записан — значит, был), кроме явных «идёт» и «упал».
function mapStepStatus(raw: string | null | undefined): StepStatus {
  const s = (raw ?? '').toLowerCase();
  if (s === 'fail' || s === 'failed' || s === 'error') return 'fail';
  if (s === 'run' || s === 'running' || s === 'start' || s === 'started') return 'run';
  return 'done';
}

// Строки шагов: фактические из записи + ожидаемые из плана, которых ещё не было.
// Незнакомый шаг наследует группу предыдущего известного — так он не теряется
// и не сбивает раскладку.
export function buildStepRows(
  rec: DeployJournalRecord | null,
  plan: DeployPlan | null,
  running: boolean,
): StepRow[] {
  const actual = rec?.steps ?? [];
  const rows: StepRow[] = [];
  let prevGroup: DeployGroup = 'building';

  actual.forEach((s: DeployJournalStep, i) => {
    const known = STEP_LABELS[s.name];
    const group = known?.group ?? prevGroup;
    prevGroup = group;
    // Последний записанный шаг незавершённой выкатки — тот, который агент делает прямо
    // сейчас, даже если статуса «идёт» в его словаре нет
    const status: StepStatus = s.status
      ? mapStepStatus(s.status)
      : (running && i === actual.length - 1 ? 'run' : 'done');
    rows.push({
      name: s.name,
      label: known?.ru ?? null,
      group,
      status,
      ms: status === 'run' ? null : sane(s.ms),
    });
  });

  const seen = new Set(actual.map(s => s.name));
  for (const p of plan?.steps ?? []) {
    if (seen.has(p.name)) continue;
    const known = STEP_LABELS[p.name];
    rows.push({
      name: p.name,
      label: known?.ru ?? null,
      group: known?.group ?? prevGroup,
      status: 'wait',
      ms: null,
    });
    prevGroup = known?.group ?? prevGroup;
  }
  return rows;
}

// Сумма фактически потраченного времени по группе — длительность пройденной фазы
export function groupMs(rows: StepRow[], group: DeployGroup): number {
  return rows
    .filter(r => r.group === group && (r.status === 'done' || r.status === 'fail'))
    .reduce((a, r) => a + (r.ms ?? 0), 0);
}

// Сколько времени записано в шагах до начала группы — от него считается секундомер фазы
export function msBeforeGroup(rows: StepRow[], group: DeployGroup): number {
  const gi = GROUP_ORDER.indexOf(group);
  return rows
    .filter(r => GROUP_ORDER.indexOf(r.group) < gi)
    .reduce((a, r) => a + (r.ms ?? 0), 0);
}

// Шаг, на котором всё кончилось: первый упавший, иначе последний записанный
export function failedStep(rows: StepRow[]): StepRow | null {
  return rows.find(r => r.status === 'fail')
    ?? [...rows].reverse().find(r => r.status === 'done' || r.status === 'run')
    ?? null;
}

// «резервная копия данных · останавливаю прод · …» — чем агент занят, пока связи нет
export function switchingHint(plan: DeployPlan | null): string {
  const names = (plan?.steps ?? [])
    .filter(s => STEP_LABELS[s.name]?.group === 'switching')
    .map(s => STEP_LABELS[s.name]!.ru);
  if (names.length > 0) return names.join(' · ');
  return Object.values(STEP_LABELS).filter(s => s.group === 'switching').map(s => s.ru).join(' · ');
}

// === Состояние карточки ===

// Что карточка показывает прямо сейчас. Первые пять — выкатка идёт, последние три —
// итог агента. 'dead' стоит особняком: это НЕ ошибка, а плановая пауза, когда сервер
// остановлен и связи с ним нет.
export type CardState =
  | 'loading' | 'queued' | 'building' | 'switching' | 'verifying' | 'dead'
  | 'succeeded' | 'rolled_back' | 'failed';

// Итог агента: словарь у него свой и может расшириться, а показать надо всегда что-то
// осмысленное. Всё, что не «вернул прежнюю версию» и не явный успех, — провал.
export function outcomeState(status: string | null | undefined, ok: boolean | undefined): CardState {
  const s = (status ?? '').toLowerCase();
  if (s === 'succeeded' || s === 'ok' || s === 'success') return 'succeeded';
  if (s === 'rolled_back' || s === 'rolledback' || s === 'rolled-back') return 'rolled_back';
  if (s === 'failed' || s === 'fail' || s === 'error') return 'failed';
  return ok ? 'succeeded' : 'failed';
}

// Фаза идущей выкатки: незнакомую считаем сборкой — она длиннее всех и безобиднее
// прочих в качестве догадки
export function runPhaseState(phase: string | null | undefined): CardState {
  return phase === 'queued' || phase === 'building' || phase === 'switching' || phase === 'verifying'
    ? phase
    : 'building';
}

export interface StateInput {
  // Заявка принята и за ней есть что смотреть
  hasDeployId: boolean;
  phase: string | null;
  result: { status?: string | null; ok?: boolean } | null;
  // Журнал хоть раз прочитан в этой жизни карточки
  loaded: boolean;
  // Состояние восстановлено из метки мёртвого окна (страницу перезагрузили без сервера)
  restored: boolean;
  // Сервер ОТВЕЧАЕТ. Отказ (403/404/500) — тоже ответ: сервер жив, просто журнал не отдал
  reachable: boolean;
}

// Единственное место, где решается, что показывать. Вынесено из компонента, потому что
// главный инвариант фичи проверяется именно здесь: обрыв связи на непройденной выкатке
// НИКОГДА не даёт 'failed' — сервер в это время погашен намеренно.
export function deriveState(v: StateInput): CardState {
  if (!v.hasDeployId) return 'queued';
  if (v.result || isTerminalPhase(v.phase)) return outcomeState(v.result?.status ?? v.phase, v.result?.ok);
  if (!v.loaded && !v.restored) return 'loading';
  if (!v.reachable) return 'dead';
  return runPhaseState(v.phase);
}

// === Вызов инструмента ===

// tool_use выкатки: mcp__wsp__deploy_start. Сравнение по суффиксу и без регистра —
// префикс MCP-сервера зависит от имени, под которым он подключён к ходу.
export function isDeployStart(name: string): boolean {
  return name.toLowerCase().endsWith('__deploy_start');
}

// deployId из ответа инструмента. Ответ — JSON от workspace-server; отказ (409/400/503)
// приходит текстом ошибки, и тогда deployId нет — карточке не за что зацепиться.
export function parseDeployId(result: string | undefined): string | null {
  if (!result) return null;
  try {
    const j = JSON.parse(result) as { deployId?: unknown };
    if (typeof j?.deployId === 'string' && j.deployId) return j.deployId;
  } catch { /* не JSON — пробуем вытащить из текста */ }
  const m = /"deployId"\s*:\s*"([^"]+)"/.exec(result);
  return m ? m[1] : null;
}

// === Переживание мёртвого окна ===
//
// На входе в переключение кладём метку в localStorage: пока сервера нет, лента поднимается
// из кеша service worker и журнал прочитать не может — без метки карточка после перезагрузки
// страницы показала бы «читаю журнал» вместо «сервер перезапускается».
//
// Признак «выкатку заказали в ЭТОЙ вкладке» живёт отдельно и в sessionStorage: localStorage
// общий на все вкладки, а переезжать на новую версию сама должна только вкладка-инициатор.

const WATCH_KEY = 'cc_deploy_watch';
const INITIATOR_KEY = 'cc_deploy_initiator';
// Метка протухает: выкатка идёт минуты, а вкладку могли закрыть на сутки
const WATCH_TTL_MS = 30 * 60_000;

export interface DeployWatch {
  deployId: string;
  startedAt: string | null;
  phase: string;
  // Когда метку поставили — по ней считается протухание
  ts: number;
}

export function writeWatch(w: Omit<DeployWatch, 'ts'>): void {
  try {
    localStorage.setItem(WATCH_KEY, JSON.stringify({ ...w, ts: Date.now() }));
  } catch { /* приватный режим/переполнение — карточка проживёт и без метки */ }
}

export function readWatch(): DeployWatch | null {
  try {
    const raw = localStorage.getItem(WATCH_KEY);
    if (!raw) return null;
    const w = JSON.parse(raw) as DeployWatch;
    if (!w?.deployId || typeof w.ts !== 'number') return null;
    if (Date.now() - w.ts > WATCH_TTL_MS) { clearWatch(); return null; }
    return w;
  } catch { return null; }
}

export function clearWatch(deployId?: string): void {
  try {
    if (deployId) {
      const w = readWatchRaw();
      if (w && w.deployId !== deployId) return;
    }
    localStorage.removeItem(WATCH_KEY);
  } catch { /* нечего убирать */ }
}

function readWatchRaw(): DeployWatch | null {
  try {
    const raw = localStorage.getItem(WATCH_KEY);
    return raw ? JSON.parse(raw) as DeployWatch : null;
  } catch { return null; }
}

export function markInitiator(deployId: string): void {
  try { sessionStorage.setItem(INITIATOR_KEY, deployId); } catch { /* без метки просто не переедем сами */ }
}

export function isInitiator(deployId: string): boolean {
  try { return sessionStorage.getItem(INITIATOR_KEY) === deployId; } catch { return false; }
}

export function clearInitiator(deployId: string): void {
  try {
    if (sessionStorage.getItem(INITIATOR_KEY) === deployId) sessionStorage.removeItem(INITIATOR_KEY);
  } catch { /* нечего убирать */ }
}
