using System.Reflection;
using System.Text.RegularExpressions;
using Haptics.Core;

namespace Haptics.Windows.Tests;

/// <summary>SPEC §10.3 / EXP-081: production code must not be able to move, click, focus, hook, or inject.</summary>
public class SafetyTests
{
    public static IEnumerable<object[]> ProductionAssemblies() =>
    [
        [typeof(HapticPolicy).Assembly.Location],
        [typeof(HapticsApi).Assembly.Location],
        [Assembly.Load("haptics-tray").Location],
        [Assembly.Load("haptics-lab").Location],
        [Assembly.Load("Wpf.Ui").Location], // Third-party code inside the tray process gets the same scan.
        [Assembly.Load("Wpf.Ui.Abstractions").Location],
    ];

    [Theory]
    [MemberData(nameof(ProductionAssemblies))]
    public void AssemblyImportsNoForbiddenApis(string path)
    {
        var violations = ForbiddenApiScanner.Scan(path);
        Assert.True(violations.Count == 0, string.Join("; ", violations));
    }

    [Fact]
    public void ScannerActuallyDetectsForbiddenImports()
    {
        // Negative control: this test assembly deliberately declares a forbidden P/Invoke and member use below.
        var v = ForbiddenApiScanner.Scan(typeof(SafetyTests).Assembly.Location);
        Assert.Contains(v, x => x.Name == "SendInput");
        Assert.Contains(v, x => x.Name == "System.Windows.Forms.Cursor.set_Position");
    }

    private static void NeverCalledForbiddenMemberControl() => System.Windows.Forms.Cursor.Position = default;

    [Fact]
    public void SourceNeverUsesCaptureOrNoLegacyOrUiAccess()
    {
        var root = ArtifactLocator.FindRepoRoot(AppContext.BaseDirectory)!;
        var bad = new Regex(@"RIDEV_NOLEGACY|RIDEV_CAPTUREMOUSE|RIDEV_NOHOTKEYS|uiAccess=""true""|0x0000000[24]\s*/\*\s*RIDEV", RegexOptions.IgnoreCase);
        var hits = Directory.GetFiles(Path.Combine(root, "src"), "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".manifest", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => bad.IsMatch(File.ReadAllText(f)))
            .ToList();
        Assert.Empty(hits);
    }

    [Fact]
    public void RawInputRegistrationIsMouseOnly()
    {
        var root = ArtifactLocator.FindRepoRoot(AppContext.BaseDirectory)!;
        var src = File.ReadAllText(Path.Combine(root, "src", "Haptics.Windows", "MessageWindowThread.cs"));
        Assert.Contains("usUsage = 0x02", src);
        Assert.DoesNotContain("usUsage = 0x06", src); // keyboard
    }

    [Fact]
    public void TrayDoesNotReferenceLabOrFixtures()
    {
        var tray = Assembly.Load("haptics-tray");
        var refs = tray.GetReferencedAssemblies().Select(a => a.Name).ToList();
        Assert.DoesNotContain("haptics-lab", refs);
        Assert.DoesNotContain(tray.GetTypes(), t => t.Name.Contains("Fixture", StringComparison.Ordinal));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint SendInput(uint n, IntPtr inputs, int size);
}
