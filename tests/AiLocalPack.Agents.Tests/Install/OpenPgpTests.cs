using System.Text;
using AiLocalPack.Agents.Claude;
using AiLocalPack.Agents.Install;

namespace AiLocalPack.Agents.Tests.Install;

/// <summary>Checking a vendor's detached OpenPGP signature with a pinned key (task 43).</summary>
public sealed class OpenPgpTests : IDisposable
{
    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "fixtures", "agents", "claude-releases");

    private readonly TestPgp _key = new();

    public void Dispose() => _key.Dispose();

    private static byte[] RealManifest => File.ReadAllBytes(Path.Combine(Fixtures, "manifest-2.1.319.json"));

    private static string RealSignature => File.ReadAllText(Path.Combine(Fixtures, "manifest-2.1.319.json.sig"));

    [Fact]
    public void The_pinned_key_is_anthropics_release_key()
    {
        // Act
        var key = ClaudeCodeDistribution.ReleaseKey;

        // Assert
        key.Fingerprint.Should().Be("31DDDE24DDFAB679F42D7BD2BAA929FF1A7ECACE");
    }

    [Fact]
    public void A_real_claude_code_manifest_checks_out_with_the_pinned_key()
    {
        // Act
        var verification = OpenPgpSignature.Verify(RealManifest, RealSignature, ClaudeCodeDistribution.ReleaseKey);

        // Assert
        verification.Should().Be(new OpenPgpVerification(OpenPgpVerdict.Valid));
    }

    [Fact]
    public void A_changed_byte_in_the_manifest_fails_the_check()
    {
        // Arrange
        var manifest = RealManifest;
        var at = Encoding.UTF8.GetString(manifest).IndexOf("1f53587e", StringComparison.Ordinal);
        manifest[at] = (byte)'0';

        // Act
        var verification = OpenPgpSignature.Verify(manifest, RealSignature, ClaudeCodeDistribution.ReleaseKey);

        // Assert
        verification.Verdict.Should().Be(OpenPgpVerdict.Invalid);
    }

    [Fact]
    public void A_signature_by_another_key_is_refused()
    {
        // Arrange
        var signature = _key.Sign(RealManifest);

        // Act
        var verification = OpenPgpSignature.Verify(RealManifest, signature, ClaudeCodeDistribution.ReleaseKey);

        // Assert
        verification.Verdict.Should().Be(OpenPgpVerdict.WrongKey);
    }

    [Fact]
    public void A_signature_claiming_the_pinned_key_but_made_by_another_fails()
    {
        // Arrange
        var signature = _key.Sign(RealManifest, issuerFingerprint: ClaudeCodeDistribution.ReleaseKeyFingerprint);

        // Act
        var verification = OpenPgpSignature.Verify(RealManifest, signature, ClaudeCodeDistribution.ReleaseKey);

        // Assert
        verification.Verdict.Should().Be(OpenPgpVerdict.Invalid);
    }

    [Fact]
    public void A_key_read_from_its_armor_checks_its_own_signatures()
    {
        // Arrange
        var data = Encoding.UTF8.GetBytes("{\"version\": \"9.9.9\"}");

        // Act
        var verification = OpenPgpSignature.Verify(data, _key.Sign(data), OpenPgpPublicKey.Read(_key.ArmoredPublicKey));

        // Assert
        verification.IsValid.Should().BeTrue();
    }

    [Fact]
    public void A_text_signature_is_not_checked()
    {
        // Arrange
        var data = Encoding.UTF8.GetBytes("text");

        // Act
        var verification = OpenPgpSignature.Verify(data, _key.Sign(data, signatureType: 0x01), _key.PublicKey);

        // Assert
        verification.Verdict.Should().Be(OpenPgpVerdict.Unsupported);
    }

    [Fact]
    public void An_unknown_critical_subpacket_refuses_the_signature()
    {
        // Arrange
        var data = Encoding.UTF8.GetBytes("data");

        // Act
        var verification = OpenPgpSignature.Verify(data, _key.Sign(data, extraHashedSubpacket: [0x80 | 100, 1, 2]), _key.PublicKey);

        // Assert
        verification.Verdict.Should().Be(OpenPgpVerdict.Unsupported);
    }

    [Fact]
    public void An_expired_signature_fails()
    {
        // Arrange
        var data = Encoding.UTF8.GetBytes("data");
        var signature = _key.Sign(data, expiresAfter: 60);

        // Act
        var verification = OpenPgpSignature.Verify(data, signature, _key.PublicKey, now: new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));

        // Assert
        verification.Verdict.Should().Be(OpenPgpVerdict.Invalid);
    }

    [Fact]
    public void A_damaged_armor_checksum_is_malformed()
    {
        // Arrange
        var data = Encoding.UTF8.GetBytes("data");
        var lines = _key.Sign(data).Split('\n');
        var crc = Array.FindIndex(lines, l => l.StartsWith('='));
        lines[crc] = lines[crc] == "=AAAA" ? "=BBBB" : "=AAAA";

        // Act
        var verification = OpenPgpSignature.Verify(data, string.Join('\n', lines), _key.PublicKey);

        // Assert
        verification.Verdict.Should().Be(OpenPgpVerdict.Malformed);
    }

    [Fact]
    public void Text_that_is_no_signature_is_malformed()
    {
        // Act
        var verification = OpenPgpSignature.Verify("data"u8, "<html>Not found</html>", _key.PublicKey);

        // Assert
        verification.Verdict.Should().Be(OpenPgpVerdict.Malformed);
    }

    [Fact]
    public void A_key_block_without_a_key_is_refused()
    {
        // Act
        var act = () => OpenPgpPublicKey.Read("-----BEGIN PGP PUBLIC KEY BLOCK-----\n\n-----END PGP PUBLIC KEY BLOCK-----\n");

        // Assert
        act.Should().Throw<OpenPgpException>();
    }
}
