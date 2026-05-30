using System;

namespace SocketJack.Net {
    public sealed class LmVsProxyRemoteModelServerSelection {
        public bool Enabled { get; set; }
        public string ServerId { get; set; } = "";
        public string ServerName { get; set; } = "";
        public string OwnerUserName { get; set; } = "";
        public string ExternalIp { get; set; } = "";
        public string HardwareSummary { get; set; } = "";
        public string Endpoint { get; set; } = "";
        public string OpenAiBaseUrl { get; set; } = "";
        public string SelectedModel { get; set; } = "";
        public string AuthorizationBearerToken { get; set; } = "";
        public string SourceJson { get; set; } = "";
        public string SelectedUtc { get; set; } = "";
        public string LeaseId { get; set; } = "";
        public bool LeaseRequired { get; set; }
        public string LeaseStatus { get; set; } = "";
        public string LeasePaymentStatus { get; set; } = "";
        public string LeaseStartedUtc { get; set; } = "";
        public string LeaseRenewedUtc { get; set; } = "";
        public string LeaseExpiresUtc { get; set; } = "";
        public string LeaseSource { get; set; } = "";
        public string LeaseMode { get; set; } = "";

        public LmVsProxyRemoteModelServerSelection Clone() {
            return new LmVsProxyRemoteModelServerSelection {
                Enabled = Enabled,
                ServerId = ServerId ?? "",
                ServerName = ServerName ?? "",
                OwnerUserName = OwnerUserName ?? "",
                ExternalIp = ExternalIp ?? "",
                HardwareSummary = HardwareSummary ?? "",
                Endpoint = Endpoint ?? "",
                OpenAiBaseUrl = OpenAiBaseUrl ?? "",
                SelectedModel = SelectedModel ?? "",
                AuthorizationBearerToken = AuthorizationBearerToken ?? "",
                SourceJson = SourceJson ?? "",
                SelectedUtc = SelectedUtc ?? "",
                LeaseId = LeaseId ?? "",
                LeaseRequired = LeaseRequired,
                LeaseStatus = LeaseStatus ?? "",
                LeasePaymentStatus = LeasePaymentStatus ?? "",
                LeaseStartedUtc = LeaseStartedUtc ?? "",
                LeaseRenewedUtc = LeaseRenewedUtc ?? "",
                LeaseExpiresUtc = LeaseExpiresUtc ?? "",
                LeaseSource = LeaseSource ?? "",
                LeaseMode = LeaseMode ?? ""
            };
        }

        public void Normalize() {
            ServerId = NormalizeText(ServerId);
            ServerName = NormalizeText(ServerName);
            OwnerUserName = NormalizeText(OwnerUserName);
            ExternalIp = NormalizeText(ExternalIp);
            HardwareSummary = NormalizeText(HardwareSummary);
            Endpoint = NormalizeText(Endpoint);
            OpenAiBaseUrl = NormalizeOpenAiBaseUrl(OpenAiBaseUrl);
            SelectedModel = NormalizeText(SelectedModel);
            AuthorizationBearerToken = NormalizeText(AuthorizationBearerToken);
            SourceJson = NormalizeText(SourceJson);
            if (string.IsNullOrWhiteSpace(SelectedUtc))
                SelectedUtc = DateTimeOffset.UtcNow.ToString("O");
            LeaseId = NormalizeText(LeaseId);
            LeaseStatus = NormalizeLeaseStatus(LeaseStatus, LeaseRequired);
            LeasePaymentStatus = NormalizeText(LeasePaymentStatus);
            LeaseStartedUtc = NormalizeText(LeaseStartedUtc);
            LeaseRenewedUtc = NormalizeText(LeaseRenewedUtc);
            LeaseExpiresUtc = NormalizeText(LeaseExpiresUtc);
            LeaseSource = NormalizeText(LeaseSource);
            LeaseMode = NormalizeText(LeaseMode);
            if (string.IsNullOrWhiteSpace(LeaseMode) && Enabled)
                LeaseMode = "local-model-illusion";
            Enabled = Enabled && !string.IsNullOrWhiteSpace(OpenAiBaseUrl);
        }

        private static string NormalizeText(string value) {
            return (value ?? "").Trim();
        }

        private static string NormalizeLeaseStatus(string status, bool leaseRequired) {
            status = NormalizeText(status).ToLowerInvariant().Replace(' ', '_').Replace('-', '_');
            if (string.IsNullOrWhiteSpace(status))
                return leaseRequired ? "pending_payment" : "active";
            return status;
        }

        public static string NormalizeOpenAiBaseUrl(string value) {
            value = NormalizeText(value);
            if (value.Length == 0)
                return "";

            if (!Uri.TryCreate(value, UriKind.Absolute, out Uri uri) ||
                string.IsNullOrWhiteSpace(uri.Scheme) ||
                string.IsNullOrWhiteSpace(uri.Host) ||
                !(uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) ||
                  uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)))
                return "";

            string path = uri.AbsolutePath ?? "";
            string loweredPath = path.ToLowerInvariant();
            int v1Index = loweredPath.IndexOf("/v1/", StringComparison.Ordinal);
            if (v1Index < 0 && loweredPath.EndsWith("/v1", StringComparison.Ordinal))
                v1Index = loweredPath.Length - 3;

            if (v1Index >= 0)
                path = path.Substring(0, v1Index);

            path = path.TrimEnd('/');
            string authority = uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
            return authority + path;
        }
    }
}
