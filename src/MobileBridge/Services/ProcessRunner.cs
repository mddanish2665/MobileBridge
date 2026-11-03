using System.Diagnostics;
using System.Text;

namespace MobileBridge.Services;

public sealed record ProcResult(int ExitCode, string StdOut, string StdErr)
{
    public string All => (StdOut + "\n" + StdErr).Trim();
}

public static class ProcessRunner
{
    /// <summary>
    /// Runs a console program and captures its output. Polls for exit instead of waiting for
    /// end-of-stream, because "adb" may spawn a background server that keeps the pipes open.
    /// </summary>
    public static async Task<ProcResult> RunAsync(string exe, IReadOnlyList<string> args, TimeSpan timeout,
        Action<IDictionary<string, string?>>? env = null, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        var dir = Path.GetDirectoryName(exe);
        if (!string.IsNullOrEmpty(dir)) psi.WorkingDirectory = dir;
        foreach (var a in args) psi.ArgumentList.Add(a);
        env?.Invoke(psi.Environment);

        var so = new StringBuilder();
        var se = new StringBuilder();
        var outDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => { if (e.Data == null) outDone.TrySetResult(); else lock (so) so.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data == null) errDone.TrySetResult(); else lock (se) se.AppendLine(e.Data); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        var sw = Stopwatch.StartNew();
        while (!p.HasExited)
        {
            if (ct.IsCancellationRequested || sw.Elapsed > timeout)
            {
                try { p.Kill(true); } catch { }
                ct.ThrowIfCancellationRequested();
                throw new TimeoutException($"'{Path.GetFileName(exe)} {string.Join(' ', args)}' did not finish in time.");
            }
            await Task.Delay(40);
        }
        await Task.WhenAny(Task.WhenAll(outDone.Task, errDone.Task), Task.Delay(1500));

        string o, e2;
        lock (so) o = so.ToString();
        lock (se) e2 = se.ToString();
        return new ProcResult(p.ExitCode, o, e2);
    }
}
