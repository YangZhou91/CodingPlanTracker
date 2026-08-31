using System;
using System.IO;
using System.Runtime.InteropServices;

namespace PlanMeter.Core.Boot;

/// <summary>
/// BOOT-01 — creates and removes the per-user Start Menu <c>shell:startup</c>
/// shortcut (<c>PlanMeter.lnk</c>). The file's existence is the single source
/// of truth for the Settings checkbox; no <c>ConfigData</c> field and no
/// <c>HKCU\Run</c> registry write.
/// </summary>
/// <remarks>
/// Call <see cref="Enable"/> / <see cref="Disable"/> on the STA UI thread
/// (WPF dispatcher). COM uses the Windows Script Host shell ProgID with
/// <c>dynamic</c> late binding — not <c>Type.InvokeMember</c> (Pitfall 3:
/// IDispatch marshaling via the runtime binder matches the machine-verified path).
/// The shortcut target is <see cref="Environment.ProcessPath"/>, not
/// <c>Assembly.Location</c>, because a single-file publish returns an empty
/// Location (Pitfall 1).
/// </remarks>
public sealed class BootShortcutManager
{
    /// <summary>ROADMAP SC#1 — fixed shortcut file name in the Startup folder.</summary>
    public const string ShortcutFileName = "PlanMeter.lnk";

    private readonly string _shortcutPath;

    /// <summary>
    /// Production path: <c>Environment.SpecialFolder.Startup</c> +
    /// <see cref="ShortcutFileName"/>. KNOWNFOLDERID-backed; honors per-user
    /// Known Folder redirection. Never parses the User Shell Folders registry.
    /// </summary>
    public BootShortcutManager() : this(
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            ShortcutFileName))
    {
    }

    /// <summary>Test seam — tests pass a temp-dir path (never the real Startup folder).</summary>
    internal BootShortcutManager(string shortcutPath)
        => _shortcutPath = shortcutPath ?? throw new ArgumentNullException(nameof(shortcutPath));

    public string ShortcutPath => _shortcutPath;

    /// <summary>SC#2 — the FILE is the single source of truth for the checkbox.</summary>
    public bool IsEnabled => File.Exists(_shortcutPath);

    /// <summary>
    /// Create the shortcut. Call on the STA UI thread.
    /// TargetPath uses <see cref="Environment.ProcessPath"/> (correct under
    /// single-file publish where <c>Assembly.Location</c> is empty).
    /// </summary>
    public void Enable()
    {
        string? exePath = Environment.ProcessPath
            ?? Path.Combine(
                AppContext.BaseDirectory,
                Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "PlanMeter") + ".exe");
        if (string.IsNullOrWhiteSpace(exePath))
        {
            throw new InvalidOperationException("Cannot resolve the current EXE path.");
        }

        string? dir = Path.GetDirectoryName(_shortcutPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        Type shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("Windows Script Host shell ProgID is not registered.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic shortcut = shell.CreateShortcut(_shortcutPath);
            shortcut.TargetPath = exePath;
            shortcut.WorkingDirectory = Path.GetDirectoryName(exePath);
            shortcut.Description = "PlanMeter — LLM plan quota widget";
            shortcut.Save();
        }
        finally
        {
            Marshal.ReleaseComObject(shell);
        }
    }

    /// <summary>Remove the shortcut. Missing file is a no-op.</summary>
    public void Disable()
    {
        if (File.Exists(_shortcutPath))
        {
            File.Delete(_shortcutPath);
        }
    }
}
