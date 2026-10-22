# Токены вкладки «Планы»

Остальная страница — как в [`features-overview-3-final-tokens.md`](features-overview-3-final-tokens.md).
Новых значений в шкалах нет.

| Элемент | Токены |
|---|---|
| сетка 1280 | список 240 + зазор `SP` 16; список `position: sticky` под вкладками |
| строка списка | фон hover `C.bgSelected`, открытая `C.accentLight`; заголовок 12.5/600 `C.textHeading` (2 строки); подпись 11 `C.textMuted`; высота 44, на 360 — 56 |
| значок роли 24 × 24, `R` 7 | план `C.planLight` / `C.planText`; разрез `C.infoBg` / `C.info`; консультация `C.bgSelected` / `C.textSecondary` |
| метка роли в читалке | те же пары; текст разреза `C.textPrimary` на `C.infoBg` |
| «без текста» | 10 `C.textMuted`, рамка 1px dashed `C.dashed` |
| заголовок документа | PT Serif 20/700 (на 360 — 18) `C.textHeading`; разделитель `C.borderLight` |
| markdown | текст 13.5/1.6 `C.textPrimary`; `##` PT Serif 16/700; `###` 13.5/700; инлайн-код и блок кода `C.bgInset`, рамка блока `C.borderLight`, `FONT.mono` 12 |
| таблица | шапка `C.bgCard`, линии `C.borderLight`, обёртка с `overflow-x: auto` и `R` 8 |
| Mermaid-заглушка | 1.5px dashed `C.dashed`, фон `C.bgCard`, кнопка «Исходник» — `ui/Button` ghost xs |
| оглавление | фон `C.bgCard`, рамка `C.borderLight`; пункты `C.accent`, hover `C.accentLight`; номера mono 11 `C.textMuted`; на 360 пункты 40 |
| заголовок раздела (свёртка) | кнопка, hover `C.bgSelected`, высота 32 (на 360 — 44), `scroll-margin-top` 56 под липкие вкладки |
| «‹ Все документы» (360) | 44, `C.textSecondary`, hover `C.bgSelected` |
| скелет, пустое, ошибка | общие: `.sk` `C.bgSelected`; ошибка `C.dangerBg` + `C.dangerBorder`, заголовок `C.dangerText` |
| пометка «plans[]: проекция» | `C.infoBg` / `C.textPrimary` (как прочие пометки бэка) |
