using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace SocketJack.Extensions {
    public static class HttpClientExtensionsModule {

        public async static Task DownloadAsync(this HttpClient client, string requestUri, Stream destination, IProgress<float> progress = null) {
            // Get the http headers first to examine the content length
            using (var response = await client.GetAsync(requestUri, HttpCompletionOption.ResponseHeadersRead)) {
                var contentLength = response.Content.Headers.ContentLength;

                using (var download = await response.Content.ReadAsStreamAsync()) {
                    // Ignore progress reporting when the content length is unknown
                    if (!contentLength.HasValue) {
                        await download.CopyToAsync(destination);
                        return;
                    }

                    // Convert absolute progress (bytes downloaded) into relative progress (0% - 100%)
                    var relativeProgress = new Progress<long>(totalBytes => progress.Report(totalBytes / (float)contentLength.Value));
                    // Use extension method to report progress while downloading
                    await download.CopyToAsync(destination, 81920, relativeProgress);
                    progress.Report(1f);
                }
            }
        }

        public async static Task DownloadAsync(this HttpClient client, string requestUri, Stream destination, CancellationToken cancellationToken, IProgress<float> progress = null) {
            // Get the http headers first to examine the content length
            using (var response = await client.GetAsync(requestUri, HttpCompletionOption.ResponseHeadersRead)) {
                var contentLength = response.Content.Headers.ContentLength;

                using (var download = await response.Content.ReadAsStreamAsync()) {
                    // Ignore progress reporting when the content length is unknown
                    if (!contentLength.HasValue) {
                        await download.CopyToAsync(destination);
                        return;
                    }

                    // Convert absolute progress (bytes downloaded) into relative progress (0% - 100%)
                    var relativeProgress = new Progress<long>(totalBytes => progress.Report(totalBytes / (float)contentLength.Value));
                    // Use extension method to report progress while downloading
                    await download.CopyToAsync(destination, 81920, cancellationToken, relativeProgress);
                    progress.Report(1f);
                }
            }
        }
    }

    public class HttpClientExtensions {

        public static async Task<IProgress<float>> DownloadFile(string DownloadURL, string filePath) {
            var handler = new HttpClientHandler() { AllowAutoRedirect = true, UseCookies = true };

            var P = new Progress<float>();
            using (var client = new HttpClient(handler)) {
                SetHeaders(client);
                client.Timeout = TimeSpan.FromMinutes(30L);

                // Create a file stream to store the downloaded data.
                // This really can be any type of writeable stream.
                using (var @file = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None)) {

                    // Use the custom extension method below to download the data.
                    // The passed progress-instance will receive the download status updates.
                    await client.DownloadAsync(DownloadURL, @file, P);
                }
            }

            return P;
        }

        public static string DownloadString(string URL) {
            using (var client = new HttpClient()) {
                SetHeaders(client);
                client.Timeout = TimeSpan.FromMinutes(0.25d);

                try {
                    return client.GetStringAsync(URL).Result;
                } catch (Exception ex) {
                    return string.Empty;
                }

            }
            return null;
        }

        public static async Task<string> DownloadStringAsync(string URL) {
            using (var client = new HttpClient()) {
                SetHeaders(client);
                client.Timeout = TimeSpan.FromMinutes(0.25d);

                try {
                    return await client.GetStringAsync(URL);
                } catch (Exception ex) {
                    return string.Empty;
                }

            }
            return null;
        }

        private static void SetHeaders(HttpClient httpClient) {
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36");
        }
    }
}