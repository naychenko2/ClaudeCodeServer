// Карточка черновика (макет 4, ADR-031, шаг 4.4):
//  • «Принять» неактивна, пока проверка пакета не пройдена (список ошибок виден);
//  • состояния (отклонён / установлен / устарел) — по данным сервера;
//  • consentRequired → новые права выделены в существующем диалоге согласия;
//  • событие plugin_draft_changed обновляет карточку (подсветка «Черновик обновлён агентом»);
//  • ошибки сервера (403/404/409) — понятным текстом.
// Окружение node: статичная часть — renderToStaticMarkup (эффекты не гоняются);
// событие — на минимальном фейк-DOM (createRoot + act, паттерн fakeDom).

import { afterAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import type { ServerMessage } from '../../types';
import type {
  PluginDraftAcceptResult, PluginDraftDiff, PluginDraftPreview, PluginDraftSummary,
} from '../../lib/api';

// renderToStaticMarkup порталов не знает: createPortal → pass-through (как в ConsentDialog.test)
vi.mock('react-dom', async (orig) => ({
  ...(await orig<typeof import('react-dom')>()),
  createPortal: (n: unknown) => n,
}));

// window (matchMedia для useIsMobile) + document (порталы) — как в ConsentDialog.test
vi.stubGlobal('window', Object.assign(new EventTarget(), {
  innerWidth: 1440, innerHeight: 900,
  matchMedia: () => ({ matches: false, addEventListener: () => {}, removeEventListener: () => {} }),
}));
vi.stubGlobal('document', {
  body: { nodeType: 1 },
  head: { appendChild: () => {} },
  documentElement: { dataset: {} },
  createElement: () => ({ id: '', textContent: '' }),
  getElementById: () => ({}),
  querySelector: () => null,
  addEventListener: () => {},
});

// onMessage — захватываем подписчика, чтобы «сдвинуть» событие в тесте
const handlers: ((m: ServerMessage) => void)[] = [];
vi.mock('../../lib/signalr', () => ({
  onMessage: (h: (m: ServerMessage) => void) => { handlers.push(h); return () => {}; },
}));

// api.plugins.* — фиксированные ответы; значения подменяются в beforeEach
vi.mock('../../lib/api', () => ({
  api: {
    plugins: {
      draftsList: vi.fn(),
      draftPreview: vi.fn(),
      draftDiff: vi.fn(),
      draftAccept: vi.fn(),
      draftReject: vi.fn(),
      draftRestore: vi.fn(),
      draftPanel: vi.fn(),
    },
  },
}));

const { api } = await import('../../lib/api');
const { DraftCard, FilesDrawer } = await import('./DraftCard');
const { ConsentDialog } = await import('./ConsentDialog');
const { useDraftCard } = await import('./useDraftCard');
const { findNodeByAttr, installFakeDom, mount, reactProps, textNodes, uninstallFakeDom } = await import(
  '../../components/chat/__tests__/fakeDom'
);
type PluginConsentData = import('./ConsentDialog').PluginConsentData;

// === Фиксированные данные ===
const SLUG = 'local-1/my-plugin';

const PREVIEW: PluginDraftPreview = {
  slug: SLUG,
  name: 'My Plugin',
  version: '1.0.0',
  panels: [{ id: 'p1', slot: 'sidebar', title: 'Моя панель' }],
  commands: ['/my-cmd'],
  skills: ['my-skill'],
  requestedPermissions: ['notes:read', 'net:api.example.com'],
  secrets: [{ name: 'API_TOKEN', required: true }],
  files: [
    { path: 'panel/index.html', isDir: false, size: 120 },
    { path: 'commands/my-cmd.md', isDir: false, size: 80 },
  ],
  validation: { isValid: true, problems: [] },
};

// Проверка не пройдена: два вопроса
const PREVIEW_INVALID: PluginDraftPreview = {
  ...PREVIEW,
  validation: {
    isValid: false,
    problems: [
      { code: 'missing-manifest', message: 'Манифест ccs-plugin.json не найден' },
      { code: 'bad-perm', message: 'Неизвестное право foo:bar' },
    ],
  },
};

const DIFF: PluginDraftDiff = {
  status: 'updated',
  slug: SLUG,
  id: 'local-1/my-plugin',
  version: '1.0.0',
  installedVersion: '0.9.0',
  truncated: false,
  added: [{ path: 'panel/index.html', size: 120, binary: false, truncated: false, lines: null }],
  removed: [],
  modified: [{
    path: 'commands/my-cmd.md',
    size: 80,
    binary: false,
    truncated: false,
    lines: [
      { oldLine: 1, newLine: 1, kind: 'ctx', text: 'before' },
      { oldLine: 2, newLine: 0, kind: 'del', text: 'old line' },
      { oldLine: 0, newLine: 2, kind: 'add', text: 'new line' },
    ],
  }],
  manifest: { permissionsAdded: ['net:api.example.com'], permissionsRemoved: [], secretsAdded: [], secretsRemoved: [], secretsRequiredChanged: [] },
};

const ACCEPT: PluginDraftAcceptResult = {
  id: 'local-1/my-plugin',
  version: '1.0.0',
  sha256: 'abcdef1234567890',
  source: 'upload:my-plugin',
  enabled: true,
  requested: ['notes:read', 'net:api.example.com'],
  granted: ['notes:read'],
  consentRequired: true,
  newPermissions: ['net:api.example.com'],
  secrets: [{ name: 'API_TOKEN', set: false }],
};

const summary = (over: Partial<PluginDraftSummary> = {}): PluginDraftSummary => ({
  slug: SLUG,
  id: 'local-1/my-plugin',
  version: '1.0.0',
  updatedAt: '2026-10-01T00:00:00Z',
  problems: 0,
  installedSameVersion: false,
  rejected: false,
  expiresAt: null,
  ...over,
});

// Рендер карточки в строку (статика: эффекты не гоняются)
function renderCard(props: {
  summary?: PluginDraftSummary;
  preview?: PluginDraftPreview | null;
  diff?: PluginDraftDiff | null;
  loading?: boolean;
  error?: string | null;
  justUpdated?: boolean;
  staleNewerSlug?: string | null;
}) {
  const p = {
    summary: props.summary ?? summary(),
    // undefined → PREVIEW; явная null (ошибка сервера) сохраняется
    preview: props.preview === undefined ? PREVIEW : props.preview,
    diff: props.diff ?? null,
    loading: props.loading ?? false,
    error: props.error ?? null,
    justUpdated: props.justUpdated ?? false,
    staleNewerSlug: props.staleNewerSlug ?? null,
    onAccept: () => {},
    onReject: () => {},
    onRestore: () => {},
    onEdit: () => {},
    onLoadDiff: () => {},
  };
  return renderToStaticMarkup(createElement(DraftCard, p));
}

// === «Принять» блокируется, пока проверка не пройдена ===
describe('DraftCard: блокировка «Принять»', () => {
  it('проверка не пройдена → «Принять…» неактивна, список ошибок виден', () => {
    const html = renderCard({ preview: PREVIEW_INVALID });
    // ровно одна неактивная кнопка — «Принять…»
    expect((html.match(/<button[^>]*disabled/g) ?? []).length).toBe(1);
    expect(html).toContain('Принять');
    // обе ошибки проверки в списке
    expect(html).toContain('Манифест ccs-plugin.json не найден');
    expect(html).toContain('Неизвестное право foo:bar');
    // вместо «Править» — «Попросить агента исправить»
    expect(html).toContain('Попросить агента исправить');
  });

  it('проверка пройдена → «Принять…» активна, «Править» на месте', () => {
    const html = renderCard({ preview: PREVIEW });
    expect(html).not.toMatch(/<button[^>]*disabled/);
    expect(html).toContain('Принять');
    expect(html).toContain('Править');
  });
});

// === Состояния — по данным сервера ===
describe('DraftCard: состояния', () => {
  it('отклонён → «Отклонён» + кнопка «Вернуть»', () => {
    const html = renderCard({ summary: summary({ rejected: true, expiresAt: '2026-12-01T00:00:00Z' }) });
    expect(html).toContain('Отклонён');
    expect(html).toContain('Вернуть');
  });

  it('установлен (та же версия) → «Установлен»', () => {
    const html = renderCard({ summary: summary({ installedSameVersion: true }) });
    expect(html).toContain('Установлен');
  });

  it('устарел (есть более новая версия) → «Черновик устарел»', () => {
    const html = renderCard({ staleNewerSlug: 'local-1/my-plugin' });
    expect(html).toContain('Черновик устарел');
  });
});

// === QA-фикс R: статус «обновление» + бейдж секрета «задан» ===
describe('DraftCard: QA-фикс R (обновление + заданный секрет)', () => {
  it('installedVersion=8.0.0 → «установленной версии 8.0.0», без «ещё не установлен»', () => {
    const html = renderCard({ preview: { ...PREVIEW, installedVersion: '8.0.0' } });
    expect(html).toContain('установленной версии 8.0.0');
    expect(html).not.toContain('ещё не установлен');
  });

  it('без installedVersion (старый сервер) → прежний статус «ещё не установлен»', () => {
    const html = renderCard({ preview: PREVIEW });
    expect(html).toContain('ещё не установлен');
    expect(html).not.toContain('установленной версии');
  });

  it('секрет set=true → «задан», без «не задан»', () => {
    const html = renderCard({ preview: { ...PREVIEW, secrets: [{ name: 'API_TOKEN', required: true, set: true }] } });
    expect(html).toContain('задан');
    expect(html).not.toContain('не задан');
  });

  it('секрет set=false (обязательный) → «не задан» (прежнее)', () => {
    const html = renderCard({ preview: { ...PREVIEW, secrets: [{ name: 'API_TOKEN', required: true, set: false }] } });
    expect(html).toContain('не задан');
  });

  it('секрет без set (старый сервер, обязательный) → «не задан» (прежнее)', () => {
    // PREVIEW.secrets[0] = { name: 'API_TOKEN', required: true } — без set
    const html = renderCard({ preview: PREVIEW });
    expect(html).toContain('не задан');
  });

  it('секрет необязательный, не задан → «не обязателен»', () => {
    const html = renderCard({ preview: { ...PREVIEW, secrets: [{ name: 'OPT_TOKEN', required: false, set: false }] } });
    expect(html).toContain('не обязателен');
    expect(html).not.toContain('не задан');
  });
});

// === Ошибки сервера — понятным текстом ===
describe('DraftCard: ошибки сервера', () => {
  it('ошибка 403/404/409 → человекочитаемый текст, не сырой статус', () => {
    const html = renderCard({ preview: null, error: 'Флаг plugins-build-yourself выключен' });
    expect(html).toContain('Не удалось загрузить черновик');
    expect(html).toContain('Флаг plugins-build-yourself выключен');
  });
});

// === consentRequired → новые права выделены в существующем диалоге согласия ===
describe('ConsentDialog: consentRequired', () => {
  it('newPermissions → бейдж «new» у нового права', () => {
    const data: PluginConsentData = {
      id: 'local-1/my-plugin',
      name: 'My Plugin',
      version: '1.0.0',
      contributions: [],
      requested: ['notes:read', 'net:api.example.com'],
      secrets: [],
      granted: ['notes:read'],
    };
    const html = renderToStaticMarkup(createElement(ConsentDialog, {
      data,
      mode: 'install',
      newPermissions: ['net:api.example.com'],
      onClose: () => {},
      onSubmit: async () => {},
    }));
    // ровно один бейдж «new» — у net:* (notes:read уже выдан, не новый)
    expect((html.match(/>new</g) ?? []).length).toBe(1);
  });

  it('без newPermissions → бейджа «new» нет', () => {
    const data: PluginConsentData = {
      id: 'local-1/my-plugin',
      name: 'My Plugin',
      version: '1.0.0',
      contributions: [],
      requested: ['notes:read'],
      secrets: [],
      granted: ['notes:read'],
    };
    const html = renderToStaticMarkup(createElement(ConsentDialog, {
      data,
      mode: 'install',
      onClose: () => {},
      onSubmit: async () => {},
    }));
    expect(html).not.toMatch(/>new</);
  });
});

// === Глеб MAJOR-1: golden-контракт ответа accept — точные ключи, согласованные с бэком ===
// Бэк (Денис) параллельно возвращает inheritedPermissions (права, переходящие на новое
// содержимое). Фронт читает ровно этот ключ; отсутствие поля (старый сервер) → undefined → ?? [].
// Тест фиксирует контракт: имя ключа и фолбэк на пустой массив (не падать).
describe('PluginDraftAcceptResult: golden-контракт (MAJOR-1)', () => {
  it('ответ с inheritedPermissions → поле читается (ключ точно inheritedPermissions)', () => {
    const raw: PluginDraftAcceptResult = {
      id: 'local-1/my-plugin', version: '2.0.0', sha256: 'abc12345', source: 'upload:my-plugin',
      enabled: true, requested: ['notes:read'], granted: ['notes:read'],
      consentRequired: true, newPermissions: [], isUpdate: true, previousVersion: '1.0.0',
      inheritedPermissions: ['notes:read'], secrets: [],
    };
    expect(raw.inheritedPermissions).toEqual(['notes:read']);
  });

  it('ответ БЕЗ inheritedPermissions (старый сервер) → undefined → ?? [] (не падает)', () => {
    const raw: PluginDraftAcceptResult = {
      id: 'local-1/my-plugin', version: '2.0.0', sha256: 'abc12345', source: 'upload:my-plugin',
      enabled: true, requested: ['notes:read'], granted: ['notes:read'],
      consentRequired: true, newPermissions: [], isUpdate: true, previousVersion: '1.0.0',
      secrets: [],
    };
    expect(raw.inheritedPermissions).toBeUndefined();
    expect(raw.inheritedPermissions ?? []).toEqual([]);
  });
});

// === QA-фикс R: golden-контракт ответа превью — точные ключи installedVersion / secrets[].set ===
// Бэк (Денис) параллельно возвращает в превью installedVersion (версия установленного плагина
// с тем же id) и secrets[].set (задан ли секрет у установленного). Фронт читает ровно эти
// ключи; оба опциональны — отсутствие (старый сервер) = первая установка / не задан.
describe('PluginDraftPreview: golden-контракт (QA-фикс R)', () => {
  it('ответ с installedVersion + secrets[].set → ключи читаются (точно installedVersion / set)', () => {
    const raw: PluginDraftPreview = {
      slug: 'local-1/my-plugin', name: 'My Plugin', version: '2.0.0',
      panels: [], commands: [], skills: [], requestedPermissions: [],
      secrets: [{ name: 'API_TOKEN', required: true, set: true }],
      files: [], validation: { isValid: true, problems: [] },
      installedVersion: '1.0.0',
    };
    expect(raw.installedVersion).toBe('1.0.0');
    expect(raw.secrets[0].set).toBe(true);
  });

  it('ответ БЕЗ installedVersion / set (старый сервер) → undefined (не падает)', () => {
    const raw: PluginDraftPreview = {
      slug: 'local-1/my-plugin', name: 'My Plugin', version: '2.0.0',
      panels: [], commands: [], skills: [], requestedPermissions: [],
      secrets: [{ name: 'API_TOKEN', required: true }],
      files: [], validation: { isValid: true, problems: [] },
    };
    expect(raw.installedVersion).toBeUndefined();
    expect(raw.secrets[0].set).toBeUndefined();
  });
});

// === Событие plugin_draft_changed обновляет карточку (подсветка) ===
// Здесь эффекты работают: минимальный фейк-DOM для createRoot (паттерн fakeDom).
describe('useDraftCard: событие plugin_draft_changed', () => {
  // Тестовый harness: useDraftCard + DraftCard
  function Harness({ s }: { s: PluginDraftSummary }) {
    const { preview, diff, loading, error, justUpdated, loadDiff } = useDraftCard(s);
    return createElement(DraftCard, {
      summary: s, preview, diff, loading, error, justUpdated,
      onAccept: () => {}, onReject: () => {}, onRestore: () => {},
      onEdit: () => {}, onLoadDiff: () => void loadDiff(),
    });
  }

  it('событие по slug → подсветка «Черновик обновлён агентом»', async () => {
    installFakeDom();
    vi.mocked(api.plugins.draftPreview).mockResolvedValue(PREVIEW);
    const { container } = mount(createElement(Harness, { s: summary() }));
    // даём первичной загрузке предпросмотра дойти
    await act(async () => { await new Promise(r => setTimeout(r, 0)); });

    // «сдвигаем» событие: все подписчики получают plugin_draft_changed по нашему slug
    await act(async () => {
      for (const h of handlers) h({ type: 'plugin_draft_changed', slug: SLUG, revision: 2 } as ServerMessage);
    });

    const text = textNodes(container).join(' ');
    expect(text).toContain('Черновик обновлён агентом');
    uninstallFakeDom();
  });
});

beforeEach(() => {
  handlers.length = 0;
  vi.mocked(api.plugins.draftPreview).mockReset();
  vi.mocked(api.plugins.draftDiff).mockReset();
  vi.mocked(api.plugins.draftsList).mockReset();
  vi.mocked(api.plugins.draftAccept).mockReset();
  vi.mocked(api.plugins.draftReject).mockReset();
  vi.mocked(api.plugins.draftRestore).mockReset();
  vi.mocked(api.plugins.draftPanel).mockReset();
});

// === «Вернуть» отклонённого черновика (макет V6, 30 дней) ===
// Отклонённый черновик показывает «Вернуть», пока срок (rejectedAt + 30 дней) не истёк;
// после — кнопки нет. Рендерим DraftCard (свёрнутый режим) через renderToStaticMarkup.
describe('CollapsedRow: «Вернуть» отклонённого черновика (V6, 30 дней)', () => {
  const baseSummary: PluginDraftSummary = {
    slug: SLUG, id: 'local-1/my-plugin', version: '1.0.0',
    installedSameVersion: false, rejected: true, expiresAt: null,
  };

  it('срок не истёк (expiresAt=null) → «Вернуть» показывается', () => {
    const html = renderToStaticMarkup(createElement(DraftCard, {
      summary: baseSummary, preview: null, diff: null,
      onRestore: () => {},
    }));
    expect(html).toContain('Вернуть');
    expect(html).toContain('Отклонён');
  });

  it('срок не истёк (expiresAt в будущем) → «Вернуть» показывается', () => {
    const future = new Date(Date.now() + 30 * 24 * 3600 * 1000).toISOString();
    const html = renderToStaticMarkup(createElement(DraftCard, {
      summary: { ...baseSummary, expiresAt: future }, preview: null, diff: null,
      onRestore: () => {},
    }));
    expect(html).toContain('Вернуть');
  });

  it('срок истёк (expiresAt в прошлом) → «Вернуть» нет', () => {
    const past = new Date(Date.now() - 24 * 3600 * 1000).toISOString();
    const html = renderToStaticMarkup(createElement(DraftCard, {
      summary: { ...baseSummary, expiresAt: past }, preview: null, diff: null,
      onRestore: () => {},
    }));
    // «Отклонён» (бейдж) есть, но кнопки «Вернуть» нет
    expect(html).toContain('Отклонён');
    expect(html).not.toContain('Вернуть');
  });

  it('onRestore не передан → «Вернуть» нет (даже если срок не истёк)', () => {
    const html = renderToStaticMarkup(createElement(DraftCard, {
      summary: baseSummary, preview: null, diff: null,
      // onRestore не передан
    }));
    expect(html).not.toContain('Вернуть');
  });
});

// === Окно «Файлы черновика» (FilesDrawer, макет plugin-files-window-v1) ===
// Структура: вкладки «Файлы» | «Изменения» | «Панель», футера нет; дерево файлов с
// метками изменений; содержимое выбранного файла реконструируется из строк diff;
// состояния: diff не загружен, diff усечён, двоичный файл, файл удалён из пакета.
// Клик в фейк-DOM: у элемента нет dispatchEvent — React держит пропсы на узле
// (reactProps), поэтому вызываем onClick напрямую, внутри act().
function clickByAttr(container: ReturnType<typeof mount>['container'], attr: string, value: string): boolean {
  const node = findNodeByAttr(container, attr, value);
  const props = node ? reactProps(node) : null;
  if (typeof props?.onClick !== 'function') return false;
  act(() => { (props!.onClick as () => void)(); });
  return true;
}

function clickByText(container: ReturnType<typeof mount>['container'], label: string): boolean {
  const textOf = (n: any): string => n.nodeType === 3 ? (n.nodeValue ?? '') : n.childNodes.map(textOf).join('');
  let hit: any = null;
  const walk = (n: any): void => {
    if (hit) return;
    if (n.nodeType === 1 && n.nodeName === 'BUTTON' && textOf(n).includes(label)) { hit = n; return; }
    for (const c of n.childNodes) walk(c);
  };
  walk(container);
  const props = hit ? reactProps(hit) : null;
  if (typeof props?.onClick !== 'function') return false;
  act(() => { (props!.onClick as () => void)(); });
  return true;
}

describe('FilesDrawer: окно по макету', () => {
  const drawerHtml = (preview: PluginDraftPreview, diff: PluginDraftDiff | null, diffError?: string | null) =>
    renderToStaticMarkup(createElement(FilesDrawer, { preview, diff, diffError, onClose: () => {} }));

  it('вкладки «Файлы» и «Изменения»; «Панель» — только если в пакете есть панели', () => {
    const html = drawerHtml(PREVIEW, DIFF);
    expect(html).toContain('Файлы');
    expect(html).toContain('Изменения');
    expect(html).toContain('Панель');
    const noPanel = drawerHtml({ ...PREVIEW, panels: [] }, DIFF);
    expect(noPanel).not.toContain('Панель');
  });

  it('футера нет: закрытие — крестик шапки, предпросмотр панели — вкладка', () => {
    const html = drawerHtml(PREVIEW, DIFF);
    expect(html).not.toContain('Предпросмотр панели');
    expect(html).toContain('aria-label="Закрыть"');
  });

  it('дерево вместо плоского перечня: каталоги data-dir, файлы data-file, метки +/~/− и размеры', () => {
    const html = drawerHtml(PREVIEW, DIFF);
    expect(html).toContain('data-dir="panel"');
    expect(html).toContain('data-file="panel/index.html"');
    expect(html).toContain('data-file="commands/my-cmd.md"');
    expect(html).toContain('aria-label="Добавлен"');
    expect(html).toContain('aria-label="Изменён"');
    expect(html).toContain('120 Б');
  });

  it('выбранный файл: вид «Файл» (ctx+add строки) и переключатель «Файл | Изменения»', () => {
    installFakeDom();
    const { container } = mount(createElement(FilesDrawer, { preview: PREVIEW, diff: DIFF, onClose: () => {} }));
    const ok = clickByAttr(container, 'data-file', 'commands/my-cmd.md');
    expect(ok).toBe(true);
    const text = textNodes(container).join(' ');
    expect(text).toContain('new line'); // содержимое новой версии
    expect(text).not.toContain('old line'); // удалённая строка в виде «Файл» не показывается
    expect(text).toContain('Файл');
    expect(text).toContain('Изменения');
    uninstallFakeDom();
  });

  it('вкладка «Изменения»: сводка с счётчиками; удалённый файл — в дереве', () => {
    const withRemoved: PluginDraftDiff = {
      ...DIFF,
      removed: [{ path: 'panel/legacy.js', size: 300, binary: false, truncated: false, lines: null }],
    };
    installFakeDom();
    const { container } = mount(createElement(FilesDrawer, { preview: PREVIEW, diff: withRemoved, onClose: () => {} }));
    expect(clickByText(container, 'Изменения')).toBe(true);
    // JSX-пробел между {label} и {n} — отдельный текстовый узел; нормализуем пробелы,
    // чтобы проверить ровно то, что видит человек: «добавлено 1».
    const text = textNodes(container).join(' ').replace(/\s+/g, ' ');
    expect(text).toContain('Что поменялось');
    expect(text).toContain('добавлено 1');
    expect(text).toContain('удалено 1');
    expect(container.querySelector('[data-file="panel/legacy.js"]')).toBeTruthy();
    uninstallFakeDom();
  });

  it('diff не загружен (ошибка) — «Не удалось сравнить» и «Повторить», а не пустота', () => {
    installFakeDom();
    const { container } = mount(createElement(FilesDrawer, {
      preview: PREVIEW, diff: null, diffError: 'Сервер не ответил', onClose: () => {}, onLoadDiff: () => {},
    }));
    expect(clickByText(container, 'Изменения')).toBe(true);
    const text = textNodes(container).join(' ');
    expect(text).toContain('Не удалось сравнить с установленной версией');
    expect(text).toContain('Повторить');
    expect(text).toContain('Файлы не сравнены');
    uninstallFakeDom();
  });

  it('усечённый diff — предупреждение «Сравнение длинное»', () => {
    installFakeDom();
    const { container } = mount(createElement(FilesDrawer, { preview: PREVIEW, diff: { ...DIFF, truncated: true }, onClose: () => {} }));
    expect(clickByText(container, 'Изменения')).toBe(true);
    expect(textNodes(container).join(' ')).toContain('Сравнение длинное');
    uninstallFakeDom();
  });

  it('двоичный файл — «Двоичный файл» с размером, не сырое содержимое', () => {
    const preview: PluginDraftPreview = { ...PREVIEW, files: [...PREVIEW.files, { path: 'assets/logo.png', isDir: false, size: 14336 }] };
    const diff: PluginDraftDiff = { ...DIFF, added: [...DIFF.added, { path: 'assets/logo.png', size: 14336, binary: true, truncated: false, lines: null }] };
    installFakeDom();
    const { container } = mount(createElement(FilesDrawer, { preview, diff, onClose: () => {} }));
    expect(clickByAttr(container, 'data-file', 'assets/logo.png')).toBe(true);
    const text = textNodes(container).join(' ');
    expect(text).toContain('Двоичный файл');
    expect(text).toContain('14,0 КБ');
    uninstallFakeDom();
  });

  it('файл удалён из пакета — состояние «Файла больше нет»', () => {
    const diff: PluginDraftDiff = {
      ...DIFF, added: [], modified: [],
      removed: [{ path: 'panel/legacy.js', size: 300, binary: false, truncated: false, lines: null }],
    };
    installFakeDom();
    const { container } = mount(createElement(FilesDrawer, { preview: PREVIEW, diff, onClose: () => {} }));
    expect(clickByText(container, 'Изменения')).toBe(true);
    expect(clickByAttr(container, 'data-file', 'panel/legacy.js')).toBe(true);
    expect(textNodes(container).join(' ')).toContain('Файла больше нет');
    uninstallFakeDom();
  });

  it('мобила 320: тач-цели 44px и без сайдбара 280px (двухуровневая раскладка)', () => {
    vi.stubGlobal('window', Object.assign(new EventTarget(), {
      innerWidth: 320, innerHeight: 640,
      matchMedia: (q: string) => ({ matches: q.includes('max-width'), addEventListener: () => {}, removeEventListener: () => {} }),
    }));
    const html = drawerHtml(PREVIEW, DIFF);
    expect(html).toContain('height:44px');
    expect(html).not.toContain('280px');
    // вернуть десктоп-окружение для остальных тестов
    vi.stubGlobal('window', Object.assign(new EventTarget(), {
      innerWidth: 1440, innerHeight: 900,
      matchMedia: () => ({ matches: false, addEventListener: () => {}, removeEventListener: () => {} }),
    }));
  });
});
