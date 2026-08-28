// Бейдж охвата «N из M» раздела «Специальности»: сколько специальностей каталога
// уже настроено в слое. Показывается в шапке витрины ролей — переключателя
// «Персоны | Специальности», на котором бейдж жил раньше, больше нет (раздел стал
// самостоятельным, вход из меню аватара).
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