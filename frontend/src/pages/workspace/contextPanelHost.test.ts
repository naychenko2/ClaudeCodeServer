// Сторожа единственного хоста панели генерации (ADR-023 §Д6): при флаге composer-context-row справа
// живёт одна панель «chatContext», «Картинок» и «Звука» в рельсе нет, а сохранённая раскладка не
// теряет место. Скан features/** держит вертикали в стороне от ключей генерации.
import { afterEach, describe, expect, it } from 'vitest';
import { readdirSync, readFileSync, statSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { FLAGS, setAllFlags } from '../../lib/featureFlags';
import { genPanelKeys as dismissedKeys } from '../../lib/genPanelDismissed';
import {
  CHAT_RIGHT_KEYS, PANEL_HOME, PANEL_KEYS, PANEL_META, WORKSPACE_KEYS, chatRightKeys, genPanelKeys, panelRivals, workspaceKeys,
} from './panelCatalog';
import { evictForeign, sanitizeZones, zoneOf, type PanelZones } from './panelStackState';

const on = () => setAllFlags({ [FLAGS.composerContextRow]: true });
afterEach(() => setAllFlags({}));

function zones(left: string[][], right: string[][], stash: { left?: string[][]; right?: string[][] } = {}): PanelZones {
  return sanitizeZones({
    left: { layout: left, stash: stash.left ?? [] },
    right: { layout: right, stash: stash.right ?? [] },
  });
}

describe('набор панелей генерации по флагу', () => {
  it('без флага: «Картинки» и «Звук», как раньше; «Контекста» в рельсе нет', () => {
    expect(genPanelKeys()).toEqual(['images', 'sound']);
    expect(workspaceKeys(false)).toContain('images');
    expect(workspaceKeys(false)).not.toContain('chatContext');
    expect(chatRightKeys(false)).not.toContain('chatContext');
    expect(panelRivals('images')).toEqual(['sound']);
  });

  it('с флагом: GEN_PANEL_KEYS = [chatContext] в обеих копиях, images и sound вне набора экрана', () => {
    on();
    expect(genPanelKeys()).toEqual(['chatContext']);
    expect(dismissedKeys()).toEqual(['chatContext']);
    for (const keys of [workspaceKeys(true), chatRightKeys(true)]) {
      expect(keys).toContain('chatContext');
      expect(keys).not.toContain('images');
      expect(keys).not.toContain('sound');
    }
    expect(panelRivals('chatContext')).toEqual([]);
  });

  it('ссылки на наборы стабильны: зона держит их в зависимостях эффектов', () => {
    expect(workspaceKeys(true)).toBe(workspaceKeys(true));
    expect(chatRightKeys(false)).toBe(CHAT_RIGHT_KEYS);
    expect(workspaceKeys(false)).toBe(WORKSPACE_KEYS);
  });

  it('chatContext — правая панель с заголовком «Контекст»; ключ context («Персона») не тронут', () => {
    expect(PANEL_KEYS).toContain('chatContext');
    expect(PANEL_HOME.chatContext).toBe('right');
    expect(PANEL_META.chatContext.title).toBe('Контекст');
    expect(PANEL_META.context.title).toBe('Персона');
  });
});

describe('сохранённая раскладка при флаге не теряет место', () => {
  const allowed = workspaceKeys(true);

  it('«Картинки» на месте в колонке становятся «Контекстом» рядом с теми же соседями', () => {
    const z = evictForeign(zones([], [['files', 'images'], ['tasks']]), 'right', allowed)!;
    expect(z.right.layout).toEqual([['files', 'chatContext'], ['tasks']]);
    expect(zoneOf(z, 'images')).toBeNull();
  });

  it('обе старые панели в раскладке дают одну «Контекст»', () => {
    const z = evictForeign(zones([], [['images', 'sound']]), 'right', allowed)!;
    expect(z.right.layout).toEqual([['chatContext']]);
  });

  it('спрятанный набор тоже сворачивается', () => {
    const z = evictForeign(zones([], [['files']], { right: [['sound']] }), 'right', allowed)!;
    expect(z.right.stash).toEqual([['chatContext']]);
  });

  it('без флага (набор прежний) раскладка не трогается', () => {
    expect(evictForeign(zones([], [['files', 'images']]), 'right', workspaceKeys(false))).toBeNull();
  });
});

// ── Скан features/**: вертикали не зовут revealWorkspacePanel с ключами генерации ──

const FEATURES = join(fileURLToPath(new URL('../../', import.meta.url)), 'features');
// Ключ генерации в первом аргументе: литерал или константа вида IMAGES_PANEL / SOUND_PANEL / VIDEO_EDITOR_PANEL
const CALL = /revealWorkspacePanel\(\s*(?:['"](?:images|sound|videoEditor|chatContext)['"]|[A-Z][A-Z_]*_PANEL\b)/g;

// Старые вызовы под выключенным флагом: список сужается в 2к, 2з, 3ф и пустеет в 4б. Число вызовов в
// файле зафиксировано — новый вызов в старом файле тоже краснит скан. У картинок (2к-2) вызов один, в
// context/reveal.ts: при флаге он идёт в revealContextPanel / «Персонажи», без флага — в «Картинки»
const LEGACY_REVEAL_CALLS: Readonly<Record<string, number>> = {
  'imageEditor/context/reveal.ts': 1,
  'audioEditor/thread/actions.ts': 3,
  'audioEditor/thread/threadStore.ts': 1,
  'audioEditor/strip/SoundStrip.tsx': 1,
};

function sources(dir: string, out: string[] = []): string[] {
  for (const name of readdirSync(dir)) {
    const p = join(dir, name);
    if (statSync(p).isDirectory()) sources(p, out);
    else if (/\.(ts|tsx)$/.test(name) && !/\.test\.(ts|tsx)$/.test(name)) out.push(p);
  }
  return out;
}

function scanRevealCalls(files: Record<string, string>): Record<string, number> {
  const found: Record<string, number> = {};
  for (const [file, text] of Object.entries(files)) {
    const n = text.match(CALL)?.length ?? 0;
    if (n) found[file] = n;
  }
  return found;
}

describe('вертикали не знают ключей панелей генерации', () => {
  const files = Object.fromEntries(sources(FEATURES).map(f => [f.slice(FEATURES.length + 1).split('\\').join('/'), readFileSync(f, 'utf8')]));

  it('вызовы revealWorkspacePanel с ключами генерации есть только в списке старых', () => {
    expect(scanRevealCalls(files)).toEqual(LEGACY_REVEAL_CALLS);
  });

  it('сканер ловит и литерал, и константу ключа в новом файле', () => {
    const fake = { 'newKind/a.ts': "revealWorkspacePanel('images', 'settings')", 'newKind/b.ts': 'revealWorkspacePanel(IMAGES_PANEL)', 'ok.ts': "revealWorkspacePanel('files')" };
    expect(scanRevealCalls(fake)).toEqual({ 'newKind/a.ts': 1, 'newKind/b.ts': 1 });
  });
});
