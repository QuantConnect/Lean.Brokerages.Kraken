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

using System;
using System.Collections.Generic;
using System.Linq;
using QuantConnect.Logging;
using QuantConnect.Securities;

namespace QuantConnect.Brokerages.Kraken
{
    /// <summary>
    /// Due to Kraken weirdness with symbols, this override helps to solve them all.
    /// Very close to <see cref="SymbolPropertiesDatabaseSymbolMapper"/>, but with Kraken tweaks
    /// </summary>
    public class KrakenSymbolMapper : ISymbolMapper
    {
        private readonly Dictionary<Symbol, SymbolProperties> _symbolPropertiesMap;
        
        // map from SymbolPropertiesDb
        private readonly Dictionary<string, Symbol> _symbolMap;
        
        // Generated map for open orders symbol, to have O(1) access
        private readonly Dictionary<string, Symbol> _openOrdersSymbolMap;
        
        // Generated map for websocket symbols, to have O(1) access
        private readonly Dictionary<string, Symbol> _wsSymbolMap;

        private Dictionary<string, string> _currencyMap => new Dictionary<string, string>
        {
            {"ZUSD", "USD"},
            {"ZEUR", "EUR"},
            {"ZGBP", "GBP"},
            {"ZAUD", "AUD"},
            {"ZCAD", "CAD"},
            {"XXBT", "BTC"},
            {"XXRP", "XRP"},
            {"XLTC", "LTC"},
            {"XETH", "ETH"},
            {"XETC", "ETC"},
            {"XREP", "REP"},
            {"XXMR", "XMR"},
        };

        public KrakenSymbolMapper()
        {
            _symbolPropertiesMap =
                SymbolPropertiesDatabase
                    .FromDataFolder()
                    .GetSymbolPropertiesList(Market.Kraken)
                    .Where(x => !string.IsNullOrWhiteSpace(x.Value.MarketTicker))
                    .ToDictionary(
                        x => Symbol.Create(x.Key.Symbol, x.Key.SecurityType, x.Key.Market),
                        x => x.Value);
            
            _symbolMap =
                _symbolPropertiesMap
                    .ToDictionary(
                        x => x.Value.MarketTicker,
                        x => x.Key);

            _openOrdersSymbolMap = new Dictionary<string, Symbol>();
            _wsSymbolMap = new Dictionary<string, Symbol>();
        }
        
        /// <summary>
        /// Get Kraken Market ticker for passed <see cref="Symbol"/>
        /// </summary>
        /// <param name="symbol">Lean <see cref="Symbol"/></param>
        /// <returns>Brokerage symbol</returns>
        /// <exception cref="ArgumentException">Wrong Lean <see cref="Symbol"/></exception>
        public string GetBrokerageSymbol(Symbol symbol)
        {
            if (symbol.ID.Market != Market.Kraken)
            {
                throw new ArgumentException($"This method applies only for Kraken symbols. Try to use other class like {nameof(SymbolPropertiesDatabaseSymbolMapper)}");
            }
            
            if (symbol.SecurityType != SecurityType.Crypto)
            {
                throw new ArgumentException($"Only crypto symbols available in Kraken now. Current symbol security type: {symbol.SecurityType}");
            }
            if (!_symbolPropertiesMap.TryGetValue(symbol, out var properties))
            {
                throw new ArgumentException($"Unknown symbol: {symbol.Value}/{symbol.SecurityType}/{symbol.ID.Market}");
            }

            return properties.MarketTicker;
        }

        /// <summary>
        /// Return Lean <see cref="Symbol"/> instance for passed Kraken market Ticker
        /// </summary>
        /// <param name="brokerageSymbol">Symbol that comes from Kraken API</param>
        /// <param name="securityType">Always Crypto</param>
        /// <param name="market">Always Kraken</param>
        /// <param name="expirationDate">Always default(no expiration symbols in Crypto)</param>
        /// <param name="strike">Always 0</param>
        /// <param name="optionRight">Always Call - not used in Crypto</param>
        /// <returns>Lean <see cref="Symbol"/></returns>
        /// <exception cref="ArgumentException">Wrong Lean <see cref="Symbol"/></exception>
        public Symbol GetLeanSymbol(string brokerageSymbol, SecurityType securityType = SecurityType.Crypto, string market = Market.Kraken, DateTime expirationDate = default(DateTime),
            decimal strike = 0, OptionRight optionRight = OptionRight.Call)
        {
            if (market != Market.Kraken)
            {
                throw new ArgumentException($"This method applies only for Kraken symbols. Try to use other class like {nameof(SymbolPropertiesDatabaseSymbolMapper)}");
            }
            
            if (securityType != SecurityType.Crypto)
            {
                throw new ArgumentException($"Only crypto symbols available in Kraken now. Current symbol security type: {securityType}");
            }
            
            if (!_symbolMap.TryGetValue(brokerageSymbol, out var symbol))
            {
                throw new ArgumentException($"Unknown symbol: {brokerageSymbol}/{securityType}/{market}");
            }

            return symbol;
        }
        
        /// <summary>
        /// Return Lean <see cref="Symbol"/> instance for passed Kraken market Ticker from OpenOrders endpoint
        /// </summary>
        /// <param name="brokerageSymbol">Symbol that comes from OpenOrders endpoint</param>
        /// <param name="securityType">Always Crypto</param>
        /// <param name="market">Always Kraken</param>
        /// <param name="expirationDate">Always default(no expiration symbols in Crypto)</param>
        /// <param name="strike">Always 0</param>
        /// <param name="optionRight">Always Call - not used in Crypto</param>
        /// <returns>Lean <see cref="Symbol"/></returns>
        /// <exception cref="ArgumentException">Wrong Lean <see cref="Symbol"/></exception>
        public Symbol GetLeanSymbolFromOpenOrders(string brokerageSymbol, SecurityType securityType = SecurityType.Crypto, string market = Market.Kraken, DateTime expirationDate = default(DateTime),
            decimal strike = 0, OptionRight optionRight = OptionRight.Call)
        {
            if (market != Market.Kraken)
            {
                throw new ArgumentException($"This method applies only for Kraken symbols. Try to use other class like {nameof(SymbolPropertiesDatabaseSymbolMapper)}");
            }
            
            if (securityType != SecurityType.Crypto)
            {
                throw new ArgumentException($"Only crypto symbols available in Kraken now. Current symbol security type: {securityType}");
            }

            Symbol leanSymbol;
            if (_openOrdersSymbolMap.TryGetValue(brokerageSymbol, out leanSymbol))
            {
                return leanSymbol;
            }

            var symbol = _symbolPropertiesMap.FirstOrDefault(kvp => kvp.Value.Description.Replace("/", string.Empty) == brokerageSymbol);
            
            if (symbol.Equals(default(KeyValuePair<Symbol, SymbolProperties>)))
            {
                throw new ArgumentException($"Unknown symbol: {brokerageSymbol}/{securityType}/{market}");
            }

            leanSymbol = symbol.Key;
            
            _openOrdersSymbolMap[brokerageSymbol] = leanSymbol;
            
            return leanSymbol;
        }
        
        /// <summary>
        /// Checks if the Lean symbol is supported by the brokerage
        /// </summary>
        /// <param name="symbol">The Lean symbol</param>
        /// <returns>True if the brokerage supports the symbol</returns>
        public bool IsKnownLeanSymbol(Symbol symbol)
        {
            return !string.IsNullOrWhiteSpace(symbol?.Value) && _symbolPropertiesMap.ContainsKey(symbol);
        }

        /// <summary>
        /// Get Kraken Websocket ticker for passed <see cref="Symbol"/>
        /// </summary>
        /// <param name="symbol">Lean <see cref="Symbol"/></param>
        /// <returns>Websocket symbol</returns>
        /// <exception cref="ArgumentException">Wrong Lean <see cref="Symbol"/></exception>
        public string GetWebsocketSymbol(Symbol symbol)
        {
            if (symbol.ID.Market != Market.Kraken)
            {
                throw new ArgumentException($"This method applies only for Kraken symbols. Try to use other class like {nameof(SymbolPropertiesDatabaseSymbolMapper)}");
            }
            
            if (symbol.SecurityType != SecurityType.Crypto)
            {
                throw new ArgumentException($"Only crypto symbols available in Kraken now. Current symbol security type: {symbol.SecurityType}");
            }
            if (!_symbolPropertiesMap.TryGetValue(symbol, out var properties))
            {
                throw new ArgumentException($"Unknown symbol: {symbol.Value}/{symbol.SecurityType}/{symbol.ID.Market}");
            }

            return properties.Description;
        }
        
        /// <summary>
        /// Get Lean <see cref="Symbol"/> from Kraken Websocket ticker </summary>
        /// <param name="wsSymbol"></param>
        /// <returns>Lean <see cref="Symbol"/></returns>
        /// <exception cref="ArgumentException">Unknown Websocket symbol</exception>
        public Symbol GetSymbolFromWebsocket(string wsSymbol)
        {
            Symbol leanSymbol;
            if (_wsSymbolMap.TryGetValue(wsSymbol, out leanSymbol))
            {
                return leanSymbol;
            }

            var symbol = _symbolPropertiesMap.FirstOrDefault(i => i.Value.Description == wsSymbol);
            if (symbol.Equals(default(KeyValuePair<Symbol, SymbolProperties>)))
            {
                throw new ArgumentException($"Unknown symbol: {wsSymbol}/{SecurityType.Crypto}/{Market.Kraken}");
            }

            leanSymbol = symbol.Key;
            
            _wsSymbolMap[wsSymbol] = leanSymbol;
            
            return leanSymbol;
        }
        
        /// <summary>
        /// Convert Kraken Currency to Lean Currency
        /// </summary>
        /// <param name="marketCurrency">Kraken currency</param>
        /// <returns>Lean currency</returns>
        public string ConvertCurrency(string marketCurrency)
        {
            if (!_currencyMap.TryGetValue(marketCurrency, out var symbol))
            {
                // Lean currency the same with Kraken one
                return marketCurrency;
            }

            return symbol;
        }
    }
}
