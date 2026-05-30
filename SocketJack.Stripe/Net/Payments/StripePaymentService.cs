using Stripe;
using Stripe.Checkout;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SocketJack.Net.Payments {

    /// <summary>
    /// Environment-backed configuration for Stripe payment calls.
    /// Keep secret keys in a vault or process environment, never in source code.
    /// </summary>
    public sealed class StripePaymentServiceOptions {
        public const string SecretKeyEnvironmentVariable = "STRIPE_SECRET_KEY";
        public const string RestrictedKeyEnvironmentVariable = "STRIPE_RESTRICTED_KEY";
        public const string PublishableKeyEnvironmentVariable = "STRIPE_PUBLISHABLE_KEY";
        public const string WebhookSecretEnvironmentVariable = "STRIPE_WEBHOOK_SECRET";
        public const string SuccessUrlEnvironmentVariable = "STRIPE_CHECKOUT_SUCCESS_URL";
        public const string CancelUrlEnvironmentVariable = "STRIPE_CHECKOUT_CANCEL_URL";

        public string SecretKey { get; set; } = "";
        public string PublishableKey { get; set; } = "";
        public string WebhookSigningSecret { get; set; } = "";
        public string SuccessUrl { get; set; } = "";
        public string CancelUrl { get; set; } = "";

        public static StripePaymentServiceOptions FromEnvironment() {
            return new StripePaymentServiceOptions {
                SecretKey = FirstNonEmpty(
                    Environment.GetEnvironmentVariable(RestrictedKeyEnvironmentVariable),
                    Environment.GetEnvironmentVariable(SecretKeyEnvironmentVariable)),
                PublishableKey = Environment.GetEnvironmentVariable(PublishableKeyEnvironmentVariable) ?? "",
                WebhookSigningSecret = Environment.GetEnvironmentVariable(WebhookSecretEnvironmentVariable) ?? "",
                SuccessUrl = Environment.GetEnvironmentVariable(SuccessUrlEnvironmentVariable) ?? "",
                CancelUrl = Environment.GetEnvironmentVariable(CancelUrlEnvironmentVariable) ?? ""
            };
        }

        private static string FirstNonEmpty(params string[] values) {
            foreach (string value in values) {
                if (!string.IsNullOrWhiteSpace(value))
                    return value.Trim();
            }
            return "";
        }
    }

    public sealed class StripeCheckoutSessionRequest {
        public IList<StripeCheckoutLineItem> LineItems { get; } = new List<StripeCheckoutLineItem>();
        public string SuccessUrl { get; set; } = "";
        public string CancelUrl { get; set; } = "";
        public string CustomerId { get; set; } = "";
        public string CustomerEmail { get; set; } = "";
        public string ClientReferenceId { get; set; } = "";
        public string IdempotencyKey { get; set; } = "";
        public string StripeAccount { get; set; } = "";
        public bool AllowPromotionCodes { get; set; }
        public bool EnableAutomaticTax { get; set; }
        public IDictionary<string, string> Metadata { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    public sealed class StripeCheckoutLineItem {
        public string PriceId { get; set; } = "";
        public string ProductId { get; set; } = "";
        public string ProductName { get; set; } = "";
        public string Description { get; set; } = "";
        public string Currency { get; set; } = "usd";
        public long? UnitAmount { get; set; }
        public long Quantity { get; set; } = 1;
        public IDictionary<string, string> Metadata { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    public sealed class StripeCheckoutSessionResult {
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

    public sealed class StripeTokenProductConfig {
        public string ProductId { get; set; } = "";
        public string PriceId { get; set; } = "";
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string Currency { get; set; } = "usd";
        public long TokenAmount { get; set; }
        public long UnitAmountCents { get; set; }
        public bool Enabled { get; set; } = true;

        public StripeTokenProductConfig Clone() {
            return (StripeTokenProductConfig)MemberwiseClone();
        }
    }

    public static class StripeTokenProductCatalog {
        public static IList<StripeTokenProductConfig> CreateDefaultProducts() {
            return new List<StripeTokenProductConfig> {
                new StripeTokenProductConfig { ProductId = "prod_UTowqvJjzX75gB", Name = "25k Tokens", Description = "25,000 Premium LLM Tokens", TokenAmount = 25000, UnitAmountCents = 125, Enabled = true },
                new StripeTokenProductConfig { ProductId = "prod_UTownCIRJOmIch", Name = "100k Tokens", Description = "100,000 Premium LLM Tokens", TokenAmount = 100000, UnitAmountCents = 500, Enabled = true },
                new StripeTokenProductConfig { ProductId = "prod_UTp22CZp6jpXfZ", Name = "250k Tokens", Description = "250,000 Premium LLM Tokens", TokenAmount = 250000, UnitAmountCents = 1250, Enabled = true },
                new StripeTokenProductConfig { ProductId = "prod_UToxceckT6Qhmv", Name = "500k Tokens", Description = "500,000 Premium LLM Tokens", TokenAmount = 500000, UnitAmountCents = 2500, Enabled = true },
                new StripeTokenProductConfig { ProductId = "prod_UTp0s5RyyT9Bxk", Name = "1M Tokens", Description = "1,000,000 Premium LLM Tokens", TokenAmount = 1000000, UnitAmountCents = 5000, Enabled = true },
                new StripeTokenProductConfig { ProductId = "prod_UToytcYmnsU2dX", Name = "One Token", Description = "Unlimited Supply - One Premium LLM Token", TokenAmount = 1, UnitAmountCents = 0, Enabled = false }
            };
        }

        public static List<StripeTokenProductConfig> Normalize(IEnumerable<StripeTokenProductConfig> products) {
            var normalized = new List<StripeTokenProductConfig>();
            if (products != null) {
                foreach (StripeTokenProductConfig product in products) {
                    if (product == null)
                        continue;

                    var next = product.Clone();
                    next.ProductId = NormalizeText(next.ProductId);
                    next.PriceId = NormalizeText(next.PriceId);
                    next.Name = NormalizeText(next.Name);
                    next.Description = NormalizeText(next.Description);
                    next.Currency = string.IsNullOrWhiteSpace(next.Currency) ? "usd" : next.Currency.Trim().ToLowerInvariant();
                    next.TokenAmount = Math.Max(0, next.TokenAmount);
                    next.UnitAmountCents = Math.Max(0, next.UnitAmountCents);
                    if (next.ProductId.Length == 0 && next.PriceId.Length == 0 && next.Name.Length == 0)
                        continue;

                    normalized.Add(next);
                }
            }

            return normalized.Count == 0
                ? CreateDefaultProducts().Select(product => product.Clone()).ToList()
                : normalized;
        }

        private static string NormalizeText(string value) {
            return (value ?? "").Trim();
        }
    }

    public sealed class SocketJackTokenPurchaseRequest {
        public string UserId { get; set; } = "";
        public string CustomerId { get; set; } = "";
        public string CustomerEmail { get; set; } = "";
        public string SuccessUrl { get; set; } = "";
        public string CancelUrl { get; set; } = "";
        public string IdempotencyKey { get; set; } = "";
        public string Currency { get; set; } = "usd";
        public long TokenAmount { get; set; }
        public decimal MinorUnitsPerToken { get; set; } = 1m;
        public bool AllowPromotionCodes { get; set; }
        public IDictionary<string, string> Metadata { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    public sealed class SocketJackServerPayoutRequest {
        public string ServerId { get; set; } = "";
        public string ServerOwnerId { get; set; } = "";
        public string ConnectedAccountId { get; set; } = "";
        public string PayoutRequestId { get; set; } = "";
        public string Currency { get; set; } = "usd";
        public string IdempotencyKey { get; set; } = "";
        public string TransferGroup { get; set; } = "";
        public long TokensSpent { get; set; }
        public decimal MinorUnitsPerToken { get; set; } = 1m;
        public IDictionary<string, string> Metadata { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    public sealed class SocketJackPayoutCalculation {
        public const int SocketJackFeeBasisPoints = 150;

        public long TokensSpent { get; set; }
        public long GrossAmount { get; set; }
        public long SocketJackFeeAmount { get; set; }
        public long ServerOwnerAmount { get; set; }
        public string Currency { get; set; } = "";
    }

    public sealed class SocketJackPayoutResult {
        public string TransferId { get; set; } = "";
        public string DestinationAccountId { get; set; } = "";
        public string Currency { get; set; } = "";
        public long GrossAmount { get; set; }
        public long SocketJackFeeAmount { get; set; }
        public long ServerOwnerAmount { get; set; }
        public string TransferGroup { get; set; } = "";
    }

    public sealed class SocketJackConnectedPayoutRequest {
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

    public sealed class SocketJackConnectedTransferRequest {
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

    public sealed class SocketJackConnectedTransferResult {
        public string TransferId { get; set; } = "";
        public string ConnectedAccountId { get; set; } = "";
        public string Currency { get; set; } = "";
        public string TransferGroup { get; set; } = "";
        public long Amount { get; set; }
        public long GrossAmount { get; set; }
        public long SocketJackFeeAmount { get; set; }
    }

    public sealed class SocketJackConnectedPayoutResult {
        public string PayoutId { get; set; } = "";
        public string ConnectedAccountId { get; set; } = "";
        public string Currency { get; set; } = "";
        public string Status { get; set; } = "";
        public string Method { get; set; } = "";
        public string ArrivalDateUtc { get; set; } = "";
        public long Amount { get; set; }
    }

    public sealed class SocketJackPayoutScheduleRequest {
        public string ConnectedAccountId { get; set; } = "";
        public string IdempotencyKey { get; set; } = "";
        public string Interval { get; set; } = "weekly";
        public string WeeklyPayoutDay { get; set; } = "friday";
    }

    public sealed class SocketJackPayoutScheduleResult {
        public string ConnectedAccountId { get; set; } = "";
        public string Interval { get; set; } = "";
        public string WeeklyPayoutDay { get; set; } = "";
    }

    public sealed class StripeConnectedAccountOnboardingRequest {
        public string ConnectedAccountId { get; set; } = "";
        public string RefreshUrl { get; set; } = "";
        public string ReturnUrl { get; set; } = "";
        public string IdempotencyKey { get; set; } = "";
    }

    public sealed class StripeConnectedAccountOnboardingResult {
        public string Url { get; set; } = "";
        public DateTime? ExpiresAt { get; set; }
    }

    /// <summary>
    /// Backend Stripe payment service for Checkout Sessions and signed webhook events.
    /// </summary>
    public sealed class StripePaymentService {
        private const string TokenPurchaseKind = "socketjack.token_purchase";
        private const string ServerOwnerPayoutKind = "socketjack.server_owner_payout";
        private readonly StripePaymentServiceOptions _options;
        private readonly SessionService _checkoutSessions;
        private readonly TransferService _transfers;
        private readonly PayoutService _payouts;
        private readonly BalanceSettingsService _balanceSettings;
        private readonly AccountLinkService _accountLinks;

        public StripePaymentService(StripePaymentServiceOptions options) {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _checkoutSessions = new SessionService();
            _transfers = new TransferService();
            _payouts = new PayoutService();
            _balanceSettings = new BalanceSettingsService();
            _accountLinks = new AccountLinkService();
        }

        public async Task<StripeCheckoutSessionResult> CreateCheckoutSessionAsync(StripeCheckoutSessionRequest request, CancellationToken cancellationToken = default) {
            SessionCreateOptions createOptions = BuildCheckoutSessionOptions(request);
            Session session = await _checkoutSessions.CreateAsync(createOptions, BuildRequestOptions(request), cancellationToken).ConfigureAwait(false);

            return ToResult(session);
        }

        public async Task<StripeCheckoutSessionResult> RetrieveCheckoutSessionAsync(string sessionId, string stripeAccount = "", CancellationToken cancellationToken = default) {
            if (string.IsNullOrWhiteSpace(sessionId))
                throw new ArgumentException("A Stripe Checkout Session id is required.", nameof(sessionId));

            var request = new StripeCheckoutSessionRequest {
                StripeAccount = stripeAccount ?? ""
            };
            Session session = await _checkoutSessions.GetAsync(sessionId.Trim(), null, BuildRequestOptions(request), cancellationToken).ConfigureAwait(false);
            return ToResult(session);
        }

        public Task<StripeCheckoutSessionResult> CreateTokenPurchaseCheckoutSessionAsync(SocketJackTokenPurchaseRequest request, CancellationToken cancellationToken = default) {
            if (request == null)
                throw new ArgumentNullException(nameof(request));
            if (request.TokenAmount <= 0)
                throw new InvalidOperationException("SocketJack token purchases require a positive token amount.");

            long purchaseAmount = CalculateMinorUnits(request.TokenAmount, request.MinorUnitsPerToken);
            if (purchaseAmount <= 0)
                throw new InvalidOperationException("SocketJack token purchases must resolve to a positive payment amount.");

            var checkout = new StripeCheckoutSessionRequest {
                SuccessUrl = request.SuccessUrl,
                CancelUrl = request.CancelUrl,
                CustomerId = request.CustomerId,
                CustomerEmail = request.CustomerEmail,
                ClientReferenceId = request.UserId,
                IdempotencyKey = request.IdempotencyKey,
                AllowPromotionCodes = request.AllowPromotionCodes
            };

            checkout.LineItems.Add(new StripeCheckoutLineItem {
                ProductName = "SocketJack Tokens",
                Description = request.TokenAmount.ToString() + " SocketJack Tokens",
                Currency = request.Currency,
                UnitAmount = purchaseAmount,
                Quantity = 1
            });

            CopyInto(checkout.Metadata, request.Metadata);
            checkout.Metadata["kind"] = TokenPurchaseKind;
            checkout.Metadata["socketjack_user_id"] = Normalize(request.UserId);
            checkout.Metadata["socketjack_tokens"] = request.TokenAmount.ToString();
            checkout.Metadata["socketjack_minor_units_per_token"] = request.MinorUnitsPerToken.ToString(System.Globalization.CultureInfo.InvariantCulture);

            return CreateCheckoutSessionAsync(checkout, cancellationToken);
        }

        public SocketJackPayoutCalculation CalculateServerOwnerPayout(SocketJackServerPayoutRequest request) {
            if (request == null)
                throw new ArgumentNullException(nameof(request));
            if (request.TokensSpent <= 0)
                throw new InvalidOperationException("Server owner payouts require a positive spent token amount.");

            long grossAmount = CalculateMinorUnits(request.TokensSpent, request.MinorUnitsPerToken);
            long feeAmount = RoundMinorUnits(grossAmount * (SocketJackPayoutCalculation.SocketJackFeeBasisPoints / 10000m));
            long ownerAmount = grossAmount - feeAmount;
            if (ownerAmount <= 0)
                throw new InvalidOperationException("Server owner payout amount must be positive after SocketJack fees.");

            return new SocketJackPayoutCalculation {
                TokensSpent = request.TokensSpent,
                GrossAmount = grossAmount,
                SocketJackFeeAmount = feeAmount,
                ServerOwnerAmount = ownerAmount,
                Currency = NormalizeCurrency(request.Currency)
            };
        }

        public async Task<SocketJackConnectedTransferResult> CreateConnectedAccountTransferAsync(SocketJackConnectedTransferRequest request, CancellationToken cancellationToken = default) {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            string connectedAccountId = Normalize(request.ConnectedAccountId);
            if (connectedAccountId.Length == 0)
                throw new InvalidOperationException("A connected Stripe account id is required before transferring funds.");
            if (request.Amount <= 0)
                throw new InvalidOperationException("Connected account transfer amount must be positive.");

            var metadata = CopyMetadata(request.Metadata);
            metadata["kind"] = ServerOwnerPayoutKind;
            metadata["socketjack_payout_request_id"] = Normalize(request.PayoutRequestId);
            metadata["socketjack_fee_basis_points"] = SocketJackPayoutCalculation.SocketJackFeeBasisPoints.ToString();
            metadata["socketjack_fee_amount"] = Math.Max(0, request.SocketJackFeeAmount).ToString();
            metadata["socketjack_gross_amount"] = Math.Max(0, request.GrossAmount).ToString();

            var options = new TransferCreateOptions {
                Amount = request.Amount,
                Currency = NormalizeCurrency(request.Currency),
                Destination = connectedAccountId,
                Description = "SocketJack server owner payout",
                Metadata = metadata,
                TransferGroup = EmptyToNull(request.TransferGroup)
            };

            Transfer transfer = await _transfers.CreateAsync(options, BuildConnectedTransferRequestOptions(request), cancellationToken).ConfigureAwait(false);
            return new SocketJackConnectedTransferResult {
                TransferId = transfer.Id ?? "",
                ConnectedAccountId = connectedAccountId,
                Currency = transfer.Currency ?? options.Currency,
                Amount = transfer.Amount,
                GrossAmount = Math.Max(0, request.GrossAmount),
                SocketJackFeeAmount = Math.Max(0, request.SocketJackFeeAmount),
                TransferGroup = transfer.TransferGroup ?? Normalize(request.TransferGroup)
            };
        }

        public async Task<SocketJackConnectedPayoutResult> CreateConnectedAccountPayoutAsync(SocketJackConnectedPayoutRequest request, CancellationToken cancellationToken = default) {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            string connectedAccountId = Normalize(request.ConnectedAccountId);
            if (connectedAccountId.Length == 0)
                throw new InvalidOperationException("A connected Stripe account id is required before creating a payout.");
            if (request.Amount <= 0)
                throw new InvalidOperationException("Connected account payout amount must be positive.");

            string method = NormalizePayoutMethod(request.Method);
            var metadata = CopyMetadata(request.Metadata);
            metadata["kind"] = "socketjack.connected_account_payout";
            metadata["socketjack_payout_request_id"] = Normalize(request.PayoutRequestId);
            metadata["socketjack_payout_method"] = method;

            var options = new PayoutCreateOptions {
                Amount = request.Amount,
                Currency = NormalizeCurrency(request.Currency),
                Method = method,
                Destination = EmptyToNull(request.Destination),
                StatementDescriptor = EmptyToNull(request.StatementDescriptor),
                Metadata = metadata
            };

            var payout = await _payouts.CreateAsync(options, BuildConnectedAccountRequestOptions(connectedAccountId, request.IdempotencyKey), cancellationToken).ConfigureAwait(false);
            return new SocketJackConnectedPayoutResult {
                PayoutId = payout.Id ?? "",
                ConnectedAccountId = connectedAccountId,
                Currency = payout.Currency ?? options.Currency,
                Status = payout.Status ?? "",
                Method = payout.Method ?? method,
                Amount = payout.Amount,
                ArrivalDateUtc = payout.ArrivalDate == default(DateTime) ? "" : payout.ArrivalDate.ToUniversalTime().ToString("O")
            };
        }

        public async Task<SocketJackPayoutScheduleResult> ConfigureWeeklyPayoutScheduleAsync(SocketJackPayoutScheduleRequest request, CancellationToken cancellationToken = default) {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            string connectedAccountId = Normalize(request.ConnectedAccountId);
            if (connectedAccountId.Length == 0)
                throw new InvalidOperationException("A connected Stripe account id is required before configuring weekly payouts.");

            string interval = NormalizePayoutScheduleInterval(request.Interval);
            string day = NormalizeWeeklyPayoutDay(request.WeeklyPayoutDay);
            var options = new BalanceSettingsUpdateOptions {
                Payments = new BalanceSettingsPaymentsOptions {
                    Payouts = new BalanceSettingsPaymentsPayoutsOptions {
                        Schedule = new BalanceSettingsPaymentsPayoutsScheduleOptions {
                            Interval = interval,
                            WeeklyPayoutDays = interval == "weekly" ? new List<string> { day } : null
                        }
                    }
                }
            };

            await _balanceSettings.UpdateAsync(options, BuildConnectedAccountRequestOptions(connectedAccountId, request.IdempotencyKey), cancellationToken).ConfigureAwait(false);
            return new SocketJackPayoutScheduleResult {
                ConnectedAccountId = connectedAccountId,
                Interval = interval,
                WeeklyPayoutDay = interval == "weekly" ? day : ""
            };
        }

        public async Task<SocketJackPayoutResult> RequestServerOwnerPayoutAsync(SocketJackServerPayoutRequest request, CancellationToken cancellationToken = default) {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            string connectedAccountId = Normalize(request.ConnectedAccountId);
            if (connectedAccountId.Length == 0)
                throw new InvalidOperationException("A connected Stripe account id is required before requesting payout.");

            SocketJackPayoutCalculation calculation = CalculateServerOwnerPayout(request);
            var metadata = CopyMetadata(request.Metadata);
            metadata["kind"] = ServerOwnerPayoutKind;
            metadata["socketjack_server_id"] = Normalize(request.ServerId);
            metadata["socketjack_server_owner_id"] = Normalize(request.ServerOwnerId);
            metadata["socketjack_payout_request_id"] = Normalize(request.PayoutRequestId);
            metadata["socketjack_tokens_spent"] = request.TokensSpent.ToString();
            metadata["socketjack_fee_basis_points"] = SocketJackPayoutCalculation.SocketJackFeeBasisPoints.ToString();
            metadata["socketjack_fee_amount"] = calculation.SocketJackFeeAmount.ToString();

            var options = new TransferCreateOptions {
                Amount = calculation.ServerOwnerAmount,
                Currency = calculation.Currency,
                Destination = connectedAccountId,
                Description = "SocketJack server owner payout",
                Metadata = metadata,
                TransferGroup = EmptyToNull(request.TransferGroup)
            };

            Transfer transfer = await _transfers.CreateAsync(options, BuildTransferRequestOptions(request), cancellationToken).ConfigureAwait(false);
            return new SocketJackPayoutResult {
                TransferId = transfer.Id ?? "",
                DestinationAccountId = connectedAccountId,
                Currency = transfer.Currency ?? calculation.Currency,
                GrossAmount = calculation.GrossAmount,
                SocketJackFeeAmount = calculation.SocketJackFeeAmount,
                ServerOwnerAmount = transfer.Amount,
                TransferGroup = transfer.TransferGroup ?? Normalize(request.TransferGroup)
            };
        }

        public async Task<StripeConnectedAccountOnboardingResult> CreateConnectedAccountOnboardingLinkAsync(StripeConnectedAccountOnboardingRequest request, CancellationToken cancellationToken = default) {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            string connectedAccountId = Normalize(request.ConnectedAccountId);
            string refreshUrl = Normalize(request.RefreshUrl);
            string returnUrl = Normalize(request.ReturnUrl);
            if (connectedAccountId.Length == 0)
                throw new InvalidOperationException("A connected Stripe account id is required for onboarding.");
            if (refreshUrl.Length == 0)
                throw new InvalidOperationException("A refresh URL is required for Stripe account onboarding.");
            if (returnUrl.Length == 0)
                throw new InvalidOperationException("A return URL is required for Stripe account onboarding.");

            var options = new AccountLinkCreateOptions {
                Account = connectedAccountId,
                RefreshUrl = refreshUrl,
                ReturnUrl = returnUrl,
                Type = "account_onboarding"
            };

            AccountLink link = await _accountLinks.CreateAsync(options, BuildAccountLinkRequestOptions(request), cancellationToken).ConfigureAwait(false);
            return new StripeConnectedAccountOnboardingResult {
                Url = link.Url ?? "",
                ExpiresAt = link.ExpiresAt
            };
        }

        public static StripeCheckoutSessionResult FromCheckoutSession(Session session) {
            return ToResult(session);
        }

        private static StripeCheckoutSessionResult ToResult(Session session) {
            var result = new StripeCheckoutSessionResult {
                Id = session.Id ?? "",
                Url = session.Url ?? "",
                Status = session.Status ?? "",
                PaymentStatus = session.PaymentStatus ?? "",
                PaymentIntentId = session.PaymentIntentId ?? "",
                CustomerId = session.CustomerId ?? "",
                ClientReferenceId = session.ClientReferenceId ?? "",
                Currency = session.Currency ?? "",
                AmountTotal = session.AmountTotal
            };
            CopyInto(result.Metadata, session.Metadata);
            return result;
        }

        public SessionCreateOptions BuildCheckoutSessionOptions(StripeCheckoutSessionRequest request) {
            if (request == null)
                throw new ArgumentNullException(nameof(request));
            if (request.LineItems.Count == 0)
                throw new InvalidOperationException("At least one Stripe Checkout line item is required.");

            string successUrl = FirstValue(request.SuccessUrl, _options.SuccessUrl);
            string cancelUrl = FirstValue(request.CancelUrl, _options.CancelUrl);
            if (string.IsNullOrWhiteSpace(successUrl))
                throw new InvalidOperationException("A Stripe Checkout success URL is required.");
            if (string.IsNullOrWhiteSpace(cancelUrl))
                throw new InvalidOperationException("A Stripe Checkout cancel URL is required.");

            var options = new SessionCreateOptions {
                Mode = "payment",
                SuccessUrl = successUrl,
                CancelUrl = cancelUrl,
                LineItems = new List<SessionLineItemOptions>(),
                Metadata = CopyMetadata(request.Metadata)
            };

            string clientReferenceId = Normalize(request.ClientReferenceId);
            if (clientReferenceId.Length > 0)
                options.ClientReferenceId = clientReferenceId;

            string customerId = Normalize(request.CustomerId);
            string customerEmail = Normalize(request.CustomerEmail);
            if (customerId.Length > 0)
                options.Customer = customerId;
            else if (customerEmail.Length > 0)
                options.CustomerEmail = customerEmail;

            if (request.AllowPromotionCodes)
                options.AllowPromotionCodes = true;

            if (request.EnableAutomaticTax) {
                options.AutomaticTax = new SessionAutomaticTaxOptions {
                    Enabled = true
                };
            }

            foreach (StripeCheckoutLineItem item in request.LineItems)
                options.LineItems.Add(BuildLineItemOptions(item));

            return options;
        }

        public Event ConstructWebhookEvent(string json, string signatureHeader, long toleranceSeconds = 300, bool throwOnApiVersionMismatch = true) {
            string webhookSecret = Normalize(_options.WebhookSigningSecret);
            if (webhookSecret.Length == 0)
                throw new InvalidOperationException("A Stripe webhook signing secret is required.");
            if (string.IsNullOrWhiteSpace(json))
                throw new ArgumentException("Webhook JSON payload is required.", nameof(json));
            if (string.IsNullOrWhiteSpace(signatureHeader))
                throw new ArgumentException("Stripe signature header is required.", nameof(signatureHeader));

            return EventUtility.ConstructEvent(json, signatureHeader, webhookSecret, toleranceSeconds, throwOnApiVersionMismatch);
        }

        private RequestOptions BuildRequestOptions(StripeCheckoutSessionRequest request) {
            string secretKey = Normalize(_options.SecretKey);
            if (secretKey.Length == 0)
                throw new InvalidOperationException("A Stripe secret key or restricted key is required.");
            if (secretKey.StartsWith("pk_", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("A Stripe publishable key cannot be used for server-side payment calls.");

            var requestOptions = new RequestOptions {
                ApiKey = secretKey
            };

            if (request != null) {
                string idempotencyKey = Normalize(request.IdempotencyKey);
                string stripeAccount = Normalize(request.StripeAccount);

                if (idempotencyKey.Length > 0)
                    requestOptions.IdempotencyKey = idempotencyKey;
                if (stripeAccount.Length > 0)
                    requestOptions.StripeAccount = stripeAccount;
            }

            return requestOptions;
        }

        private RequestOptions BuildTransferRequestOptions(SocketJackServerPayoutRequest request) {
            var requestOptions = BuildRequestOptions(null);
            string idempotencyKey = Normalize(request?.IdempotencyKey);
            if (idempotencyKey.Length > 0)
                requestOptions.IdempotencyKey = idempotencyKey;

            return requestOptions;
        }

        private RequestOptions BuildAccountLinkRequestOptions(StripeConnectedAccountOnboardingRequest request) {
            var requestOptions = BuildRequestOptions(null);
            string idempotencyKey = Normalize(request?.IdempotencyKey);
            if (idempotencyKey.Length > 0)
                requestOptions.IdempotencyKey = idempotencyKey;

            return requestOptions;
        }

        private RequestOptions BuildConnectedTransferRequestOptions(SocketJackConnectedTransferRequest request) {
            var requestOptions = BuildRequestOptions(null);
            string idempotencyKey = Normalize(request?.IdempotencyKey);
            if (idempotencyKey.Length > 0)
                requestOptions.IdempotencyKey = idempotencyKey;

            return requestOptions;
        }

        private RequestOptions BuildConnectedAccountRequestOptions(string connectedAccountId, string idempotencyKey) {
            var requestOptions = BuildRequestOptions(null);
            connectedAccountId = Normalize(connectedAccountId);
            idempotencyKey = Normalize(idempotencyKey);
            if (connectedAccountId.Length > 0)
                requestOptions.StripeAccount = connectedAccountId;
            if (idempotencyKey.Length > 0)
                requestOptions.IdempotencyKey = idempotencyKey;

            return requestOptions;
        }

        private static SessionLineItemOptions BuildLineItemOptions(StripeCheckoutLineItem item) {
            if (item == null)
                throw new InvalidOperationException("Stripe Checkout line items cannot be null.");

            long quantity = item.Quantity <= 0 ? 1 : item.Quantity;
            string priceId = Normalize(item.PriceId);
            if (priceId.Length > 0) {
                return new SessionLineItemOptions {
                    Price = priceId,
                    Quantity = quantity
                };
            }

            string productId = Normalize(item.ProductId);
            string productName = Normalize(item.ProductName);
            if (productName.Length == 0 && productId.Length == 0)
                throw new InvalidOperationException("A Stripe Checkout line item needs a PriceId, ProductId, or ProductName.");
            if (!item.UnitAmount.HasValue || item.UnitAmount.Value <= 0)
                throw new InvalidOperationException("A Stripe Checkout line item with inline price data needs a positive UnitAmount.");

            var priceData = new SessionLineItemPriceDataOptions {
                Currency = FirstValue(item.Currency, "usd").ToLowerInvariant(),
                UnitAmount = item.UnitAmount.Value
            };
            if (productId.Length > 0) {
                priceData.Product = productId;
            } else {
                priceData.ProductData = new SessionLineItemPriceDataProductDataOptions {
                    Name = productName,
                    Description = EmptyToNull(item.Description),
                    Metadata = CopyMetadata(item.Metadata)
                };
            }

            return new SessionLineItemOptions {
                Quantity = quantity,
                PriceData = priceData
            };
        }

        private static Dictionary<string, string> CopyMetadata(IDictionary<string, string> metadata) {
            var copy = new Dictionary<string, string>(StringComparer.Ordinal);
            if (metadata == null)
                return copy;

            foreach (KeyValuePair<string, string> pair in metadata) {
                string key = Normalize(pair.Key);
                if (key.Length > 0)
                    copy[key] = pair.Value ?? "";
            }

            return copy;
        }

        private static void CopyInto(IDictionary<string, string> target, IDictionary<string, string> source) {
            if (target == null || source == null)
                return;

            foreach (KeyValuePair<string, string> pair in source) {
                string key = Normalize(pair.Key);
                if (key.Length > 0)
                    target[key] = pair.Value ?? "";
            }
        }

        private static long CalculateMinorUnits(long tokenAmount, decimal minorUnitsPerToken) {
            if (minorUnitsPerToken <= 0)
                throw new InvalidOperationException("Token value must be positive.");

            return RoundMinorUnits(tokenAmount * minorUnitsPerToken);
        }

        private static long RoundMinorUnits(decimal value) {
            if (value > long.MaxValue)
                throw new InvalidOperationException("Calculated Stripe amount is too large.");

            return (long)decimal.Round(value, 0, MidpointRounding.AwayFromZero);
        }

        private static string EmptyToNull(string value) {
            string normalized = Normalize(value);
            return normalized.Length == 0 ? null : normalized;
        }

        private static string FirstValue(string first, string second) {
            string normalizedFirst = Normalize(first);
            if (normalizedFirst.Length > 0)
                return normalizedFirst;

            return Normalize(second);
        }

        private static string NormalizeCurrency(string value) {
            string normalized = Normalize(value);
            return normalized.Length == 0 ? "usd" : normalized.ToLowerInvariant();
        }

        private static string NormalizePayoutMethod(string value) {
            string normalized = Normalize(value).ToLowerInvariant();
            if (normalized == "instant")
                return "instant";
            return "standard";
        }

        private static string NormalizePayoutScheduleInterval(string value) {
            string normalized = Normalize(value).ToLowerInvariant();
            if (normalized == "manual" || normalized == "daily" || normalized == "monthly")
                return normalized;
            return "weekly";
        }

        private static string NormalizeWeeklyPayoutDay(string value) {
            string normalized = Normalize(value).ToLowerInvariant();
            string[] validDays = { "monday", "tuesday", "wednesday", "thursday", "friday" };
            return validDays.Contains(normalized) ? normalized : "friday";
        }

        private static string Normalize(string value) {
            return (value ?? "").Trim();
        }
    }
}
