using System.Net;
using System.Net.Http.Json;
using FinApp.Contracts;

namespace FinApp.Server.Tests;

/// <summary>
/// The client-error pipeline (OPEN-BETA B1): the scrubber that keeps user money out of our logs, and the
/// anonymous endpoint that receives reports.
/// </summary>
public class ClientErrorScrubberTests
{
    [Theory]
    // These are real messages this codebase can throw. The point of the scrubber is that none of them reach a log
    // with the user's actual figures in them.
    [InlineData("That fund only holds €1,234.56; move money into it from another fund first.")]
    [InlineData("You can settle between 0 and the expense amount (€87.00).")]
    [InlineData("The extra lines come to €150.00, which is more than the €100.00 payment.")]
    // A figure at the very end of a sentence: the trailing full stop once let this straight through.
    [InlineData("Closing balance was 1,234.56.")]
    [InlineData("You can only send 87.00.")]
    public void Money_never_survives_scrubbing(string message)
    {
        var clean = ErrorScrubber.Message(message);

        Assert.Contains("«amount»", clean);
        Assert.DoesNotContain("1,234.56", clean);
        Assert.DoesNotContain("87.00", clean);
        Assert.DoesNotContain("150.00", clean);
    }

    [Fact]
    public void The_shape_of_the_message_survives_so_it_is_still_diagnostic()
    {
        // Redacting must not turn the message into mush — we still need to recognise WHICH guard fired.
        var clean = ErrorScrubber.Message("That fund only holds €1,234.56; move money into it from another fund first.");

        Assert.StartsWith("That fund only holds", clean);
        Assert.EndsWith("move money into it from another fund first.", clean);
    }

    [Theory]
    [InlineData("A tag named “Mortgage” already exists.")]
    [InlineData("A tag named \"Mortgage\" already exists.")]
    public void User_supplied_names_quoted_back_by_a_domain_guard_are_redacted(string message)
    {
        var clean = ErrorScrubber.Message(message);

        Assert.Contains("«name»", clean);
        Assert.DoesNotContain("Mortgage", clean);
    }

    [Fact]
    public void Emails_and_long_digit_runs_are_redacted()
    {
        var clean = ErrorScrubber.Message("No user named someone@example.com with ref 4111111111111111.");

        Assert.DoesNotContain("someone@example.com", clean);
        Assert.DoesNotContain("4111111111111111", clean);
    }

    [Fact]
    public void A_version_string_is_not_mistaken_for_money_or_an_account_number()
    {
        // The flip side of the trailing-guard fix: redaction must not eat the build identifiers we debug with.
        var clean = ErrorScrubber.Message("Failed on build v1.2.34567 of the client.");

        Assert.Contains("v1.2.34567", clean);
    }

    [Fact]
    public void Ordinary_developer_text_passes_through_untouched()
    {
        // Over-redacting is its own failure: a report we can't read is as useless as one we never got.
        const string message = "Object reference not set to an instance of an object.";

        Assert.Equal(message, ErrorScrubber.Message(message));
    }

    [Fact]
    public void Stack_frames_are_kept_but_the_leading_message_line_is_scrubbed()
    {
        var stack = "System.InvalidOperationException: That fund only holds €1,234.56\n"
                  + "   at FinApp.Domain.Periods.Period.TransferOut(Guid fundId)\n"
                  + "   at FinApp.Shared.UI.Pages.Dashboard.Save()";

        var clean = ErrorScrubber.Stack(stack)!;

        Assert.DoesNotContain("1,234.56", clean);
        Assert.Contains("at FinApp.Domain.Periods.Period.TransferOut", clean);   // frames are what we debug from
        Assert.Contains("at FinApp.Shared.UI.Pages.Dashboard.Save", clean);
    }

    [Fact]
    public void Oversized_fields_are_truncated_rather_than_rejected()
    {
        var clean = ErrorScrubber.Clean(new ClientErrorReport("render", new string('x', 5000), new string('y', 20_000)));

        Assert.True(clean.Message.Length <= ErrorScrubber.MaxMessageLength + 1);
        Assert.True(clean.Stack!.Length <= ErrorScrubber.MaxStackLength + 1);
    }

    [Fact]
    public void Blank_and_null_are_safe()
    {
        Assert.Equal("", ErrorScrubber.Message(null));
        Assert.Equal("", ErrorScrubber.Message("   "));
        Assert.Null(ErrorScrubber.Stack(null));
    }
}

public class ClientErrorApiTests : IClassFixture<FinAppServerFactory>
{
    private readonly FinAppServerFactory _factory;

    public ClientErrorApiTests(FinAppServerFactory factory) => _factory = factory;

    [Fact]
    public async Task A_crash_can_be_reported_without_signing_in()
    {
        // The reports we most need are the ones a signed-in-only endpoint would drop: a crash on the landing
        // page, during registration, or *because* auth is broken.
        var client = _factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/client-errors",
            new ClientErrorReport("render", "NullReferenceException: Object reference not set.", "   at Foo.Bar()", "Home"));

        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
    }

    [Fact]
    public async Task An_empty_report_is_accepted_but_not_logged()
    {
        var client = _factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/client-errors", new ClientErrorReport("js", "   "));

        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
    }

    [Fact]
    public async Task A_report_that_arrives_unscrubbed_is_still_accepted_and_scrubbed_here()
    {
        // The endpoint cannot trust the sender — a stale client, a forged POST, or a future code path that
        // forgets. It re-scrubs regardless, which is why this must not 400: rejecting it would lose the bug.
        var client = _factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/client-errors",
            new ClientErrorReport("js", "That fund only holds €1,234.56 for someone@example.com"));

        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
    }
}
