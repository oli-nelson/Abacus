using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Abacus;

// One ordered JSONL stream, optionally mirrored to an append-only file. No logging framework.
public sealed class EventReporter : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
    private readonly object gate = new();
    private readonly TextWriter? stdout;
    private readonly StreamWriter? file;
    private readonly Action<string>? reportFailure;
    private readonly string runId = Guid.NewGuid().ToString("N");
    private long sequence;
    private bool stdoutFailed;
    private bool fileFailed;

    public EventReporter(TextWriter? stdout = null, string? path = null, Action<string>? reportFailure = null)
    {
        this.stdout = stdout;
        this.reportFailure = reportFailure;
        if (path is not null)
            file = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read),
                new UTF8Encoding(false)) { AutoFlush = true };
    }

    public bool HasFailed { get { lock (gate) return stdoutFailed || fileFailed; } }

    public void Emit(string type, object? data = null)
    {
        lock (gate)
        {
            var line = JsonSerializer.Serialize(new
            {
                version = 1, runId, sequence = ++sequence, timestamp = DateTimeOffset.UtcNow, type, data,
            }, JsonOptions);
            Write(file, line, ref fileFailed);
            Write(stdout, line, ref stdoutFailed);
        }
    }

    private void Write(TextWriter? writer, string line, ref bool failed)
    {
        if (writer is null || failed) return;
        try { writer.WriteLine(line); writer.Flush(); }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            failed = true;
            // Reporting must never throw into ticket recovery or prevent host cleanup.
            reportFailure?.Invoke($"event output failed: {exception.Message}");
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            try { file?.Dispose(); }
            catch (IOException) { fileFailed = true; }
        }
    }
}
