// Бейдж охвата «N из M» для переключателя режима раздела «Персоны»: сколько
// специальностей каталога уже настроено в слое, на котором открылся бы экран
// «Специальности». Используется и на переключателе (главная точка входа —
// макет v4), и в самом экране PersonasSpecialties. Хук общий, чтобы обе точки
// считали одинаково и бейдж не «прыгал» при открытии экрана.
//
// Волна 4 убрала owner/user-слои — считаем всегда по global.

import { useMemo } from 'react';
import { useSpecialtyCatalog } from '../../lib/specialties';
import { useSpecialtySettings } from '../../lib/presets';
import { coverageOf } from '../specialties/specialRules/model';

export function useSpecialtiesCoverage(_isAdmin: boolean): string | null {
  const settings = useSpecialtySettings();
  const catalog = useSpecialtyCatalog();

  return useMemo(() => {
    if (!settings || !catalog) return null;
    const { configured, total } = coverageOf(settings.global, catalog);
    return configured > 0 ? `${configured} из ${total}` : null;
  }, [settings, catalog]);
}