using LarisVMS.Core;

namespace LarisVMS.Tests;

/// <summary>Covers the shared old→new diff string every Update-style audit entry uses for its
/// Details field — including the guarantee that a secret field's value never reaches the log, which
/// matters because the audit log itself is readable by anyone with Logs.View.</summary>
public class AuditDiffTests
{
    [Fact]
    public void ReturnsNullWhenNothingChanged()
    {
        var details = AuditDiff.Build(
            AuditDiff.Of("Name", "Front Door", "Front Door"),
            AuditDiff.Of("Enabled", "True", "True"));

        Assert.Null(details);
    }

    [Fact]
    public void ReturnsNullForNoFieldsAtAll()
    {
        Assert.Null(AuditDiff.Build());
    }

    [Fact]
    public void ReportsASingleChangedField()
    {
        var details = AuditDiff.Build(AuditDiff.Of("Name", "Front Door", "Front Porch"));

        Assert.Equal("Name: Front Door → Front Porch", details);
    }

    [Fact]
    public void ListsEveryChangedFieldAndSkipsUnchangedOnes()
    {
        var details = AuditDiff.Build(
            AuditDiff.Of("Name", "Front Door", "Front Porch"),
            AuditDiff.Of("Node", "NVR1", "NVR1"),
            AuditDiff.Of("Enabled", "True", "False"));

        Assert.Equal("Name: Front Door → Front Porch; Enabled: True → False", details);
    }

    [Fact]
    public void RendersAnEmptyValueAsNoneRatherThanBlank()
    {
        var details = AuditDiff.Build(AuditDiff.Of("Group", null, "Parking"));

        Assert.Equal("Group: (none) → Parking", details);
    }

    [Fact]
    public void TreatsNullAndEmptyAsTheSameValue()
    {
        // A nullable column cleared to null and a text box submitted blank are the same edit — one
        // shouldn't log a phantom change the other doesn't.
        Assert.Null(AuditDiff.Build(AuditDiff.Of("Notes", null, "")));
        Assert.Null(AuditDiff.Build(AuditDiff.Of("Notes", "", null)));
    }

    [Fact]
    public void ASecretFieldReportsThatItChangedWithoutEitherValue()
    {
        const string oldSecret = "hunter2-old-password";
        const string newSecret = "correct-horse-battery-staple";

        var details = AuditDiff.Build(AuditDiff.Secret("Password", oldSecret, newSecret));

        Assert.Equal("Password: changed", details);
        // Asserted against the literal secrets, not just the format — the whole point of the secret
        // flag is that these strings can never reach the log, so check for their actual absence.
        Assert.DoesNotContain(oldSecret, details);
        Assert.DoesNotContain(newSecret, details);
    }

    [Fact]
    public void AnUnchangedSecretFieldIsNotMentionedAtAll()
    {
        var details = AuditDiff.Build(AuditDiff.Secret("Password", "same-secret", "same-secret"));

        Assert.Null(details);
    }

    [Fact]
    public void SecretChangedReportsTheFieldOnlyWhenItActuallyChanged()
    {
        Assert.Equal("Password: changed", AuditDiff.Build(AuditDiff.SecretChanged("Password", true)));
        Assert.Null(AuditDiff.Build(AuditDiff.SecretChanged("Password", false)));
    }

    [Fact]
    public void MixesSecretAndNonSecretFieldsInOneEntry()
    {
        const string secret = "super-secret-value";

        var details = AuditDiff.Build(
            AuditDiff.Of("Name", "Front Door", "Front Porch"),
            AuditDiff.Secret("Password", "old", secret));

        Assert.Equal("Name: Front Door → Front Porch; Password: changed", details);
        Assert.DoesNotContain(secret, details);
    }
}
