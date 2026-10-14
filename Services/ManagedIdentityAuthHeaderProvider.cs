using System.Net.Http.Headers;
using Azure.Core;

namespace ProjectPulse.Processor.Services;

public sealed class ManagedIdentityAuthHeaderProvider(TokenCredential credential) : IAuthHeaderProvider
{
    public async Task ApplyBearerTokenAsync(HttpRequestMessage request, string scope, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            return;
        }

        var token = await credential.GetTokenAsync(
            new TokenRequestContext([scope]),
            cancellationToken);

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
    }
}
