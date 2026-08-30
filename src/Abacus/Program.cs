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
            try
            {
                var runner = new CommandRunner(Console.Error);
                var preflight = new Preflight(runner);
                var validated = await preflight.RunAsync(parsed.Value!, cancellation.Token);
                await new AbacusApplication(runner, Console.Error)
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
