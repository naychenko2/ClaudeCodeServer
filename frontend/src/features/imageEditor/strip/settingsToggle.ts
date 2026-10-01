// Кнопка-сводка полосы «Картинки»: куда ведёт и как выглядит. Настройки живут в панели
// «Картинки» — колонкой рабочей области (panel) или шторкой полосы (sheet).

export type SettingsPlace = 'panel' | 'sheet';

// opened — настройки сейчас на экране: панель стоит колонкой или шторка открыта
export function settingsToggle(place: SettingsPlace, opened: boolean): { on: boolean; title: string } {
  if (place === 'panel') {
    return opened
      ? { on: true, title: 'Настройки открыты в панели «Картинки» справа' }
      : { on: false, title: 'Открыть настройки в панели «Картинки»' };
  }
  return { on: opened, title: 'Настройки генерации' };
}

// Сводка на телефоне: «2 вар. · ≈ $0.08», кредиты коротко («≈ 4 кр.») — иначе режутся на 360
export const mobileSummary = (count: number, price: string | null) =>
  price ? `${count} вар. · ${price.replace(/ кредит\S*$/, ' кр.')}` : `${count} вар.`;
