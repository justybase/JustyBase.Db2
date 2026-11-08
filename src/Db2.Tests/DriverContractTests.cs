using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace JustyBase.Db2.Tests;

public sealed class DriverContractTests
{
    private static readonly Regex Header = new(
        "^<!-- normative; v1; sha256:([0-9a-f]{64}); generated from the maintainer contract; do not edit -->$",
        RegexOptions.CultureInvariant);

    [Fact]
    public void NormativeContractCopyIsSelfConsistent()
    {
        var text = File.ReadAllText(FindContract()).Replace("\r\n", "\n", StringComparison.Ordinal);
        var newline = text.IndexOf('\n');
        Assert.True(newline > 0, "contract has no header line");
        var match = Header.Match(text[..newline]);
        Assert.True(match.Success, "contract header is missing or malformed");
        var body = text[(newline + 1)..];
        var actual = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
        Assert.Equal(match.Groups[1].Value, actual);
        Assert.False(text.Contains("db2-conformance", StringComparison.OrdinalIgnoreCase));
    }

    private static string FindContract()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "docs", "DRIVER-CONTRACT.md");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("Could not locate docs/DRIVER-CONTRACT.md above the test output directory.");
    }
}
