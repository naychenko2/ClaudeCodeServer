// Ярлыки меню «＋» композера и пустой ленты: входы, которые заводят объект контекста (ADR-023,
// `create` вида): «Картинка» заводит черновик и становится основным объектом. Вид без чата не нужен.
import { SLOT_CONTEXT_KIND, useSlot } from '../../lib/subsystems/registry';
import type { ContextKindApi } from '../../lib/chatContext/types';
import { useIsMobile } from '../../lib/breakpoints';
import type { ComposerShortcut } from '../../lib/subsystems/registry';

export function useCreateShortcuts(projectId: string | null, sessionId: string | null): ComposerShortcut[] {
  const kinds = useSlot<never, ContextKindApi>(SLOT_CONTEXT_KIND);
  const isMobile = useIsMobile();
  return createShortcuts(kinds, { projectId, sessionId, isMobile });
}

export function createShortcuts(
  kinds: readonly { name?: string; order?: number; action?: ContextKindApi }[],
  o: { projectId: string | null; sessionId: string | null; isMobile: boolean },
): ComposerShortcut[] {
  const { sessionId } = o;
  if (!sessionId) return [];
  return kinds
    .filter(c => c.action?.create)
    .sort((a, b) => (a.order ?? 0) - (b.order ?? 0))
    .map(c => ({ name: c.name ?? c.action!.kinds[0], create: c.action!.create! }))
    .map(({ name, create }) => ({
      key: `create:${name}`, title: create.title, hint: create.hint, icon: create.icon,
      onSelect: () => create.run({ projectId: o.projectId, sessionId, isMobile: o.isMobile }),
    }));
}

// Ярлыки строками «⋯»: «＋» уехала с полосы (не влезла или скрыта глазиком), а её пункт в «⋯» —
// только «Прикрепить файл». Без этих строк «Картинка», «Звук», «Видео» были бы недостижимы
export function shortcutOverflowItems(
  shortcuts: readonly ComposerShortcut[], attachInStrip: boolean,
): { key: string; icon: ComposerShortcut['icon']; label: string; sublabel?: string; onClick: () => void }[] {
  if (attachInStrip) return [];
  return shortcuts.map(sc => ({ key: sc.key, icon: sc.icon, label: sc.title, sublabel: sc.hint, onClick: sc.onSelect }));
}

// Подсказка кнопки «＋»: «Прикрепить файл, звук…» — из ярлыков, а не зашитой строкой
export function plusButtonTitle(shortcuts: Pick<ComposerShortcut, 'title'>[]): string {
  if (shortcuts.length === 0) return 'Прикрепить файл';
  const titles = shortcuts.map(s => s.title.charAt(0).toLocaleLowerCase('ru') + s.title.slice(1));
  return `Прикрепить файл, ${titles.join(', ')}…`;
}
