using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SocketJack.Net;
using SocketJack.Net.Payments;
using HeirowLlmHost = SocketJack.Net.HeirowLlm;

namespace SocketJack.Stripe
{
    public static class heirowLLMStripeExtensions
    {
        public static HeirowLlmHost UseStripePaymentsFromEnvironment(this HeirowLlmHost proxy)
        {
            if (proxy == null)
                throw new ArgumentNullException(nameof(proxy));

            proxy.PaymentProcessor = new StripeheirowLLMPaymentProcessor(StripePaymentServiceOptions.FromEnvironment);
            return proxy;
        }

        public static HeirowLlmHost UseStripePayments(this HeirowLlmHost proxy, StripePaymentServiceOptions options)
        {
            if (proxy == null)
                throw new ArgumentNullException(nameof(proxy));

            proxy.PaymentProcessor = new StripeheirowLLMPaymentProcessor(() => options ?? new StripePaymentServiceOptions());
            return proxy;
        }
    }

    internal sealed class StripeheirowLLMPaymentProcessor : IheirowLLMPaymentProcessor
    {
        private readonly Func<StripePaymentServiceOptions> _optionsFactory;

        public StripeheirowLLMPaymentProcessor(Func<StripePaymentServiceOptions> optionsFactory)
        {
            _optionsFactory = optionsFactory ?? StripePaymentServiceOptions.FromEnvironment;
        }

        public heirowLLMPaymentProcessorStatus GetStatus()
        {
            StripePaymentServiceOptions options = GetOptions();
            bool hasSecretKey = !string.IsNullOrWhiteSpace(options.SecretKey);
            return new heirowLLMPaymentProcessorStatus
            {
                ProviderName = "Stripe",
                CheckoutConfigured = hasSecretKey,
                PublishableKeyConfigured = !string.IsNullOrWhiteSpace(options.PublishableKey),
                WebhookConfigured = !string.IsNullOrWhiteSpace(options.WebhookSigningSecret),
                PayoutsConfigured = hasSecretKey
            };
        }

        public IEnumerable<heirowLLMPaymentTokenProductConfig> GetTokenProducts()
        {
            return StripeTokenProductCatalog.Normalize(StripeTokenProductCatalog.CreateDefaultProducts())
                .Select(product => new heirowLLMPaymentTokenProductConfig
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

        public async Task<heirowLLMPaymentCheckoutSessionResult> CreateCheckoutSessionAsync(heirowLLMPaymentCheckoutSessionRequest request, CancellationToken cancellationToken = default)
        {
            StripePaymentService service = CreateService();
            StripeCheckoutSessionResult result = await service.CreateCheckoutSessionAsync(ToStripeCheckoutSessionRequest(request), cancellationToken).ConfigureAwait(false);
            return ToheirowLLMCheckoutSessionResult(result);
        }

        public async Task<heirowLLMPaymentCheckoutSessionResult> RetrieveCheckoutSessionAsync(string sessionId, string connectedAccountId = "", CancellationToken cancellationToken = default)
        {
            StripePaymentService service = CreateService();
            StripeCheckoutSessionResult result = await service.RetrieveCheckoutSessionAsync(sessionId, connectedAccountId, cancellationToken).ConfigureAwait(false);
            return ToheirowLLMCheckoutSessionResult(result);
        }

        public heirowLLMVerifiedPaymentWebhookEvent VerifyWebhookEvent(string json, string signatureHeader)
        {
            StripePaymentService service = CreateService();
            global::Stripe.Event stripeEvent = service.ConstructWebhookEvent(json, signatureHeader);
            return new heirowLLMVerifiedPaymentWebhookEvent
            {
                Id = stripeEvent?.Id ?? "",
                Type = stripeEvent?.Type ?? ""
            };
        }

        public async Task<heirowLLMConnectedTransferResult> CreateConnectedAccountTransferAsync(heirowLLMConnectedTransferRequest request, CancellationToken cancellationToken = default)
        {
            StripePaymentService service = CreateService();
            SocketJackConnectedTransferResult result = await service.CreateConnectedAccountTransferAsync(ToStripeConnectedTransferRequest(request), cancellationToken).ConfigureAwait(false);
            return new heirowLLMConnectedTransferResult
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

        public async Task<heirowLLMConnectedPayoutResult> CreateConnectedAccountPayoutAsync(heirowLLMConnectedPayoutRequest request, CancellationToken cancellationToken = default)
        {
            StripePaymentService service = CreateService();
            SocketJackConnectedPayoutResult result = await service.CreateConnectedAccountPayoutAsync(ToStripeConnectedPayoutRequest(request), cancellationToken).ConfigureAwait(false);
            return new heirowLLMConnectedPayoutResult
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

        public async Task<heirowLLMPayoutScheduleResult> ConfigureWeeklyPayoutScheduleAsync(heirowLLMPayoutScheduleRequest request, CancellationToken cancellationToken = default)
        {
            StripePaymentService service = CreateService();
            SocketJackPayoutScheduleResult result = await service.ConfigureWeeklyPayoutScheduleAsync(new SocketJackPayoutScheduleRequest
            {
                ConnectedAccountId = request?.ConnectedAccountId ?? "",
                IdempotencyKey = request?.IdempotencyKey ?? "",
                Interval = request?.Interval ?? "weekly",
                WeeklyPayoutDay = request?.WeeklyPayoutDay ?? "friday"
            }, cancellationToken).ConfigureAwait(false);

            return new heirowLLMPayoutScheduleResult
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

        private static StripeCheckoutSessionRequest ToStripeCheckoutSessionRequest(heirowLLMPaymentCheckoutSessionRequest request)
        {
            request = request ?? new heirowLLMPaymentCheckoutSessionRequest();
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
            foreach (heirowLLMPaymentCheckoutLineItem item in request.LineItems)
                stripeRequest.LineItems.Add(ToStripeCheckoutLineItem(item));
            return stripeRequest;
        }

        private static StripeCheckoutLineItem ToStripeCheckoutLineItem(heirowLLMPaymentCheckoutLineItem item)
        {
            item = item ?? new heirowLLMPaymentCheckoutLineItem();
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

        private static heirowLLMPaymentCheckoutSessionResult ToheirowLLMCheckoutSessionResult(StripeCheckoutSessionResult result)
        {
            result = result ?? new StripeCheckoutSessionResult();
            var mapped = new heirowLLMPaymentCheckoutSessionResult
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

        private static SocketJackConnectedTransferRequest ToStripeConnectedTransferRequest(heirowLLMConnectedTransferRequest request)
        {
            request = request ?? new heirowLLMConnectedTransferRequest();
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

        private static SocketJackConnectedPayoutRequest ToStripeConnectedPayoutRequest(heirowLLMConnectedPayoutRequest request)
        {
            request = request ?? new heirowLLMConnectedPayoutRequest();
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
