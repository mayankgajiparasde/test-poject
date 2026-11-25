using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using HotelUnderwriting.Core;
using HotelUnderwriting.Excel;

namespace HotelUnderwriting.Api;

/// <summary>The <c>Upload:*</c> settings. The init-only members other than <see cref="BodyTimeout"/> are test seams.</summary>
public sealed record UploadLimits(TimeSpan Timeout, int MaxHeapMb, int MaxConcurrent)
{
    // Below this a part near the 50 MB limit that is one long token cannot be inspected.
    public const int MinHeapMb = 384;

    /// <summary>Wall clock for receiving the body.</summary>
    public TimeSpan BodyTimeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Most bytes read from the parser's output: as many as a JSON request body may have.</summary>
    public int MaxOutputBytes { get; init; } = 1 << 20;
    /// <summary>Test seam: another program in place of the .NET host.</summary>
    public string? Executable { get; init; }
    /// <summary>Test seam: the folder the .NET host loads the child's application from.</summary>
    public string? ApplicationDirectory { get; init; }
    /// <summary>Test seam: replaces killing the child's process tree.</summary>
    public Action<Process>? Kill { get; init; }
    /// <summary>Test seam: runs once the child is up, before it is sent the file. Its token ends when the child is to be killed.</summary>
    public Func<Process, CancellationToken, Task>? BeforeInput { get; init; }

    public static UploadLimits Read(IConfiguration config)
    {
        int Get(string name, int fallback, int min, int max) =>
            config[$"Upload:{name}"] is not { } text ? fallback
            : int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var v) && v >= min && v <= max ? v
            : throw new InvalidOperationException($"Upload:{name} must be a whole number from {min} to {max}.");
        return new(TimeSpan.FromSeconds(Get("TimeoutSeconds", 30, 1, 300)), Get("MaxHeapMb", 512, MinHeapMb, 4096), Get("MaxConcurrent", 2, 1, 16))
        {
            BodyTimeout = TimeSpan.FromSeconds(Get("BodyTimeoutSeconds", 30, 1, 300)),
        };
    }
}

/// <summary>
/// The command the API runs in a child process: workbook bytes on stdin. On stdout one byte as soon as it is up, then the assumptions
/// JSON, the reason for refusing the file, or the type name of a server fault, told apart by the exit code.
/// </summary>
public static class WorkbookWorker
{
    public const string Command = "parse-workbook";
    public const int Rejected = 10, OutOfMemory = 11, ServerFault = 12;
    public const byte Ready = (byte)'R';
    /// <summary>How long after its time limit a child that nobody killed ends itself.</summary>
    public static readonly TimeSpan SelfExitMargin = TimeSpan.FromSeconds(5);

    public static int Run(Stream input, Stream output, string timeLimitMilliseconds)
    {
        // An orphan (its parent was killed) must not run for ever.
        var limit = TimeSpan.FromMilliseconds(double.Parse(timeLimitMilliseconds, CultureInfo.InvariantCulture)) + SelfExitMargin;
        using var selfExit = new Timer(_ => Process.GetCurrentProcess().Kill(), null, limit, System.Threading.Timeout.InfiniteTimeSpan);
        output.WriteByte(Ready);
        output.Flush();
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            output.Write(Parse(input));
            return 0;
        }
        catch (Exception e)
        {
            var chain = new List<Exception>();
            for (Exception? x = e; x is not null; x = x.InnerException) chain.Add(x);
            if (chain.Any(x => x is OutOfMemoryException)) return OutOfMemory;
            // The installation is broken, not the file: a dependency is missing or cannot be loaded or initialised.
            if (chain.FirstOrDefault(x => x is FileNotFoundException or FileLoadException or BadImageFormatException or TypeLoadException
                or TypeInitializationException or MissingMemberException or DllNotFoundException) is { } fault)
            {
                output.Write(Encoding.UTF8.GetBytes(fault.GetType().Name));
                return ServerFault;
            }
            // Only WorkbookException messages are safe to show: a library's message can quote the file.
            // Compared by name, so that handling a load failure does not itself need the Excel assembly.
            output.Write(Encoding.UTF8.GetBytes(e.GetType().Name == "WorkbookException" ? e.Message : "The workbook could not be read."));
            return Rejected;
        }
    }

    // not inlined: a dependency that cannot be loaded must fail inside the try above
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static byte[] Parse(Stream input)
    {
        var buffer = new MemoryStream();
        input.CopyTo(buffer);
        var file = buffer.ToArray();
        PackageInspector.Inspect(file);
        var assumptions = WorkbookReader.ReadAssumptions(new MemoryStream(file, false));
        return JsonSerializer.SerializeToUtf8Bytes(assumptions, ApiHost.Json);
    }
}

/// <summary>Parses an untrusted workbook in a short-lived child process with a heap limit, a wall-clock limit and a cap on its output.</summary>
public sealed class WorkbookParser(UploadLimits limits, ILogger<WorkbookParser> log) : IDisposable
{
    public const string LimitsMessage = "The workbook could not be processed within limits.";
    private static readonly TimeSpan KillGrace = TimeSpan.FromSeconds(5);

    // An allow list: connection strings, Auth settings, OTEL headers and anything added later never reach the child.
    private static readonly string[] Inherited =
    [
        "PATH", "SystemRoot", "SystemDrive", "windir", "TEMP", "TMP", "TMPDIR", "HOME", "USERPROFILE", "LOCALAPPDATA", "LANG", "LC_ALL",
        "DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_ROOT_ARM64", "DOTNET_ROOT(x86)", "DOTNET_ROLL_FORWARD", "DOTNET_SYSTEM_GLOBALIZATION_INVARIANT",
    ];

    /// <summary>One per upload being processed.</summary>
    public SemaphoreSlim Slots { get; } = new(limits.MaxConcurrent);

    public void Dispose() => Slots.Dispose();

    /// <summary>
    /// At most one of the two results is set: the assumptions, or why the file is refused. Neither means the server failed (logged here).
    /// Cancelling <paramref name="abort"/> kills the child and throws.
    /// </summary>
    public async Task<(Assumptions? Assumptions, string? Refused)> Parse(byte[] file, CancellationToken abort)
    {
        abort.ThrowIfCancellationRequested();
        using var deadline = new CancellationTokenSource(limits.Timeout);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(abort, deadline.Token);
        using var giveUp = new CancellationTokenSource();
        Process child;
        try { child = Process.Start(StartInfo()) ?? throw new InvalidOperationException(); }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            log.LogError("Workbook parser could not be started: {ExceptionType}", e.GetType().Name);
            return (null, null);
        }
        using var disposal = child;
        var output = new MemoryStream();
        var up = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tooMuch = false;
        int? exit = null;
        try
        {
            // Killing the child closes its pipes, which ends the exchange. Should that fail, the wait is given up so the slot is not lost.
            using var kill = stop.Token.Register(() =>
            {
                Kill(child);
                giveUp.CancelAfter(KillGrace);
            });
            try { await Exchange().WaitAsync(giveUp.Token); }
            catch (OperationCanceledException) when (giveUp.IsCancellationRequested)
            {
                log.LogWarning("Workbook parser did not finish after it was to be killed; no longer waiting for it");
            }
        }
        finally
        {
            if (!child.HasExited) Kill(child);
            if (child.WaitForExit(KillGrace)) exit = child.ExitCode;
            else log.LogWarning("Workbook parser process {ProcessId} did not end after being killed", child.Id);
            Close(() => child.StandardInput.Dispose());
            Close(() => child.StandardOutput.Dispose());
            Close(() => child.StandardError.Dispose());
        }

        abort.ThrowIfCancellationRequested();
        var started = up.Task.IsCompletedSuccessfully && up.Task.Result;
        if (!tooMuch)
        {
            if (exit == WorkbookWorker.Rejected && started) return (null, Text());
            if (exit == WorkbookWorker.ServerFault && started)
            {
                log.LogError("Workbook parser failed on the server: {ExceptionType}", Text());
                return (null, null);
            }
            if (exit == 0 && started)
            {
                try
                {
                    if (JsonSerializer.Deserialize<Assumptions>(output.ToArray(), ApiHost.Json) is { } parsed) return (parsed, null);
                }
                catch (JsonException) { } // judged below
            }
            if (!started && !deadline.IsCancellationRequested)
            {
                // the host could not load the application, or the process ended before running it
                log.LogError("Workbook parser did not start up (exit code {ExitCode})", exit);
                return (null, null);
            }
        }
        var cause = tooMuch ? "OutputLimit"
            : exit == WorkbookWorker.OutOfMemory ? "MemoryLimit"
            : exit == 0 ? "BadOutput"
            : deadline.IsCancellationRequested ? "TimeLimit"
            : "ChildCrash";
        log.LogWarning("Workbook parsing failed: {Cause} (exit code {ExitCode})", cause, exit);
        return (null, LimitsMessage);

        string Text() => Encoding.UTF8.GetString(output.ToArray());

        async Task Exchange()
        {
            var errors = child.StandardError.BaseStream.CopyToAsync(Stream.Null, CancellationToken.None); // drained, never logged: it may quote the file
            var reading = ReadOutput();
            try
            {
                if (await up.Task)
                {
                    if (limits.BeforeInput is { } hook) await hook(child, stop.Token);
                    await child.StandardInput.BaseStream.WriteAsync(file, stop.Token);
                    child.StandardInput.Close();
                }
            }
            catch (Exception e) when (e is IOException or OperationCanceledException) { } // the child is gone or being killed; judged above
            await reading;
            await child.WaitForExitAsync(CancellationToken.None);
            await errors;
        }

        async Task ReadOutput()
        {
            try
            {
                var chunk = new byte[81920];
                if (await child.StandardOutput.BaseStream.ReadAsync(chunk.AsMemory(0, 1), CancellationToken.None) == 0 || chunk[0] != WorkbookWorker.Ready) return;
                up.TrySetResult(true);
                int n;
                while ((n = await child.StandardOutput.BaseStream.ReadAsync(chunk, CancellationToken.None)) > 0)
                {
                    if (output.Length + n > limits.MaxOutputBytes)
                    {
                        tooMuch = true;
                        Kill(child);
                        return;
                    }
                    output.Write(chunk, 0, n);
                }
            }
            finally { up.TrySetResult(false); }
        }
    }

    /// <summary>This application again, with the worker command: its own executable when it runs as one, otherwise the .NET host and its dll.</summary>
    private ProcessStartInfo StartInfo()
    {
        var assembly = typeof(WorkbookParser).Assembly;
        var name = assembly.GetName().Name;
        var self = Environment.ProcessPath;
        var isSelf = self is not null && Path.GetFileName(self) is var f && (f == name || f == name + ".exe");
        // <root>/shared/Microsoft.NETCore.App/<version>/ -> <root>/dotnet
        var host = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        var info = new ProcessStartInfo(limits.Executable ?? (isSelf ? self! : host))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        if (!isSelf) info.ArgumentList.Add(Path.Combine(limits.ApplicationDirectory ?? AppContext.BaseDirectory, name + ".dll"));
        info.ArgumentList.Add(WorkbookWorker.Command);
        info.ArgumentList.Add(limits.Timeout.TotalMilliseconds.ToString(CultureInfo.InvariantCulture));
        info.Environment.Clear();
        foreach (var key in Inherited)
            if (Environment.GetEnvironmentVariable(key) is { } value) info.Environment[key] = value;
        info.Environment["DOTNET_GCHeapHardLimit"] = ((long)limits.MaxHeapMb << 20).ToString("X"); // the runtime reads it as hex
        info.Environment["DOTNET_gcServer"] = "0";
        return info;
    }

    private void Kill(Process child)
    {
        try
        {
            if (limits.Kill is { } kill) kill(child);
            else child.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { } // already gone
        catch (Exception e) when (e is Win32Exception or AggregateException)
        {
            log.LogWarning("Workbook parser process could not be killed: {ExceptionType}", e.GetType().Name);
        }
    }

    private static void Close(Action dispose)
    {
        try { dispose(); }
        catch (IOException) { } // unsent input of a child that is gone
    }
}
