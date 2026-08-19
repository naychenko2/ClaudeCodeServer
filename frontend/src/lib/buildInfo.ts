// Метка сборки фронта: когда и из какого коммита собран этот бандл. Подставляется
// на этапе сборки (define в vite.config.ts); в dev-сервере тоже работает.
// Задача: «старый бандл» больше нельзя принять за «фикс не сделан» — метка в меню
// аватара показывает возраст сборки, сверяется с временем правки .tsx за секунды.
declare const __BUILD_SHA__: string
declare const __BUILD_AT__: string

// Рабочее окружение без define (например, сторонний раннер) не должно падать на ReferenceError
export const BUILD_SHA: string = typeof __BUILD_SHA__ === 'string' ? __BUILD_SHA__ : ''
export const BUILD_AT: string = typeof __BUILD_AT__ === 'string' ? __BUILD_AT__ : ''

// «сборка 19.08 23:52 · 15be3e8» — локальное время машины, где смотрят UI (стенд/прод
// живут на одном хосте с QA, поэтому время сборки сравнивается с файлами напрямую)
export function buildStamp(): string {
  const parts: string[] = []
  if (BUILD_AT) {
    const d = new Date(BUILD_AT)
    if (!Number.isNaN(d.getTime())) {
      const p = (n: number) => String(n).padStart(2, '0')
      parts.push(`${p(d.getDate())}.${p(d.getMonth() + 1)} ${p(d.getHours())}:${p(d.getMinutes())}`)
    }
  }
  if (BUILD_SHA) parts.push(BUILD_SHA)
  return parts.length > 0 ? `сборка ${parts.join(' · ')}` : 'сборка без метки'
}
