using Microsoft.Extensions.Logging;
using Solnet.Programs;
using Solnet.Rpc;
using Solnet.Rpc.Builders;
using Solnet.Rpc.Core.Http;
using Solnet.Rpc.Core.Sockets;
using Solnet.Rpc.Messages;
using Solnet.Rpc.Models;
using Solnet.Rpc.Types;
using Solnet.Rpc.Utilities;
using Solnet.Serum.Models;
using Solnet.Wallet;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Solnet.Serum
{
    /// <summary> 
    /// A manager class for Serum <see cref="Market"/>s that returns user friendly data.
    /// </summary>
    public class MarketManager : IMarketManager
    {
        /// <summary>
        /// The owner's <see cref="PublicKey"/>.
        /// </summary>
        private readonly PublicKey _ownerAccount;

        /// <summary>
        /// The <see cref="Market"/>'s <see cref="PublicKey"/>.
        /// </summary>
        private readonly PublicKey _marketAccount;

        /// <summary>
        /// The <see cref="PublicKey"/> of the SRM token account to use for fee discount..
        /// </summary>
        private readonly PublicKey _srmAccount;

        /// <summary>
        /// A delegate method type used to request the user to sign transactions crafted by the <see cref="MarketManager"/>.
        /// </summary>
        private readonly RequestSignature _requestSignature;

        /// <summary>
        /// The logger instance.
        /// </summary>
        private readonly ILogger _logger;

        /// <summary>
        /// The serum client instance to use to fetch and decode Serum data.
        /// </summary>
        private readonly ISerumClient _serumClient;

        /// <summary>
        /// Signers that may be necessary for transactions in which it is needed to create either an ATA or an OOA.
        /// </summary>
        private readonly IList<Account> _signers;

        /// <summary>
        /// The serializer options to parse request result errors.
        /// </summary>
        private readonly JsonSerializerOptions _jsonSerializerOptions;

        /// <summary>
        /// The <see cref="OpenOrdersAccount"/>'s <see cref="PublicKey"/>.
        /// </summary>
        private PublicKey _openOrdersAccount;

        /// <summary>
        /// The decimals of the base token for the current <see cref="Market"/>.
        /// </summary>
        private byte _baseDecimals;

        /// <summary>
        /// The decimals of the quote token for the current <see cref="Market"/>.
        /// </summary>
        private byte _quoteDecimals;

        /// <summary>
        /// The subscription object corresponding to the <see cref="Market"/>'s <see cref="EventQueue"/>.
        /// </summary>
        private Subscription _eventQueueSubscription;

        /// <summary>
        /// The subscription object corresponding to the <see cref="Market"/>'s <see cref="OpenOrdersAccount"/>.
        /// </summary>
        private Subscription _openOrdersSubscription;

        /// <summary>
        /// The subscription object corresponding to the <see cref="Market"/>'s bid <see cref="OrderBookSide"/>.
        /// </summary>
        private Subscription _bidSideSubscription;

        /// <summary>
        /// The subscription object corresponding to the <see cref="Market"/>'s ask <see cref="OrderBookSide"/>.
        /// </summary>
        private Subscription _askSideSubscription;

        /// <summary>
        /// The bid side of the <see cref="OrderBook"/>.
        /// </summary>
        private OrderBookSide _bidSide;

        /// <summary>
        /// The ask side of the <see cref="OrderBook"/>.
        /// </summary>
        private OrderBookSide _askSide;

        /// <summary>
        /// The public key of the base token account.
        /// </summary>
        private PublicKey _baseTokenAccount;

        /// <summary>
        /// The public key of the quote token account.
        /// </summary>
        private PublicKey _quoteTokenAccount;

        /// <summary>
        /// Initialize the <see cref="Market"/> manager with the given market <see cref="PublicKey"/>.
        /// </summary>
        /// <param name="marketAccount">The <see cref="PublicKey"/> of the <see cref="Market"/>.</param>
        /// <param name="ownerAccount">The <see cref="PublicKey"/> of the owner account.</param>
        /// <param name="signatureMethod">A delegate method used to request a signature for transactions crafted by the <see cref="MarketManager"/> which will submit, cancel orders, or settle funds.</param>
        /// <param name="srmAccount">The <see cref="PublicKey"/> of the serum account to use for fee discount, not used when not provided.</param>
        /// <param name="url">The cluster to use when not passing in a serum client instance.</param>
        /// <param name="logger">The logger instance to use.</param>
        /// <param name="serumClient">The serum client instance to use.</param>
        internal MarketManager(PublicKey marketAccount, PublicKey ownerAccount = null,
            RequestSignature signatureMethod = null,
            PublicKey srmAccount = null, string url = null, ILogger logger = null, ISerumClient serumClient = default)
        {
            _marketAccount = marketAccount;
            _ownerAccount = ownerAccount;
            _srmAccount = srmAccount;
            _requestSignature = signatureMethod;
            _signers = new List<Account>();
            _logger = logger;
            _jsonSerializerOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
            };

            if (url != null)
            {
                _serumClient = serumClient ?? ClientFactory.GetClient(url, logger);
            }
            else
            {
                _serumClient = serumClient ?? ClientFactory.GetClient(Cluster.MainNet, logger);
            }
        }

        #region Manager Setup

        /// <summary>
        /// Get the decimals for the quote token.
        /// </summary>
        private async Task GetQuoteDecimalsAsync()
        {
            _quoteDecimals = await GetTokenDecimalsAsync(Market.QuoteMint);
            _logger?.Log(LogLevel.Information, $"Decimals for Quote Token: {_quoteDecimals}");
        }

        /// <summary>
        /// Get the decimals for the base token.
        /// </summary>
        private async Task GetBaseDecimalsAsync()
        {
            _baseDecimals = await GetTokenDecimalsAsync(Market.BaseMint);
            _logger?.Log(LogLevel.Information, $"Decimals for Base Token: {_baseDecimals}");
        }

        /// <summary>
        /// Get the market data.
        /// </summary>
        private async Task GetMarketAsync()
        {
            Market = await _serumClient.GetMarketAsync(_marketAccount);
            _logger?.Log(LogLevel.Information,
                $"Fetched Market data for: {Market.OwnAddress.Key} ::" +
                $" Base Token: {Market.BaseMint.Key} / Quote Token: {Market.QuoteMint.Key}");
        }

        /// <summary>
        /// Gets the decimals for the given token mint.
        /// </summary>
        /// <param name="tokenMint">The public key of the token mint.</param>
        private async Task<byte> GetTokenDecimalsAsync(PublicKey tokenMint)
        {
            RequestResult<ResponseValue<AccountInfo>> accountInfo = await
                _serumClient.RpcClient.GetAccountInfoAsync(tokenMint);
            return !accountInfo.WasRequestSuccessfullyHandled
                ? (byte)0
                : MarketUtils.DecimalsFromTokenMintData(Convert.FromBase64String(accountInfo.Result.Value.Data[0]));
        }

        /// <summary>
        /// Gets the <see cref="OpenOrdersAccount"/> for the given <see cref="Market"/> and owner address.
        /// </summary>
        private async Task GetOpenOrdersAccountAsync()
        {
            List<MemCmp> filters = new()
            {
                new MemCmp { Offset = 13, Bytes = _marketAccount },
                new MemCmp { Offset = 45, Bytes = _ownerAccount }
            };
            RequestResult<List<AccountKeyPair>> accounts = await
                _serumClient.RpcClient.GetProgramAccountsAsync(SerumProgram.ProgramIdKey,
                    dataSize: OpenOrdersAccount.Layout.SpanLength, memCmpList: filters);

            if (!accounts.WasRequestSuccessfullyHandled) return;

            if (accounts.Result.Count != 0)
            {
                _openOrdersAccount = (PublicKey)accounts.Result[0].PublicKey;
                OpenOrdersAccount =
                    OpenOrdersAccount.Deserialize(Convert.FromBase64String(accounts.Result[0].Account.Data[0]));
                return;
            }

            _logger?.Log(LogLevel.Information,
                $"Could not find open orders account for market {_marketAccount} and owner {_ownerAccount}");
        }

        /// <summary>
        /// Gets the associated token account for the given mint and the owner address,
        /// </summary>
        /// <param name="mint">The <see cref="PublicKey"/> of the token mint</param>
        private async Task<TokenAccount> GetAssociatedTokenAccountAsync(PublicKey mint)
        {
            RequestResult<ResponseValue<List<TokenAccount>>> accounts = await
                _serumClient.RpcClient.GetTokenAccountsByOwnerAsync(_ownerAccount, mint);

            if (!accounts.WasRequestSuccessfullyHandled) return null;

            if (accounts.Result.Value.Count != 0)
                return accounts.Result.Value[0];

            _logger?.Log(LogLevel.Information,
                $"Could not find associated token account for mint {mint} and owner {_ownerAccount}");
            return null;
        }

        #endregion

        #region Data Retrieval

        /// <inheritdoc cref="IMarketManager.InitAsync"/>
        public async Task InitAsync()
        {
            // Get the decoded market data
            await GetMarketAsync();

            // Get decimals for the market's tokens
            await GetBaseDecimalsAsync();
            await GetQuoteDecimalsAsync();

            if (_ownerAccount == null) return;
            // Get the ATAs for both token mints, if they exist
            BaseAccount = await GetAssociatedTokenAccountAsync(Market.BaseMint);
            if (BaseAccount != null) _baseTokenAccount = new PublicKey(BaseAccount.PublicKey);
            QuoteAccount = await GetAssociatedTokenAccountAsync(Market.QuoteMint);
            if (QuoteAccount != null) _quoteTokenAccount = new PublicKey(QuoteAccount.PublicKey);

            // Get the open orders account for this market, if it exists
            await GetOpenOrdersAccountAsync();
        }

        /// <inheritdoc cref="IMarketManager.Init"/>
        public void Init() => InitAsync().Wait();

        /// <inheritdoc cref="IMarketManager.ReloadAsync"/>
        public async Task ReloadAsync()
        {
            // Get the ATAs for both token mints
            BaseAccount = await GetAssociatedTokenAccountAsync(Market.BaseMint);
            _baseTokenAccount = new PublicKey(BaseAccount.PublicKey);
            QuoteAccount = await GetAssociatedTokenAccountAsync(Market.QuoteMint);
            _quoteTokenAccount = new PublicKey(QuoteAccount.PublicKey);

            // Get the open orders account for this market, if it exists
            await GetOpenOrdersAccountAsync();
        }

        /// <inheritdoc cref="IMarketManager.Reload"/>
        public void Reload() => ReloadAsync().Wait();

        /// <inheritdoc cref="IMarketManager.SubscribeTradesAsync"/>
        public async Task SubscribeTradesAsync(Action<IList<TradeEvent>, ulong> action)
        {
            _eventQueueSubscription = await _serumClient.SubscribeEventQueueAsync((_, queue, slot) =>
            {
                List<TradeEvent> tradeEvents =
                    (from evt in queue.Events
                        where evt.Flags.IsFill && evt.NativeQuantityPaid > 0
                        select MarketUtils.ProcessTradeEvent(evt, _baseDecimals, _quoteDecimals)).ToList();
                action(tradeEvents, slot);
            }, Market.EventQueue, Commitment.Confirmed);
        }

        /// <inheritdoc cref="IMarketManager.SubscribeTrades"/>
        public void SubscribeTrades(Action<IList<TradeEvent>, ulong> action) => SubscribeTradesAsync(action).Wait();

        /// <inheritdoc cref="IMarketManager.SubscribeOrderBookAsync"/>
        public async Task SubscribeOrderBookAsync(Action<OrderBook, ulong> action)
        {
            _bidSideSubscription = await _serumClient.SubscribeOrderBookSideAsync((_, orderBookSide, slot) =>
            {
                _bidSide = orderBookSide;
                OrderBook ob = new()
                {
                    Bids = orderBookSide,
                    Asks = _askSide,
                    BaseDecimals = _baseDecimals,
                    QuoteDecimals = _quoteDecimals,
                    BaseLotSize = Market.BaseLotSize,
                    QuoteLotSize = Market.QuoteLotSize,
                };
                action(ob, slot);
            }, Market.Bids, Commitment.Confirmed);

            _askSideSubscription = await _serumClient.SubscribeOrderBookSideAsync((_, orderBookSide, slot) =>
            {
                _askSide = orderBookSide;
                OrderBook ob = new()
                {
                    Bids = _bidSide,
                    Asks = orderBookSide,
                    BaseDecimals = _baseDecimals,
                    QuoteDecimals = _quoteDecimals,
                    BaseLotSize = Market.BaseLotSize,
                    QuoteLotSize = Market.QuoteLotSize,
                };
                action(ob, slot);
            }, Market.Asks, Commitment.Confirmed);
        }

        /// <inheritdoc cref="IMarketManager.SubscribeOrderBook"/>
        public void SubscribeOrderBook(Action<OrderBook, ulong> action) => SubscribeOrderBookAsync(action).Wait();

        /// <inheritdoc cref="IMarketManager.SubscribeOpenOrdersAsync"/>
        public async Task SubscribeOpenOrdersAsync(Action<IList<OpenOrder>, ulong> action)
        {
            _openOrdersSubscription = await _serumClient.SubscribeOpenOrdersAccountAsync((_, account, slot) =>
            {
                OpenOrdersAccount = account;
                action(account.Orders, slot);
            }, _openOrdersAccount);
        }

        /// <inheritdoc cref="IMarketManager.SubscribeOpenOrders"/>
        public void SubscribeOpenOrders(Action<IList<OpenOrder>, ulong> action) =>
            SubscribeOpenOrdersAsync(action).Wait();

        /// <inheritdoc cref="IMarketManager.UnsubscribeTradesAsync"/>
        public async Task UnsubscribeTradesAsync()
            => await _serumClient.UnsubscribeEventQueueAsync(_eventQueueSubscription.Address);

        /// <inheritdoc cref="IMarketManager.UnsubscribeOrderBookAsync"/>
        public async Task UnsubscribeOrderBookAsync()
        {
            await _serumClient.UnsubscribeOrderBookSideAsync(_bidSideSubscription.Address);
            await _serumClient.UnsubscribeOrderBookSideAsync(_askSideSubscription.Address);
        }

        /// <inheritdoc cref="IMarketManager.UnsubscribeOpenOrdersAsync"/>
        public async Task UnsubscribeOpenOrdersAsync()
            => await _serumClient.UnsubscribeOpenOrdersAccountAsync(_openOrdersSubscription.Address);

        #endregion

        #region Order Management

        /// <inheritdoc cref="IMarketManager.NewOrderAsync(Order)"/>
        public async Task<SignatureConfirmation> NewOrderAsync(Order order)
        {
            if (_requestSignature == null)
                throw new Exception("signature request method hasn't been set");

            string blockHash = await GetBlockHash();

            TransactionBuilder txBuilder = new TransactionBuilder()
                .SetRecentBlockHash(blockHash)
                .SetFeePayer(_ownerAccount);

            (PublicKey bAta, bool bWrapped) = GetOrCreateBaseTokenAccountAndWrapSolIfNeeded(txBuilder, order);
            (PublicKey qAta, bool qWrapped) = GetOrCreateQuoteTokenAccountAndWrapSolIfNeeded(txBuilder, order);
            PublicKey ooa = await GetOrCreateOpenOrdersAccount(txBuilder);

            order.ConvertOrderValues(_baseDecimals, _quoteDecimals, Market);

            txBuilder.AddInstruction(SerumProgram.NewOrderV3(
                Market,
                ooa,
                order.Side == Side.Buy ? qAta : bAta,
                _ownerAccount,
                order,
                _srmAccount));

            txBuilder.AddInstruction(SerumProgram.SettleFunds(
                Market,
                ooa,
                _ownerAccount,
                bAta,
                qAta));

            if (bWrapped)
                txBuilder.AddInstruction(TokenProgram.CloseAccount(
                    bAta,
                    _ownerAccount,
                    _ownerAccount,
                    TokenProgram.ProgramIdKey));

            if (qWrapped)
                txBuilder.AddInstruction(TokenProgram.CloseAccount(
                    qAta,
                    _ownerAccount,
                    _ownerAccount,
                    TokenProgram.ProgramIdKey));

            byte[] txBytes = txBuilder.CompileMessage();

            byte[] signatureBytes = _requestSignature(txBytes);

            List<byte[]> signatures = new() { signatureBytes };
            signatures.AddRange(_signers.Select(signer => signer.Sign(txBytes)));
            _signers.Clear();

            Transaction tx = Transaction.Populate(
                Message.Deserialize(txBytes), signatures);

            return await SendTransactionAndSubscribeSignature(tx.Serialize());
        }

        /// <inheritdoc cref="IMarketManager.NewOrder(Order)"/>
        public SignatureConfirmation NewOrder(Order order) => NewOrderAsync(order).Result;

        /// <inheritdoc cref="IMarketManager.NewOrderAsync(Side, OrderType, SelfTradeBehavior, float, float, ulong)"/>
        public async Task<SignatureConfirmation> NewOrderAsync(
            Side side, OrderType type, SelfTradeBehavior selfTradeBehavior, float size, float price,
            ulong clientId = ulong.MaxValue)
        {
            if (_requestSignature == null)
                throw new Exception("signature request method hasn't been set");

            string blockHash = await GetBlockHash();

            Order order = new OrderBuilder()
                .SetPrice(price)
                .SetQuantity(size)
                .SetOrderType(type)
                .SetSide(side)
                .SetClientOrderId(clientId)
                .SetSelfTradeBehavior(selfTradeBehavior)
                .Build();

            order.ConvertOrderValues(_baseDecimals, _quoteDecimals, Market);

            TransactionBuilder txBuilder = new TransactionBuilder()
                .SetRecentBlockHash(blockHash)
                .SetFeePayer(_ownerAccount);

            (PublicKey bAta, bool bWrapped) = GetOrCreateBaseTokenAccountAndWrapSolIfNeeded(txBuilder, order);
            (PublicKey qAta, bool qWrapped) = GetOrCreateQuoteTokenAccountAndWrapSolIfNeeded(txBuilder, order);
            PublicKey ooa = await GetOrCreateOpenOrdersAccount(txBuilder);

            txBuilder.AddInstruction(SerumProgram.NewOrderV3(
                    Market,
                    ooa,
                    order.Side == Side.Buy ? qAta : bAta,
                    _ownerAccount,
                    order,
                    _srmAccount));

            txBuilder.AddInstruction(SerumProgram.SettleFunds(
                Market,
                ooa,
                _ownerAccount,
                bAta,
                qAta));

            if (bWrapped)
                txBuilder.AddInstruction(TokenProgram.CloseAccount(
                    bAta,
                    _ownerAccount,
                    _ownerAccount,
                    TokenProgram.ProgramIdKey));

            if (qWrapped)
                txBuilder.AddInstruction(TokenProgram.CloseAccount(
                        qAta,
                        _ownerAccount,
                        _ownerAccount,
                        TokenProgram.ProgramIdKey));

            byte[] txBytes = txBuilder.CompileMessage();

            byte[] signatureBytes = _requestSignature(txBytes);

            List<byte[]> signatures = new() { signatureBytes };
            signatures.AddRange(_signers.Select(signer => signer.Sign(txBytes)));
            _signers.Clear();

            Transaction tx = Transaction.Populate(
                Message.Deserialize(txBytes), signatures);

            return await SendTransactionAndSubscribeSignature(tx.Serialize());
        }

        /// <inheritdoc cref="IMarketManager.NewOrder(Side, OrderType, SelfTradeBehavior, float, float, ulong)"/>
        public SignatureConfirmation NewOrder(
            Side side, OrderType type, SelfTradeBehavior selfTradeBehavior, float size, float price,
            ulong clientId = ulong.MaxValue) =>
            NewOrderAsync(side, type, selfTradeBehavior, size, price, clientId).Result;

        /// <inheritdoc cref="IMarketManager.CancelOrderAsync(BigInteger)"/>
        public async Task<SignatureConfirmation> CancelOrderAsync(BigInteger orderId)
        {
            if (_requestSignature == null)
                throw new Exception("signature request method hasn't been set");

            string blockHash = await GetBlockHash();

            OpenOrder openOrder = OpenOrders.FirstOrDefault(order => order.OrderId.Equals(orderId));

            if (openOrder == null)
                throw new Exception("could not find open order for given order id");

            TransactionBuilder txBuilder = new TransactionBuilder()
                .SetRecentBlockHash(blockHash)
                .SetFeePayer(_ownerAccount);

            (PublicKey bAta, bool bWrapped) = GetOrCreateBaseTokenAccountAndWrapSolIfNeeded(txBuilder, isCancelOrder: true);
            (PublicKey qAta, bool qWrapped) = GetOrCreateQuoteTokenAccountAndWrapSolIfNeeded(txBuilder, isCancelOrder: true);

            txBuilder.AddInstruction(SerumProgram.CancelOrderV2(
                        Market,
                        _openOrdersAccount,
                        _ownerAccount,
                        openOrder.IsBid ? Side.Buy : Side.Sell,
                        openOrder.OrderId))
                .AddInstruction(SerumProgram.SettleFunds(
                    Market,
                    _openOrdersAccount,
                    _ownerAccount,
                    bWrapped ? bAta : new PublicKey(BaseAccount.PublicKey),
                    qWrapped ? qAta : new PublicKey(QuoteAccount.PublicKey))
                );

            if (bWrapped)
                txBuilder.AddInstruction(TokenProgram.CloseAccount(
                    bAta,
                    _ownerAccount,
                    _ownerAccount,
                    TokenProgram.ProgramIdKey));

            if (qWrapped)
                txBuilder.AddInstruction(TokenProgram.CloseAccount(
                    qAta,
                    _ownerAccount,
                    _ownerAccount,
                    TokenProgram.ProgramIdKey));

            byte[] txBytes = txBuilder.CompileMessage();

            byte[] signatureBytes = _requestSignature(txBytes);

            List<byte[]> signatures = new() { signatureBytes };
            signatures.AddRange(_signers.Select(signer => signer.Sign(txBytes)));
            _signers.Clear();

            Transaction tx = Transaction.Populate(
                Message.Deserialize(txBytes), signatures);
            return await SendTransactionAndSubscribeSignature(tx.Serialize());
        }

        /// <inheritdoc cref="IMarketManager.CancelOrder(BigInteger)"/>
        public SignatureConfirmation CancelOrder(BigInteger orderId) => CancelOrderAsync(orderId).Result;

        /// <inheritdoc cref="IMarketManager.CancelOrderAsync(ulong)"/>
        public async Task<SignatureConfirmation> CancelOrderAsync(ulong clientId)
        {
            if (_requestSignature == null)
                throw new Exception("signature request method hasn't been set");

            string blockHash = await GetBlockHash();

            TransactionBuilder txBuilder = new TransactionBuilder()
                .SetRecentBlockHash(blockHash)
                .SetFeePayer(_ownerAccount);

            (PublicKey bAta, bool bWrapped) = GetOrCreateBaseTokenAccountAndWrapSolIfNeeded(txBuilder, isCancelOrder: true);
            (PublicKey qAta, bool qWrapped) = GetOrCreateQuoteTokenAccountAndWrapSolIfNeeded(txBuilder, isCancelOrder: true);

            txBuilder.AddInstruction(SerumProgram.CancelOrderByClientIdV2(
                        Market,
                        _openOrdersAccount,
                        _ownerAccount, clientId))
                .AddInstruction(SerumProgram.SettleFunds(
                        Market,
                        _openOrdersAccount,
                        _ownerAccount,
                        bWrapped ? bAta : new PublicKey(BaseAccount.PublicKey),
                        qWrapped ? qAta : new PublicKey(QuoteAccount.PublicKey))
                    );

            if (bWrapped)
                txBuilder.AddInstruction(TokenProgram.CloseAccount(
                    bAta,
                    _ownerAccount,
                    _ownerAccount,
                    TokenProgram.ProgramIdKey));

            if (qWrapped)
                txBuilder.AddInstruction(TokenProgram.CloseAccount(
                    qAta,
                    _ownerAccount,
                    _ownerAccount,
                    TokenProgram.ProgramIdKey));

            byte[] txBytes = txBuilder.CompileMessage();

            byte[] signatureBytes = _requestSignature(txBytes);

            List<byte[]> signatures = new() { signatureBytes };
            signatures.AddRange(_signers.Select(signer => signer.Sign(txBytes)));
            _signers.Clear();

            Transaction tx = Transaction.Populate(
                Message.Deserialize(txBytes), signatures);

            return await SendTransactionAndSubscribeSignature(tx.Serialize());
        }

        /// <inheritdoc cref="IMarketManager.CancelOrder(ulong)"/>
        public SignatureConfirmation CancelOrder(ulong clientId) => CancelOrderAsync(clientId).Result;

        /// <inheritdoc cref="IMarketManager.CancelAllOrdersAsync"/>
        public async Task<IList<SignatureConfirmation>> CancelAllOrdersAsync()
        {
            if (_requestSignature == null)
                throw new Exception("signature request method hasn't been set");

            string blockHash = await GetBlockHash();

            return await Task.Run(async () =>
            {
                IList<SignatureConfirmation> signatureConfirmations = new List<SignatureConfirmation>();
                TransactionBuilder txBuilder = new TransactionBuilder()
                    .SetRecentBlockHash(blockHash)
                    .SetFeePayer(_ownerAccount);

                (PublicKey bAta, bool bWrapped) = GetOrCreateBaseTokenAccountAndWrapSolIfNeeded(txBuilder, isCancelOrder: true);
                (PublicKey qAta, bool qWrapped) = GetOrCreateQuoteTokenAccountAndWrapSolIfNeeded(txBuilder, isCancelOrder: true);

                TransactionInstruction settleIx = SerumProgram.SettleFunds(
                    Market,
                    _openOrdersAccount,
                    _ownerAccount,
                    bWrapped ? bAta : new PublicKey(BaseAccount.PublicKey),
                    qWrapped ? qAta : new PublicKey(QuoteAccount.PublicKey));

                for (int i = 0; i < OpenOrders.Count; i++)
                {
                    SignatureConfirmation sigConf = null;

                    TransactionInstruction txInstruction =
                        SerumProgram.CancelOrderV2(
                            Market,
                            _openOrdersAccount,
                            _ownerAccount,
                            OpenOrders[i].IsBid ? Side.Buy : Side.Sell,
                            OpenOrders[i].OrderId);

                    txBuilder.AddInstruction(txInstruction);
                    byte[] txBytes = txBuilder.CompileMessage();

                    if (txBytes.Length < 850 && i != OpenOrders.Count - 1) continue;

                    txBuilder.AddInstruction(settleIx);

                    if (i == OpenOrders.Count - 1 && bWrapped)
                        txBuilder.AddInstruction(TokenProgram.CloseAccount(
                            bAta,
                            _ownerAccount,
                            _ownerAccount,
                            TokenProgram.ProgramIdKey));

                    if (i == OpenOrders.Count - 1 && qWrapped)
                        txBuilder.AddInstruction(TokenProgram.CloseAccount(
                            qAta,
                            _ownerAccount,
                            _ownerAccount,
                            TokenProgram.ProgramIdKey));


                    txBytes = txBuilder.CompileMessage();

                    byte[] signatureBytes = _requestSignature(txBytes);

                    List<byte[]> signatures = new() { signatureBytes };
                    signatures.AddRange(_signers.Select(signer => signer.Sign(txBytes)));
                    _signers.Clear();

                    Transaction tx = Transaction.Populate(
                        Message.Deserialize(txBytes), signatures);

                    if (signatureBytes != null) sigConf = await SendTransactionAndSubscribeSignature(tx.Serialize());
                    if (sigConf != null)
                    {
                        signatureConfirmations.Add(sigConf);
                        if (sigConf.SimulationLogs != null) break;
                    }

                    blockHash = await GetBlockHash();
                    txBuilder = new TransactionBuilder()
                        .SetRecentBlockHash(blockHash)
                        .SetFeePayer(_ownerAccount);
                }

                return signatureConfirmations;
            });
        }

        /// <inheritdoc cref="IMarketManager.CancelAllOrders"/>
        public IList<SignatureConfirmation> CancelAllOrders() => CancelAllOrdersAsync().Result;

        /// <inheritdoc cref="IMarketManager.SettleFundsAsync"/>
        public async Task<SignatureConfirmation> SettleFundsAsync(PublicKey referrer = null)
        {
            if (_requestSignature == null)
                throw new Exception("signature request method hasn't been set");

            string blockHash = await GetBlockHash();

            TransactionBuilder txBuilder = new TransactionBuilder()
                .SetRecentBlockHash(blockHash)
                .SetFeePayer(_ownerAccount);

            (PublicKey bAta, bool bWrapped) = GetOrCreateBaseTokenAccountAndWrapSolIfNeeded(txBuilder, isCancelOrder: true);
            (PublicKey qAta, bool qWrapped) = GetOrCreateQuoteTokenAccountAndWrapSolIfNeeded(txBuilder, isCancelOrder: true);

            txBuilder.AddInstruction(SerumProgram.SettleFunds(
                        Market,
                        _openOrdersAccount,
                        _ownerAccount,
                        bWrapped ? bAta : new PublicKey(BaseAccount.PublicKey),
                        qWrapped ? qAta : new PublicKey(QuoteAccount.PublicKey),
                        referrer)
                    );

            if (bWrapped)
                txBuilder.AddInstruction(TokenProgram.CloseAccount(
                    bAta,
                    _ownerAccount,
                    _ownerAccount,
                    TokenProgram.ProgramIdKey));

            if (qWrapped)
                txBuilder.AddInstruction(TokenProgram.CloseAccount(
                    qAta,
                    _ownerAccount,
                    _ownerAccount,
                    TokenProgram.ProgramIdKey));

            byte[] txBytes = txBuilder.CompileMessage();

            byte[] signatureBytes = _requestSignature(txBytes);

            List<byte[]> signatures = new() { signatureBytes };
            signatures.AddRange(_signers.Select(signer => signer.Sign(txBytes)));
            _signers.Clear();

            Transaction tx = Transaction.Populate(
                Message.Deserialize(txBytes), signatures);

            return await SendTransactionAndSubscribeSignature(tx.Serialize());
        }

        /// <inheritdoc cref="IMarketManager.SettleFundsAsync"/>
        public SignatureConfirmation SettleFunds(PublicKey referrer = null) => SettleFundsAsync(referrer).Result;

        /// <summary>
        /// Gets or creates an associated token account for the quote token of the market.
        /// If the quote token mint is equivalent to <see cref="MarketUtils.WrappedSolMint"/>, and a token account for it
        /// is not found, this will wrap enough SOL to submit the order.
        /// </summary>
        /// <param name="txBuilder">The transaction builder instance to add the CreateAssociatedTokenAccount instruction to.</param>
        /// <param name="order">The order to calculate the amount of lamports necessary to wrap, if needed.</param>
        /// <returns>The associated token account <see cref="PublicKey"/> of the base token mint.</returns>
        private (PublicKey tokenAccount, bool wrapped) GetOrCreateQuoteTokenAccountAndWrapSolIfNeeded(
            TransactionBuilder txBuilder, Order order = null, bool isCancelOrder = false)
        {
            if (_quoteTokenAccount != null)
                return (_quoteTokenAccount, Market.QuoteMint.Key == MarketUtils.WrappedSolMint);

            if (order == null && !isCancelOrder)
                return (GetOrCreateQuoteTokenAccount(txBuilder), false);

            if (order != null && (order.Side != Side.Buy || Market.QuoteMint.Key != MarketUtils.WrappedSolMint.Key))
                return (GetOrCreateQuoteTokenAccount(txBuilder), false);

            if (isCancelOrder)
                return (WrapSolForOrder(txBuilder), true);

            PublicKey payer = WrapSolForOrder(txBuilder, order);
            return (payer, true);
        }

        /// <summary>
        /// Checks if the open orders account actually exists, in a case it does not, in a case it does not, adds an instruction to the transaction builder
        /// instance to initialize one.
        /// </summary>
        /// <param name="txBuilder">The transaction builder instance to add the CreateAssociatedTokenAccount instruction to.</param>
        /// <returns>The associated token account <see cref="PublicKey"/> of the quote token mint.</returns>
        private PublicKey GetOrCreateQuoteTokenAccount(TransactionBuilder txBuilder)
        {
            if (_quoteTokenAccount != null)
                return _quoteTokenAccount;

            PublicKey associatedTokenAccount =
                AssociatedTokenAccountProgram.DeriveAssociatedTokenAccount(_ownerAccount, Market.QuoteMint);
            TransactionInstruction txInstruction =
                AssociatedTokenAccountProgram.CreateAssociatedTokenAccount(_ownerAccount, _ownerAccount,
                    Market.QuoteMint);
            txBuilder.AddInstruction(txInstruction);
            _quoteTokenAccount = associatedTokenAccount;
            return associatedTokenAccount;
        }

        /// <summary>
        /// Gets or creates an associated token account for the base token of the market.
        /// If the base token mint is equivalent to <see cref="MarketUtils.WrappedSolMint"/>, and a token account for it
        /// is not found, this will wrap enough SOL to submit the order.
        /// </summary>
        /// <param name="txBuilder">The transaction builder instance to add the CreateAssociatedTokenAccount instruction to.</param>
        /// <param name="order">The order to calculate the amount of lamports necessary to wrap, if needed.</param>
        /// <returns>The associated token account <see cref="PublicKey"/> of the base token mint.</returns>
        private (PublicKey tokenAccount, bool wrapped) GetOrCreateBaseTokenAccountAndWrapSolIfNeeded(
            TransactionBuilder txBuilder, Order order = null, bool isCancelOrder = false)
        {
            if (_baseTokenAccount != null)
                return (_baseTokenAccount, Market.BaseMint.Key == MarketUtils.WrappedSolMint);

            if (order == null && !isCancelOrder)
                return (GetOrCreateBaseTokenAccount(txBuilder), false);

            if (order != null && (order.Side != Side.Sell || Market.BaseMint.Key != MarketUtils.WrappedSolMint.Key))
                return (GetOrCreateBaseTokenAccount(txBuilder), false);

            if (isCancelOrder)
                return (WrapSolForOrder(txBuilder), true);

            PublicKey payer = WrapSolForOrder(txBuilder, order);
            return (payer, true);
        }

        /// <summary>
        /// Checks if the base token associated token account actually exists, in a case it does not, adds an instruction to the transaction builder
        /// instance to initialize one.
        /// </summary>
        /// <param name="txBuilder">The transaction builder instance to add the CreateAssociatedTokenAccount instruction to.</param>
        /// <returns>The associated token account <see cref="PublicKey"/> of the base token mint.</returns>
        private PublicKey GetOrCreateBaseTokenAccount(TransactionBuilder txBuilder)
        {
            PublicKey associatedTokenAccount =
                AssociatedTokenAccountProgram.DeriveAssociatedTokenAccount(_ownerAccount, Market.BaseMint);
            TransactionInstruction txInstruction =
                AssociatedTokenAccountProgram.CreateAssociatedTokenAccount(_ownerAccount, _ownerAccount,
                    Market.BaseMint);
            txBuilder.AddInstruction(txInstruction);
            _baseTokenAccount = associatedTokenAccount;

            return associatedTokenAccount;
        }

        /// <summary>
        /// Creates an account and wraps enough lamports to submit the trade.
        /// </summary>
        /// <param name="txBuilder">The transaction builder instance to add the CreateAssociatedTokenAccount instruction to.</param>
        /// <param name="order">The order to calculate the amount of lamports necessary to wrap, if needed.</param>
        /// <returns>The <see cref="PublicKey"/> of the created token account.</returns>
        private PublicKey WrapSolForOrder(TransactionBuilder txBuilder, Order order = null)
        {
            Account payer = new();
            ulong lamports = order != null ? MarketUtils.GetMinimumLamportsForWrapping(order.Price, order.Quantity, order.Side,
                OpenOrdersAccount) : 0;
            txBuilder.AddInstruction(SystemProgram.CreateAccount(
                _ownerAccount,
                payer,
                lamports + 10_000_000,
                TokenProgram.TokenAccountDataSize,
                TokenProgram.ProgramIdKey));
            txBuilder.AddInstruction(TokenProgram.InitializeAccount(
                payer,
                MarketUtils.WrappedSolMint,
                _ownerAccount));
            _signers.Add(payer);
            return payer;
        }

        /// <summary>
        /// Checks if the open orders account actually exists, in a case it does not, adds an instruction to the transaction builder
        /// instance to initialize one.
        /// </summary>
        /// <param name="txBuilder">The transaction builder instance to add the InitOpenOrders instruction to.</param>
        /// <returns>The open orders account <see cref="PublicKey"/>.</returns>
        private async Task<PublicKey> GetOrCreateOpenOrdersAccount(TransactionBuilder txBuilder)
        {
            if (_openOrdersAccount != null)
                return _openOrdersAccount;

            RequestResult<ulong> lamports =
                await _serumClient.RpcClient.GetMinimumBalanceForRentExemptionAsync(OpenOrdersAccount.Layout
                    .SpanLength);

            Account account = new();
            TransactionInstruction txInstruction =
                SystemProgram.CreateAccount(
                    _ownerAccount,
                    account,
                    lamports.Result,
                    OpenOrdersAccount.Layout.SpanLength,
                    SerumProgram.ProgramIdKey);
            txBuilder.AddInstruction(txInstruction);
            txInstruction = SerumProgram.InitOpenOrders(
                account,
                _ownerAccount,
                _marketAccount);
            txBuilder.AddInstruction(txInstruction);
            _signers.Add(account);
            _openOrdersAccount = account;
            return account;
        }

        /// <summary>
        /// Submits a transaction to the cluster and subscribes to its confirmation.
        /// </summary>
        /// <param name="transaction">The signed transaction bytes.</param>
        /// <returns>A task which may return a <see cref="SubscriptionState"/>.</returns>
        private async Task<SignatureConfirmation> SendTransactionAndSubscribeSignature(byte[] transaction)
        {
            RequestResult<string> req = await SubmitTransaction(transaction);
            SignatureConfirmation sigConf = new() { Signature = req.Result, Result = req };

            if (req.ServerErrorCode != 0)
            {
                if (req.ErrorData != null)
                {
                    bool exists = req.ErrorData.TryGetValue("data", out object value);
                    if (!exists) return sigConf;
                    string elem = ((JsonElement)value).ToString();
                    if (elem == null) return sigConf;
                    SimulationLogs simulationLogs =
                        JsonSerializer.Deserialize<SimulationLogs>(elem, _jsonSerializerOptions);
                    sigConf.ChangeState(simulationLogs);
                }

                return sigConf;
            }

            SubscriptionState sub = await _serumClient.StreamingRpcClient.SubscribeSignatureAsync(req.Result,
                (state, value) =>
                {
                    sigConf.ChangeState(state, value);
                }, Commitment.Confirmed);

            sigConf.Subscription = sub;

            return sigConf;
        }

        /// <summary>
        /// Attempts to submit a transaction to the cluster.
        /// </summary>
        /// <param name="transaction">The signed transaction bytes.</param>
        /// <returns>A task which may return a <see cref="RequestResult{IEnumerable}"/>.</returns>
        private async Task<RequestResult<string>> SubmitTransaction(byte[] transaction)
        {
            RequestResult<string> req =
                await _serumClient.RpcClient.SendTransactionAsync(transaction, false, Commitment.Confirmed);

            return req;
        }

        /// <summary>
        /// Gets a recent block hash.
        /// </summary>
        /// <returns>A task which may return the block hash.</returns>
        private async Task<string> GetBlockHash()
        {
            RequestResult<ResponseValue<BlockHash>> blockHash =
                await _serumClient.RpcClient.GetRecentBlockHashAsync();

            return blockHash.Result.Value.Blockhash;
        }

        #endregion

        /// <inheritdoc cref="IMarketManager.OpenOrders"/>
        public IList<OpenOrder> OpenOrders => OpenOrdersAccount?.Orders;

        /// <inheritdoc cref="IMarketManager.OpenOrdersAccount"/>
        public OpenOrdersAccount OpenOrdersAccount { get; private set; }

        /// <inheritdoc cref="IMarketManager.OpenOrdersAddress"/>
        public PublicKey OpenOrdersAddress => _openOrdersAccount;

        /// <inheritdoc cref="IMarketManager.BaseAccount"/>
        public TokenAccount BaseAccount { get; private set; }

        /// <inheritdoc cref="IMarketManager.OpenOrdersAddress"/>
        public PublicKey BaseTokenAccountAddress => _baseTokenAccount;

        /// <inheritdoc cref="IMarketManager.QuoteAccount"/>
        public TokenAccount QuoteAccount { get; private set; }

        /// <inheritdoc cref="IMarketManager.OpenOrdersAddress"/>
        public PublicKey QuoteTokenAccountAddress => _quoteTokenAccount;

        /// <inheritdoc cref="IMarketManager.QuoteDecimals"/>
        public byte QuoteDecimals => _quoteDecimals;

        /// <inheritdoc cref="IMarketManager.BaseDecimals"/>
        public byte BaseDecimals => _baseDecimals;

        /// <inheritdoc cref="IMarketManager.Market"/>
        public Market Market { get; private set; }

        /// <summary>
        /// The method type which the callee must provide in order to sign a given message crafted by the <see cref="MarketManager"/>.
        /// </summary>
        public delegate byte[] RequestSignature(ReadOnlySpan<byte> messageData);
    }
}