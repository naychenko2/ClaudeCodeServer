// Карточка черновика в ленте (шаг 4.5b, ADR-031): запись plugin_draft рисуется карточкой,
// 404 → нейтральная строка, без флага → не рисуется, событие обновляет карточку, из данных
// записи используется только slug.
//
// Окружение node: асинхронные загрузки (список + предпросмотр) и событие — на минимальном
// фейк-DOM (createRoot + act, паттерн fakeDom).

import { afterAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, createElement } from 'react';
import type { ChatItem, ServerMessage } from '../../../types';
import type { ChatItemToolCtx } from '../../../lib/subsystems/registryCore';
import type { PluginDraftPreview, PluginDraftSummary } from '../../../lib/api';
import { shouldAcceptDraftReply } from '../../../pages/ChatsPage';

// onMessage — захватываем подписчика, чтобы «сдвинуть» событие в тесте
const handlers: ((m: ServerMessage) => void)[] = [];
vi.mock('../../../lib/signalr', () => ({
  onMessage: (h: (m: ServerMessage) => void) => { handlers.push(h); return () => {}; },
}));

// api.plugins.* — фиксированные ответы; значения подменяются в beforeEach
vi.mock('../../../lib/api', () => ({
  api: {
    plugins: {
      draftsList: vi.fn(),
      draftPreview: vi.fn(),
      draftAccept: vi.fn(),
      draftReject: vi.fn(),
      draftRestore: vi.fn(),
      grant: vi.fn(),
      setSecret: vi.fn(),
    },
  },
}));

const { api } = await import('../../../lib/api');
const { setAllFlags, setFlagLocal } = await import('../../../lib/featureFlags');
const { installFakeDom, mount, textNodes, uninstallFakeDom } = await import(
  '../../../components/chat/__tests__/fakeDom'
);
const { DraftAnchor } = await import('./records');
const { __resetDraftsStore, getDraftSnapshot } = await import('./draftsStore');

// === Фиксированные данные ===
// Запись plugin_draft несёт ГОЛЫЙ slug (без publisher): isDraftSlug принимает
// ^[a-z0-9-]+$. Полный id пакета (publisher/slug) — в SUMMARY.id, не в slug.
const SLUG = 'my-plugin';

const PREVIEW: PluginDraftPreview = {
  slug: SLUG,
  name: 'My Plugin',
  version: '1.0.0',
  panels: [],
  commands: [],
  skills: [],
  requestedPermissions: ['notes:read'],
  secrets: [],
  files: [],
  validation: { isValid: true, problems: [] },
};

const SUMMARY: PluginDraftSummary = {
  slug: SLUG,
  id: 'local-1/my-plugin',
  version: '1.0.0',
  updatedAt: '2026-10-01T00:00:00Z',
  problems: 0,
  installedSameVersion: false,
  rejected: false,
  expiresAt: null,
};

// Установлен той же версией: карточка сворачивается в строку со ссылкой «Открыть в «Плагинах»»
const SUMMARY_INSTALLED: PluginDraftSummary = { ...SUMMARY, installedSameVersion: true };

// Запись plugin_draft: в данных только slug
function recordItem(data: Record<string, unknown>, fallback = 'Черновик: my-plugin'): ChatItem {
  return { kind: 'module_record', module: 'plugins', recordType: 'plugin_draft', data, fallback } as ChatItem;
}

function ctxFor(item: ChatItem): ChatItemToolCtx {
  return { item, online: true, projectId: 'proj-1', sessionId: 'chat-1', persona: null };
}

// Дожидаемся, пока все отложенные обновления (эффект + промисы loadList/loadPreview)
// улягутся. Один setTimeout(0) НЕ гарантирует этого: React-эффект шедюлится через
// setImmediate (check-фаза event-loop), а setTimeout(0) — timers-фаза, которая идёт
// РАНЬШЕ check-фазы; поэтому act закрывался до того, как эффект сработал, и setEntry
// (setState) попадал вне act → «not wrapped in act». Фикс: (1) дать эффекту
// (setImmediate, в очереди раньше нашего) сработать; (2) повторять flush микро/
// макротасков, пока снимок стора перестанет меняться (все .then-ы промисов отработали).
async function flushDrafts(slug: string) {
  await new Promise<void>(r => setImmediate(r));
  let prev = JSON.stringify(getDraftSnapshot(slug));
  for (let i = 0; i < 20; i++) {
    await new Promise(r => setTimeout(r, 0));
    const cur = JSON.stringify(getDraftSnapshot(slug));
    if (cur === prev) return;
    prev = cur;
  }
}

// Монтирует якорь и даёт асинхронным загрузкам (список + предпросмотр) дойти
async function renderAnchor(item: ChatItem) {
  installFakeDom();
  const { container } = mount(createElement(DraftAnchor, { ctx: ctxFor(item) }));
  const slug = String((item as { data?: { slug?: unknown } }).data?.slug ?? '');
  await act(async () => { await flushDrafts(slug); });
  return { container, text: () => textNodes(container).join(' ') };
}

beforeEach(() => {
  handlers.length = 0;
  vi.mocked(api.plugins.draftsList).mockReset();
  vi.mocked(api.plugins.draftPreview).mockReset();
  vi.mocked(api.plugins.draftAccept).mockReset();
  vi.mocked(api.plugins.draftReject).mockReset();
  vi.mocked(api.plugins.draftRestore).mockReset();
  vi.mocked(api.plugins.grant).mockReset();
  vi.mocked(api.plugins.setSecret).mockReset();
  setAllFlags({});
  __resetDraftsStore();
});

afterAll(() => { uninstallFakeDom(); });

// === Запись рисуется карточкой ===
describe('DraftAnchor: запись plugin_draft', () => {
  it('флаг on + черновик в списке → карточка (имя, «Принять», бейдж «Черновик»)', async () => {
    setFlagLocal('plugins-build-yourself', true);
    vi.mocked(api.plugins.draftsList).mockResolvedValue([SUMMARY]);
    vi.mocked(api.plugins.draftPreview).mockResolvedValue(PREVIEW);
    const { text } = await renderAnchor(recordItem({ slug: SLUG }));
    expect(text()).toContain('My Plugin');
    expect(text()).toContain('Принять');
    expect(text()).toContain('Черновик');
  });

  it('из данных записи используется только slug: лишние поля не влияют на рендер', async () => {
    setFlagLocal('plugins-build-yourself', true);
    vi.mocked(api.plugins.draftsList).mockResolvedValue([SUMMARY]);
    vi.mocked(api.plugins.draftPreview).mockResolvedValue(PREVIEW);
    const { text } = await renderAnchor(recordItem({ slug: SLUG, extra: 'ignored', other: 42 }));
    // та же карточка, что и с чистым { slug }
    expect(text()).toContain('My Plugin');
    expect(text()).toContain('Принять');
  });
});

// === 404: slug не в списке владельца → нейтральная строка ===
describe('DraftAnchor: 404', () => {
  it('slug не в списке → «Черновик «slug» больше недоступен», без ошибок и кнопок', async () => {
    setFlagLocal('plugins-build-yourself', true);
    vi.mocked(api.plugins.draftsList).mockResolvedValue([]); // slug не найден
    vi.mocked(api.plugins.draftPreview).mockResolvedValue(PREVIEW);
    const { text } = await renderAnchor(recordItem({ slug: SLUG }));
    expect(text()).toContain(`Черновик «${SLUG}» больше недоступен`);
    // без кнопок карточки
    expect(text()).not.toContain('Принять');
  });
});

// === Без флага → не рисуется ===
describe('DraftAnchor: флаг plugins-build-yourself', () => {
  it('флаг off → карточка не рисуется (даже если черновик в списке)', async () => {
    setFlagLocal('plugins-build-yourself', false);
    vi.mocked(api.plugins.draftsList).mockResolvedValue([SUMMARY]);
    vi.mocked(api.plugins.draftPreview).mockResolvedValue(PREVIEW);
    const { text } = await renderAnchor(recordItem({ slug: SLUG }));
    // карточка (с «Принять»/именем) не на месте
    expect(text()).not.toContain('Принять');
    expect(text()).not.toContain('My Plugin');
  });

  it('флаг off + есть fallback → нейтральная строка fallback', async () => {
    setFlagLocal('plugins-build-yourself', false);
    vi.mocked(api.plugins.draftsList).mockResolvedValue([SUMMARY]);
    vi.mocked(api.plugins.draftPreview).mockResolvedValue(PREVIEW);
    const { text } = await renderAnchor(recordItem({ slug: SLUG }, 'Черновик: my-plugin'));
    expect(text()).toContain('Черновик: my-plugin');
    expect(text()).not.toContain('Принять');
  });
});

// === Событие plugin_draft_changed обновляет карточку ===
describe('DraftAnchor: событие plugin_draft_changed', () => {
  it('событие по slug → подсветка «Черновик обновлён агентом»', async () => {
    setFlagLocal('plugins-build-yourself', true);
    vi.mocked(api.plugins.draftsList).mockResolvedValue([SUMMARY]);
    vi.mocked(api.plugins.draftPreview).mockResolvedValue(PREVIEW);
    const { text } = await renderAnchor(recordItem({ slug: SLUG }));
    expect(text()).toContain('My Plugin');

    // «сдвигаем» событие: все подписчики получают plugin_draft_changed по нашему slug
    await act(async () => {
      for (const h of handlers) h({ type: 'plugin_draft_changed', slug: SLUG, revision: 2 } as ServerMessage);
      await flushDrafts(SLUG);
    });

    expect(text()).toContain('Черновик обновлён агентом');
  });
});

// === п. 1 (MAJOR): событие по НОВОМУ slug, когда список УЖЕ загружен ===
// Сценарий: в чате уже есть черновик draft-1 (список владельца прочитан, _listLoaded=true).
// Агент создаёт второй черновик draft-2 → запись монтирует его карточку; loadList() блокирован
// гардом (список уже в кэше) → listResolved остаётся false → «Загружаем черновик…».
// Событие plugin_draft_changed по draft-2 обязано перечитать список (draftsList повторно),
// иначе карточка draft-2 залипает в «Загружаем…» навсегда.
describe('DraftAnchor: событие по новому slug при уже загруженном списке (п. 1)', () => {
  const SLUG1 = 'draft-1';
  const SLUG2 = 'draft-2';
  const SUMMARY1: PluginDraftSummary = {
    slug: SLUG1, id: 'local-1/draft-1', version: '1.0.0', updatedAt: '2026-10-01T00:00:00Z',
    problems: 0, installedSameVersion: false, rejected: false, expiresAt: null,
  };
  const SUMMARY2: PluginDraftSummary = {
    slug: SLUG2, id: 'local-1/draft-2', version: '1.0.0', updatedAt: '2026-10-02T00:00:00Z',
    problems: 0, installedSameVersion: false, rejected: false, expiresAt: null,
  };
  const PREVIEW1: PluginDraftPreview = {
    slug: SLUG1, name: 'First Plugin', version: '1.0.0',
    panels: [], commands: [], skills: [], requestedPermissions: [], secrets: [], files: [],
    validation: { isValid: true, problems: [] },
  };
  const PREVIEW2: PluginDraftPreview = {
    slug: SLUG2, name: 'Second Plugin', version: '1.0.0',
    panels: [], commands: [], skills: [], requestedPermissions: [], secrets: [], files: [],
    validation: { isValid: true, problems: [] },
  };

  it('список загружен → событие по новому slug → карточка нового черновика рисуется (draftsList повторно)', async () => {
    setFlagLocal('plugins-build-yourself', true);
    // draftsList: 1-й вызов (монтирование draft-1) — только draft-1;
    // 2-й вызов (ре-чит после события) — уже и draft-1, и draft-2.
    vi.mocked(api.plugins.draftsList)
      .mockReturnValueOnce(Promise.resolve([SUMMARY1]))
      .mockReturnValueOnce(Promise.resolve([SUMMARY1, SUMMARY2]));
    vi.mocked(api.plugins.draftPreview).mockImplementation((slug: string) =>
      Promise.resolve(slug === SLUG2 ? PREVIEW2 : PREVIEW1));

    // 1) draft-1: список читается, карточка рисуется
    const r1 = await renderAnchor(recordItem({ slug: SLUG1 }));
    expect(r1.text()).toContain('First Plugin');
    expect(vi.mocked(api.plugins.draftsList)).toHaveBeenCalledTimes(1);

    // 2) draft-2 (новый slug): список уже в кэше → loadList() заблокирован,
    //    карточка в «Загружаем черновик…» (listResolved ещё false)
    const r2 = await renderAnchor(recordItem({ slug: SLUG2 }));
    expect(r2.text()).toContain('Загружаем черновик');
    expect(vi.mocked(api.plugins.draftsList)).toHaveBeenCalledTimes(1); // loadList не пошёл повторно

    // 3) событие по draft-2 → bump → перечитать список
    await act(async () => {
      for (const h of handlers) h({ type: 'plugin_draft_changed', slug: SLUG2, revision: 2 } as ServerMessage);
      await flushDrafts(SLUG2);
    });

    // draftsList вызван повторно (2-й раз) — иначе обновление по событию потеряно
    expect(vi.mocked(api.plugins.draftsList)).toHaveBeenCalledTimes(2);
    // карточка draft-2 теперь рисуется (сводка из перечитанного списка)
    expect(r2.text()).toContain('Second Plugin');
  });
});

// === п. 2 (MINOR): первая загрузка списка ПАДАЕТ → следующий триггер перечитывает ===
// .catch в loadList раньше ставил _listLoaded = true, из-за чего любая последующая попытка
// (монтирование) блокировалась гардом и карточка залипала в «Загружаем…». При ошибке —
// _listLoaded = false: следующий триггер (новое монтирование) повторит попытку.
describe('DraftAnchor: первая загрузка списка упала (п. 2)', () => {
  it('сбой первой загрузки → повторное монтирование перечитывает список и карточка рисуется', async () => {
    setFlagLocal('plugins-build-yourself', true);
    // 1-й вызов draftsList — сбой; 2-й (после повторного монтирования) — успех
    vi.mocked(api.plugins.draftsList)
      .mockRejectedValueOnce(new Error('Сбой списка'))
      .mockResolvedValueOnce([SUMMARY]);
    vi.mocked(api.plugins.draftPreview).mockResolvedValue(PREVIEW);

    // 1) первое монтирование: список упал → «Загружаем черновик…» (listResolved ещё false)
    const r1 = await renderAnchor(recordItem({ slug: SLUG }));
    expect(r1.text()).toContain('Загружаем черновик');
    expect(vi.mocked(api.plugins.draftsList)).toHaveBeenCalledTimes(1);

    // 2) повторное монтирование (новый корень, тот же slug): эффект снова зовёт loadList().
    //    Без фикса (_listLoaded=true из catch) гард блокирует → залипает; с фиксом — перечитывает.
    const r2 = await renderAnchor(recordItem({ slug: SLUG }));
    expect(vi.mocked(api.plugins.draftsList)).toHaveBeenCalledTimes(2);
    expect(r2.text()).toContain('My Plugin');
  });
});

// === «Принять…» из ленты: сбой выдачи прав (grant) ===
// Тип ноды fakeDom (класс не экспортируется) — через сигнатуру textNodes
type FNode = Parameters<typeof textNodes>[0];

// Весь текст поддерева (для проверок видимости)
function textOf(n: FNode): string {
  let out = '';
  const walk = (x: FNode) => {
    if (x.nodeType === 3) out += x.nodeValue;
    for (const c of x.childNodes) walk(c);
  };
  walk(n);
  return out;
}

// Кнопка по её точной подписи: React держит props на узле под ключом __reactProps$<random>
function findButton(container: FNode, label: string): { onClick: () => void } | null {
  let hit: { onClick: () => void } | null = null;
  const walk = (x: FNode) => {
    if (hit) return;
    if (x.nodeType === 1) {
      const p = Object.keys(x).find(k => k.startsWith('__reactProps$'));
      if (p) {
        const props = (x as unknown as Record<string, unknown>)[p] as Record<string, unknown> | undefined;
        if (props?.onClick && textOf(x) === label) hit = { onClick: props.onClick as () => void };
      }
    }
    for (const c of x.childNodes) walk(c);
  };
  walk(container);
  return hit;
}

describe('DraftFeedCard: «Принять…» (диалог согласия)', () => {
  it('сбой grant → ошибка видна, диалог остаётся открытым, карточка перечитана (мутация: убрать try/catch)', async () => {
    setFlagLocal('plugins-build-yourself', true);
    vi.mocked(api.plugins.draftsList).mockResolvedValue([SUMMARY]);
    vi.mocked(api.plugins.draftPreview).mockResolvedValue(PREVIEW);
    vi.mocked(api.plugins.draftAccept).mockResolvedValue({
      id: 'local-1/my-plugin',
      version: '1.0.0',
      sha256: 'abc12345',
      source: 'upload:my-plugin',
      enabled: true,
      requested: ['notes:read'],
      granted: [],
      consentRequired: true,
      newPermissions: [],
      secrets: [],
    });
    vi.mocked(api.plugins.grant).mockRejectedValue(new Error('Сбой сети'));

    const { container } = await renderAnchor(recordItem({ slug: SLUG }));

    // «Принять…» на карточке → диалог согласия (портал в document.body)
    const accept = findButton(container, 'Принять…');
    expect(accept, 'кнопка «Принять…» не найдена').not.toBeNull();
    await act(async () => { accept!.onClick(); });

    const body = (globalThis as { document: { body: FNode } }).document.body;
    const install = findButton(body, 'Установить');
    expect(install, 'кнопка «Установить» не найдена').not.toBeNull();
    await act(async () => { install!.onClick(); });

    // grant упал: ошибка видна в диалоге, диалог не закрыт,
    // карточка перечитана (предпросмотр запрошен повторно)
    expect(textOf(body)).toContain('Сбой сети');
    expect(findButton(body, 'Установить'), 'диалог закрылся после ошибки').not.toBeNull();
    expect(vi.mocked(api.plugins.draftPreview).mock.calls.length).toBeGreaterThanOrEqual(2);
  });

  // consentRequired: false — диалог согласия НЕ открывается (права уже выданы/не нужны).
  // Мутация «игнорировать consentRequired» (всегда открывать) красит этот тест.
  it('accept с consentRequired: false → диалог согласия не открывается', async () => {
    setFlagLocal('plugins-build-yourself', true);
    vi.mocked(api.plugins.draftsList).mockResolvedValue([SUMMARY]);
    vi.mocked(api.plugins.draftPreview).mockResolvedValue(PREVIEW);
    vi.mocked(api.plugins.draftAccept).mockResolvedValue({
      id: 'local-1/my-plugin',
      version: '1.0.0',
      sha256: 'abc12345',
      source: 'upload:my-plugin',
      enabled: true,
      requested: ['notes:read'],
      granted: ['notes:read'],
      consentRequired: false,
      newPermissions: [],
      secrets: [],
    });

    const { container } = await renderAnchor(recordItem({ slug: SLUG }));
    // document.body живёт между тестами (uninstallFakeDom только в afterAll) —
    // чистим leftover-диалог предыдущего теста, иначе «Установить» найдётся не от нашего accept
    const body = (globalThis as { document: { body: FNode } }).document.body;
    for (const c of [...body.childNodes]) body.removeChild(c);

    const accept = findButton(container, 'Принять…');
    expect(accept, 'кнопка «Принять…» не найдена').not.toBeNull();
    await act(async () => { accept!.onClick(); await flushDrafts(SLUG); });

    // диалог не открыт: кнопки «Установить» согласия нет в body
    expect(findButton(body, 'Установить'), 'диалог согласия открылся без consentRequired').toBeNull();
  });
});

describe('DraftFeedCard: «Установлен» → ссылка «Открыть в «Плагинах»»', () => {
  it('установлен той же версией → свёрнутая строка со ссылкой на экран плагинов', async () => {
    setFlagLocal('plugins-build-yourself', true);
    vi.mocked(api.plugins.draftsList).mockResolvedValue([SUMMARY_INSTALLED]);
    vi.mocked(api.plugins.draftPreview).mockResolvedValue(PREVIEW);

    const { container, text } = await renderAnchor(recordItem({ slug: SLUG }));
    // бейдж «Установлен · 1.0.0» и ссылка «Открыть в «Плагинах»» на месте
    expect(text()).toContain('Установлен');
    expect(findButton(container, 'Открыть в «Плагинах»'), 'ссылка «Открыть в «Плагинах»» не найдена').not.toBeNull();
  });
});

// === Шаг 4: ошибки действий карточки не проглатываются (дефект 6035ec94) ===
// «Принять…»/«Вернуть» при 409/сбое сети показывают текст сервера на карточке,
// а не «ничего не произошло». Мутация «убрать показ ошибки» красит эти тесты.
describe('DraftFeedCard: ошибки «Принять…» / «Вернуть» на карточке', () => {
  const SUMMARY_REJECTED: PluginDraftSummary = { ...SUMMARY, rejected: true };

  it('«Принять…» → 409 → текст ошибки сервера виден на карточке', async () => {
    setFlagLocal('plugins-build-yourself', true);
    vi.mocked(api.plugins.draftsList).mockResolvedValue([SUMMARY]);
    vi.mocked(api.plugins.draftPreview).mockResolvedValue(PREVIEW);
    vi.mocked(api.plugins.draftAccept).mockRejectedValue(new Error('Черновик уже существует'));

    const { container, text } = await renderAnchor(recordItem({ slug: SLUG }));
    const accept = findButton(container, 'Принять…');
    expect(accept, 'кнопка «Принять…» не найдена').not.toBeNull();
    await act(async () => { accept!.onClick(); await flushDrafts(SLUG); });

    // текст 409 на карточке (не только в консоли)
    expect(text()).toContain('Черновик уже существует');
  });

  it('«Принять…» → сбой сети → понятный текст на карточке', async () => {
    setFlagLocal('plugins-build-yourself', true);
    vi.mocked(api.plugins.draftsList).mockResolvedValue([SUMMARY]);
    vi.mocked(api.plugins.draftPreview).mockResolvedValue(PREVIEW);
    vi.mocked(api.plugins.draftAccept).mockRejectedValue(new Error('Действие недоступно офлайн'));

    const { container, text } = await renderAnchor(recordItem({ slug: SLUG }));
    const accept = findButton(container, 'Принять…');
    expect(accept, 'кнопка «Принять…» не найдена').not.toBeNull();
    await act(async () => { accept!.onClick(); await flushDrafts(SLUG); });

    expect(text()).toContain('Действие недоступно офлайн');
  });

  it('«Вернуть» → 409 → текст ошибки виден на свёрнутой строке', async () => {
    setFlagLocal('plugins-build-yourself', true);
    vi.mocked(api.plugins.draftsList).mockResolvedValue([SUMMARY_REJECTED]);
    vi.mocked(api.plugins.draftPreview).mockResolvedValue(PREVIEW);
    vi.mocked(api.plugins.draftRestore).mockRejectedValue(new Error('Черновик уже существует'));

    const { container, text } = await renderAnchor(recordItem({ slug: SLUG }));
    const restore = findButton(container, 'Вернуть');
    expect(restore, 'кнопка «Вернуть» не найдена').not.toBeNull();
    await act(async () => { restore!.onClick(); await flushDrafts(SLUG); });

    expect(text()).toContain('Черновик уже существует');
  });

  it('«Вернуть» → сбой сети → понятный текст на свёрнутой строке', async () => {
    setFlagLocal('plugins-build-yourself', true);
    vi.mocked(api.plugins.draftsList).mockResolvedValue([SUMMARY_REJECTED]);
    vi.mocked(api.plugins.draftPreview).mockResolvedValue(PREVIEW);
    vi.mocked(api.plugins.draftRestore).mockRejectedValue(new Error('Действие недоступно офлайн'));

    const { container, text } = await renderAnchor(recordItem({ slug: SLUG }));
    const restore = findButton(container, 'Вернуть');
    expect(restore, 'кнопка «Вернуть» не найдена').not.toBeNull();
    await act(async () => { restore!.onClick(); await flushDrafts(SLUG); });

    expect(text()).toContain('Действие недоступно офлайн');
  });
});

// === Шаг 1: cc-draft-reply в чате вне проекта (дефект 659560f9) ===
// Реплика «Править»/«Попросить агента исправить» из карточки черновика в ленте (вне
// проекта) должна попасть в поле ввода активного чата (cc-compose-prefill), не
// отправляясь сама. Условие приёма — shouldAcceptDraftReply (ChatsPage); мутация → красный.
describe('cc-draft-reply: чат вне проекта (шаг 1)', () => {

  it('project: undefined + reply → принимается (реплика в композере)', () => {
    expect(shouldAcceptDraftReply(undefined, 'Поправь плагин «X»')).toBe(true);
  });

  it('project: {id} + reply → не принимается (проектный чат слушает WorkspacePage)', () => {
    expect(shouldAcceptDraftReply({ id: 'p1' }, 'test')).toBe(false);
  });

  it('project: undefined + reply: "" → не принимается (пустая реплика)', () => {
    expect(shouldAcceptDraftReply(undefined, '')).toBe(false);
  });

  it('project: null + reply → принимается', () => {
    expect(shouldAcceptDraftReply(null, 'text')).toBe(true);
  });
});
