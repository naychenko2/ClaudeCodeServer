// Бейдж охвата «N из M» для переключателя режима раздела «Персоны»: сколько
// специальностей каталога уже настроено в слое, на котором открылся бы экран
// «Специальности». Используется и на переключателе (главная точка входа —
// макет v4), и в самом экране PersonasSpecialties. Хук общий, чтобы обе точки
// считали одинаково и бейдж не «прыгал» при открытии экрана.
//
// Считаем по стартовому слою (pickStartScope): пустой общий → на «Только для
// меня» — иначе экран встретил бы пользователя пустотой, пока все правила
// лежат в личном слое (решение владельца 14.08.2026 для вкладки «Правила»).

import { useMemo } from 'react';
import { useSpecialtyCatalog } from '../../lib/specialties';
import { useSpecialtySettings } from '../../lib/presets';
import { coverageOf, pickStartScope } from '../specialties/specialRules/model';

export function useSpecialtiesCoverage(isAdmin: boolean): string | null {
  const settings = useSpecialtySettings();
  const catalog = useSpecialtyCatalog();

  return useMemo(() => {
    if (!settings || !catalog) return null;
    const startScope = pickStartScope(settings, catalog, isAdmin);
    const { configured, total } = coverageOf(settings[startScope], catalog);
    return configured > 0 ? `${configured} из ${total}` : null;
  }, [settings, catalog, isAdmin]);
}