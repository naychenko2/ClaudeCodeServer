namespace ClaudeHomeServer.Services.Execution;

// Метка убиваемости процесса (ProcessSpec.TurnId) — ЕДИНСТВЕННЫЙ генератор на продукт.
// Длина ровно 12 символов жёстко привязана к docker-раннеру: pid-файл /tmp/turns/{id}.pid и
// 12-символьный плейсхолдер оценки длины командной строки (DockerProcessRunner). Свою копию
// формулы не заводить: раньше их было две, и мутации длины проходили мимо сторожей.
public static class ProcessTurnIds
{
    public const int Length = 12;

    public static string New() => Guid.NewGuid().ToString("N")[..Length];
}
