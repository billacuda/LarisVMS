using LarisVMS.Web.Areas.Identity.Pages.Account;

namespace LarisVMS.Tests;

/// <summary>
/// Only DescribeRejection is unit-testable in isolation — everything else in
/// ExternalLoginModel.OnGetCallbackAsync is a direct SignInManager/UserManager call chain (external
/// login info, sign-in, AddLogin), which needs a real Identity store to meaningfully exercise; this
/// app has no precedent for faking those out for a PageModel test elsewhere either. This locks down the
/// one piece of that flow that's pure: what an admin/end user actually sees when Entra reports no
/// matching local account.
/// </summary>
public class ExternalLoginTests
{
    [Fact]
    public void ARecognizedEmailWithNoLocalAccountNamesTheEmailAndPointsAtAnAdmin()
    {
        var message = ExternalLoginModel.DescribeRejection("someone@example.com");

        Assert.Contains("someone@example.com", message);
        Assert.Contains("administrator", message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NoEmailClaimAtAllGetsItsOwnMessageRatherThanNamingABlankEmail(string? email)
    {
        var message = ExternalLoginModel.DescribeRejection(email);

        Assert.DoesNotContain("\"\"", message);
        Assert.Contains("email address", message, StringComparison.OrdinalIgnoreCase);
    }
}
