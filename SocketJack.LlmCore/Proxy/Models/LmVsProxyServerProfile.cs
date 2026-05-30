using System;

namespace SocketJack.Net {
    public sealed class LmVsProxyServerProfile {
        public string ServerId { get; set; } = "";
        public string OwnerUserName { get; set; } = "";
        public string ServerName { get; set; } = "";
        public string PublicHost { get; set; } = "";
        public double CostFactor { get; set; } = 1;
        public string GpuName { get; set; } = "";
        public string GpuAvailable { get; set; } = "";
        public string CpuName { get; set; } = "";
        public string CpuAvailable { get; set; } = "";
        public string RamAvailable { get; set; } = "";
        public string StorageAvailable { get; set; } = "";
        public long StorageLimitMegabytes { get; set; } = 500;
        public string AvailableResources { get; set; } = "";
        public string AvailableModels { get; set; } = "";
        public string ToolsAllowed { get; set; } = "";
        public string AvailableVram { get; set; } = "";
        public long MaxTokens { get; set; }
        public long AdvertisedTokenRate { get; set; }
        public string ModelCapabilitiesJson { get; set; } = "";
        public string ModelBenchmarksJson { get; set; } = "";
        public bool RequiresPayment { get; set; }
        public string StripePriceId { get; set; } = "";
        public string StripeAccount { get; set; } = "";
        public long StripeUnitAmountCents { get; set; }
        public string StripeCurrency { get; set; } = "usd";
        public string StartedUtc { get; set; } = "";
        public long UptimeSeconds { get; set; }
        public bool IsOnline { get; set; } = true;
        public string StatusText { get; set; } = "";
        public string UpdatedUtc { get; set; } = "";

        public LmVsProxyServerProfile Clone() {
            return new LmVsProxyServerProfile {
                ServerId = ServerId ?? "",
                OwnerUserName = OwnerUserName ?? "",
                ServerName = ServerName ?? "",
                PublicHost = PublicHost ?? "",
                CostFactor = CostFactor,
                GpuName = GpuName ?? "",
                GpuAvailable = GpuAvailable ?? "",
                CpuName = CpuName ?? "",
                CpuAvailable = CpuAvailable ?? "",
                RamAvailable = RamAvailable ?? "",
                StorageAvailable = StorageAvailable ?? "",
                StorageLimitMegabytes = StorageLimitMegabytes,
                AvailableResources = AvailableResources ?? "",
                AvailableModels = AvailableModels ?? "",
                ToolsAllowed = ToolsAllowed ?? "",
                AvailableVram = AvailableVram ?? "",
                MaxTokens = MaxTokens,
                AdvertisedTokenRate = AdvertisedTokenRate,
                ModelCapabilitiesJson = ModelCapabilitiesJson ?? "",
                ModelBenchmarksJson = ModelBenchmarksJson ?? "",
                RequiresPayment = RequiresPayment,
                StripePriceId = StripePriceId ?? "",
                StripeAccount = StripeAccount ?? "",
                StripeUnitAmountCents = StripeUnitAmountCents,
                StripeCurrency = string.IsNullOrWhiteSpace(StripeCurrency) ? "usd" : StripeCurrency.Trim().ToLowerInvariant(),
                StartedUtc = StartedUtc ?? "",
                UptimeSeconds = UptimeSeconds,
                IsOnline = IsOnline,
                StatusText = StatusText ?? "",
                UpdatedUtc = UpdatedUtc ?? ""
            };
        }

        public void Normalize(string defaultServerName) {
            ServerId = NormalizeText(ServerId);
            OwnerUserName = NormalizeText(OwnerUserName);
            ServerName = NormalizeText(ServerName);
            if (string.IsNullOrWhiteSpace(ServerName))
                ServerName = string.IsNullOrWhiteSpace(defaultServerName) ? "LmVsProxy Host" : defaultServerName.Trim();

            PublicHost = NormalizeText(PublicHost);
            GpuName = NormalizeText(GpuName);
            GpuAvailable = NormalizeText(GpuAvailable);
            CpuName = NormalizeText(CpuName);
            CpuAvailable = NormalizeText(CpuAvailable);
            RamAvailable = NormalizeText(RamAvailable);
            StorageAvailable = NormalizeText(StorageAvailable);
            StorageLimitMegabytes = ClampStorageLimitMegabytes(StorageLimitMegabytes);
            AvailableResources = NormalizeText(AvailableResources);
            AvailableModels = NormalizeText(AvailableModels);
            ToolsAllowed = NormalizeText(ToolsAllowed);
            AvailableVram = NormalizeText(AvailableVram);
            ModelCapabilitiesJson = NormalizeText(ModelCapabilitiesJson);
            ModelBenchmarksJson = NormalizeText(ModelBenchmarksJson);
            if (MaxTokens < 0)
                MaxTokens = 0;
            if (AdvertisedTokenRate < 0)
                AdvertisedTokenRate = 0;
            StripePriceId = NormalizeText(StripePriceId);
            StripeAccount = NormalizeText(StripeAccount);
            StripeCurrency = string.IsNullOrWhiteSpace(StripeCurrency) ? "usd" : StripeCurrency.Trim().ToLowerInvariant();
            if (StripeUnitAmountCents < 0)
                StripeUnitAmountCents = 0;
            StartedUtc = NormalizeText(StartedUtc);
            if (UptimeSeconds < 0)
                UptimeSeconds = 0;
            StatusText = NormalizeText(StatusText);
            CostFactor = ClampCostFactor(CostFactor);
            if (string.IsNullOrWhiteSpace(UpdatedUtc))
                UpdatedUtc = DateTimeOffset.UtcNow.ToString("O");
            if (!RequiresPayment)
                ClearModelInventoryForFreeServer();
        }

        private static double ClampCostFactor(double value) {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return 1;
            return Math.Max(1, Math.Min(5, value));
        }

        private static long ClampStorageLimitMegabytes(long value) {
            if (value <= 0)
                return 500;
            return Math.Max(100, Math.Min(102400, value));
        }

        private static string NormalizeText(string value) {
            return (value ?? "").Trim();
        }

        private void ClearModelInventoryForFreeServer() {
            AvailableModels = "";
            ModelCapabilitiesJson = "";
            ModelBenchmarksJson = "";
            MaxTokens = 0;
            AvailableResources = RemoveModelInventoryLines(AvailableResources);
        }

        private static string RemoveModelInventoryLines(string value) {
            value = NormalizeText(value);
            if (string.IsNullOrWhiteSpace(value))
                return "";

            string[] lines = value.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var builder = new System.Text.StringBuilder();
            foreach (string rawLine in lines) {
                string line = rawLine ?? "";
                string trimmed = line.Trim();
                if (trimmed.StartsWith("Available Models:", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("Max Tokens:", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("Needs LM Studio metadata:", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (builder.Length > 0)
                    builder.AppendLine();
                builder.Append(line);
            }

            return builder.ToString().Trim();
        }
    }
}
