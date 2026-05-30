using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SocketJack.Net.Payments
{
    public sealed class JackLLMPaymentProcessorStatus
    {
        public string ProviderName { get; set; } = "";
        public bool CheckoutConfigured { get; set; }
        public bool PublishableKeyConfigured { get; set; }
        public bool WebhookConfigured { get; set; }
        public bool PayoutsConfigured { get; set; }
    }

    public sealed class JackLLMPaymentTokenProductConfig
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

    public sealed class JackLLMPaymentCheckoutSessionRequest
    {
        public IList<JackLLMPaymentCheckoutLineItem> LineItems { get; } = new List<JackLLMPaymentCheckoutLineItem>();
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

    public sealed class JackLLMPaymentCheckoutLineItem
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

    public sealed class JackLLMPaymentCheckoutSessionResult
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

    public sealed class JackLLMVerifiedPaymentWebhookEvent
    {
        public string Id { get; set; } = "";
        public string Type { get; set; } = "";
    }

    public sealed class JackLLMConnectedTransferRequest
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

    public sealed class JackLLMConnectedTransferResult
    {
        public string TransferId { get; set; } = "";
        public string ConnectedAccountId { get; set; } = "";
        public string Currency { get; set; } = "";
        public string TransferGroup { get; set; } = "";
        public long Amount { get; set; }
        public long GrossAmount { get; set; }
        public long SocketJackFeeAmount { get; set; }
    }

    public sealed class JackLLMConnectedPayoutRequest
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

    public sealed class JackLLMConnectedPayoutResult
    {
        public string PayoutId { get; set; } = "";
        public string ConnectedAccountId { get; set; } = "";
        public string Currency { get; set; } = "";
        public string Status { get; set; } = "";
        public string Method { get; set; } = "";
        public string ArrivalDateUtc { get; set; } = "";
        public long Amount { get; set; }
    }

    public sealed class JackLLMPayoutScheduleRequest
    {
        public string ConnectedAccountId { get; set; } = "";
        public string IdempotencyKey { get; set; } = "";
        public string Interval { get; set; } = "weekly";
        public string WeeklyPayoutDay { get; set; } = "friday";
    }

    public sealed class JackLLMPayoutScheduleResult
    {
        public string ConnectedAccountId { get; set; } = "";
        public string Interval { get; set; } = "";
        public string WeeklyPayoutDay { get; set; } = "";
    }

    public interface IJackLLMPaymentProcessor
    {
        JackLLMPaymentProcessorStatus GetStatus();
        IEnumerable<JackLLMPaymentTokenProductConfig> GetTokenProducts();
        Task<JackLLMPaymentCheckoutSessionResult> CreateCheckoutSessionAsync(JackLLMPaymentCheckoutSessionRequest request, CancellationToken cancellationToken = default);
        Task<JackLLMPaymentCheckoutSessionResult> RetrieveCheckoutSessionAsync(string sessionId, string connectedAccountId = "", CancellationToken cancellationToken = default);
        JackLLMVerifiedPaymentWebhookEvent VerifyWebhookEvent(string json, string signatureHeader);
        Task<JackLLMConnectedTransferResult> CreateConnectedAccountTransferAsync(JackLLMConnectedTransferRequest request, CancellationToken cancellationToken = default);
        Task<JackLLMConnectedPayoutResult> CreateConnectedAccountPayoutAsync(JackLLMConnectedPayoutRequest request, CancellationToken cancellationToken = default);
        Task<JackLLMPayoutScheduleResult> ConfigureWeeklyPayoutScheduleAsync(JackLLMPayoutScheduleRequest request, CancellationToken cancellationToken = default);
    }
}
