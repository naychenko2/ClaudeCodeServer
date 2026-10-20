# Токены карточки плана хода в ленте (решение 20.10)

Новых значений в шкалах нет.

| Элемент | Токены |
|---|---|
| строка плана (чат с фичами) | высота 36 (на 360 — 44, перенос на 2 строки), `R` 10, рамка `C.borderLight`, фон `C.bgCard`; «План хода» 12.5/700 `C.textHeading`, «сейчас: …» `C.textSecondary` с обрезкой многоточием |
| «2 из 5» | `FONT.mono` 11, `C.textSecondary` |
| прогресс плана | дорожка `C.progressTrack`, заливка `C.accent`, 44 × 4 |
| «Подробнее ›» | 12/600 `C.accent`, высота 28 (на 360 — 40), hover `C.accentLight` |
| полный чек-лист (чат без фич) | без изменений: `R` 10, рамка `C.borderLight`, фон `C.bgCard`; заголовок PT Serif 14 `C.textHeading`; пункты 12, сделанный `C.textMuted` зачёркнут, квадрат `C.success`, текущий 600 `C.textHeading` с рамкой `C.accent`, впереди рамка `C.progressTrack` |
| снимок прошлого хода | высота 28 (на 360 — 40), 11.5 `C.textMuted`, hover `C.bgSelected`; раскрытый чек-лист — линия `C.divider` |
| попап (заглушка) | `C.bgWhite`, рамка `C.border`, `SHADOW.dropdown`; на 360 — шторка `SHADOW.sheet`, затемнение `C.overlay` |
| панель А (для сверки) | утверждена, тона `C.infoBg` / `C.dangerBg` + `C.dangerBorder`, точки этапов `C.success` / `C.progressTrack` |
