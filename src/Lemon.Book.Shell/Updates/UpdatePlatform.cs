using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Lemon.Book.Shell.Updates;

/// <summary>
/// Picks the package that suits this computer when a release ships one per platform.
/// </summary>
/// <remarks>
/// Platforms are named like .NET runtime identifiers: <c>win-x64</c>, <c>win-arm64</c>, <c>osx-arm64</c>,
/// <c>osx-x64</c>, <c>linux-x64</c>; <c>any</c> is a package that runs everywhere. A bare operating system
/// (<c>osx</c>) stands for a package that covers every architecture of it, such as a universal macOS build.
/// <para>
/// A build that ships the .NET runtime (self-contained) adds <see cref="FullSuffix"/>: <c>win-x64-full</c>.
/// A release can carry both kinds for one platform, and an installed app updates from the kind it is: a
/// self-contained install never receives a package that needs the runtime installed, and a
/// framework-dependent one only falls back to a self-contained package when its own kind is missing.
/// </para>
/// </remarks>
public static partial class UpdatePlatform
{
    public const string Any = "any";

    /// <summary>Marks a self-contained package: <c>win-x64-full</c> runs without the .NET runtime installed.</summary>
    public const string FullSuffix = "-full";

    // Longest first, so ".tar.gz" wins over ".gz".
    private static readonly string[] PackageExtensions =
        [".tar.gz", ".tgz", ".zip", ".7z", ".exe", ".msi", ".msix", ".dmg", ".pkg", ".appimage", ".deb", ".rpm"];

    // Installers are preferred over archives when a release offers both for the same platform.
    private static readonly string[] InstallerExtensions = [".msi", ".msix", ".exe", ".dmg", ".pkg"];

    /// <summary>
    /// True when this app ships its own .NET runtime (published self-contained). Declared before
    /// <see cref="Current"/>, which reads it: static initializers run in declaration order.
    /// </summary>
    /// <remarks>
    /// Native AOT builds (which the book's releases are) always are; otherwise the runtime's folder tells. The runtime
    /// directory rather than an assembly's location: native AOT has no assembly files, and flags reading their location.
    /// </remarks>
    public static bool IsSelfContained { get; } = !RuntimeFeature.IsDynamicCodeSupported ||
        IsSelfContainedRuntime(RuntimeEnvironment.GetRuntimeDirectory(), AppContext.BaseDirectory);

    /// <summary>
    /// This process's platform, e.g. <c>win-x64</c>, <c>osx-arm64</c>, or <c>win-x64-full</c> for a
    /// self-contained install; <c>any</c> when it has no name here.
    /// </summary>
    public static string Current { get; } = Describe(RuntimeInformation.ProcessArchitecture, IsSelfContained);

    /// <summary>
    /// Keys to look for, best first: the exact platform; the x64 build on arm64 (Windows on Arm emulation,
    /// Rosetta 2 on Apple Silicon); a build for the whole operating system; a package for any platform.
    /// A framework-dependent install then accepts the same list self-contained (it runs anywhere, only
    /// larger); a self-contained one (<c>…-full</c>) accepts only self-contained packages, because the
    /// computer may not have the runtime the others need.
    /// </summary>
    /// <param name="platform">The installed platform, see <see cref="Current"/>.</param>
    /// <param name="published">
    /// The release's platform keys. When none of its packages for this operating system is <c>-full</c>,
    /// the release does not tell the two kinds apart there (published before they were, or a macOS build,
    /// which is always self-contained), so a self-contained install takes those packages as they are.
    /// </param>
    public static IReadOnlyList<string> Candidates(string platform, IEnumerable<string>? published = null)
    {
        var full = IsFull(platform);
        var rid = full ? platform[..^FullSuffix.Length] : platform;

        var native = new List<string>();
        var parts = rid.Split('-', 2);
        if (parts.Length == 2)
        {
            native.Add(rid);
            if (parts[1] == "arm64")
            {
                native.Add($"{parts[0]}-x64");
            }

            native.Add(parts[0]);
        }

        var selfContained = native.Select(x => x + FullSuffix).ToList();
        if (full)
        {
            var os = parts[0];
            var distinguishes = published is null || published.Any(x =>
                IsFull(x) && (x.StartsWith(os + "-", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(x, os + FullSuffix, StringComparison.OrdinalIgnoreCase)));
            return distinguishes ? selfContained : [.. native, Any];
        }

        return [.. native, Any, .. selfContained];
    }

    /// <summary>True for a self-contained platform key such as <c>win-x64-full</c>.</summary>
    public static bool IsFull(string platform) => platform.EndsWith(FullSuffix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A framework-dependent app runs on the shared install's runtime (<c>…/dotnet/shared/Microsoft.NETCore.App/10.0.x/</c>);
    /// a self-contained one on the runtime in its own folder (or inside its single-file bundle, with no folder at all).
    /// </summary>
    internal static bool IsSelfContainedRuntime(string? runtimeDirectory, string baseDirectory)
    {
        if (string.IsNullOrEmpty(runtimeDirectory))
        {
            return true;
        }

        try
        {
            var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory));
            var runtime = Path.TrimEndingDirectorySeparator(Path.GetFullPath(runtimeDirectory));
            return string.Equals(runtime, folder, StringComparison.OrdinalIgnoreCase) ||
                   runtime.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>The first candidate the release has a package for.</summary>
    public static Uri? Select(IReadOnlyDictionary<string, Uri> downloads, IReadOnlyList<string> candidates)
    {
        foreach (var candidate in candidates)
        {
            if (downloads.TryGetValue(candidate, out var uri))
            {
                return uri;
            }
        }

        return null;
    }

    /// <summary>True for the file types a release asset can be installed from (checksums, signatures and the like are not).</summary>
    public static bool IsPackage(string fileName) =>
        PackageExtensions.Any(x => fileName.EndsWith(x, StringComparison.OrdinalIgnoreCase));

    /// <summary>Lower is better; see <see cref="InstallerExtensions"/>.</summary>
    public static int PackageRank(string fileName) =>
        InstallerExtensions.Any(x => fileName.EndsWith(x, StringComparison.OrdinalIgnoreCase)) ? 0 : 1;

    /// <summary>
    /// The platform a file name mentions, such as <c>MyApp-1.2.0-osx-arm64.zip</c> → <c>osx-arm64</c>, or
    /// <c>MyApp-1.2.0-win-x64-full.zip</c> → <c>win-x64-full</c>; null when it names no operating system,
    /// or an architecture other than x64, x86 and arm64.
    /// </summary>
    /// <remarks>
    /// Also understands the spellings other build tools use: windows / macos / mac / darwin, amd64 /
    /// x86_64 / aarch64 / 386, win64, "universal" for a macOS build covering both architectures, and
    /// full / self-contained / selfcontained for a build that ships the runtime.
    /// </remarks>
    public static string? FromFileName(string fileName)
    {
        var name = fileName.ToLowerInvariant();
        var extension = PackageExtensions.FirstOrDefault(name.EndsWith);
        if (extension is not null)
        {
            name = name[..^extension.Length];
        }

        name = name.Replace("x86_64", "x64").Replace("amd64", "x64").Replace("aarch64", "arm64");

        string? os = null;
        string? architecture = null;
        var full = false;
        var previous = string.Empty;
        foreach (var token in TokenSeparator().Split(name))
        {
            if (token is "full" or "selfcontained" || (token == "contained" && previous == "self"))
            {
                full = true;
            }

            previous = token;
            switch (token)
            {
                case "win" or "windows":
                    os = "win";
                    break;
                case "win64":
                    os = "win";
                    architecture ??= "x64";
                    break;
                case "osx" or "macos" or "mac" or "darwin":
                    os = "osx";
                    break;
                case "linux":
                    os = "linux";
                    break;
                case "x64" or "x86" or "arm64":
                    architecture = token;
                    break;
                case "386" or "i386" or "i686":
                    architecture = "x86";
                    break;
                case "arm" or "armv6" or "armv7" or "armhf" or "ppc64le" or "s390x" or "riscv64" or "loong64":
                    // An architecture .NET desktop apps are not published for here: never offer it.
                    return null;
            }
        }

        if (os is null)
        {
            return null;
        }

        var platform = architecture is null ? os : $"{os}-{architecture}";
        return full ? platform + FullSuffix : platform;
    }

    private static string Describe(Architecture architecture, bool selfContained)
    {
        var os = OperatingSystem.IsWindows() ? "win"
            : OperatingSystem.IsMacOS() ? "osx"
            : OperatingSystem.IsLinux() ? "linux"
            : null;

        var arch = architecture switch
        {
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            Architecture.Arm64 => "arm64",
            _ => null,
        };

        if (os is null || arch is null)
        {
            return Any;
        }

        return selfContained ? $"{os}-{arch}{FullSuffix}" : $"{os}-{arch}";
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex TokenSeparator();
}
