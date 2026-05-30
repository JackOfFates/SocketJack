using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SocketJack.Net;
using SocketJack.Net.Payments;
using JackLLMProxy = SocketJack.Net.LmVsProxy;

namespace SocketJack.Stripe
{
    public static class JackLLMStripeExtensions
    {
        public static JackLLMProxy UseStripePaymentsFromEnvironment(this JackLLMProxy proxy)
        {
            if (proxy == null)
                throw new ArgumentNullException(nameof(proxy));

            proxy.PaymentProcessor = new StripeJackLLMPaymentProcessor(StripePaymentServiceOptions.FromEnvironment);
            return proxy;
        }

        public static JackLLMProxy UseStripePayments(this JackLLMProxy proxy, StripePaymentServiceOptions options)
        {
            if (proxy == null)
                throw new ArgumentNullException(nameof(proxy));

            proxy.PaymentProcessor = new StripeJackLLMPaymentProcessor(() => options ?? new StripePaymentServiceOptions());
            return proxy;
        }
    }

    internal sealed class StripeJackLLMPaymentProcessor : IJackLLMPaymentProcessor
    {
        private readonly Func<StripePaymentServiceOptions> _optionsFactory;

        public StripeJackLLMPaymentProcessor(Func<StripePaymentServiceOptions> optionsFactory)
        {
            _optionsFactory = optionsFactory ?? StripePaymentServiceOptions.FromEnvironment;
        }

        public JackLLMPaymentProcessorStatus GetStatus()
        {
            StripePaymentServiceOptions options = GetOptions();
            bool hasSecretKey = !string.IsNullOrWhiteSpace(options.SecretKey);
            return new JackLLMPaymentProcessorStatus
            {
                ProviderName = "Stripe",
                CheckoutConfigured = hasSecretKey,
                PublishableKeyConfigured = !string.IsNullOrWhiteSpace(options.PublishableKey),
                WebhookConfigured = !string.IsNullOrWhiteSpace(options.WebhookSigningSecret),
                PayoutsConfigured = hasSecretKey
            };
        }

        public IEnumerable<JackLLMPaymentTokenProductConfig> GetTokenProducts()
        {
            return StripeTokenProductCatalog.Normalize(StripeTokenProductCatalog.CreateDefaultProducts())
                .Select(product => new JackLLMPaymentTokenProductConfig
                {
                    ProductId = product.ProductId,
                    PriceId = product.PriceId,
                    Name = product.Name,
                    Description = product.Description,
                    Currency = product.Currency,
                    TokenAmount = product.TokenAmount,
                    UnitAmountCents = product.UnitAmountCents,
                    Enabled = product.Enabled
                })
                .ToList();
        }

        public async Task<JackLLMPaymentCheckoutSessionResult> CreateCheckoutSessionAsync(JackLLMPaymentCheckoutSessionRequest request, CancellationToken cancellationToken = default)
        {
            StripePaymentService service = CreateService();
            StripeCheckoutSessionResult result = await service.CreateCheckoutSessionAsync(ToStripeCheckoutSessionRequest(request), cancellationToken).ConfigureAwait(false);
            return ToJackLLMCheckoutSessionResult(result);
        }

        public async Task<JackLLMPaymentCheckoutSessionResult> RetrieveCheckoutSessionAsync(string sessionId, string connectedAccountId = "", CancellationToken cancellationToken = default)
        {
            StripePaymentService service = CreateService();
            StripeCheckoutSessionResult result = await service.RetrieveCheckoutSessionAsync(sessionId, connectedAccountId, cancellationToken).ConfigureAwait(false);
            return ToJackLLMCheckoutSessionResult(result);
        }

        public JackLLMVerifiedPaymentWebhookEvent VerifyWebhookEvent(string json, string signatureHeader)
        {
            StripePaymentService service = CreateService();
            global::Stripe.Event stripeEvent = service.ConstructWebhookEvent(json, signatureHeader);
            return new JackLLMVerifiedPaymentWebhookEvent
            {
                Id = stripeEvent?.Id ?? "",
                Type = stripeEvent?.Type ?? ""
            };
        }

        public async Task<JackLLMConnectedTransferResult> CreateConnectedAccountTransferAsync(JackLLMConnectedTransferRequest request, CancellationToken cancellationToken = default)
        {
            StripePaymentService service = CreateService();
            SocketJackConnectedTransferResult result = await service.CreateConnectedAccountTransferAsync(ToStripeConnectedTransferRequest(request), cancellationToken).ConfigureAwait(false);
            return new JackLLMConnectedTransferResult
            {
                TransferId = result.TransferId,
                ConnectedAccountId = result.ConnectedAccountId,
                Currency = result.Currency,
                TransferGroup = result.TransferGroup,
                Amount = result.Amount,
                GrossAmount = result.GrossAmount,
                SocketJackFeeAmount = result.SocketJackFeeAmount
            };
        }

        public async Task<JackLLMConnectedPayoutResult> CreateConnectedAccountPayoutAsync(JackLLMConnectedPayoutRequest request, CancellationToken cancellationToken = default)
        {
            StripePaymentService service = CreateService();
            SocketJackConnectedPayoutResult result = await service.CreateConnectedAccountPayoutAsync(ToStripeConnectedPayoutRequest(request), cancellationToken).ConfigureAwait(false);
            return new JackLLMConnectedPayoutResult
            {
                PayoutId = result.PayoutId,
                ConnectedAccountId = result.ConnectedAccountId,
                Currency = result.Currency,
                Status = result.Status,
                Method = result.Method,
                ArrivalDateUtc = result.ArrivalDateUtc,
                Amount = result.Amount
            };
        }

        public async Task<JackLLMPayoutScheduleResult> ConfigureWeeklyPayoutScheduleAsync(JackLLMPayoutScheduleRequest request, CancellationToken cancellationToken = default)
        {
            StripePaymentService service = CreateService();
            SocketJackPayoutScheduleResult result = await service.ConfigureWeeklyPayoutScheduleAsync(new SocketJackPayoutScheduleRequest
            {
                ConnectedAccountId = request?.ConnectedAccountId ?? "",
                IdempotencyKey = request?.IdempotencyKey ?? "",
                Interval = request?.Interval ?? "weekly",
                WeeklyPayoutDay = request?.WeeklyPayoutDay ?? "friday"
            }, cancellationToken).ConfigureAwait(false);

            return new JackLLMPayoutScheduleResult
            {
                ConnectedAccountId = result.ConnectedAccountId,
                Interval = result.Interval,
                WeeklyPayoutDay = result.WeeklyPayoutDay
            };
        }

        private StripePaymentService CreateService()
        {
            return new StripePaymentService(GetOptions());
        }

        private StripePaymentServiceOptions GetOptions()
        {
            return _optionsFactory() ?? new StripePaymentServiceOptions();
        }

        private static StripeCheckoutSessionRequest ToStripeCheckoutSessionRequest(JackLLMPaymentCheckoutSessionRequest request)
        {
            request = request ?? new JackLLMPaymentCheckoutSessionRequest();
            var stripeRequest = new StripeCheckoutSessionRequest
            {
                SuccessUrl = request.SuccessUrl,
                CancelUrl = request.CancelUrl,
                CustomerId = request.CustomerId,
                CustomerEmail = request.CustomerEmail,
                ClientReferenceId = request.ClientReferenceId,
                IdempotencyKey = request.IdempotencyKey,
                StripeAccount = request.ConnectedAccountId,
                AllowPromotionCodes = request.AllowPromotionCodes,
                EnableAutomaticTax = request.EnableAutomaticTax
            };

            CopyMetadata(request.Metadata, stripeRequest.Metadata);
            foreach (JackLLMPaymentCheckoutLineItem item in request.LineItems)
                stripeRequest.LineItems.Add(ToStripeCheckoutLineItem(item));
            return stripeRequest;
        }

        private static StripeCheckoutLineItem ToStripeCheckoutLineItem(JackLLMPaymentCheckoutLineItem item)
        {
            item = item ?? new JackLLMPaymentCheckoutLineItem();
            var stripeItem = new StripeCheckoutLineItem
            {
                PriceId = item.PriceId,
                ProductId = item.ProductId,
                ProductName = item.ProductName,
                Description = item.Description,
                Currency = item.Currency,
                UnitAmount = item.UnitAmount,
                Quantity = item.Quantity
            };
            CopyMetadata(item.Metadata, stripeItem.Metadata);
            return stripeItem;
        }

        private static JackLLMPaymentCheckoutSessionResult ToJackLLMCheckoutSessionResult(StripeCheckoutSessionResult result)
        {
            result = result ?? new StripeCheckoutSessionResult();
            var mapped = new JackLLMPaymentCheckoutSessionResult
            {
                Id = result.Id,
                Url = result.Url,
                Status = result.Status,
                PaymentStatus = result.PaymentStatus,
                PaymentIntentId = result.PaymentIntentId,
                CustomerId = result.CustomerId,
                ClientReferenceId = result.ClientReferenceId,
                Currency = result.Currency,
                AmountTotal = result.AmountTotal
            };
            CopyMetadata(result.Metadata, mapped.Metadata);
            return mapped;
        }

        private static SocketJackConnectedTransferRequest ToStripeConnectedTransferRequest(JackLLMConnectedTransferRequest request)
        {
            request = request ?? new JackLLMConnectedTransferRequest();
            var mapped = new SocketJackConnectedTransferRequest
            {
                ConnectedAccountId = request.ConnectedAccountId,
                PayoutRequestId = request.PayoutRequestId,
                Currency = request.Currency,
                IdempotencyKey = request.IdempotencyKey,
                TransferGroup = request.TransferGroup,
                Amount = request.Amount,
                GrossAmount = request.GrossAmount,
                SocketJackFeeAmount = request.SocketJackFeeAmount
            };
            CopyMetadata(request.Metadata, mapped.Metadata);
            return mapped;
        }

        private static SocketJackConnectedPayoutRequest ToStripeConnectedPayoutRequest(JackLLMConnectedPayoutRequest request)
        {
            request = request ?? new JackLLMConnectedPayoutRequest();
            var mapped = new SocketJackConnectedPayoutRequest
            {
                ConnectedAccountId = request.ConnectedAccountId,
                PayoutRequestId = request.PayoutRequestId,
                Currency = request.Currency,
                IdempotencyKey = request.IdempotencyKey,
                Destination = request.Destination,
                Method = request.Method,
                StatementDescriptor = request.StatementDescriptor,
                Amount = request.Amount
            };
            CopyMetadata(request.Metadata, mapped.Metadata);
            return mapped;
        }

        private static void CopyMetadata(IDictionary<string, string> source, IDictionary<string, string> destination)
        {
            if (source == null || destination == null)
                return;

            foreach (KeyValuePair<string, string> pair in source)
            {
                if (!string.IsNullOrWhiteSpace(pair.Key))
                    destination[pair.Key] = pair.Value ?? "";
            }
        }
    }
}
