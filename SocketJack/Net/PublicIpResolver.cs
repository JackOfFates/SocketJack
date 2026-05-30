using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace SocketJack.Net
{
    public static class PublicIpResolver
    {
        private static readonly Uri[] PublicIpEndpoints =
        {
            new Uri("https://api.ipify.org/"),
            new Uri("https://checkip.amazonaws.com/"),
            new Uri("https://icanhazip.com/")
        };

        public static async Task<IPAddress> GetExternalIpAddressAsync(CancellationToken cancellationToken = default)
        {
            Exception lastError = null;
            using (var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(6) })
            {
                foreach (Uri endpoint in PublicIpEndpoints)
                {
                    try
                    {
                        using (HttpResponseMessage response = await client.GetAsync(endpoint, cancellationToken).ConfigureAwait(false))
                        {
                            response.EnsureSuccessStatusCode();
                            string text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                            string candidate = (text ?? "").Trim();
                            if (IPAddress.TryParse(candidate, out IPAddress address) &&
                                address != null &&
                                !IPAddress.IsLoopback(address))
                                return address;
                        }
                    }
                    catch (Exception ex) when (!(ex is OperationCanceledException))
                    {
                        lastError = ex;
                    }
                }
            }

            throw new InvalidOperationException("Unable to resolve external IP address.", lastError);
        }

        public static async Task<string> GetExternalIpStringAsync(CancellationToken cancellationToken = default)
        {
            IPAddress address = await GetExternalIpAddressAsync(cancellationToken).ConfigureAwait(false);
            return address.ToString();
        }
    }
}
