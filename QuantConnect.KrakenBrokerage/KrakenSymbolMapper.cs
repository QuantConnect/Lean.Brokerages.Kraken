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
using Fasterflect;
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
        
        private readonly Dictionary<string, Symbol> _symbolMap;

        private Dictionary<string, string> _currencyMap => new Dictionary<string, string>
        {
            {"ZUSD", "USD"},
            {"ZEUR", "EUR"},
            {"ZGBP", "GBP"},
            {"ZAUD", "AUD"},
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
        }
        /// <summary>
        /// Get Kraken Market ticker for passed <see cref="Symbol"/>
        /// </summary>
        /// <param name="symbol"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentException"></exception>
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
        /// <param name="brokerageSymbol"></param>
        /// <param name="securityType"></param>
        /// <param name="market"></param>
        /// <param name="expirationDate"></param>
        /// <param name="strike"></param>
        /// <param name="optionRight"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentException"></exception>
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
        /// <param name="symbol"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentException"></exception>
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
        /// <returns></returns>
        /// <exception cref="ArgumentException"></exception>
        public Symbol GetSymbolFromWebsocket(string wsSymbol)
        {
            var symbol = _symbolPropertiesMap.Where(i => i.Value.Description == wsSymbol);
            if (symbol == null || !symbol.Any())
            {
                throw new ArgumentException($"Unknown symbol: {wsSymbol}/{SecurityType.Crypto}/{Market.Kraken}");
            }
            return symbol.First().Key;
        }
        
        /// <summary>
        /// Convert Kraken Currency to Lean Currency
        /// </summary>
        /// <param name="marketCurrency"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentException"></exception>
        public string ConvertCurrency(string marketCurrency)
        {
            if (!_currencyMap.TryGetValue(marketCurrency, out var symbol))
            {
                throw new ArgumentException($"Unknown currency: {marketCurrency}/{SecurityType.Crypto}/{Market.Kraken}");
            }

            return symbol;
        }
    }
}
