namespace ClaudeHomeServer.Services.Execution;

// ВЕРХНЯЯ оценка длины ОДНОГО аргумента argv в широких символах после экранирования
// .NET (ProcessStartInfo → PasteArguments):
//   длина + 2 (обрамляющие кавычки, если есть пробел/таб/" в тексте) +
//   2 × число внутренних " (каждая удваивается: " → \") +
//   серии \ перед каждой " и в конце аргумента (каждая серия из n слэшей удваивается —
//   n → 2n), плюс по +1 на саму кавычку (мы её уже считали выше).
//
// Занижать нельзя: по заниженной цифре мы бы пропустили ход, который Process.Start
// отвергнет с Win32 206 (ревью dc641949, волна 3: блокер был именно в занижении
// оценки длины cmdline).
//
// ЕДИНСТВЕННОЕ место, где живёт формула: раннеры (LocalProcessRunner / DockerProcessRunner)
// используют её в EstimateCommandLineLength, TurnPromptAssembler — в ApplyBudget.
// Расхождение формул = расхождение оценки и реальной сборки .NET, и тесты
// DockerProcessRunnerCmdlineEstimationTests его ловят.
public static class CmdlineEstimate
{
    public static int ArgCost(string a)
    {
        if (string.IsNullOrEmpty(a)) return 1;  // один пробел-разделитель даже для пустого
        var quotes = 0;
        var slashDoubled = 0;
        var run = 0;
        foreach (var c in a)
        {
            if (c == '"')
            {
                quotes++;
                // Серия слэшей ПЕРЕД кавычкой: run слэшей → 2*run. +1 сама кавычка уже учтена.
                slashDoubled += run;
                run = 0;
            }
            else if (c == '\\') run++;
            else run = 0;
        }
        // Серия слэшей В КОНЦЕ аргумента (без завершающей кавычки) тоже удваивается.
        slashDoubled += run;
        return a.Length + 2 + 2 * quotes + slashDoubled + 1;
    }
}
