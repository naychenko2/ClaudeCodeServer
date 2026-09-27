namespace ClaudeHomeServer.Services.Composition;

// Вклад подсистемы в конвейер: своя терминальная ветка статики (Viaduct 10.1 —
// раздача собранного Viaduct из вертикали Architecture).
//
// Почему не `IAppPhaseSubsystem.ConfigureApp`: `UseSubsystems()` стоит в Program.cs
// ДО защитных middleware (UseForwardedHeaders, HTTPS-редирект боевого домена,
// перехватчик превью-хоста) — ветка, поставленная там, ушла бы из-под них. А у
// динамического модуля `ConfigureApp` не зовёт никто: ModuleLoader вызывает только
// `Register`. Поэтому вертикаль регистрирует реализацию в `Register`, а Main резолвит
// все вклады одним генерическим циклом на своём месте конвейера — перед SPA-фолбэком.
public interface IStaticBranchContributor
{
    void Configure(IApplicationBuilder app);
}
