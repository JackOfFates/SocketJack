using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.Json;
namespace SocketJack.Net
{
    public partial class LmVsProxy
    {
private sealed class MarketplaceLeaseRecord
        {
            public string Id { get; set; } = "";
            public string OwnerKey { get; set; } = "";
            public string UserName { get; set; } = "";
            public string ServerId { get; set; } = "";
            public string ServerName { get; set; } = "";
            public string HostOwnerKey { get; set; } = "";
            public string HostUserName { get; set; } = "";
            public string RemoteEndpoint { get; set; } = "";
            public string OpenAiBaseUrl { get; set; } = "";
            public string SelectedModel { get; set; } = "";
            public string HardwareSummary { get; set; } = "";
            public string Status { get; set; } = "reserved";
            public string PaymentStatus { get; set; } = "not_required";
            public bool LeaseRequired { get; set; }
            public string CheckoutSessionId { get; set; } = "";
            public string PaymentId { get; set; } = "";
            public double QuoteUsd { get; set; }
            public long TokenRate { get; set; }
            public long LeaseMinutes { get; set; } = 60;
            public string StartedUtc { get; set; } = "";
            public string RenewedUtc { get; set; } = "";
            public string ExpiresUtc { get; set; } = "";
            public string EndedUtc { get; set; } = "";
            public string LastHeartbeatUtc { get; set; } = "";
            public string CreatedUtc { get; set; } = "";
            public string UpdatedUtc { get; set; } = "";
            public string TrustRiskTier { get; set; } = "unknown";
            public string PolicyJson { get; set; } = "{}";
            public string MetadataJson { get; set; } = "{}";
        }

private sealed class FinanceIdentity
        {
            public string OwnerKey { get; set; } = "";
            public string UserName { get; set; } = "";
            public string AuthType { get; set; } = "";
            public bool Authenticated { get; set; }
            public bool IsAdmin { get; set; }
        }

private sealed class FinancePeriod
        {
            public string Key { get; set; } = "30d";
            public DateTimeOffset StartUtc { get; set; }
            public DateTimeOffset EndUtc { get; set; }
        }

private sealed class FinanceAccountProfile
        {
            public string OwnerKey { get; set; } = "";
            public string UserName { get; set; } = "";
            public string StripeCustomerId { get; set; } = "";
            public string StripeConnectedAccountId { get; set; } = "";
            public string BillingEmail { get; set; } = "";
            public string TaxCountry { get; set; } = "US";
            public string TaxClassification { get; set; } = "";
            public string TaxpayerName { get; set; } = "";
            public string TaxIdentifierMasked { get; set; } = "";
            public bool TaxFormConsent { get; set; }
            public string CreatedUtc { get; set; } = "";
            public string UpdatedUtc { get; set; } = "";
            public string PayoutSchedule { get; set; } = "weekly";
            public string PayoutDay { get; set; } = DefaultWeeklyPayoutDay;
            public string PayoutMethod { get; set; } = "direct_deposit";
            public string InstantPayoutDestinationId { get; set; } = "";
            public string LastWeeklyPayoutUtc { get; set; } = "";
        }

private sealed class FinancePaymentRecord
        {
            public string Id { get; set; } = "";
            public string Kind { get; set; } = "token_purchase";
            public string Status { get; set; } = "created";
            public string OwnerKey { get; set; } = "";
            public string UserName { get; set; } = "";
            public string ServerId { get; set; } = "";
            public string ServerName { get; set; } = "";
            public string SessionId { get; set; } = "";
            public string LeaseId { get; set; } = "";
            public string StripeCheckoutSessionId { get; set; } = "";
            public string StripePaymentIntentId { get; set; } = "";
            public string StripeChargeId { get; set; } = "";
            public string StripeCustomerId { get; set; } = "";
            public string StripeAccountId { get; set; } = "";
            public string Currency { get; set; } = "usd";
            public long AmountCents { get; set; }
            public long SocketJackFeeCents { get; set; }
            public long HostAmountCents { get; set; }
            public long TokenAmount { get; set; }
            public long LeaseMinutes { get; set; }
            public string CheckoutUrl { get; set; } = "";
            public string CreatedUtc { get; set; } = "";
            public string UpdatedUtc { get; set; } = "";
            public string PaidUtc { get; set; } = "";
            public string RefundedUtc { get; set; } = "";
            public string DisputedUtc { get; set; } = "";
            public string ExpiresUtc { get; set; } = "";
            public string MetadataJson { get; set; } = "{}";
            public string Note { get; set; } = "";
        }

private sealed class FinancePayoutRecord
        {
            public string Id { get; set; } = "";
            public string OwnerKey { get; set; } = "";
            public string UserName { get; set; } = "";
            public string Status { get; set; } = "requested";
            public string Currency { get; set; } = "usd";
            public double GrossUsd { get; set; }
            public double SocketJackFeeUsd { get; set; }
            public double NetUsd { get; set; }
            public long Tokens { get; set; }
            public string PeriodStartUtc { get; set; } = "";
            public string PeriodEndUtc { get; set; } = "";
            public string RequestedUtc { get; set; } = "";
            public string PaidUtc { get; set; } = "";
            public string StripeTransferId { get; set; } = "";
            public string Method { get; set; } = "direct_deposit";
            public string Schedule { get; set; } = "manual";
            public string DestinationType { get; set; } = "bank_account";
            public double InstantFeeUsd { get; set; }
            public double InstantFeeTaxUsd { get; set; }
            public double PayoutAmountUsd { get; set; }
            public string StripePayoutId { get; set; } = "";
            public string StripeConnectedAccountId { get; set; } = "";
            public string AvailableOnUtc { get; set; } = "";
            public string MetadataJson { get; set; } = "{}";
            public string Note { get; set; } = "";
        }

private sealed class FinanceTokenOperationRecord
        {
            public string Id { get; set; } = "";
            public string OwnerKey { get; set; } = "";
            public string UserName { get; set; } = "";
            public string Operation { get; set; } = "usage_charge";
            public long TokensDelta { get; set; }
            public double TokenCostUsd { get; set; }
            public double ElectricityCostUsd { get; set; }
            public double ServiceTaxCostUsd { get; set; }
            public double TotalCostUsd { get; set; }
            public string CreatedUtc { get; set; } = "";
            public string ServerId { get; set; } = "";
            public string SessionId { get; set; } = "";
            public string Note { get; set; } = "";
            public double GpuSecondsDelta { get; set; }
            public double CpuComputeSecondsDelta { get; set; }
            public double RamGbSecondsDelta { get; set; }
            public double SystemSecondsDelta { get; set; }
            public long IoBytesDelta { get; set; }
            public double GpuElectricityCostUsd { get; set; }
            public double CpuElectricityCostUsd { get; set; }
            public double RamElectricityCostUsd { get; set; }
            public double SystemElectricityCostUsd { get; set; }
            public double IoElectricityCostUsd { get; set; }
            public double GpuSecondsPerToken { get; set; }
            public double CpuComputeSecondsPerToken { get; set; }
        }

private sealed class FinanceMoneySummary
        {
            public long Tokens { get; set; }
            public double GrossUsd { get; set; }
            public double TokenCostUsd { get; set; }
            public double ElectricityCostUsd { get; set; }
            public double ServiceTaxCostUsd { get; set; }
            public double SocketJackFeeUsd { get; set; }
            public double TotalCostUsd { get; set; }
            public double NetUsd { get; set; }
            public double ProfitMargin { get; set; }
        }

private sealed class FinanceDashboard
        {
            public FinanceMoneySummary Summary { get; set; }
            public List<object> Payments { get; set; }
            public List<object> Payouts { get; set; }
            public List<object> TokenOperations { get; set; }
            public List<object> UsageLedger { get; set; }
            public IEnumerable<object> TopEarners { get; set; }
            public IEnumerable<object> TopExploitedServers { get; set; }
            public List<object> Charts { get; set; }
        }

private sealed class FinancePiePart
        {
            public FinancePiePart(string label, double value)
            {
                Label = label ?? "";
                Value = value;
            }

            public string Label { get; }
            public double Value { get; }
        }
    }
}