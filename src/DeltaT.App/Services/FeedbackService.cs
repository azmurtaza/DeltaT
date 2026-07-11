using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using DeltaT.Core.Machine;

namespace DeltaT.App.Services;

/// <summary>Sends a bug report or idea to the maintainer's private feedback backend
/// (a Supabase Edge Function). The report carries what's needed to triage a thermal
/// issue — app version, OS, machine model, CPU, whether it ran elevated — plus the
/// user's typed message and an optional contact they choose to add. Nothing else about
/// the machine leaves; there is no telemetry beyond what a person deliberately submits.
///
/// The publishable key below is safe to ship in the open: the feedback table has RLS
/// enabled with no anon policies, so this key cannot read or write it. Only the Edge
/// Function (running as the service role) can insert a row, and reading reports back is
/// gated behind a separate admin token the app never sees. So a leaked key buys nothing
/// but the ability to POST feedback — which is the whole point of the endpoint.</summary>
public sealed class FeedbackService
{
    private const string Endpoint = "https://gslnypazsjkbrvjjycfr.supabase.co/functions/v1/feedback";
    private const string PublishableKey = "sb_publishable_5uxzDFvPbAK1m_uxtKtQzw_0mjOFNxE";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private readonly MachineIdentity _machine;
    private readonly bool _elevated;

    public FeedbackService(MachineIdentity machine, bool elevated)
    {
        _machine = machine;
        _elevated = elevated;
    }

    /// <param name="kind">"bug" or "idea".</param>
    /// <returns>(true, null) on success, else (false, a short reason to show the user).</returns>
    public async Task<(bool Ok, string? Error)> SendAsync(string kind, string message, string? contact, CancellationToken ct = default)
    {
        var payload = new
        {
            kind,
            message,
            contact = string.IsNullOrWhiteSpace(contact) ? null : contact.Trim(),
            app_version = UpdateService.CurrentVersionLabel,
            os = RuntimeInformation.OSDescription,
            machine = _machine.Display,
            cpu = _machine.CpuName,
            elevated = _elevated,
        };

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint);
            req.Headers.TryAddWithoutValidation("apikey", PublishableKey);
            req.Headers.TryAddWithoutValidation("x-deltat-app", "1");
            req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            using HttpResponseMessage resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode
                ? (true, null)
                : (false, $"server said {(int)resp.StatusCode}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
