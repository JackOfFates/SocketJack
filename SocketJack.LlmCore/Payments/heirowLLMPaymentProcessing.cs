using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SocketJack.Net.Payments
{
    public sealed class heirowLLMPaymentProcessorStatus
    {
        public string ProviderName { get; set; } = "";
        public bool CheckoutConfigured { get; set; }
        public bool PublishableKeyConfigured { get; set; }
        public bool WebhookConfigured { get; set; }
        public bool PayoutsConfigured { get; set; }
    }

    public sealed class heirowLLMPaymentTokenProductConfig
    {
        public string ProductId { get; set; } = "";
        public string PriceId { get; set; } = "";
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string Currency { get; set; } = "usd";
        public long TokenAmount { get; set; }
        public long UnitAmountCents { get; set; }
        public bool Enabled { get; set; } = true;
    }

    public sealed class heirowLLMPaymentCheckoutSessionRequest
    {
        public IList<heirowLLMPaymentCheckoutLineItem> LineItems { get; } = new List<heirowLLMPaymentCheckoutLineItem>();
        public string SuccessUrl { get; set; } = "";
        public string CancelUrl { get; set; } = "";
        public string CustomerId { get; set; } = "";
        public string CustomerEmail { get; set; } = "";
        public string ClientReferenceId { get; set; } = "";
        public string IdempotencyKey { get; set; } = "";
        public string ConnectedAccountId { get; set; } = "";
        public bool AllowPromotionCodes { get; set; }
        public bool EnableAutomaticTax { get; set; }
        public IDictionary<string, string> Metadata { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    public sealed class heirowLLMPaymentCheckoutLineItem
    {
        public string PriceId { get; set; } = "";
        public string ProductId { get; set; } = "";
        public string ProductName { get; set; } = "";
        public string Description { get; set; } = "";
        public string Currency { get; set; } = "usd";
        public long? UnitAmount { get; set; }
        public long Quantity { get; set; } = 1;
        public IDictionary<string, string> Metadata { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    public sealed class heirowLLMPaymentCheckoutSessionResult
    {
        public string Id { get; set; } = "";
        public string Url { get; set; } = "";
        public string Status { get; set; } = "";
        public string PaymentStatus { get; set; } = "";
        public string PaymentIntentId { get; set; } = "";
        public string CustomerId { get; set; } = "";
        public string ClientReferenceId { get; set; } = "";
        public string Currency { get; set; } = "";
        public long? AmountTotal { get; set; }
        public IDictionary<string, string> Metadata { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    public sealed class heirowLLMVerifiedPaymentWebhookEvent
    {
        public string Id { get; set; } = "";
        public string Type { get; set; } = "";
    }

    public sealed class heirowLLMConnectedTransferRequest
    {
        public string ConnectedAccountId { get; set; } = "";
        public string PayoutRequestId { get; set; } = "";
        public string Currency { get; set; } = "usd";
        public string IdempotencyKey { get; set; } = "";
        public string TransferGroup { get; set; } = "";
        public long Amount { get; set; }
        public long GrossAmount { get; set; }
        public long SocketJackFeeAmount { get; set; }
        public IDictionary<string, string> Metadata { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    public sealed class heirowLLMConnectedTransferResult
    {
        public string TransferId { get; set; } = "";
        public string ConnectedAccountId { get; set; } = "";
        public string Currency { get; set; } = "";
        public string TransferGroup { get; set; } = "";
        public long Amount { get; set; }
        public long GrossAmount { get; set; }
        public long SocketJackFeeAmount { get; set; }
    }

    public sealed class heirowLLMConnectedPayoutRequest
    {
        public string ConnectedAccountId { get; set; } = "";
        public string PayoutRequestId { get; set; } = "";
        public string Currency { get; set; } = "usd";
        public string IdempotencyKey { get; set; } = "";
        public string Destination { get; set; } = "";
        public string Method { get; set; } = "standard";
        public string StatementDescriptor { get; set; } = "SOCKETJACK";
        public long Amount { get; set; }
        public IDictionary<string, string> Metadata { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    public sealed class heirowLLMConnectedPayoutResult
    {
        public string PayoutId { get; set; } = "";
        public string ConnectedAccountId { get; set; } = "";
        public string Currency { get; set; } = "";
        public string Status { get; set; } = "";
        public string Method { get; set; } = "";
        public string ArrivalDateUtc { get; set; } = "";
        public long Amount { get; set; }
    }

    public sealed class heirowLLMPayoutScheduleRequest
    {
        public string ConnectedAccountId { get; set; } = "";
        public string IdempotencyKey { get; set; } = "";
        public string Interval { get; set; } = "weekly";
        public string WeeklyPayoutDay { get; set; } = "friday";
    }

    public sealed class heirowLLMPayoutScheduleResult
    {
        public string ConnectedAccountId { get; set; } = "";
        public string Interval { get; set; } = "";
        public string WeeklyPayoutDay { get; set; } = "";
    }

    public interface IheirowLLMPaymentProcessor
    {
        heirowLLMPaymentProcessorStatus GetStatus();
        IEnumerable<heirowLLMPaymentTokenProductConfig> GetTokenProducts();
        Task<heirowLLMPaymentCheckoutSessionResult> CreateCheckoutSessionAsync(heirowLLMPaymentCheckoutSessionRequest request, CancellationToken cancellationToken = default);
        Task<heirowLLMPaymentCheckoutSessionResult> RetrieveCheckoutSessionAsync(string sessionId, string connectedAccountId = "", CancellationToken cancellationToken = default);
        heirowLLMVerifiedPaymentWebhookEvent VerifyWebhookEvent(string json, string signatureHeader);
        Task<heirowLLMConnectedTransferResult> CreateConnectedAccountTransferAsync(heirowLLMConnectedTransferRequest request, CancellationToken cancellationToken = default);
        Task<heirowLLMConnectedPayoutResult> CreateConnectedAccountPayoutAsync(heirowLLMConnectedPayoutRequest request, CancellationToken cancellationToken = default);
        Task<heirowLLMPayoutScheduleResult> ConfigureWeeklyPayoutScheduleAsync(heirowLLMPayoutScheduleRequest request, CancellationToken cancellationToken = default);
    }
}
