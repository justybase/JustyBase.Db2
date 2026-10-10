using System.Diagnostics;
using System.Text.Json;

namespace Db2Replay;

/// <summary>Runs fixture generation and the peer outside the measured client process.</summary>
public sealed class ReplayProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Task<string> _errors;
    public int Port { get; }
    public string Dsn(int blockSize = 65535) =>
        $"Host=127.0.0.1;Port={Port};Database=REPLAY;User=test;Pooling=false;SecurityMechanism=3;BlockSize={blockSize}";

    public ReplayProcess(string fixture, int blockSize = 65535)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(typeof(ReplayProcess).Assembly.Location);
        start.ArgumentList.Add(fixture);
        start.ArgumentList.Add(blockSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
        // A trace launched with dotnet-trace profiles the client only. Its
        // diagnostic startup port must not suspend or attach the child peer.
        start.Environment.Remove("DOTNET_DiagnosticPorts");
        start.Environment.Remove("DOTNET_DefaultDiagnosticPortSuspend");
        _process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start replay peer.");
        _errors = _process.StandardError.ReadToEndAsync();
        try
        {
            var port = _process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(60)).GetAwaiter().GetResult();
            if (!int.TryParse(port, out var value))
                throw new InvalidOperationException("Replay peer did not provide a port: " + _errors.GetAwaiter().GetResult());
            Port = value;
        }
        catch
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            _process.Dispose();
            throw;
        }
    }

    public ReplayCounters Check()
    {
        _process.StandardInput.WriteLine("stats"); _process.StandardInput.Flush();
        var line = _process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        if (line is null) throw new InvalidOperationException("Replay peer exited: " + _errors.GetAwaiter().GetResult());
        var counters = JsonSerializer.Deserialize<ReplayCounters>(line) ?? throw new InvalidDataException("Missing replay counters.");
        if (counters.ActiveQueries != 0) throw new InvalidDataException("Replay query was not completed.");
        return counters;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                await _process.StandardInput.WriteLineAsync("stop");
                await _process.StandardInput.FlushAsync();
                try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
                catch { _process.Kill(entireProcessTree: true); throw; }
            }
            var errors = await _errors;
            if (_process.ExitCode != 0) throw new InvalidOperationException("Replay peer failed: " + errors);
        }
        finally { _process.Dispose(); }
    }
}
