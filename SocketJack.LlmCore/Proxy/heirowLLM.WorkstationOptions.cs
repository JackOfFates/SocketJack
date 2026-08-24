using System;
using System.Threading;
using System.Threading.Tasks;

namespace SocketJack.Net
{
    public partial class HeirowLlm
    {
        public Func<CancellationToken, Task<string>> WorkstationOptionsCatalogProviderAsync { get; set; }
        public Func<string, CancellationToken, Task<string>> WorkstationOptionsCatalogUpdaterAsync { get; set; }

        private void RegisterWorkstationOptionsRoutes(HttpServer server)
        {
            server.Map("GET", "/api/workstation-options", (connection, request, cancellationToken) =>
                HandleWorkstationOptionsCatalogRequest(connection, request, cancellationToken, save: false));
            server.Map("POST", "/api/workstation-options", (connection, request, cancellationToken) =>
                HandleWorkstationOptionsCatalogRequest(connection, request, cancellationToken, save: true));
        }

        private object HandleWorkstationOptionsCatalogRequest(NetworkConnection connection, HttpRequest request, CancellationToken cancellationToken, bool save)
        {
            if (!TryAuthorizeDatabaseAdministrator(connection, request, out _, out string authorizationError))
                return BuildDatabaseAdminOnlyJsonError(request, authorizationError);

            try
            {
                string json;
                if (save)
                {
                    if (WorkstationOptionsCatalogUpdaterAsync == null)
                        return BuildJsonError(request, 503, "Service Unavailable", "The Workstation options editor is not connected to the desktop host.");
                    json = WorkstationOptionsCatalogUpdaterAsync(request.Body ?? "{}", cancellationToken).GetAwaiter().GetResult();
                }
                else
                {
                    if (WorkstationOptionsCatalogProviderAsync == null)
                        return BuildJsonError(request, 503, "Service Unavailable", "The Workstation options catalog is not connected to the desktop host.");
                    json = WorkstationOptionsCatalogProviderAsync(cancellationToken).GetAwaiter().GetResult();
                }

                request.Context.Response.ContentType = "application/json; charset=utf-8";
                return string.IsNullOrWhiteSpace(json) ? "{\"ok\":true,\"options\":[]}" : json;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return BuildJsonError(request, 500, "Internal Server Error", "Workstation options failed: " + ex.Message);
            }
        }
    }
}
