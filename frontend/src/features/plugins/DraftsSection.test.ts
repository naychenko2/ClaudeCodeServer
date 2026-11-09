// Секция «Черновики от агента» (шаг 4.4): «Принять…» → диалог согласия; сбой выдачи
// прав (grant) — ошибка видна, диалог остаётся открытым, список перечитан (черновик
// уже установлен accept'ом и не должен висеть в списке).
//
// Окружение node: минимальный фейк-DOM (createRoot + act, паттерн fakeDom); диалог
// согласия — портал в document.body.

import { afterAll, afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, createElement } from 'react';
import type { Project } from '../../types';
import type { PluginDraftPreview, PluginDraftSummary } from '../../lib/api';

// onMessage — заглушка (события в этом тесте не сдвигаем)
vi.mock('../../lib/signalr', () => ({
  onMessage: () => () => {},
}));

// api.plugins.* — фиксированные ответы; значения подменяются в тесте
vi.mock('../../lib/api', () => ({
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

const { api } = await import('../../lib/api');
const { setAllFlags, setFlagLocal } = await import('../../lib/featureFlags');
const { installFakeDom, mount, textNodes, uninstallFakeDom, findNodeByAttr } = await import(
  '../../components/chat/__tests__/fakeDom'
);
const { DraftsSection } = await import('./DraftsSection');
// Корни, смонтированные тестами: unmount в afterEach снимает подписки useFeature/useDraftCard
// на модульных сторах, иначе beforeEach-emit следующего теста рендерит «зомби»-секции вне act
const mountedRoots: { unmount: () => void }[] = [];

// === Фиксированные данные ===
const SLUG = 'local-1/my-plugin';

const PROJECT: Project = {
  id: 'proj-1',
  name: 'Тест',
  rootPath: '/test',
  createdAt: '2026-01-01T00:00:00Z',
  updatedAt: '2026-01-01T00:00:00Z',
};

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

// === Хелперы (типы нод fakeDom не экспортируются — через сигнатуру textNodes) ===
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

// Монтирует секцию и даёт асинхронной загрузке (список + предпросмотр) дойти
async function renderSection() {
  installFakeDom();
  const { root, container } = mount(createElement(DraftsSection, { project: PROJECT }));
  mountedRoots.push(root);
  await act(async () => { await new Promise(r => setTimeout(r, 0)); });
  return { container, text: () => textNodes(container).join(' ') };
}

beforeEach(() => {
  vi.mocked(api.plugins.draftsList).mockReset();
  vi.mocked(api.plugins.draftPreview).mockReset();
  vi.mocked(api.plugins.draftAccept).mockReset();
  vi.mocked(api.plugins.draftReject).mockReset();
  vi.mocked(api.plugins.draftRestore).mockReset();
  vi.mocked(api.plugins.grant).mockReset();
  vi.mocked(api.plugins.setSecret).mockReset();
  setAllFlags({});
});

afterEach(() => {
  for (const r of mountedRoots.splice(0, mountedRoots.length)) act(() => r.unmount());
});

afterAll(() => { uninstallFakeDom(); });

describe('DraftsSection: «Принять…» (диалог согласия)', () => {
  it('сбой grant → ошибка видна, диалог остаётся открытым, список перечитан (мутация: убрать try/catch)', async () => {
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

    const { container } = await renderSection();

    // «Принять…» на карточке → диалог согласия (портал в document.body)
    const accept = findButton(container, 'Принять…');
    expect(accept, 'кнопка «Принять…» не найдена').not.toBeNull();
    await act(async () => { accept!.onClick(); });

    const body = (globalThis as { document: { body: FNode } }).document.body;
    const install = findButton(body, 'Установить');
    expect(install, 'кнопка «Установить» не найдена').not.toBeNull();
    await act(async () => { install!.onClick(); });

    // grant упал: ошибка видна в диалоге, диалог не закрыт,
    // список перечитан (черновик уже установлен — не должен висеть в списке)
    expect(textOf(body)).toContain('Сбой сети');
    expect(findButton(body, 'Установить'), 'диалог закрылся после ошибки').not.toBeNull();
    expect(vi.mocked(api.plugins.draftsList).mock.calls.length).toBeGreaterThanOrEqual(2);
  });

  // consentRequired: false — диалог согласия НЕ открывается (права уже выданы/не нужны).
  // Мутация «игнорировать consentRequired» (всегда открывать) краснит этот тест.
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

    const { container } = await renderSection();
    // document.body живёт между тестами (uninstallFakeDom только в afterAll) —
    // чистим leftover-диалог предыдущего теста, иначе «Установить» найдётся не от нашего accept
    const body = (globalThis as { document: { body: FNode } }).document.body;
    for (const c of [...body.childNodes]) body.removeChild(c);

    const accept = findButton(container, 'Принять…');
    expect(accept, 'кнопка «Принять…» не найдена').not.toBeNull();
    await act(async () => { accept!.onClick(); await new Promise(r => setTimeout(r, 0)); });

    // диалог не открыт: кнопки «Установить» согласия нет в body
    expect(findButton(body, 'Установить'), 'диалог согласия открылся без consentRequired').toBeNull();
  });
});

describe('DraftsSection: человекочитаемое имя пакета (D2)', () => {
  const ACCEPT_BASE = {
    id: 'local-1/weather', version: '1.0.0', sha256: 'abc12345', source: 'upload:weather',
    enabled: true, requested: ['notes:read'], granted: [], consentRequired: true,
    newPermissions: [], secrets: [],
  };

  async function acceptAndGetDialog(summary: PluginDraftSummary, accept: Record<string, unknown>) {
    setFlagLocal('plugins-build-yourself', true);
    vi.mocked(api.plugins.draftsList).mockResolvedValue([summary]);
    vi.mocked(api.plugins.draftPreview).mockResolvedValue({ ...PREVIEW, name: 'Weather' });
    vi.mocked(api.plugins.draftAccept).mockResolvedValue({ ...ACCEPT_BASE, ...accept });
    const { container, text } = await renderSection();
    const body = (globalThis as { document: { body: FNode } }).document.body;
    for (const c of [...body.childNodes]) body.removeChild(c);
    const cardText = text();
    await act(async () => { findButton(container, 'Принять…')!.onClick(); });
    return { cardText, dialogText: textOf(body) };
  }

  it('displayName «Погода» → в карточке и в диалоге согласия «Погода», а не «Weather» (мутация: вернуть humanizeId)', async () => {
    const { cardText, dialogText } = await acceptAndGetDialog(
      { ...SUMMARY, id: 'local-1/weather', displayName: 'Погода' },
      { displayName: 'Погода' },
    );
    expect(cardText).toContain('Погода');
    expect(cardText).not.toContain('Weather');
    expect(dialogText).toContain('Погода');
    expect(dialogText).not.toContain('Weather');
  });

  it('без displayName (пакет без панелей) → прежний fallback: имя манифеста в карточке, humanizeId(id) в диалоге', async () => {
    const { cardText, dialogText } = await acceptAndGetDialog(
      { ...SUMMARY, id: 'local-1/weather', displayName: null },
      { displayName: null },
    );
    expect(cardText).toContain('Weather');
    expect(dialogText).toContain('Weather');
    expect(dialogText).not.toContain('Погода');
  });

  it('пустой displayName считается «не пришло» → fallback', async () => {
    const { dialogText } = await acceptAndGetDialog(
      { ...SUMMARY, id: 'local-1/weather', displayName: '  ' },
      { displayName: '  ' },
    );
    expect(dialogText).toContain('Weather');
  });
});

describe('DraftsSection: «Установлен» → ссылка «Открыть в «Плагинах»»', () => {
  it('установлен той же версией → свёрнутая строка со ссылкой на экран плагинов', async () => {
    setFlagLocal('plugins-build-yourself', true);
    vi.mocked(api.plugins.draftsList).mockResolvedValue([SUMMARY_INSTALLED]);
    vi.mocked(api.plugins.draftPreview).mockResolvedValue(PREVIEW);

    const { container, text } = await renderSection();
    // бейдж «Установлен · 1.0.0» и ссылка «Открыть в «Плагинах»» на месте
    expect(text()).toContain('Установлен');
    expect(findButton(container, 'Открыть в «Плагинах»'), 'ссылка «Открыть в «Плагинах»» не найдена').not.toBeNull();
  });
});

// === MAJOR 4: режим диалога согласия по isUpdate (обновление → modify, установка → install) ===
// Обновление (isUpdate: true) с новым правом: диалог в режиме modify — выданное ранее
// (granted) отмечено, новое (не в granted) НЕ отмечено. Первая установка (isUpdate false/
// undefined) — режим install: все запрошенные (кроме net:*) отмечены. Мутация «игнорировать
// isUpdate» (всегда install) красит тест обновления (новое право становится отмеченным).
describe('DraftsSection: MAJOR 4 — режим диалога согласия (isUpdate)', () => {
  const body = () => (globalThis as { document: { body: FNode } }).document.body;
  // Состояние чекбокса права по его подписи: null — чекбокс не найден
  function checked(b: FNode, label: string): boolean | null {
    const node = findNodeByAttr(b, 'aria-label', label);
    if (!node) return null;
    const v = node.getAttribute('aria-checked');
    return v === null ? null : v === 'true';
  }
  // accept → «Принять…» → диалог согласия (портал в body); body чистим от leftover-диалога
  async function openConsent(accept: object) {
    setFlagLocal('plugins-build-yourself', true);
    vi.mocked(api.plugins.draftsList).mockResolvedValue([SUMMARY]);
    vi.mocked(api.plugins.draftPreview).mockResolvedValue(PREVIEW);
    vi.mocked(api.plugins.draftAccept).mockResolvedValue(accept as never);
    const { container } = await renderSection();
    const b = body();
    for (const c of [...b.childNodes]) b.removeChild(c);
    const acceptBtn = findButton(container, 'Принять…');
    expect(acceptBtn, 'кнопка «Принять…» не найдена').not.toBeNull();
    await act(async () => { acceptBtn!.onClick(); await new Promise(r => setTimeout(r, 0)); });
  }

  it('обновление (isUpdate: true) с новым правом → новое НЕ отмечено, выданное отмечено (modify)', async () => {
    await openConsent({
      id: 'local-1/my-plugin', version: '2.0.0', sha256: 'abc12345', source: 'upload:my-plugin',
      enabled: true, requested: ['notes:read', 'tasks:read'], granted: ['notes:read'],
      consentRequired: true, newPermissions: ['tasks:read'], isUpdate: true, previousVersion: '1.0.0', secrets: [],
    });
    const b = body();
    expect(checked(b, 'Читать заметки проекта'), 'выданное право не отмечено').toBe(true);
    expect(checked(b, 'Читать задачи проекта'), 'новое право отмечено (должно быть нет)').toBe(false);
  });

  it('первая установка (isUpdate: false) → все запрошенные (кроме net:*) отмечены (install)', async () => {
    await openConsent({
      id: 'local-1/my-plugin', version: '2.0.0', sha256: 'abc12345', source: 'upload:my-plugin',
      enabled: true, requested: ['notes:read', 'tasks:read'], granted: [],
      consentRequired: true, newPermissions: ['notes:read', 'tasks:read'], isUpdate: false, previousVersion: null, secrets: [],
    });
    const b = body();
    expect(checked(b, 'Читать заметки проекта'), 'право не отмечено (install)').toBe(true);
    expect(checked(b, 'Читать задачи проекта'), 'право не отмечено (install)').toBe(true);
  });

  it('ответ без isUpdate (старый сервер) → считать false → install (все запрошенные отмечены)', async () => {
    await openConsent({
      id: 'local-1/my-plugin', version: '2.0.0', sha256: 'abc12345', source: 'upload:my-plugin',
      enabled: true, requested: ['notes:read', 'tasks:read'], granted: [],
      consentRequired: true, newPermissions: ['notes:read', 'tasks:read'], secrets: [],
    });
    const b = body();
    expect(checked(b, 'Читать задачи проекта'), 'право не отмечено (install, без isUpdate)').toBe(true);
  });
});

// === Глеб MAJOR-1: обновление УСТАНОВЛЕННОГО плагина с унаследованными правами ===
// Сценарий: агент обновляет установленный плагин (isUpdate), просит ТЕ ЖЕ права (newPermissions=[])
// → consentRequired=true (у установленного есть выданные), inheritedPermissions=['notes:read'].
// Диалог (modify) показывает блок «уже выданные права» и требует явного подтверждения («Сохранить»),
// даже когда новых прав нет. При обеих пустых (первая установка / права не выданы) →
// consentRequired=false → диалога нет.
describe('DraftsSection: MAJOR-1 — обновление с унаследованными правами', () => {
  const body = () => (globalThis as { document: { body: FNode } }).document.body;
  const allText = () => textNodes(body()).join(' ');

  async function openConsent(accept: object) {
    setFlagLocal('plugins-build-yourself', true);
    vi.mocked(api.plugins.draftsList).mockResolvedValue([SUMMARY]);
    vi.mocked(api.plugins.draftPreview).mockResolvedValue(PREVIEW);
    vi.mocked(api.plugins.draftAccept).mockResolvedValue(accept as never);
    const { container } = await renderSection();
    const b = body();
    for (const c of [...b.childNodes]) b.removeChild(c);
    const acceptBtn = findButton(container, 'Принять…');
    expect(acceptBtn, 'кнопка «Принять…» не найдена').not.toBeNull();
    await act(async () => { acceptBtn!.onClick(); await new Promise(r => setTimeout(r, 0)); });
  }

  it('newPermissions пуст + inheritedPermissions=notes:read (isUpdate) → блок «уже выданные» виден, «Сохранить» активна', async () => {
    await openConsent({
      id: 'local-1/my-plugin', version: '2.0.0', sha256: 'abc12345', source: 'upload:my-plugin',
      enabled: true, requested: ['notes:read'], granted: ['notes:read'],
      consentRequired: true, newPermissions: [], isUpdate: true, previousVersion: '1.0.0',
      inheritedPermissions: ['notes:read'], secrets: [],
    });
    expect(allText(), 'блок «уже выданные права» не показан')
      .toContain('Эти права уже выданы и продолжат действовать для новой версии');
    // явное подтверждение: «Сохранить» (modify) доступна, даже без новых прав
    expect(findButton(body(), 'Сохранить'), '«Сохранить» не найдена (явное подтверждение недоступно)').not.toBeNull();
  });

  it('обе пусты (newPermissions=[] + inheritedPermissions=[]) → consentRequired=false → диалога нет', async () => {
    await openConsent({
      id: 'local-1/my-plugin', version: '2.0.0', sha256: 'abc12345', source: 'upload:my-plugin',
      enabled: true, requested: ['notes:read'], granted: [],
      consentRequired: false, newPermissions: [], isUpdate: false, previousVersion: null,
      inheritedPermissions: [], secrets: [],
    });
    // диалог не открыт: ни «Сохранить» (modify), ни «Установить» (install) в body
    expect(findButton(body(), 'Сохранить'), 'диалог modify открылся без consentRequired').toBeNull();
    expect(findButton(body(), 'Установить'), 'диалог install открылся без consentRequired').toBeNull();
  });
});

// === Минор 7: «Отклонить» — ошибка (409/сеть) видна на карточке, а не проглатывается ===
// Как «Принять…»/«Вернуть»: текст сервера на карточке, состояние согласовано. Мутация
// «вернуть пустой catch» красит тест.
describe('DraftsSection: «Отклонить» — ошибка видна (минор 7)', () => {
  it('«Отклонить» → 409 → текст ошибки сервера виден на карточке', async () => {
    setFlagLocal('plugins-build-yourself', true);
    vi.mocked(api.plugins.draftsList).mockResolvedValue([SUMMARY]);
    vi.mocked(api.plugins.draftPreview).mockResolvedValue(PREVIEW);
    vi.mocked(api.plugins.draftReject).mockRejectedValue(new Error('Черновик уже существует'));

    const { container, text } = await renderSection();
    const reject = findButton(container, 'Отклонить');
    expect(reject, 'кнопка «Отклонить» не найдена').not.toBeNull();
    await act(async () => { reject!.onClick(); await new Promise(r => setTimeout(r, 0)); });

    expect(text()).toContain('Черновик уже существует');
  });
});
