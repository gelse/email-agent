using System.Text;
using FluentAssertions;
using Xunit;

namespace EmailAgent.UnitTests.Spa;

/// <summary>
/// M2T6 acceptance criterion: "No <c>AUTH__SigningKey</c> or
/// <c>PLUGIN__SigningKey</c> value appears in the SPA bundle." This is the
/// build-time-style check the Definition of Done asks for: byte-search the
/// SPA's own build output (the config this test run itself was built with),
/// the app assemblies at the bin root, the source wwwroot and the SPA's own
/// source files for the environment/config spellings of both signing keys,
/// the bare token "SigningKey", the known test-key literals, the credential
/// literals the M2 tests use (passphrases and the DEK the unit tests plant),
/// and any quote-delimited 32-byte base64 string — the shape of a hard-coded
/// DEK. A planted-pattern control proves the search is not vacuously green.
/// </summary>
public class SpaBundleSecretTests
{
    /// <summary>Environment variable and appsettings spellings of the two keys.</summary>
    private static readonly string[] ConfigurationPatterns =
    {
        "AUTH__SigningKey",
        "Auth:SigningKey",
        "PLUGIN__SigningKey",
        "Plugin:SigningKey",
    };

    /// <summary>The known throwaway keys the test host injects (ApiTestFactory / AuthSmokeTests).</summary>
    private static readonly string[] TestKeyLiterals =
    {
        "m1t4-integration-test-signing-key-0123456789",
        "m2t3-integration-test-plugin-signing-key-0123456789",
    };

    /// <summary>
    /// M2T6 review fix (WARNING): the credentials the suites exercise, so a
    /// hard-coded passphrase or the planted DEK cannot be dropped into SPA
    /// source and go unnoticed. These literals belong to tests only — none of
    /// them may ever appear in the bundle, its wwwroot, or the SPA's sources.
    /// </summary>
    private static readonly string[] CredentialLiterals =
    {
        "m2t6-flow-passphr4se",
        "m2t6-unwr4p-passphr4se",
        "correct horse battery staple",
        "correct-horse-battery-staple-42!",
        "definitely-not-the-passphrase",
        "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=",
    };

    /// <summary>
    /// The shape of a hard-coded 32-byte DEK: exactly 43 base64 characters,
    /// one padding <c>=</c>, wrapped in quotes. Quote-anchored and fixed-width
    /// on purpose — an unanchored pattern would match the sha256 hashes in
    /// <c>_framework/blazor.boot.json</c> and the SVG data-URI in
    /// <c>app.css</c> (both legitimate), which would turn this scan into
    /// noise. <c>_framework</c> is generated, never authored, so it is out of
    /// scope for the pattern as well.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex Base64DekPattern =
        new(@"[""'][A-Za-z0-9+/]{43}=[""']",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Extensions the DEK-shape pattern is applied to (authored text only — binaries are covered by the literal searches).</summary>
    private static readonly string[] DekShapeExtensions =
    {
        ".cs", ".razor", ".json", ".js", ".html",
    };

    /// <summary>
    /// The bare token is legitimately spelled inside three third-party
    /// IdentityModel/JWT assemblies (compiled type metadata), so those files
    /// are excluded from THIS pattern only — the configuration spellings and
    /// the literals are searched everywhere, including them.
    /// </summary>
    private const string BarePattern = "SigningKey";

    private static readonly string[] BarePatternExclusions =
    {
        "Microsoft.IdentityModel.JsonWebTokens",
        "Microsoft.IdentityModel.Tokens",
        "System.IdentityModel.Tokens.Jwt",
    };

    [Fact]
    public void SpaBundle_ContainsNoSigningKeySecrets()
    {
        var root = FindRepositoryRoot();
        var config = FindBuildConfig();

        var spaBin = Path.Combine(root, "src", "EmailAgent.Spa", "bin", config, "net10.0");
        var bundleWwwroot = Path.Combine(spaBin, "wwwroot");
        Directory.Exists(bundleWwwroot).Should().BeTrue(
            "the SPA bundle must be built for this check — expected "
            + $"{bundleWwwroot}; build the solution (dotnet build EmailAgent.sln) first");

        var sourceRoot = Path.Combine(root, "src", "EmailAgent.Spa");
        var sourceWwwroot = Path.Combine(sourceRoot, "wwwroot");
        Directory.Exists(sourceWwwroot).Should().BeTrue(
            $"the SPA's source wwwroot must exist — expected {sourceWwwroot}");

        var files = EnumerateScanSet(bundleWwwroot, spaBin, sourceWwwroot, sourceRoot).ToList();
        files.Should().NotBeEmpty("the scan must actually see the bundle");

        List<string> findings = [];
        foreach (var file in files)
        {
            foreach (var pattern in ConfigurationPatterns.Concat(TestKeyLiterals).Concat(CredentialLiterals))
            {
                if (FileContains(file, pattern))
                {
                    findings.Add($"'{pattern}' found in {file}");
                }
            }

            if (!IsBarePatternExcluded(file) && FileContains(file, BarePattern))
            {
                findings.Add($"'{BarePattern}' found in {file}");
            }

            if (IsDekShapeCandidate(file, sourceRoot)
                && Base64DekPattern.IsMatch(File.ReadAllText(file)))
            {
                findings.Add($"a 32-byte base64 (DEK-shaped) string found in {file}");
            }
        }

        findings.Should().BeEmpty(
            "no signing key, configuration name, test-key literal, passphrase "
            + "or DEK may reach the WASM bundle, the served wwwroot or the SPA's "
            + "own sources (M2T6 acceptance criterion)");
    }

    [Fact]
    public void BundleSecretScan_DetectsPlantedPattern()
    {
        // Control: the byte search must find a pattern inside otherwise
        // binary content — otherwise the fact above could pass vacuously.
        var directory = Path.Combine(
            Path.GetTempPath(), $"ea-m2t6-bundle-scan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var planted = Path.Combine(directory, "planted.wasm");
            File.WriteAllBytes(planted, new byte[] { 0x00, 0x01, 0x7F, 0xFE });
            FileContains(planted, "AUTH__SigningKey").Should().BeFalse(
                "the control file starts clean");

            File.AppendAllText(planted, " middle AUTH__SigningKey tail");

            FileContains(planted, "AUTH__SigningKey").Should().BeTrue(
                "the search finds a pattern embedded in binary content");
            FileContains(planted, "PLUGIN__SigningKey").Should().BeFalse(
                "an absent pattern stays absent");

            // The M2T6 additions must be non-vacuous too: a planted
            // passphrase literal is found by the same byte search, and the
            // DEK-shape regex accepts a 32-byte base64 while rejecting the
            // sha256-shaped 44-character run the bundle manifest carries.
            FileContains(planted, CredentialLiterals[0]).Should().BeFalse(
                "the credential literal starts absent");
            File.AppendAllText(planted, " passphrase=" + CredentialLiterals[0]);
            FileContains(planted, CredentialLiterals[0]).Should().BeTrue(
                "a hard-coded passphrase in SPA output is caught");

            var dekShaped = "\"" + new string('A', 43) + "=\"";
            Base64DekPattern.IsMatch(dekShaped).Should().BeTrue(
                "a quote-wrapped 32-byte base64 is the shape this scan hunts");
            Base64DekPattern.IsMatch("\"" + new string('A', 42) + "==\"").Should().BeFalse(
                "64-byte (sha256-shape) padding is deliberately not a match");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    /// <summary>
    /// The bundle's wwwroot, the app assemblies beside it
    /// (<c>EmailAgent.Spa.*</c> / <c>EmailAgent.Contracts.*</c> — third-party
    /// bin-root dlls are copies of what _framework already carries), the
    /// source wwwroot that nginx serves in development, and the SPA's own
    /// source files (authored credential literals land in compiled string
    /// heaps as UTF-16, which an ASCII byte search of the dll would miss).
    /// </summary>
    private static IEnumerable<string> EnumerateScanSet(
        string bundleWwwroot, string spaBin, string sourceWwwroot, string sourceRoot)
    {
        foreach (var file in Directory.EnumerateFiles(
                     bundleWwwroot, "*", SearchOption.AllDirectories))
        {
            yield return file;
        }

        foreach (var file in Directory.EnumerateFiles(
                     spaBin, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(file);
            if (name.StartsWith("EmailAgent.Spa.", StringComparison.Ordinal)
                || name.StartsWith("EmailAgent.Contracts.", StringComparison.Ordinal))
            {
                yield return file;
            }
        }

        foreach (var file in Directory.EnumerateFiles(
                     sourceWwwroot, "*", SearchOption.AllDirectories))
        {
            yield return file;
        }

        var spaSources = sourceRoot;
        foreach (var file in Directory.EnumerateFiles(
                     spaSources, "*", SearchOption.AllDirectories))
        {
            if (IsBuildOutput(file))
            {
                continue;
            }

            var extension = Path.GetExtension(file);
            if (SourceFileExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                yield return file;
            }
        }
    }

    /// <summary>Authoring extensions a credential could be dropped into.</summary>
    private static readonly string[] SourceFileExtensions =
    {
        ".cs", ".razor", ".json", ".js", ".css", ".html", ".txt", ".md", ".svg",
    };

    /// <summary>True for files under a <c>bin/</c> or <c>obj/</c> directory.</summary>
    private static bool IsBuildOutput(string path)
    {
        var parts = Path.GetFullPath(path)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Any(part =>
            part.Equals("bin", StringComparison.OrdinalIgnoreCase)
            || part.Equals("obj", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Authored text under <paramref name="spaSourceRoot"/> that the DEK-shape
    /// regex may judge. Generated output is out: <c>blazor.boot.json</c>'s
    /// sha256 hashes are 44-character base64 too, and every bundle file is a
    /// copy of an authored original that is scanned in place anyway.
    /// </summary>
    private static bool IsDekShapeCandidate(string path, string spaSourceRoot)
        => path.StartsWith(spaSourceRoot, StringComparison.Ordinal)
            && !IsBuildOutput(path)
            && DekShapeExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private static bool IsBarePatternExcluded(string path)
        => BarePatternExclusions.Any(exclusion =>
            Path.GetFileName(path).Contains(exclusion, StringComparison.Ordinal));

    /// <summary>ASCII byte search — works on text, wasm binaries and compressed variants alike.</summary>
    private static bool FileContains(string path, string pattern)
    {
        var haystack = File.ReadAllBytes(path);
        var needle = Encoding.ASCII.GetBytes(pattern);
        if (needle.Length == 0 || haystack.Length < needle.Length)
        {
            return false;
        }

        var last = haystack.Length - needle.Length;
        for (var start = 0; start <= last; start++)
        {
            if (haystack[start] != needle[0])
            {
                continue;
            }

            var matched = true;
            for (var offset = 1; offset < needle.Length; offset++)
            {
                if (haystack[start + offset] != needle[offset])
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The checkout root: the first ancestor holding EmailAgent.sln.</summary>
    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "EmailAgent.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"no EmailAgent.sln above {AppContext.BaseDirectory} — run the tests from the repository checkout");
    }

    /// <summary>The build configuration (Release/Debug) this test assembly itself came from.</summary>
    private static string FindBuildConfig()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && directory.Parent?.Name != "bin")
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new InvalidOperationException(
                $"AppContext.BaseDirectory ({AppContext.BaseDirectory}) is not under a bin/<config> layout");
        }

        return directory.Name;
    }
}
