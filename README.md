![header-cheetah](https://user-images.githubusercontent.com/79997186/184224088-de4f3003-0c22-4a17-8cc7-b341b8e5b55d.png)

&nbsp;
&nbsp;
&nbsp;

## Introduction

This repository hosts the Kraken Brokerage Plugin Integration with the QuantConnect LEAN Algorithmic Trading Engine. LEAN is a brokerage agnostic operating system for quantitative finance. Thanks to open-source plugins such as this [LEAN](https://github.com/QuantConnect/Lean) can route strategies to almost any market.

[LEAN](https://github.com/QuantConnect/Lean) is maintained primarily by [QuantConnect](https://www.quantconnect.com), a US based technology company hosting a cloud algorithmic trading platform. QuantConnect has successfully hosted more than 200,000 live algorithms since 2015, and trades more than $1B volume per month.

### About Kraken

<p align="center">
<picture >
  <source media="(prefers-color-scheme: dark)" srcset="https://cdn.quantconnect.com/i/tu/kraken-ds-logo.svg">
  <source media="(prefers-color-scheme: light)" srcset="https://cdn.quantconnect.com/i/tu/kraken-ds-logo.svg">
  <img alt="introduction" width="40%">
</picture>
<p>

[Kraken](https://www.kraken.com/) was founded by Jesse Powell in 2011 with the goal to "accelerate the adoption of cryptocurrency so that you and the rest of the world can achieve financial freedom and inclusion". Kraken provides access to trading Crypto through spot and Futures markets for clients with a minimum deposit of around $0-$150 USD for [currency](https://support.kraken.com/hc/en-us/articles/360000381846) and [Crypto deposits](https://support.kraken.com/hc/en-us/articles/360000292886-Cryptocurrency-deposit-fees-and-minimums). Kraken also provides staking services, educational content, and a developer grant program.


## Using the Brokerage Plugin
  
### QuantConnect Cloud

  This plugin is integrated in the QuantConnect Cloud Platform where you can use this integration with a simple visual interface, and harness the QuantConnect Live Data Feed. For most users this is substantially cheaper and easier than self-hosting. For more information see the [Kraken documentation page](https://www.quantconnect.com/docs/v2/our-platform/live-trading/brokerages/kraken). 
  
### Locally

Follow these steps to start local live trading with the Kraken brokerage:

1.  Open a terminal in your [CLI root directory](https://www.quantconnect.com/docs/v2/lean-cli/initialization/directory-structure#02-lean-init).
2.  Run lean live "`<projectName>`" to start a live deployment wizard for the project in ./`<projectName>` and then enter the brokerage number.

    ```
    $ lean live 'My Project'
     
    Select a brokerage:
    1. Paper Trading
    2. Interactive Brokers
    3. Tradier
    4. OANDA
    5. Bitfinex
    6. Coinbase Pro
    7. Binance
    8. Zerodha
    9. Samco
    10. Terminal Link
    11. Atreyu
    12. Trading Technologies
    13. Kraken
    14. FTX
    Enter an option: 
    ```
  
3.  Enter the number of the organization that has a subscription for the Kraken module.

    ```
    $ lean live "My Project" 

    Select the organization with the Kraken module subscription:
    1. Organization 1
    2. Organization 2
    3. Organization 3
       Enter an option: 
    ```

4.  Enter your Kraken credentials.

    ```
    $ lean live "My Project" 
    Create an API key by logging in and accessing the Kraken API Management page (https://www.kraken.com/u/security/api).
    API key:
    API secret:
    ```

5.  Enter your Kraken verification tier.

    ```
    $ lean live "My Project" 
    Select the Verification Tier (Starter, Intermediate, Pro) [Starter]:
    ```

6.  Enter the number of the data feed to use and then follow the steps required for the data connection.

    ``` 
    $ lean live 'My Project'

    Select a data feed:
    1. Interactive Brokers
    2. Tradier
    3. Oanda
    4. Bitfinex
    5. Coinbase Pro
    6. Binance
    7. Zerodha
    8. Samco
    9. Terminal Link
    10. Trading Technologies
    11. Kraken
    12. FTX
    13. IQFeed
    14. Polygon Data Feed
    15. Custom Data Only
  
        To enter multiple options, separate them with comma:
    ```

7. View the result in the `<projectName>`/live/`<timestamp>` directory. Results are stored in real-time in JSON format. You can save results to a different directory by providing the --output `<path>` option in step 2.

If you already have a live environment configured in your [Lean configuration file](https://www.quantconnect.com/docs/v2/lean-cli/initialization/configuration#03-Lean-Configuration), you can skip the interactive wizard by providing the --environment `<value>` option in step 2. The value of this option must be the name of an environment which has live-mode set to true.

## Account Types

Kraken supports cash and margin accounts.

## Order Types and Asset Classes


Kraken supports trading Crypto with the following order types:

- Market Order
- Limit Order
- Limit-If-Touch Order
- Stop-Market Order
- Stop-Limit Order


## Downloading Data

For local deployment, the algorithm needs to download the following dataset:

[Kraken Crypto Price Data](https://www.quantconnect.com/datasets/kraken-crypto-price-data) provided by CoinAPI


## Brokerage Model

Lean models the brokerage behavior for backtesting purposes. The margin model is used in live trading to avoid placing orders that will be rejected due to insufficient buying power.

You can set the Brokerage Model with the following statements
```
SetBrokerageModel(BrokerageName.Kraken, AccountType.Cash);
SetBrokerageModel(BrokerageName.Kraken, AccountType.Margin);
```
[Read Documentation](https://www.quantconnect.com/docs/v2/our-platform/live-trading/brokerages/kraken)

### Fees

We model the order fees of Kraken. For trading pairs that contain only Crypto assets, we model the lowest tier in their tiered fee structure, which is a 0.16% maker fee and a 0.26% taker fee. If you add liquidity to the order book by placing a limit order that doesn't cross the spread, you pay maker fees. If you remove liquidity from the order book by placing an order that crosses the spread, you pay taker fees. For trading pairs that have any of the following currencies as the base currency in the pair, the fee is 0.2%:
- CAD
- EUR
- GBP
- JPY
- USD
- USDT
- DAI
- USDC

Kraken adjusts your fees based on your 30-day trading volume, but we don't currently model trading volume to adjust fees.

To check the latest fees at all the fee tiers, see the [Fee Schedule page](https://www.kraken.com/features/fee-schedule) on the Kraken website.

### Margin

We model buying power and margin calls to ensure your algorithm stays within the margin requirements.

[Read Documentation](https://www.quantconnect.com/docs/v2/our-platform/live-trading/brokerages/kraken)

#### Buying Power

Kraken allows 1x leverage for most trades done in margin accounts. The following table shows pairs that have additional leverage available:

| Quote Currency | Base Currencies | Leverage |
| --- | --- | --- |
| ADA | BTC, ETH, USD, EUR | 3 |
| BCH | BTC, USD, EUR | 2 |
| BTC | USD, EUR | 5 |
| DASH | BTC, USD, EUR | 3 |
| EOS | BTC, ETH, USD, EUR | 3 |
| ETH | BTC, USD, EUR | 5 |
| LINK | BTC, ETH, USD, EUR | 3 |
| LTC | BTC, USD, EUR | 3 |
| REP | BTC, ETH, USD, EUR | 2 |
| TRX | BTC, ETH, USD, EUR | 3 |
| USDC | USD, EUR | 3 |
| USDT | USD, EUR | 2 |
| XMR | BTC, USD, EUR | 2 |
| XRP | BTC, USD, EUR | 3 |
| XTZ | BTC, ETH, USD, EUR | 2 |

#### Margin Calls

Regulation T margin rules apply. When the amount of margin remaining in your portfolio drops below 5% of the total portfolio value, you receive a [warning](https://www.quantconnect.com/docs/v2/writing-algorithms/reality-modeling/margin-calls#08-Monitor-Margin-Call-Events). When the amount of margin remaining in your portfolio drops to zero or goes negative, the portfolio sorts the generated margin call orders by their unrealized profit and executes each order synchronously until your portfolio is within the margin requirements.

### Slippage

Orders through Kraken do not experience slippage in backtests. In live trading, your orders may experience slippage.

### Fills

We fill market orders immediately and completely in backtests. In live trading, if the quantity of your market orders exceeds the quantity available at the top of the order book, your orders are filled according to what is available in the order book.

### Settlements

Trades settle immediately after the transaction.

### Deposits and Withdraws

You can deposit and withdraw cash from your brokerage account while you run an algorithm that's connected to the account. We sync the algorithm's cash holdings with the cash holdings in your brokerage account every day at 7:45 AM Eastern Time (ET).

&nbsp;
&nbsp;
&nbsp;

![whats-lean](https://user-images.githubusercontent.com/79997186/184042682-2264a534-74f7-479e-9b88-72531661e35d.png)

&nbsp;
&nbsp;
&nbsp;

LEAN Engine is an open-source algorithmic trading engine built for easy strategy research, backtesting, and live trading. We integrate with common data providers and brokerages, so you can quickly deploy algorithmic trading strategies.

The core of the LEAN Engine is written in C#, but it operates seamlessly on Linux, Mac and Windows operating systems. To use it, you can write algorithms in Python 3.8 or C#. QuantConnect maintains the LEAN project and uses it to drive the web-based algorithmic trading platform on the website.

## Contributions

Contributions are warmly very welcomed but we ask you to read the existing code to see how it is formatted, commented and ensure contributions match the existing style. All code submissions must include accompanying tests. Please see the [contributor guide lines](https://github.com/QuantConnect/Lean/blob/master/CONTRIBUTING.md).

## Code of Conduct

We ask that our users adhere to the community [code of conduct](https://www.quantconnect.com/codeofconduct) to ensure QuantConnect remains a safe, healthy environment for
high quality quantitative trading discussions.

## License Model

Licensed under the Apache License, Version 2.0 (the "License"); you may not use this file except in compliance with the License. You
may obtain a copy of the License at

<http://www.apache.org/licenses/LICENSE-2.0>

Unless required by applicable law or agreed to in writing, software distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied. See the License for the specific language
governing permissions and limitations under the License.