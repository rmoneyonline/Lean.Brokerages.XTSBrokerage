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

using QuantConnect.Configuration;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Logging;
using QuantConnect.Util;
using System;
using System.Collections.Generic;
using System.Linq;
using XTSAPI.MarketData;
using QuantConnect.Brokerages.XTS;
using Microsoft.VisualBasic;
using System.Threading.Tasks;
using XTSAPI.Interactive;
using XTSAPI;
using IQFeed.CSharpApiClient.Extensions;
using QuantConnect.Algorithm.Framework.Portfolio;

namespace QuantConnect.ToolBox.XTSDataDownloader
{
    /// <summary>
    /// XTS Data Downloader class
    /// </summary>
    public class XTSDataDownloaderProgram : IDataDownloader
    {
        private readonly XTSMarketData _XTSAPI = new(Config.Get("xts-url"));
        private readonly XTSSymbolMapper _symbolMapper = new();

        /// <summary>
        /// Initializes a new instance of the <see cref="XTSDataDownloaderProgram"/> class
        /// </summary>
        public XTSDataDownloaderProgram()
        {
            InteractiveLoginResult login;
            Task<InteractiveLoginResult> login1 = _XTSAPI.LoginAsync<InteractiveLoginResult>(Config.Get("xts-marketdata-appkey"), Config.Get("xts-marketdata-secretkey"), "WebAPI");
            login1.Wait();
            login = login1.Result;
        }

        /// <summary>
        /// XTS Data Downloader Toolbox Project For LEAN Algorithmic Trading Engine. By Balamurali
        /// Pandranki a.k.a @itsbalamurali
        /// </summary>
        public void XTSDataDownloader(IList<string> tickers, string market, string resolution, string securityType, DateTime startDate, DateTime endDate)
        {
            if (resolution.IsNullOrEmpty() || tickers.IsNullOrEmpty())
            {
                Log.Error("XTSDataDownloader ERROR: '--tickers=', --securityType, '--market' or '--resolution=' parameter is missing");
                Log.Error("--tickers=eg JSWSTEEL,TCS,INFY");
                Log.Error("--market=MCX/NSE/NFO/CDS/BSE");
                Log.Error("--security-type=Equity/Future/Option");
                Log.Error("--resolution=Minute/Hour/Daily/Tick");
                Environment.Exit(1);
            }

            var castResolution = (Resolution)Enum.Parse(typeof(Resolution), resolution);
            var castSecurityType = (SecurityType)Enum.Parse(typeof(SecurityType), securityType);

            if (castSecurityType == SecurityType.Forex || castSecurityType == SecurityType.Cfd || castSecurityType == SecurityType.Crypto || castSecurityType == SecurityType.Base)
            {
                throw new ArgumentException("Invalid security type: " + castSecurityType);
            }

            if (startDate >= endDate)
            {
                throw new ArgumentException("The end date must be greater or equal than the start date.");
            }

            if (castResolution == Resolution.Tick || castResolution == Resolution.Second)
            {
                throw new ArgumentException("XTS Doesn't support tick or second resolution");
            }

            // Load settings from config.json and create downloader
            var dataDirectory = Globals.DataFolder;

            foreach (var pair in tickers)
            {
                try
                {
                    var pairObject = Symbol.Create(pair, castSecurityType, market);

                    // Write data
                    var writer = new LeanDataWriter(castResolution, pairObject, dataDirectory);
                    IList<TradeBar> fileEnum = new List<TradeBar>();

                    var dataDownloaderParameters = new DataDownloaderGetParameters(pairObject, castResolution, startDate, endDate, TickType.Trade);
                    var history = Get(dataDownloaderParameters);

                    //foreach (var bar in history)
                    //{
                    //    fileEnum.Add((TradeBar)bar);
                    //}
                    writer.Write(fileEnum);
                    Log.Trace($"XTSDataDownloaderProgram.XTSDataDownloader(): Successfully saved data for symbol: {pairObject.Value}");
                }
                catch (Exception err)
                {
                    Log.Error($"XTSDataDownloaderProgram.XTSDataDownloader(): Message: {err.Message} Exception: {err.InnerException}");
                }
            }
        }

        /// <summary>
        /// Get historical data enumerable for a single symbol, type and resolution given this start and end time (in UTC).
        /// </summary>
        /// <param name="dataDownloaderGetParameters">model class for passing in parameters for historical data</param>
        /// <returns>Enumerable of base data for this symbol</returns>
        public IEnumerable<BaseData> Get(DataDownloaderGetParameters dataDownloaderGetParameters)
        {
            var symbol = dataDownloaderGetParameters.Symbol;
            var resolution = dataDownloaderGetParameters.Resolution;
            var startUtc = dataDownloaderGetParameters.StartUtc;
            var endUtc = dataDownloaderGetParameters.EndUtc;
            var tickType = dataDownloaderGetParameters.TickType;
            var securityType = symbol.SecurityType;

            if (tickType != TickType.Trade)
            {
                return Enumerable.Empty<BaseData>();
            }

            if (resolution == Resolution.Tick || resolution == Resolution.Second)
            {
                throw new ArgumentException($"Resolution not available: {resolution}");
            }

            //if (!_symbolMapper.IsKnownBrokerageSymbol(symbol.ID.Symbol))
            //{
            //    throw new ArgumentException($"The ticker {symbol.Value} is not available.");
            //}

            if (endUtc < startUtc)
            {
                throw new ArgumentException("The end date must be greater or equal than the start date.");
            }

            if (securityType == SecurityType.Forex || securityType == SecurityType.Cfd || securityType == SecurityType.Crypto || securityType == SecurityType.Base)
            {
                throw new ArgumentException("Invalid security type: " + securityType);
            }
            return null;
            //(ExchangeSegment exchangeSegment, long exchangeInstrumentId, DateTime startTime, DateTime endTime, int compressionValue)
            //var exchange = _symbolMapper.GetExchange(symbol);
            //var exchgInstrumentID = _XTSAPI.SearchByStringAsync(symbol);
            ////var isIndex = securityType == SecurityType.Index;
            //var history = _XTSAPI.GetOHLCHistoryAsync(ExchangeSegment.NSECM, exchgInstrumentID.Result[0].ExchangeInstrumentID, startUtc, endUtc, 50 /*compression value*/);
            //var data = history.Result.dataResponse;
            //char[] sep = { ',' };
            //string[] info = data.Split(sep);
            //char[] sep2 = { '|' };
            //foreach(var candles in info)
            //{
            //    string[] candle = candles.Split(sep2);
            //    if (candle?.Any() == null)
            //    {
            //        //yield break;
            //    }
                //else
                //{
                //    yield return new TradeBar()
                //    {
                //        //Time = candle[0],
                //        Symbol = symbol,
                //        Low = candle[3].ToDecimal(),
                //        High = candle[2].ToDecimal(),
                //        Open = candle[1].ToDecimal(),
                //        Close = candle[4].ToDecimal(),
                //        Volume = candle[5].ToDecimal(),
                //        Value = candle[6].ToDecimal(),
                //        DataType = MarketDataType.TradeBar,
                //        Period = resolution.ToTimeSpan()

                //    };
                //}
            
        }
            //return Enumerable.Empty<BaseData>();

        //}
    }
}