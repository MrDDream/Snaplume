using System.Diagnostics;
using System.Text;

namespace Snaplume.Platform.Linux.Util;

/// <summary>What a finished external program left behind.</summary>
internal sealed record ToolResult(int ExitCode, byte[] Output, string Errors)
{
    public string Text => Encoding.UTF8.GetString(Output);
    public bool Succeeded => ExitCode == 0;
}

/// <summary>
/// Runs the command-line programs Snaplume relies on (ffmpeg, tesseract, gio, lp...). Programs are resolved to an
/// absolute path from PATH and receive their arguments one by one (ArgumentList): no shell ever sees user data,
/// so a file name with quotes, spaces or "$(...)" stays a file name.
/// </summary>
internal static class ExternalTools
{
    /// <summary>Searched when PATH is empty (session started by a minimal launcher).</summary>
    private static readonly string[] DefaultPath = { "/usr/local/bin", "/usr/bin", "/bin" };

    /// <summary>Absolute path of an executable found in PATH, or null.</summary>
    /// <remarks>Relative PATH entries (".", "bin") are ignored: they would make the result depend on the current folder.</remarks>
    public static string? Find(string name) => Find(name, Environment.GetEnvironmentVariable("PATH"));

    internal static string? Find(string name, string? pathVariable)
    {
        if (name.Contains('/')) return null;
        var folders = string.IsNullOrEmpty(pathVariable) ? DefaultPath : pathVariable.Split(':', StringSplitOptions.RemoveEmptyEntries);
        foreach (var folder in folders)
        {
            if (!folder.StartsWith('/')) continue;
            var candidate = Path.Combine(folder, name);
            if (IsExecutable(candidate)) return candidate;
        }
        return null;
    }

    private static bool IsExecutable(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            if (OperatingSystem.IsWindows()) return true;
            var mode = File.GetUnixFileMode(path);
            return (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Prepared start information: no shell, no window, arguments passed as a list.</summary>
    public static ProcessStartInfo StartInfo(string executable, IEnumerable<string> arguments, IReadOnlyDictionary<string, string>? environment = null)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        if (environment != null)
            foreach (var (key, value) in environment) info.Environment[key] = value;
        return info;
    }

    /// <summary>Environment that makes programs print untranslated messages, for the parsers.</summary>
    public static readonly IReadOnlyDictionary<string, string> CLocale = new Dictionary<string, string> { ["LC_ALL"] = "C" };

    /// <summary>Runs a program to completion and collects its output.</summary>
    /// <param name="input">Bytes written to the standard input, which is then closed; null leaves it unredirected.</param>
    /// <exception cref="TimeoutException">The program ran longer than <paramref name="timeout"/> (it is killed).</exception>
    /// <exception cref="OperationCanceledException">Cancelled (the program is killed).</exception>
    public static async Task<ToolResult> RunAsync(string executable, IEnumerable<string> arguments, TimeSpan timeout,
        CancellationToken cancel = default, byte[]? input = null, IReadOnlyDictionary<string, string>? environment = null)
    {
        var info = StartInfo(executable, arguments, environment);
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.RedirectStandardInput = input != null;
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"{executable} could not be started");
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timer.CancelAfter(timeout);
        try
        {
            var output = new MemoryStream();
            var outputTask = process.StandardOutput.BaseStream.CopyToAsync(output, timer.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timer.Token);
            if (input != null)
            {
                try
                {
                    await process.StandardInput.BaseStream.WriteAsync(input, timer.Token).ConfigureAwait(false);
                    process.StandardInput.Close();
                }
                catch (IOException)
                {
                    // The program stopped reading (broken pipe): its exit code tells what happened.
                }
            }
            await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
            await process.WaitForExitAsync(timer.Token).ConfigureAwait(false);
            return new ToolResult(process.ExitCode, output.ToArray(), await errorTask.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            if (cancel.IsCancellationRequested) throw;
            throw new TimeoutException($"{Path.GetFileName(executable)} did not finish within {timeout.TotalSeconds:0} s");
        }
    }

    /// <summary>Synchronous variant for the few calls made where awaiting is not possible (short programs only).</summary>
    public static ToolResult Run(string executable, IEnumerable<string> arguments, TimeSpan timeout,
        byte[]? input = null, IReadOnlyDictionary<string, string>? environment = null) =>
        Task.Run(() => RunAsync(executable, arguments, timeout, CancellationToken.None, input, environment)).GetAwaiter().GetResult();

    /// <summary>
    /// Starts a program that may stay in the background (xdg-open, xclip, wl-copy fork to serve their data) and
    /// waits only for the launcher to return. Standard output and error are not redirected: a forked child keeping
    /// them open would otherwise block the wait.
    /// </summary>
    /// <returns>The exit code, or null if it was still running after <paramref name="timeout"/>.</returns>
    public static async Task<int?> LaunchAsync(string executable, IEnumerable<string> arguments, TimeSpan timeout, byte[]? input = null)
    {
        var info = StartInfo(executable, arguments);
        info.RedirectStandardInput = input != null;
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"{executable} could not be started");
        if (input != null)
        {
            try
            {
                await process.StandardInput.BaseStream.WriteAsync(input).ConfigureAwait(false);
                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // Exit code below.
            }
        }
        using var timer = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(timer.Token).ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    public static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // Already gone.
        }
    }
}
