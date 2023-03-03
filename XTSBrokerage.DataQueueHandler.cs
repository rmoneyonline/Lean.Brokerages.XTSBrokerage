/*
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
using QuantConnect.Packets;
using QuantConnect.Util;
using System;
using System.Collections.Generic;

namespace Quantconnect.XTSBrokerage
{
    /// <summary>
    /// SamcoBrokerage: IDataQueueHandler implementation
    /// </summary>
    public partial class XTSBrokerage
    {
        #region IDataQueueHandler implementation

        /// <summary>
        /// Sets the job we're subscribing for
        /// </summary>
        /// <param name="job">Job we're subscribing for</param>
        public void SetJob(LiveNodePacket job)
        {
            Initialize(
                job.BrokerageData["xts-interactive-appkey"],
                job.BrokerageData["xts-interactive-secretkey"],
                job.BrokerageData["xts-marketdata-appkey"],
                job.BrokerageData["xts-marketdata-secretkey"],
                job.BrokerageData["xts-trading-segment"],
                job.BrokerageData["xts-product-type"],
                null,
                Composer.Instance.GetExportedValueByTypeName<IDataAggregator>(Config.Get("data-aggregator", "QuantConnect.Lean.Engine.DataFeeds.AggregationManager"),
                    forceTypeNameOnExisting: false)
            );

            if (!IsConnected)
            {
                Connect();
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
            var symbol = dataConfig.Symbol;
            if (!CanSubscribe(symbol))
            {
                return null;
            }

            var enumerator = _aggregator.Add(dataConfig, newDataAvailableHandler);
            _subscriptionManager.Subscribe(dataConfig);

            return enumerator;
        }

        /// <summary>
        /// UnSubscribe to the specified configuration
        /// </summary>
        /// <param name="dataConfig">defines the parameters to subscribe to a data feed</param>
        public void Unsubscribe(SubscriptionDataConfig dataConfig)
        {
            _subscriptionManager.Unsubscribe(dataConfig);
            _aggregator.Remove(dataConfig);
        }

        /// <summary>
        /// Returns true if this data provide can handle the specified symbol
        /// </summary>
        /// <param name="symbol">The symbol to be handled</param>
        /// <returns>True if this data provider can get data for the symbol, false otherwise</returns>
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

        #endregion IDataQueueHandler implementation
    }
}