namespace ClaudeHomeServer.Services.Composition;

// Единый манифест динамического модуля (сценарий Б) — секция "DynamicModules" конфига:
// общие поля (Key/Title/Version/Enabled) + необязательные под-объекты Backend/Frontend.
// Модуль — отдельная сборка, грузится ModuleLoader'ом по Backend.AssemblyPath (относительный
// путь — от базового каталога процесса) при старте, а не через ProjectReference.
// Контракт подключения — реализация IAppSubsystem (Key/Title/Register), см. IAppSubsystem.cs.
//
// Ключи секции конфига (appsettings.json, секция "DynamicModules"):
//   DynamicModules:0:Key / Title / Version / Enabled
//   DynamicModules:0:Backend:AssemblyPath      — загрузчик берёт именно его
//   DynamicModules:0:Frontend:RemoteUrl / ExposedModule   — необязательные (MF-remote)
// Enabled=false — модуль в реестре, но загрузчик его пропускает (как гейт подсистем).
public sealed record ModuleDescriptor(
    string Key,
    string Title,
    string Version,
    bool Enabled = true,
    ModuleDescriptorBackend? Backend = null,
    ModuleDescriptorFrontend? Frontend = null);

// Бэкенд-часть модуля: путь к DLL, которую грузит ModuleLoader. Относительный — от базового
// каталога процесса (рядом с exe); абсолютный — как есть.
public sealed record ModuleDescriptorBackend(
    string AssemblyPath);

// Фронт-часть модуля (Module Federation): URL remoteEntry.js + путь expose.
// Необязательна: у «чисто бэкенд»-модуля (только контроллеры/эндпоинты) её может не быть.
public sealed record ModuleDescriptorFrontend(
    string RemoteUrl,
    string ExposedModule);
