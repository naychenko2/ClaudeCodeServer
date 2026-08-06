import { Link2 } from 'lucide-react';
import { QuickOptionCard } from './QuickOptionCard';
import { ICON_SIZE, ICON_STROKE } from './ui/icons';
import { C, FONT } from '../lib/design';
import { chainSummary, presetIdOf, presetRoute, usePresets, type ChainLabelContext } from '../lib/presets';
import { requestNewPreset } from '../lib/modelProvidersNav';

// Группа «Пресеты» в панелях выбора модели (спека, блок 2): между карточками уровней
// и списком моделей. Пресет — третий вариант в том же выборе, отдельного контрола нет.
// Пустая группа не показывается вовсе (пока пресетов нет — интерфейс не меняется).
// scope — ограничить слой: место каталога общее для всех, поэтому «Кто что выполняет»
// показывает только общие пресеты (личный бэкенд отклонит 400 — у других пользователей
// он был бы битой ссылкой).
export function PresetOptions({ value, onPick, ctx, scope }: {
  value: string;
  onPick: (route: string) => void;
  ctx: ChainLabelContext;
  scope?: 'global' | 'owner';
}) {
  const all = usePresets();
  const presets = scope ? all.filter(p => p.scope === scope) : all;
  // В контексте «только общие» (места каталога) личные пресеты скрыты фильтром — без
  // подсказки это выглядело как «пресетов нет вовсе» (дефект приёмки 19d8f18e)
  const hiddenByScope = scope === 'global' && all.length > presets.length;
  const scopeNote = (
    <div style={{ fontSize: 11.5, color: C.textMuted, lineHeight: 1.4, padding: '0 2px' }}>
      Местам доступны только общие пресеты — личные здесь не показываются.
    </div>
  );
  if (presets.length === 0) return hiddenByScope ? scopeNote : null;
  const activeId = presetIdOf(value);
  return (
    <>
      <div style={{
        fontFamily: FONT.sans, fontSize: 11, fontWeight: 700, color: C.textMuted,
        textTransform: 'uppercase', letterSpacing: '0.4px', margin: '2px 0 0',
      }}>
        Пресеты
      </div>
      {presets.map(p => (
        <div key={p.id} style={{ position: 'relative' }}>
          <QuickOptionCard
            title={p.name}
            subtitle={chainSummary(p, ctx)}
            active={activeId === p.id}
            onClick={() => onPick(presetRoute(p.id))}
          />
          <Link2
            size={ICON_SIZE.xs} strokeWidth={ICON_STROKE}
            style={{ position: 'absolute', top: 8, right: 10, color: C.textMuted, pointerEvents: 'none' }}
          />
        </div>
      ))}
      {hiddenByScope && scopeNote}
      {/* Вместо инлайн-цепочки «только для этого места» — переход в редактор пресетов
          (спека, расхождение п.2). В контексте «только общие» (места каталога) не
          показываем: кнопка начинает ЛИЧНЫЙ черновик, а месту он всё равно не годится */}
      {scope !== 'global' && (
        <button
          type="button"
          onClick={requestNewPreset}
          style={{
            alignSelf: 'flex-start', font: 'inherit', fontSize: 12, fontWeight: 600,
            color: C.accent, background: 'none', border: 'none', padding: '2px 2px',
            cursor: 'pointer', fontFamily: FONT.sans,
          }}
        >
          Собрать цепочку…
        </button>
      )}
    </>
  );
}
