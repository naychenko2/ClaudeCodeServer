# Шрифт Hanken Grotesk для панели плагина

Файлы шрифта, которые вшивает в `srcdoc` панели плагина (шаг S10).
Панель — sandbox-iframe с CSP `default-src 'none'`, поэтому системный
`@import` Google Fonts в неё не доезжает: шрифт кладём в репозиторий и
встраиваем как data-URL.

## Файлы

| Файл | Подмножество | Вес |
|------|--------------|----|
| `HankenGrotesk-latin.woff2` | latin (U+0000-00FF, …) | 400/500/600/700 |
| `HankenGrotesk-cyrillic.woff2` | cyrillic-ext (U+0460-052F, …) | 400/500/600/700 |
| `OFL.txt` | лицензия SIL Open Font License 1.1 | — |

## Источник и версия

- **Шрифт:** Hanken Grotesk
- **Источник:** Google Fonts — <https://fonts.google.com/specimen/Hanken+Grotesk>
- **Версия:** v12 (URL `fonts.gstatic.com/s/hankengrotesk/v12/…`)
- **Лицензия:** SIL Open Font License 1.1 (см. `OFL.txt`)
- **CSS-запрос, из которого взяты файлы:**
  `https://fonts.googleapis.com/css2?family=Hanken+Grotesk:wght@400;500;600;700&display=swap`
  (User-Agent: Chrome → woff2)

## Важное: это variable font

Google Fonts отдаёт Hanken Grotesk как **variable font**: все четыре веса
(400/500/600/700) — **один и тот же файл** (одинаковый URL/hэш) на подмножество.
Вес выбирается CSS-свойством `font-weight`, отдельного файла на каждый вес нет.
Поэтому в папке 2 файла, а не 8.

## Важное: покрытие кириллицы

Подмножество, которое Google называет `cyrillic-ext` (диапазон U+0460-052F),
в бесплатном Hanken Grotesk v12 — **вырожденный placeholder**: полный файл
шрифта (550 глифов) содержит **0 кириллических букв** (U+0400-045F и
U+0460-052F отсутствуют; проверено cmap: А, Б, В, Р, Я, а, б, в, р, я, ё, Ё —
все отсутствуют). Кириллицу Hanken добавляет в **HK Grotesk Pro** (платная
Pro-версия), а не в бесплатный шрифт на Google Fonts.

Следствие: если вшить только эти файлы, **русский текст панели может откатиться
на системный шрифт**. Финальную проверку рендера русского текста делает Кира
(шаг S10) — см. отчёт S10a.
