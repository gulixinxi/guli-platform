using System.Net;
using System.Text;

namespace GuliERP.Crm.Infrastructure.Services;

/// <summary>Retries only credential or quota rejection, never an ambiguous request failure.</summary>
internal static class QuakeRequestSender
{
    internal static async Task<string> SendAsync(HttpClient http, string url, string jsonBody,
        IReadOnlyList<QuakeRuntimeCredentialStore.QuakeKey> candidates, CancellationToken ct,
        Func<string, Task>? observeAttempt = null)
    {
        if (candidates.Count == 0) throw new InvalidOperationException("Quake API key is not configured.");
        for (var i = 0; i < candidates.Count; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
            };
            request.Headers.Add("X-QuakeToken", candidates[i].Value);
            HttpResponseMessage response;
            try { response = await http.SendAsync(request, ct); }
            catch (Exception)
            {
                if (observeAttempt is not null) await observeAttempt("uncertain");
                throw;
            }
            using (response)
            {
            if (observeAttempt is not null) await observeAttempt($"http_{(int)response.StatusCode}");
            if (response.IsSuccessStatusCode)
                return await response.Content.ReadAsStringAsync(ct);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                or HttpStatusCode.TooManyRequests)
            {
                if (i + 1 < candidates.Count) continue;
                throw new InvalidOperationException("All Quake keys were rejected or rate limited.");
            }
            throw new InvalidOperationException($"Quake API returned HTTP {(int)response.StatusCode}.");
            }
        }
        throw new InvalidOperationException("No usable Quake key remains.");
    }
}
