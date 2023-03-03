﻿/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
*/

using System;
using System.Linq;
using QuantConnect.Configuration;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Interfaces;
using QuantConnect.Logging;
using QuantConnect.Orders;
using QuantConnect.Orders.Fees;
using QuantConnect.Securities;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Threading;
using Quobject.SocketIoClientDotNet.Client;
using Newtonsoft.Json;
using System.Globalization;
using XTSAPI.Interactive;
using XTSAPI.MarketData;
using QuantConnect.XTSBrokerage;
using QuantConnect.Api;
using QuantConnect.Util;
using QuantConnect.Lean.Engine.DataFeeds;
using QuantConnect.Packets;
using XTSAPI;
using Fasterflect;
using System.Data;
using Microsoft.VisualBasic;

namespace QuantConnect.Brokerages.XTS
{
    [BrokerageFactory(typeof(XTSBrokerageFactory))]
    public class XtsBrokerage : Brokerage, IDataQueueHandler
    {
        private readonly object _connectionLock = new();
        private IDataAggregator _aggregator;
        private IAlgorithm _algorithm;
        private ISecurityProvider _securityProvider;
        private Task _fillMonitorTask;
        private readonly AutoResetEvent _fillMonitorResetEvent = new AutoResetEvent(false);
        private Task[] _checkConnectionTask = new Task[2];
        private XTSSymbolMapper _XTSMapper;
        private DataQueueHandlerSubscriptionManager _subscriptionManager;
        private readonly MarketHoursDatabase _mhdb = MarketHoursDatabase.FromDataFolder();
        private readonly CancellationTokenSource _ctsFillMonitor = new CancellationTokenSource();
        private XTSInteractive interactive = null;
        private XTSMarketData marketdata = null;
        private readonly List<long> _subscribeInstrumentTokens = new List<long>();
        private readonly List<long> _unSubscribeInstrumentTokens = new List<long>();
        private readonly ConcurrentDictionary<long, Symbol> _subscriptionsById = new ConcurrentDictionary<long, Symbol>();
        public ConcurrentDictionary<long, Order> CachedOrderIDs = new ConcurrentDictionary<long, Order>(); // A list of currently active orders
        private readonly ConcurrentDictionary<int, decimal> _fills = new ConcurrentDictionary<int, decimal>();
        private string _interactiveApiKey;
        private string _interactiveApiSecret;
        private string _marketApiKey;
        private string _marketApiSecret;
        private string _XTSProductType;
        private string userID;
        private bool _isInitialized;
        private bool socketConnected = false;
        private readonly int _fillMonitorTimeout = Config.GetInt("XTS.FillMonitorTimeout", 500);
        /// <summary>
        /// Returns true if we're currently connected to the broker
        /// </summary>
        public override bool IsConnected => socketConnected;
        



        /// <summary>
        /// Parameterless constructor for brokerage
        /// </summary>
        /// <remarks>This parameterless constructor is required for brokerages implementing <see cref="IDataQueueHandler"/></remarks>
        public XtsBrokerage()
            : this(Composer.Instance.GetPart<IDataAggregator>())
        {
        }

        /// <summary>
        /// Creates a new instance
        /// </summary>
        /// <param name="aggregator">consolidate ticks</param>
        public XtsBrokerage(IDataAggregator aggregator) : base("XTSBrokerage")
        {
            // Useful for some brokerages:

            // Brokerage helper class to lock websocket message stream while executing an action, for example placing an order
            // avoid race condition with placing an order and getting filled events before finished placing
        }

        /// <summary>
        /// Constructor for brokerage
        /// </summary>
        /// <param name="tradingSegment">Trading Segment</param>
        /// <param name="productType">Product Type</param>
        /// <param name="apiKey">api key</param>
        /// <param name="apiSecret">api secret</param>
        /// <param name="algorithm">the algorithm instance is required to retrieve account type</param>
        /// <param name="yob">year of birth</param>
        public XtsBrokerage(string tradingSegment, string productType, string interactiveSecretKey,
            string interactiveapiKey, string marketSecretKey, string marketApiKey, IAlgorithm algorithm, IDataAggregator aggregator)
            : base("XTS")
        {
            Initialize(tradingSegment, productType, interactiveSecretKey, interactiveapiKey, marketSecretKey, marketApiKey, algorithm, aggregator);
        }

        private bool IsExchangeOpen(bool extendedMarketHours)
        {
            var leanSymbol = Symbol.Create("SBIN", SecurityType.Equity, Market.India);
            var securityExchangeHours = _mhdb.GetExchangeHours(Market.India, leanSymbol, SecurityType.Equity);
            var localTime = DateTime.UtcNow.ConvertFromUtc(securityExchangeHours.TimeZone);
            return securityExchangeHours.IsOpen(localTime, extendedMarketHours);
        }


        private void WaitTillConnected()
        {
            while (!IsConnected)
            {
                Thread.Sleep(500);
            }
        }



        private void CheckConnection()
        {
            var timeoutLoop = TimeSpan.FromMinutes(1);
            while (!_ctsFillMonitor.Token.IsCancellationRequested)
            {
                _ctsFillMonitor.Token.WaitHandle.WaitOne(timeoutLoop);

                try
                {
                    // we start trying to reconnect during extended market hours so we are all set for normal hours
                    if (!IsConnected && IsExchangeOpen(extendedMarketHours: true))
                    {
                        socketConnected = false;
                        Log.Trace($"XTSBrokerage.CheckConnection(): resetting connection...",
                            overrideMessageFloodProtection: true);

                        try
                        {
                            Disconnect();
                        }
                        catch
                        {
                            // don't let it stop us from reconnecting
                        }
                        Thread.Sleep(100);

                        // create a new instance
                        Connect();
                    }
                }
                catch (Exception e)
                {
                    Log.Error(e);
                }
            }
        }

        private static string ConvertOrderDirection(OrderDirection orderDirection)
        {
            if (orderDirection == OrderDirection.Buy || orderDirection == OrderDirection.Sell)
            {
                return orderDirection.ToString().ToUpperInvariant();
            }
            throw new NotSupportedException($"XTSBrokerage.ConvertOrderDirection: Unsupported order direction: {orderDirection}");
        }


        private static string ConvertOrderType(Orders.OrderType orderType)
        {
            switch (orderType)
            {
                case Orders.OrderType.Limit:
                    return "LIMIT";

                case Orders.OrderType.Market:
                    return "MARKET";

                case Orders.OrderType.StopMarket:
                    return "STOPMARKET";

                case Orders.OrderType.StopLimit:
                    return "STOPLIMIT";

                default:
                    throw new NotSupportedException($"XTSBrokerage.ConvertOrderType: Unsupported order type: {orderType}");
            }
        }


        private void onConnectionStateEvent(object obj, ConnectionEventArgs args)
        {
            if (args.ConnectionState == ConnectionEvents.connect)
            {
                Log.Trace($"Connecting to {args.ConnectionState}");
            }
            if (args.ConnectionState == ConnectionEvents.disconnect)
            {
                socketConnected = false;
                Log.Trace($"Disconnected {args.ConnectionState}");
            }
            if (args.ConnectionState == ConnectionEvents.joined)
            {
                socketConnected = true;
                Log.Trace($"Joined {args.Data}.... Subscribing");
                Subscribe(GetSubscribed());
            }
            if (args.ConnectionState == ConnectionEvents.success)
            {
                Log.Trace($"success {args.Data}");
            }
            if (args.ConnectionState == ConnectionEvents.warning)
            {
                Log.Trace($"warning {args.Data}");
            }
            if (args.ConnectionState == ConnectionEvents.error)
            {
                Log.Error($"XTSBrokerage.OnError(): Message: {args.Data}");
                if (!IsExchangeOpen(extendedMarketHours: true))
                {
                    //Disconnect socket
                }
            }
            if (args.ConnectionState == ConnectionEvents.logout)
            {
                Log.Error($"XTSBrokerage.OnError(): Message: {args.Data}");
                if (!IsExchangeOpen(extendedMarketHours: true))
                {
                    socketConnected = false;
                }
            }

        }



        private void onInteractiveEvent(object sender, InteractiveEventArgs args)
        {
            //Whenever there is change in order status this event will be generated
            if (args.InteractiveMessageType == InteractiveMessageType.Order)
            {
                OrderResult order = JsonConvert.DeserializeObject<OrderResult>(args.Data.ToString());
                if (order.OrderStatus.ToUpperInvariant() == "REJECTED")
                {
                    if (CachedOrderIDs.TryGetValue(order.AppOrderID, out Order data))
                    {
                        OnOrderEvent(new OrderEvent(data, DateTime.UtcNow, OrderFee.Zero, "XTS Order Event") { Status = OrderStatus.Invalid });
                    }
                }
                if (order.OrderStatus.ToUpperInvariant() == "CANCELLEd")
                {
                    if (CachedOrderIDs.TryGetValue(order.AppOrderID, out Order data))
                    {
                        OnOrderEvent(new OrderEvent(data, DateTime.UtcNow, OrderFee.Zero, "XTS Order Event") { Status = OrderStatus.Canceled });
                    }
                }
                if (order.OrderStatus.ToUpperInvariant() == "NEW" || order.OrderStatus.ToUpperInvariant() == "OPEN")
                {
                    if (CachedOrderIDs.TryGetValue(order.AppOrderID, out Order data))
                    {
                        OnOrderEvent(new OrderEvent(data, DateTime.UtcNow, OrderFee.Zero, "XTS Order Event") { Status = OrderStatus.Submitted });
                    }
                }
                if (order.OrderStatus.ToUpperInvariant() == "PARTIALLYFILLED")
                {
                    if (CachedOrderIDs.TryGetValue(order.AppOrderID, out Order data))
                    {
                        //how to update the traded quantity
                        OnOrderEvent(new OrderEvent(data, DateTime.UtcNow, OrderFee.Zero, "XTS Order Event") { Status = OrderStatus.PartiallyFilled });
                    }
                }
                if (order.OrderStatus.ToUpperInvariant() == "FILLED")
                {
                    if (CachedOrderIDs.TryGetValue(order.AppOrderID, out Order data))
                    {
                        OnOrderEvent(new OrderEvent(data, DateTime.UtcNow, OrderFee.Zero, "XTS Order Event") { Status = OrderStatus.Filled });
                    }
                }

            }
            //When any order gets executed (filled, partially filled), a new trade event will be generated. 
            if (args.InteractiveMessageType == InteractiveMessageType.Trade)
            {

            }
            //If any position change happens then this will be generated
            if (args.InteractiveMessageType == InteractiveMessageType.Position)
            {

            }
        }


        private void onMarketDataEvent(object sender, MarketDataEventArgs args)
        {
            //TODO: whenever change in ltp emit tradeQuoteEvent and touchline event emit Emitquote Event(not confirmed)
            if (args.MarketDataPorts == MarketDataPorts.marketDepthEvent)
            {
                var data = args.Value;
            }

            if (args.MarketDataPorts == MarketDataPorts.touchlineEvent)
            {
                Log.Trace($"Data: {args.SourceData}");
                Touchline data = JsonConvert.DeserializeObject<Touchline>(args.SourceData.ToString());
                try
                {
                    var tick = new Tick
                    {
                        AskPrice = data.AskInfo.Price.ToString().ToDecimal(),
                        BidPrice = data.BidInfo.Price.ToString().ToDecimal(),
                        Value = data.AverageTradedPrice.ToString().ToDecimal(),
                        Symbol = XTSInstrumentList.GetLeanSymbolFromInstrumentID(data.ExchangeInstrumentID),
                        Time = DateTime.Parse(data.ExchangeTimeStamp.ToString()),
                        Exchange = XTSAPI.Globals.GetExchangefromInt(data.ExchangeSegment),
                        TickType = TickType.Quote,
                        AskSize = data.AskInfo.Size,
                        BidSize = data.BidInfo.Size,
                    };

                    //lock (TickLocker)
                    //{
                    _aggregator.Update(tick);
                    //}
                }
                catch (Exception exception)
                {
                    throw new Exception($"XTSBrokerage.EmitQuoteTick(): Message: {exception.Message} Exception: {exception.InnerException}");
                }

            }
            if (args.MarketDataPorts == MarketDataPorts.candleDataEvent)
            {
                var data = args.Value;
            }

            if (args.MarketDataPorts == MarketDataPorts.openInterestEvent)
            {
                OI data = JsonConvert.DeserializeObject<OI>(args.SourceData.ToString());
                try
                {
                    var tick = new Tick
                    {
                        TickType = TickType.OpenInterest,
                        Value = data.OpenInterest,
                        Exchange = XTSAPI.Globals.GetExchangefromInt(data.ExchangeSegment),
                        Symbol = XTSInstrumentList.GetLeanSymbolFromInstrumentID(data.ExchangeInstrumentID),
                    };

                    //lock (TickLocker)
                    //{
                    _aggregator.Update(tick);
                    //}
                }
                catch (Exception exception)
                {
                    throw new Exception($"XTSBrokerage.EmitOpenInterestTick(): Message: {exception.Message} Exception: {exception.InnerException}");
                }
            }
        }


        private static bool CanSubscribe(Symbol symbol)
        {
            var market = symbol.ID.Market;
            var securityType = symbol.ID.SecurityType;
            if (symbol.Value.IndexOfInvariant("universe", true) != -1 || symbol.IsCanonical())
            {
                return false;
            }
            // Include future options as a special case with no matching market, otherwise our
            // subscriptions are removed without any sort of notice.
            return
                (securityType == SecurityType.Equity ||
                securityType == SecurityType.Option ||
                securityType == SecurityType.Index ||
                securityType == SecurityType.Future) &&
                market == Market.India;
        }


        private void OnOrderClose(OrderResult orderDetails)  
        {
            var brokerId = orderDetails.AppOrderID;
            if (orderDetails.OrderStatus.ToLower() == "cancelled")
            {
                var order = CachedOrderIDs
                    .FirstOrDefault(o => o.Value.BrokerId.Contains(brokerId.ToString()))
                    .Value;
                if (order == null)
                {
                    order = _algorithm.Transactions.GetOrdersByBrokerageId(brokerId)?.SingleOrDefault();
                    if (order == null)
                    {
                        // not our order, nothing else to do here
                        return;
                    }
                }
                Order outOrder;
                if (CachedOrderIDs.TryRemove(order.Id, out outOrder))
                {
                    OnOrderEvent(new OrderEvent(order,
                        DateTime.UtcNow,
                        OrderFee.Zero,
                        "XTS Order Event")
                    { Status = OrderStatus.Canceled });
                }
            }
        }

        private void FillMonitorAction()
        {
            Log.Trace("XTSBrokerage.FillMonitorAction(): task started");

            try
            {
                WaitTillConnected();
                foreach (var order in GetOpenOrders())
                {
                    CachedOrderIDs.TryAdd(order.BrokerId.First().ToInt64(), order);
                }

                while (!_ctsFillMonitor.IsCancellationRequested)
                {
                    try
                    {
                        WaitTillConnected();
                        _fillMonitorResetEvent.WaitOne(TimeSpan.FromMilliseconds(_fillMonitorTimeout), _ctsFillMonitor.Token);

                        foreach (var kvp in CachedOrderIDs)
                        {
                            var orderId = kvp.Key;
                            var order = kvp.Value;

                            var response = interactive.GetOrderAsync(orderId);
                            var result = response.Result;
                            if (response.Status != null)
                            {
                                if (response.Status.ToLower() == "rejected")
                                {
                                    OnMessage(new BrokerageMessageEvent(
                                        BrokerageMessageType.Warning,
                                        -1,
                                        $"XTSBrokerage.FillMonitorAction(): request failed: [{response.Status}] {result[0].MessageCode}, Content: {response.ToString()}, ErrorMessage: {result}"));

                                    continue;
                                }
                            }

                            //Process cancelled orders here.
                            if (result[0].OrderStatus.ToLower() == "cancelled")
                            {
                                OnOrderClose(result[0]);
                            }

                            if (result[0].OrderStatus.ToLower() == "open" || result[0].OrderStatus.ToLower() == "new")
                            {
                                // Process rest of the orders here.
                                EmitFillOrder(result[0]);
                            }
                        }
                    }
                    catch (Exception exception)
                    {
                        OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, -1, exception.Message));
                    }
                }
            }
            catch (Exception exception)
            {
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Error, -1, exception.Message));
            }

            Log.Trace("XTSBrokerage.FillMonitorAction(): task ended");
        }


        //can be call in partiallyfilled case 
        private void EmitFillOrder(OrderResult orderResponse)
        {
            try
            {
                var brokerId = orderResponse.AppOrderID;
                var order = CachedOrderIDs
                    .FirstOrDefault(o => o.Value.BrokerId.Contains(brokerId.ToString()))
                    .Value;
                if (order == null)
                {
                    order = _algorithm.Transactions.GetOrdersByBrokerageId(brokerId)?.SingleOrDefault();
                    if (order == null)
                    {
                        // not our order, nothing else to do here
                        return;
                    }
                }
                var contract = XTSInstrumentList.GetContractInfoFromInstrumentID(orderResponse.ExchangeInstrumentID);
                var brokerageSecurityType = _XTSMapper.GetBrokerageSecurityType(orderResponse.ExchangeInstrumentID);
                var symbol = _XTSMapper.GetLeanSymbol(contract.Name, brokerageSecurityType, Market.India);
                var fillPrice = decimal.Parse(orderResponse.OrderPrice.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture);
                var fillQuantity = orderResponse.OrderQuantity - orderResponse.LeavesQuantity;
                var updTime = DateTime.UtcNow;
                var security = _securityProvider.GetSecurity(order.Symbol);
                var orderFee = security.FeeModel.GetOrderFee(new OrderFeeParameters(security, order));
                var status = OrderStatus.Filled;

                if (order.Direction == OrderDirection.Sell)
                {
                    fillQuantity = -1 * fillQuantity;
                }

                if (fillQuantity != order.Quantity)
                {
                    decimal totalFillQuantity;
                    _fills.TryGetValue(order.Id, out totalFillQuantity);
                    totalFillQuantity += fillQuantity;
                    _fills[order.Id] = totalFillQuantity;

                    if (totalFillQuantity != order.Quantity)
                    {
                        status = OrderStatus.PartiallyFilled;
                        orderFee = OrderFee.Zero;
                    }
                }

                var orderEvent = new OrderEvent
                (
                    order.Id, symbol, updTime, status,
                    order.Direction, fillPrice, fillQuantity,
                    orderFee, $"XTS Order Event {order.Direction}"
                );

                // if the order is closed, we no longer need it in the active order list
                if (status == OrderStatus.Filled)
                {
                    Order outOrder;
                    CachedOrderIDs.TryRemove(order.Id, out outOrder);
                    decimal ignored;
                    _fills.TryRemove(order.Id, out ignored);
                    CachedOrderIDs.TryRemove(brokerId, out outOrder);
                }

                OnOrderEvent(orderEvent);
            }
            catch (Exception exception)
            {
                throw new Exception($"XTSBrokerage.EmitFillOrder(): Message: {exception.Message} Exception: {exception.InnerException}");
            }
        }



        private OrderStatus ConvertOrderStatus(OrderResult orderDetails)
        {
            //TODO: order state diagram
            var filledQty = Convert.ToInt32(orderDetails.OrderQuantity, CultureInfo.InvariantCulture);
            var pendingQty = Convert.ToInt32(orderDetails.LeavesQuantity, CultureInfo.InvariantCulture);

            if (orderDetails.OrderStatus.ToUpperInvariant() == "NEW" || orderDetails.OrderStatus.ToUpperInvariant() == "OPEN")
            {
                return OrderStatus.Submitted;
            }

            else if (filledQty > 0 && pendingQty > 0 && orderDetails.OrderStatus.ToUpperInvariant() == "PARTIALLYFILLED")
            {
                return OrderStatus.PartiallyFilled;
            }
            else if (orderDetails.OrderStatus.ToUpperInvariant() == "PENDINGNEW")
            {
                return OrderStatus.PartiallyFilled;
            }
            else if (pendingQty == 0 && orderDetails.OrderStatus.ToUpperInvariant() == "FILLED")
            {
                return OrderStatus.Filled;
            }
            else if (orderDetails.OrderStatus.ToUpperInvariant() == "CANCELLED")
            {
                return OrderStatus.Canceled;
            }

            return OrderStatus.None;
        }


        private OrderIdResult placeXTSOrder(Order order)
        {
            var xtsProductType = _XTSProductType;
            var orderProperties = order.Properties as IndiaOrderProperties;
            if (orderProperties.ProductType != null)
            {
                xtsProductType = orderProperties.ProductType;
            }
            else if (string.IsNullOrEmpty(xtsProductType))
            {
                throw new ArgumentException("Please set ProductType in config or provide a value in DefaultOrderProperties");
            }

            var info = _XTSMapper.GetBrokerageSymbol(order.Symbol);
            var data = JsonConvert.DeserializeObject<ContractInfo>(info);
            var exchange = XTSAPI.Globals.GetExchangefromInt(data.ExchangeSegment);
            var orderDirection = ConvertOrderDirection(order.Direction);
            var orderType = ConvertOrderType(order.Type);
            var orderPrice = double.Parse(order.Price.ToString(), CultureInfo.InvariantCulture);


            Task<OrderIdResult> responseTask = interactive.PlaceOrderAsync(
                exchange, data.ExchangeInstrumentID, orderDirection, orderType,
                order.Quantity.ToString().ToInt32(), orderPrice, 0.0, xtsProductType,
                order.TimeInForce.ToString(), 0, order.ContingentId.ToString()
                );

            responseTask.Wait();
            if (responseTask.IsCompleted && responseTask.Result != null)
            {
                var response = responseTask.Result;
                return response;
            }
            else
            {
                throw new ArgumentException("Error in Placing Order");
            }

        }


        private void Initialize(string tradingSegment, string productType, string interactiveSecretKey,
           string interactiveapiKey, string marketSecretKey, string marketeapiKey, IAlgorithm algorithm, IDataAggregator aggregator)
        {
            if (_isInitialized)
            {
                return;
            }
            _isInitialized = true;
            //_tradingSegment = tradingSegment;
            _XTSProductType = productType;
            _algorithm = algorithm;
            _securityProvider = algorithm?.Portfolio;
            _aggregator = aggregator;
            interactive = new XTSInteractive(Config.Get("xts-url") + "/interactive");
            marketdata = new XTSMarketData(Config.Get("xts-url") + "/marketdata");
            _interactiveApiKey = interactiveapiKey;
            _interactiveApiSecret = interactiveSecretKey;
            //_messageHandler = new BrokerageConcurrentMessageHandler<Socket>(OnMessageImpl);
            _marketApiSecret = marketSecretKey;
            _marketApiKey = marketeapiKey;
            _XTSMapper = new XTSSymbolMapper();
            _checkConnectionTask[0] = null;
            _checkConnectionTask[1] = null;
            var subscriptionManager = new EventBasedDataQueueHandlerSubscriptionManager();


            interactive.ConnectionState += onConnectionStateEvent;
            interactive.Interactive += onInteractiveEvent;
            marketdata.MarketData += onMarketDataEvent;
            subscriptionManager.SubscribeImpl += (s, t) =>
            {
                Subscribe(s);
                return true;
            };
            subscriptionManager.UnsubscribeImpl += (s, t) => Unsubscribe(s);

            _subscriptionManager = subscriptionManager;
            _fillMonitorTask = Task.Factory.StartNew(FillMonitorAction, _ctsFillMonitor.Token);

            //ValidateSubscription();
            Log.Trace("XTSBrokerage(): Start XTS Brokerage");
        }


        /// <summary>
        /// Connects the client to the broker's remote servers
        /// </summary>
        public override void Connect()
        {
            lock (_connectionLock)
            {
                if (IsConnected)
                    return;

                //To connect with Order related Broadcast

                if (!socketConnected)
                {
                    Log.Trace("XTSBrokerage.Connect(): Connecting...");
                    Task<InteractiveLoginResult> login1 = interactive.LoginAsync<InteractiveLoginResult>(_interactiveApiKey, _interactiveApiSecret, "WebAPI");
                    login1.Wait();
                    userID = login1.Result.userID;
                    if (login1.IsCompleted && login1 != null)
                    {
                        if (interactive.ConnectToSocket())
                        {
                            socketConnected = true;
                            if (_checkConnectionTask[0] == null)
                            {
                                // we start a task that will be in charge of expiring and refreshing our session id
                                _checkConnectionTask[0] = Task.Factory.StartNew(CheckConnection, _ctsFillMonitor.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                            }
                        }
                        else
                        {
                            throw new ArgumentException("Server not connected: Check UserId or Token or server url ");
                        }
                    }
                }

                //To connect with MarketData Broadcast
                if (socketConnected)
                {
                    Log.Trace("XTSMarketData.Connect(): Connecting...");
                    MarketDataLoginResult mlogin;
                    Task<MarketDataLoginResult> mlogin1 = marketdata.LoginAsync<MarketDataLoginResult>(_marketApiKey, _marketApiSecret, "WebAPI");
                    mlogin1.Wait();
                    if (mlogin1.IsCompleted && mlogin1 != null)
                    {
                        MarketDataPorts[] marketports = {
                                                    MarketDataPorts.marketDepthEvent,MarketDataPorts.candleDataEvent,MarketDataPorts.exchangeTradingStatusEvent,
                                                    MarketDataPorts.generalMessageBroadcastEvent,MarketDataPorts.indexDataEvent
                                                 };
                        if (marketdata.ConnectToSocket(marketports, PublishFormat.JSON, BroadcastMode.Full, "WebAPI"))
                        {
                            socketConnected = true;
                            if (_checkConnectionTask[1] == null)
                            {
                                // we start a task that will be in charge of expiring and refreshing our session id
                                _checkConnectionTask[1] = Task.Factory.StartNew(CheckConnection, _ctsFillMonitor.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                            }
                        }
                    }
                }

            }
        }


        /// <summary>
        /// Subscribe to the specified configuration
        /// </summary>
        /// <param name="dataConfig">defines the parameters to subscribe to a data feed</param>
        /// <param name="newDataAvailableHandler">handler to be fired on new data available</param>
        /// <returns>The new enumerator for this subscription request</returns>
        public IEnumerator<BaseData> Subscribe(SubscriptionDataConfig dataConfig, EventHandler newDataAvailableHandler)
        {
            if (!CanSubscribe(dataConfig.Symbol))
            {
                return null;
            }

            var enumerator = _aggregator.Add(dataConfig, newDataAvailableHandler);
            _subscriptionManager.Subscribe(dataConfig);

            return enumerator;
        }

        /// <summary>
        /// Removes the specified configuration
        /// </summary>
        /// <param name="dataConfig">Subscription config to be removed</param>
        public void Unsubscribe(SubscriptionDataConfig dataConfig)
        {
            _subscriptionManager.Unsubscribe(dataConfig);
            _aggregator.Remove(dataConfig);
        }


        /// <summary>
        /// Subscribes to the requested symbols (using an individual streaming channel)
        /// </summary>
        /// <param name="symbols">The list of symbols to subscribe</param>
        public void Subscribe(IEnumerable<Symbol> symbols)
        {
            if (symbols.Count() <= 0)
            {
                return;
            }
            var sub = new SubscriptionPayload();
            ContractInfo contract;
            //re add already subscribed symbols and send in one go
            foreach (var instrumentID in _subscribeInstrumentTokens)
            {
                try
                {
                    contract = XTSInstrumentList.GetContractInfoFromInstrumentID(instrumentID);
                    List<Instruments> instruments = new List<Instruments> { new Instruments { exchangeSegment = contract.ExchangeSegment, exchangeInstrumentID = contract.ExchangeInstrumentID } };
                    Task<QuoteResult<ListQuotesBase>> data = marketdata.SubscribeAsync<ListQuotesBase>(1502, instruments);
                    data.Wait();
                    if (data.Result != null)
                    {
                        Log.Trace($"InstrumentID: {instrumentID} subscribed");
                    }
                }
                catch (Exception exception)
                {
                    throw new Exception($"XTSBrokerage.Subscribe(): Message: {exception.Message} Exception: {exception.InnerException}");
                }
            }
            foreach (var symbol in symbols)
            {
                try
                {
                    var contractString = _XTSMapper.GetBrokerageSymbol(symbol);
                    contract = JsonConvert.DeserializeObject<ContractInfo>(contractString);
                    var instrumentID = contract.ExchangeInstrumentID;
                    if (!_subscribeInstrumentTokens.Contains(instrumentID))
                    {
                        List<Instruments> instruments = new List<Instruments> { new Instruments { exchangeSegment = contract.ExchangeSegment, exchangeInstrumentID = contract.ExchangeInstrumentID } };
                        var info = marketdata.SubscribeAsync<Touchline>(1501, instruments);
                        info.Wait();
                        if (info.Result != null)
                        {
                            Log.Trace($"InstrumentID: {instrumentID} subscribed");
                            _subscribeInstrumentTokens.Add(instrumentID);
                            _subscriptionsById[instrumentID] = symbol;
                        }

                    }
                }
                catch (Exception exception)
                {
                    throw new Exception($"XTSBrokerage.Subscribe(): Message: {exception.Message} Exception: {exception.InnerException}");
                }
            }

        }

        /// <summary>
        /// Removes the specified symbols to the subscription
        /// </summary>
        /// <param name="symbols">The symbols to be removed keyed by SecurityType</param>
        private bool Unsubscribe(IEnumerable<Symbol> symbols)
        {
            if (IsConnected)
            {
                if (symbols.Count() > 0)
                {
                    return false;
                }
                foreach (var symbol in symbols)
                {
                    try
                    {
                        var contract = _XTSMapper.GetBrokerageSymbol(symbol);
                        var data = JsonConvert.DeserializeObject<ContractInfo>(contract);
                        if (!_unSubscribeInstrumentTokens.Contains(data.ExchangeInstrumentID))
                        {
                            List<Instruments> instruments = new List<Instruments> { new Instruments { exchangeSegment = data.ExchangeSegment, exchangeInstrumentID = data.ExchangeInstrumentID } };
                            Task<UnsubscriptionResult> info = marketdata.UnsubscribeAsync(1502, instruments);
                            info.Wait();
                            if (info.Result != null)
                            {
                                _unSubscribeInstrumentTokens.Add(data.ExchangeInstrumentID);
                                _subscribeInstrumentTokens.Remove(data.ExchangeInstrumentID);
                                Symbol unSubscribeSymbol;
                                _subscriptionsById.TryRemove(data.ExchangeInstrumentID, out unSubscribeSymbol);
                            }
                            else
                            {
                                throw new Exception($"XTSBrokerage Unsubscribe error: {info.Exception}");
                            }
                        }
                    }
                    catch (Exception exception)
                    {
                        throw new Exception($"XTSBrokerage.Unsubscribe(): Message: {exception.Message} Exception: {exception.InnerException}");
                    }
                }
                return true;
            }
            return false;
        }


        /// <summary>
        /// Gets all open orders on the account.
        /// NOTE: The order objects returned do not have QC order IDs.
        /// </summary>
        /// <returns>The open orders returned from IB</returns>
        public override List<Order> GetOpenOrders()
        {
            var allOrders = interactive.GetOrderAsync();
            allOrders.Wait();

            List<Order> list = new List<Order>();

            //Only loop if there are any actual orders inside response
            if (allOrders.IsCompleted && allOrders.Result.Length > 0)
            {

                foreach (var item in allOrders.Result.Where(z => z.OrderStatus.ToUpperInvariant() == "OPEN" || z.OrderStatus.ToUpperInvariant() == "NEW"))
                {
                    Order order;
                    var contract = XTSInstrumentList.GetContractInfoFromInstrumentID(item.ExchangeInstrumentID);
                    var brokerageSecurityType = _XTSMapper.GetBrokerageSecurityType(item.ExchangeInstrumentID);
                    var symbol = _XTSMapper.GetLeanSymbol(contract.Name, brokerageSecurityType, Market.India);
                    var time = Convert.ToDateTime(item.OrderGeneratedDateTime, CultureInfo.InvariantCulture);
                    var price = Convert.ToDecimal(item.OrderPrice, CultureInfo.InvariantCulture);
                    var quantity = item.LeavesQuantity;

                    if (item.OrderType.ToUpperInvariant() == "MARKET")
                    {
                        order = new MarketOrder(symbol, quantity, time);
                    }
                    else if (item.OrderType.ToUpperInvariant() == "LIMIT")
                    {
                        order = new LimitOrder(symbol, quantity, price, time);
                    }
                    else if (item.OrderType.ToUpperInvariant() == "STOPMARKET")
                    {
                        order = new StopMarketOrder(symbol, quantity, price, time);
                    }
                    else if (item.OrderType.ToUpperInvariant() == "STOPLIMIT")
                    {
                        order = new StopLimitOrder();
                    }
                    else
                    {
                        OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Error, item.MessageCode,
                            "XTSBrorage.GetOpenOrders: Unsupported order type returned from brokerage: " + item.OrderType));
                        continue;
                    }

                    order.BrokerId.Add(item.AppOrderID.ToString());
                    order.Status = ConvertOrderStatus(item);

                    list.Add(order);
                }
                foreach (var item in list)
                {
                    if (item.Status.IsOpen())
                    {
                        var cached = CachedOrderIDs.Where(c => c.Value.BrokerId.Contains(item.BrokerId.First()));
                        if (cached.Any())
                        {
                            CachedOrderIDs[cached.First().Key] = item;
                        }
                    }
                }
            }
            return list;
        }



        /// <summary>
        /// Gets all holdings for the account
        /// </summary>
        /// <returns>The current holdings from the account</returns>
        public override List<Holding> GetAccountHoldings()
        {
            var holdingsList = new List<Holding>();
            var xtsProductTypeUpper = _XTSProductType.ToUpperInvariant();

            if (string.IsNullOrEmpty(xtsProductTypeUpper))
            {
                var daypositions = interactive.GetDayPositionAsync();
                daypositions.Wait();
                if (daypositions.Result.positionList.Length > 0 && daypositions.IsCompleted)
                {
                    foreach (var position in daypositions.Result.positionList)
                    {
                        Holding holding = new Holding
                        {
                            AveragePrice = Convert.ToDecimal((position.NetAmount.ToDecimal() / position.Quantity.ToDecimal()), CultureInfo.InvariantCulture),
                            Symbol = _XTSMapper.GetLeanSymbol(position.TradingSymbol, _XTSMapper.GetBrokerageSecurityType(position.ExchangeInstrumentID.ToInt64()), Market.India),
                            MarketPrice = Convert.ToDecimal(position.BuyAveragePrice, CultureInfo.InvariantCulture),
                            Quantity = position.Quantity.ToDecimal(),
                            UnrealizedPnL = Convert.ToDecimal(position.UnrealizedMTM, CultureInfo.InvariantCulture),
                            CurrencySymbol = Currencies.GetCurrencySymbol("INR"),
                            MarketValue = Convert.ToDecimal(position.BuyAmount)
                        };
                        holdingsList.Add(holding);
                    }
                }

                var holdingResponse = interactive.GetHoldingsAsync(userID);
                holdingResponse.Wait();
                if (holdingResponse.IsCompleted && holdingResponse.Result.RMSHoldingList != null)
                {
                    foreach (var item in holdingResponse.Result.RMSHoldingList)
                    {
                        Holding holding = new Holding
                        {
                            AveragePrice = item.IsBuyAvgPriceProvided ? item.BuyAvgPrice : 0,
                            //Symbol = _XTSMapper.GetLeanSymbol(item.TargetProduct, _XTSMapper.GetBrokerageSecurityType(interactive)),
                            MarketPrice = 0,
                            Quantity = Convert.ToDecimal(item.HoldingQuantity, CultureInfo.InvariantCulture),
                            //UnrealizedPnL = (item.averagePrice - item.lastTradedPrice) * item.holdingsQuantity,
                            CurrencySymbol = Currencies.GetCurrencySymbol("INR"),
                            //MarketValue = item.lastTradedPrice * item.holdingsQuantity
                        };
                        holdingsList.Add(holding);
                    }
                }

                var netpositions = interactive.GetNetPositionAsync();
                netpositions.Wait();
                if (netpositions.IsCompleted && netpositions.Result.positionList.Length > 0)
                {
                    foreach (var position in netpositions.Result.positionList)
                    {
                        Holding holding = new Holding
                        {
                            AveragePrice = Convert.ToDecimal((position.NetAmount.ToDecimal() / position.Quantity.ToDecimal()), CultureInfo.InvariantCulture),
                            Symbol = _XTSMapper.GetLeanSymbol(position.TradingSymbol, _XTSMapper.GetBrokerageSecurityType(position.ExchangeInstrumentID.ToInt64()), Market.India),
                            MarketPrice = Convert.ToDecimal(position.BuyAveragePrice, CultureInfo.InvariantCulture),
                            Quantity = position.Quantity.ToDecimal(),
                            UnrealizedPnL = Convert.ToDecimal(position.UnrealizedMTM, CultureInfo.InvariantCulture),
                            CurrencySymbol = Currencies.GetCurrencySymbol("INR"),
                            MarketValue = Convert.ToDecimal(position.BuyAmount)
                        };
                        holdingsList.Add(holding);
                    }
                }
            }
            return holdingsList;

        }


        /// <summary>
        /// Gets the current cash balance for each currency held in the brokerage account
        /// </summary>
        /// <returns>The current cash balance for each currency available for trading</returns>
        public override List<CashAmount> GetCashBalance()
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Places a new order and assigns a new broker ID to the order
        /// </summary>
        /// <param name="order">The order to be placed</param>
        /// <returns>True if the request for a new order has been placed, false otherwise</returns>
        public override bool PlaceOrder(Order order)
        {
            //order placing and get the instant result
            var orderFee = OrderFee.Zero;
            var orderProperties = order.Properties as IndiaOrderProperties;
            if (orderProperties == null || orderProperties.Exchange == null)
            {
                var errorMessage = $"Order failed, Order Id: {order.Id} timestamp: {order.Time} quantity: {order.Quantity} content: Please specify a valid order properties with an exchange value";
                OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, orderFee, "XTS Order Event") { Status = OrderStatus.Invalid });
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, -1, errorMessage));
                return false;
            }
            var orderResponse = placeXTSOrder(order);
            if (orderResponse.AppOrderID != null)
            {
                if (!order.BrokerId.Contains(orderResponse.AppOrderID.ToString()))
                {
                    order.BrokerId.Add(orderResponse.AppOrderID.ToString());
                    CachedOrderIDs.TryAdd(orderResponse.AppOrderID, order);
                }
                return true;
            }
            else
            {
                var message = $"Order failed, Order Id: {order.Id} timestamp: {order.Time} quantity: {order.Quantity} content: {orderResponse}";
                OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, orderFee, "XTS Order Event") { Status = OrderStatus.Invalid });
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, -1, message));
                return false;
            }

        }

        /// <summary>
        /// Updates the order with the same id
        /// </summary>
        /// <param name="order">The new order information</param>
        /// <returns>True if the request was made for the order to be updated, false otherwise</returns>
        public override bool UpdateOrder(Order order)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Cancels the order with the specified ID
        /// </summary>
        /// <param name="order">The order to cancel</param>
        /// <returns>True if the request was made for the order to be canceled, false otherwise</returns>
        public override bool CancelOrder(Order order)
        {
            throw new NotImplementedException();
        }

        private IEnumerable<Symbol> GetSubscribed()
        {
            return _subscriptionManager.GetSubscribedSymbols() ?? Enumerable.Empty<Symbol>();
        }

        /// <summary>
        /// Sets the job we're subscribing for
        /// </summary>
        /// <param name="job">Job we're subscribing for</param>
        public void SetJob(LiveNodePacket job)
        {
            throw new NotImplementedException();
        }

        #region IDataQueueUniverseProvider

        /// <summary>
        /// Method returns a collection of Symbols that are available at the data source.
        /// </summary>
        /// <param name="symbol">Symbol to lookup</param>
        /// <param name="includeExpired">Include expired contracts</param>
        /// <param name="securityCurrency">Expected security currency(if any)</param>
        /// <returns>Enumerable of Symbols, that are associated with the provided Symbol</returns>
        public IEnumerable<Symbol> LookupSymbols(Symbol symbol, bool includeExpired, string securityCurrency = null)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Returns whether selection can take place or not.
        /// </summary>
        /// <remarks>This is useful to avoid a selection taking place during invalid times, for example IB reset times or when not connected,
        /// because if allowed selection would fail since IB isn't running and would kill the algorithm</remarks>
        /// <returns>True if selection can take place</returns>
        public bool CanPerformSelection()
        {
            throw new NotImplementedException();
        }

        #endregion

        /// <summary>
        /// Disconnects the client from the broker's remote servers
        /// </summary>
        public override void Disconnect()
        {
            if (IsConnected)
            {
                // interactiveSocket.Close();
                return;
            }
        }
    }
}