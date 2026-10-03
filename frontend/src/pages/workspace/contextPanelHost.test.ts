// Сторожа единственного хоста панели генерации (ADR-023 §Д6): справа живёт одна панель «chatContext»,
// «Картинок», «Звука» и «Видео» в рельсе нет, а сохранённая раскладка со старыми ключами не теряет
// место (LEGACY_KEY_ALIASES). Скан features/** держит вертикали в стороне от ключей генерации.
import { describe, expect, it } from 'vitest';
import { readdirSync, readFileSync, statSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { genPanelKeys as dismissedKeys } from '../../lib/genPanelDismissed';
import { toGenPanelKey } from '../../lib/genPanelKeys';
import {
  CHAT_RIGHT_KEYS, PANEL_HOME, PANEL_KEYS, PANEL_META, WORKSPACE_KEYS, genPanelKeys, migrateLegacyKey,
} from './panelCatalog';
import { migrateZones, sanitizeLayout, sanitizeZones, zoneOf, type PanelZones } from './panelStackState';

// Ключи упразднённых панелей генерации: в каталоге их быть не должно
const REMOVED = ['images', 'sound', 'videoEditor'];

function zones(left: string[][], right: string[][], stash: { left?: string[][]; right?: string[][] } = {}): PanelZones {
  return sanitizeZones({
    left: { layout: left, stash: stash.left ?? [] },
    right: { layout: right, stash: stash.right ?? [] },
  });
}

describe('набор панелей генерации', () => {
  it('GEN_PANEL_KEYS = [chatContext] в обеих копиях', () => {
    expect(genPanelKeys()).toEqual(['chatContext']);
    expect(dismissedKeys()).toEqual(['chatContext']);
  });

  it('упразднённых ключей нет в каталоге, наборах экранов, мете и домашних зонах', () => {
    for (const k of REMOVED) {
      expect(PANEL_KEYS as readonly string[]).not.toContain(k);
      expect(WORKSPACE_KEYS as readonly string[]).not.toContain(k);
      expect(CHAT_RIGHT_KEYS as readonly string[]).not.toContain(k);
      expect(Object.keys(PANEL_META)).not.toContain(k);
      expect(Object.keys(PANEL_HOME)).not.toContain(k);
    }
  });

  it('chatContext — правая панель с заголовком «Контекст» в проекте и в правой зоне личного чата; ключ context («Персона») не тронут', () => {
    expect(PANEL_KEYS).toContain('chatContext');
    expect(WORKSPACE_KEYS).toContain('chatContext');
    expect(CHAT_RIGHT_KEYS).toContain('chatContext');
    expect(PANEL_HOME.chatContext).toBe('right');
    expect(PANEL_META.chatContext.title).toBe('Контекст');
    expect(PANEL_META.context.title).toBe('Персона');
  });

  it('старый вызов с ключом упразднённой панели ведёт в chatContext', () => {
    for (const k of REMOVED) expect(toGenPanelKey(k)).toBe('chatContext');
    expect(toGenPanelKey('files')).toBe('files');
  });
});

describe('сохранённая раскладка со старыми ключами не теряет место (LEGACY_KEY_ALIASES)', () => {
  it('ключи images, sound, videoEditor переводятся в chatContext, прочие остаются', () => {
    for (const k of REMOVED) expect(migrateLegacyKey(k)).toBe('chatContext');
    expect(migrateLegacyKey('files')).toBe('files');
    expect(migrateLegacyKey('personas')).toBe('team');
    expect(migrateLegacyKey('нет-такой')).toBeNull();
  });

  it('«Картинки» на месте в колонке становятся «Контекстом» рядом с теми же соседями', () => {
    const z = zones([], [['files', 'images'], ['tasks']]);
    expect(z.right.layout).toEqual([['files', 'chatContext'], ['tasks']]);
    expect(zoneOf(z, 'chatContext')).toBe('right');
  });

  it('все три старые панели в одной раскладке дают одну «Контекст» на месте первой', () => {
    expect(sanitizeLayout([['sound', 'files'], ['images', 'videoEditor']])).toEqual([['chatContext', 'files']]);
  });

  it('спрятанный набор тоже переводится', () => {
    const z = zones([], [['files']], { right: [['sound']] });
    expect(z.right.stash).toEqual([['chatContext']]);
  });

  it('«Контекст» уже слева, а старая панель справа — панель остаётся в одном месте (правая зона главнее)', () => {
    const z = zones([['chatContext']], [['files', 'images']]);
    expect(zoneOf(z, 'chatContext')).toBe('right');
    expect(z.right.layout).toEqual([['files', 'chatContext']]);
    expect(z.left.layout).toEqual([]);
  });

  it('привязка к зоне, веса, ящик и порядок кнопок со старыми ключами тоже переводятся', () => {
    const z = sanitizeZones({
      left: { layout: [], stash: [] },
      right: { layout: [], stash: [] },
      home: { images: 'left', files: 'right' },
      weights: { sound: 2 },
      tucked: ['videoEditor', 'sound'],
      railOrder: ['images', 'files'],
    });
    expect(z.home.chatContext).toBe('left');
    expect(z.weights.chatContext).toBe(2);
    expect(z.tucked).toEqual(['chatContext']);
    expect(z.railOrder).toEqual(['chatContext', 'files']);
  });

  it('раскладка в формате до зон (cc_*_panels_layout) со старыми ключами читается', () => {
    const store: Record<string, string> = {
      cc_ws_panels_layout: JSON.stringify([['files', 'images'], ['sound']]),
      cc_ws_left_panels_layout: JSON.stringify([['tasks', 'videoEditor']]),
    };
    const z = migrateZones(k => store[k] ?? null, 'ws')!;
    expect(z.right.layout).toEqual([['files', 'chatContext']]);
    expect(z.left.layout).toEqual([['tasks']]);
  });
});

// ── Скан features/**: вертикали не зовут revealWorkspacePanel с ключами генерации ──

const FEATURES = join(fileURLToPath(new URL('../../', import.meta.url)), 'features');
// Ключ генерации в первом аргументе: литерал или константа вида IMAGES_PANEL / SOUND_PANEL / VIDEO_EDITOR_PANEL
const CALL = /revealWorkspacePanel\(\s*(?:['"](?:images|sound|videoEditor|chatContext)['"]|[A-Z][A-Z_]*_PANEL\b)/g;

// Вызовы revealWorkspacePanel с ключами генерации во вклады вертикалей не допускаются совсем: панель «Контекст» открывает
// revealContextPanel (allow-list пуст, 4б-2). Число вызовов в файле фиксировать не нужно — список пуст.
const LEGACY_REVEAL_CALLS: Readonly<Record<string, number>> = {};

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

  it('вызовов revealWorkspacePanel с ключами генерации в features/** нет (allow-list пуст)', () => {
    expect(scanRevealCalls(files)).toEqual(LEGACY_REVEAL_CALLS);
  });

  it('сканер ловит и литерал, и константу ключа в новом файле', () => {
    const fake = { 'newKind/a.ts': "revealWorkspacePanel('images', 'settings')", 'newKind/b.ts': 'revealWorkspacePanel(IMAGES_PANEL)', 'ok.ts': "revealWorkspacePanel('files')" };
    expect(scanRevealCalls(fake)).toEqual({ 'newKind/a.ts': 1, 'newKind/b.ts': 1 });
  });
});
