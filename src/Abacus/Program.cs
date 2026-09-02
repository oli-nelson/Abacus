namespace Abacus;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var parsed = Options.Parse(args);
            if (parsed.ShowHelp)
            {
                Console.Out.WriteLine(Options.Usage);
                return 0;
            }

            using var cancellation = new CancellationTokenSource();
            ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };

            Console.CancelKeyPress += cancelHandler;
            using var output = new ConsoleOutput(
                Console.Error,
                parsed.Value!.Agents.Select(static agent => agent.Name),
                parsed.Value.Model,
                parsed.Value.Verbose);
            try
            {
                var runner = new CommandRunner(output);
                var preflight = new Preflight(runner);
                var validated = await preflight.RunAsync(parsed.Value, cancellation.Token);
                if (parsed.Value.CheckOnly)
                {
                    await output.SystemAsync(
                        $"Preflight checks passed for {validated.Agents.Count} agent{(validated.Agents.Count == 1 ? string.Empty : "s")}; no tickets claimed");
                    return 0;
                }

                await output.SystemAsync(
                    $"Preflight complete; starting {AgentCommandFactory.DisplayName(parsed.Value.AgentMode)} agent loops");
                await new AbacusApplication(runner, output)
                    .RunAsync(validated, cancellation.Token);
                return 0;
            }
            finally
            {
                Console.CancelKeyPress -= cancelHandler;
            }
        }
        catch (OptionsException exception)
        {
            Console.Error.WriteLine($"abacus: {exception.Message}");
            Console.Error.WriteLine(Options.ShortUsage);
            return 2;
        }
        catch (OperationCanceledException)
        {
            return 130;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"abacus: {exception.Message}");
            return 1;
        }
    }
}
