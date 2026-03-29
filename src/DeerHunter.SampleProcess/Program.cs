var config = SampleProcessConfiguration.Parse(args);

if (!string.IsNullOrWhiteSpace(config.Stdout))
{
    Console.WriteLine(config.Stdout);
}

if (!string.IsNullOrWhiteSpace(config.Stderr))
{
    Console.Error.WriteLine(config.Stderr);
}

if (config.DelayMilliseconds > 0)
{
    await Task.Delay(config.DelayMilliseconds);
}

if (config.WaitForExit)
{
    await Task.Delay(Timeout.InfiniteTimeSpan);
}

return config.ExitCode;

internal sealed record SampleProcessConfiguration(
    string? Stdout,
    string? Stderr,
    int DelayMilliseconds,
    bool WaitForExit,
    int ExitCode)
{
    public static SampleProcessConfiguration Parse(string[] args)
    {
        string? stdout = null;
        string? stderr = null;
        var delayMilliseconds = 0;
        var waitForExit = false;
        var exitCode = 0;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--stdout":
                    stdout = ReadValue(args, ref index);
                    break;
                case "--stderr":
                    stderr = ReadValue(args, ref index);
                    break;
                case "--delay-ms":
                    delayMilliseconds = int.Parse(ReadValue(args, ref index));
                    break;
                case "--wait":
                    waitForExit = true;
                    break;
                case "--exit-code":
                    exitCode = int.Parse(ReadValue(args, ref index));
                    break;
            }
        }

        return new SampleProcessConfiguration(stdout, stderr, delayMilliseconds, waitForExit, exitCode);
    }

    private static string ReadValue(string[] args, ref int index)
    {
        if (index + 1 >= args.Length)
        {
            throw new InvalidOperationException($"Missing value for argument '{args[index]}'.");
        }

        index++;
        return args[index];
    }
}
