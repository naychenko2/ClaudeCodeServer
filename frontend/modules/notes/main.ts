// Точка входа Vite для сборки MF remote notes.
// В рантайме хост не рендерит этот модуль как приложение — он только
// грузит remoteEntry.js и вызывает expose'ы. Файл нужен, чтобы Vite
// имел entry point для сборки.

// Импорт subsystem — чтобы модуль попал в graph сборки (иначе Vite
// не знает, что собирать, если index.html не ссылается на manifest).
import './subsystem';
