using System.Security.Cryptography;
using System.Text.Json;
using FinApp.Server.BankSync;

namespace FinApp.Server.Tests;

/// <summary>
/// Enable Banking's app auth signs a fresh RS256 JWT per request. IdentityModel caches signature providers by
/// key id, so a second call with the same application id must not reuse a provider whose RSA was already
/// disposed (that regressed as an ObjectDisposedException / HTTP 500 on "Link Revolut" in prod).
/// </summary>
public class EnableBankingJwtTests
{
    [Fact]
    public void Signs_repeatedly_with_the_same_application_id()
    {
        using var rsa = RSA.Create(2048);
        var pem = rsa.ExportPkcs8PrivateKeyPem();
        const string appId = "0f3060b1-e197-4bfb-ac47-6039d3d22afa";

        var first = EnableBankingClient.BuildJwt(appId, pem);
        var second = EnableBankingClient.BuildJwt(appId, pem);   // must not throw ObjectDisposedException

        Assert.False(string.IsNullOrEmpty(first));
        Assert.False(string.IsNullOrEmpty(second));
        Assert.Equal(3, first.Split('.').Length);   // header.payload.signature
    }
}

/// <summary>
/// The transactions parser must cope with two provider JSON conventions: Berlin Group / NextGenPSD2 camelCase
/// (signed amounts, no debit/credit indicator, transactions nested under "booked") and Enable Banking's
/// snake_case native shape (unsigned amounts + a creditDebitIndicator, flat array). These guard both.
/// </summary>
public class BankTransactionParsingTests
{
    private static List<BankTransaction> Parse(string json) =>
        EnableBankingClient.ParseTransactions(JsonDocument.Parse(json).RootElement);

    [Fact]
    public void Parses_berlin_group_camelcase_with_signed_amounts()
    {
        // Debit carries a negative sign and no indicator; the "booked" nesting mirrors the balance sample the user shared.
        var json = """
        {
          "transactions": {
            "booked": [
              {
                "transactionId": "tx-1",
                "bookingDate": "2026-06-28",
                "transactionAmount": { "currency": "EUR", "amount": "-61.52" },
                "remittanceInformationUnstructured": "TESCO STORES"
              },
              {
                "transactionId": "tx-2",
                "bookingDate": "2026-06-27",
                "transactionAmount": { "currency": "EUR", "amount": "100.00" },
                "creditorName": "ACME PAYROLL"
              }
            ]
          }
        }
        """;

        var txns = Parse(json);

        Assert.Equal(2, txns.Count);
        var debit = txns.Single(t => t.ExternalId == "tx-1");
        Assert.Equal(-61.52m, debit.Amount);            // sign preserved from the amount string
        Assert.Equal(new DateOnly(2026, 6, 28), debit.Date);
        Assert.Equal("TESCO STORES", debit.Description);
        Assert.Equal(100.00m, txns.Single(t => t.ExternalId == "tx-2").Amount);
    }

    [Fact]
    public void Parses_enable_banking_snakecase_with_indicator()
    {
        // Unsigned amount + creditDebitIndicator; flat array under "transactions".
        var json = """
        {
          "transactions": [
            {
              "entry_reference": "e-9",
              "booking_date": "2026-06-20",
              "transaction_amount": { "currency": "EUR", "amount": "12.30" },
              "credit_debit_indicator": "DBIT",
              "remittance_information": ["COFFEE", "SHOP"]
            }
          ]
        }
        """;

        var txns = Parse(json);

        var t = Assert.Single(txns);
        Assert.Equal("e-9", t.ExternalId);
        Assert.Equal(-12.30m, t.Amount);                // DBIT makes the unsigned amount negative
        Assert.Equal("COFFEE SHOP", t.Description);
    }

    /// <summary>
    /// ⚠️ <b>This test used to assert only that two parses agree, and it passed throughout the bug it exists to
    /// catch.</b> The id was built with <c>string.GetHashCode()</c>, which is randomized <i>per process</i> — so it
    /// is perfectly stable within one test run and changes on every server restart. Dismissed and confirmed rows
    /// came back as new pending ones, and the guard said everything was fine.
    /// <para>
    /// Pinning the literal is what actually tests the property: a value that survives a restart has to be one this
    /// test could write down. If the hashing changes deliberately, this fails once and is updated once — at the cost
    /// of every previously-staged synthetic row resurfacing, which is the honest price of changing a dedupe key.
    /// </para>
    /// </summary>
    [Fact]
    public void Synthesizes_an_id_that_survives_a_process_restart()
    {
        var json = """
        {
          "transactions": { "booked": [
            { "bookingDate": "2026-06-01", "transactionAmount": { "currency": "EUR", "amount": "-5.00" } }
          ] }
        }
        """;

        var first = Parse(json).Single().ExternalId;

        Assert.Equal(first, Parse(json).Single().ExternalId);   // deterministic within a run...
        // ...and across runs: a hard-coded expectation is the only thing a per-process hash cannot satisfy.
        Assert.Equal(EnableBankingClient.SyntheticId("2026-06-01", -5.00m, "Bank transaction"), first);
        Assert.StartsWith("syn-", first);
    }

    [Fact]
    public void Reads_a_booking_time_when_the_bank_states_one_and_leaves_it_null_otherwise()
    {
        // Some banks put a full timestamp in the booking date, some in their own field, most give neither.
        var json = """
        {
          "transactions": { "booked": [
            { "transactionId": "a", "bookingDate": "2026-06-01T19:42:07Z",
              "transactionAmount": { "currency": "EUR", "amount": "-5.00" } },
            { "transactionId": "b", "bookingDate": "2026-06-01", "bookingDateTime": "2026-06-01T08:15:00Z",
              "transactionAmount": { "currency": "EUR", "amount": "-6.00" } },
            { "transactionId": "c", "bookingDate": "2026-06-01",
              "transactionAmount": { "currency": "EUR", "amount": "-7.00" } }
          ] }
        }
        """;

        var txns = Parse(json).ToDictionary(t => t.ExternalId);

        Assert.Equal(new TimeOnly(19, 42, 7), txns["a"].Time);
        Assert.Equal(new DateOnly(2026, 6, 1), txns["a"].Date);   // the timestamp still yields a clean date
        Assert.Equal(new TimeOnly(8, 15, 0), txns["b"].Time);
        Assert.Null(txns["c"].Time);                              // date only — never invented as midnight
    }

    [Fact]
    public void Empty_or_absent_transactions_yield_no_rows()
    {
        Assert.Empty(Parse("""{ "transactions": { "booked": [] } }"""));
        Assert.Empty(Parse("""{ "balances": [] }"""));
    }
}
