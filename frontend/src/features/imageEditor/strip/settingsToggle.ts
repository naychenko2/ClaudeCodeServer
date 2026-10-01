// Кнопка-сводка полосы «Картинки»: куда ведёт и как выглядит. С флагом панели настройки
// живут в панели «Картинки» — колонкой рабочей области (panel) или шторкой полосы (sheet);
// без флага — карточкой над полосой (card).

export type SettingsPlace = 'panel' | 'sheet' | 'card';

// opened — настройки сейчас на экране: панель стоит колонкой, шторка или карточка открыты
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

// Свёрнутая полоса: клик по сводке открывает панель; без флага панели — разворачивает полосу
export const summaryOpensPanel = (place: SettingsPlace) => place !== 'card';
