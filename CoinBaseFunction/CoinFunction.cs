using System;
using System.Collections.Generic; 
using System.IO; 
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Coinbase.Pro;
using Coinbase.Pro.Models;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CoinBaseFunction
{
    public static class CoinFunction
    { 
        [FunctionName("CoinFunction")] 
        public static async Task Run([TimerTrigger("0 0 * * * *")] TimerInfo myTimer, ILogger logir)
 
        {
            logir.LogInformation($"C# Timer trigger function executed at: {DateTime.Now}");
              
            #region INITIAL VARS
  
            decimal size = 0;
            decimal limitPrice = 0m;
            string enviroment; // what  environment are we in prod or Sandbox
            string productsConfiged; //the products comma seperated i want to buy sell.
            decimal percentageBuySell;// //how much of a swing should i bet on? 
            decimal minimumAccountAvailable;//  //for ALL currencies... 
            Dictionary<string, bool> noExistingBuy = new Dictionary<string, bool>();
            Dictionary<string, bool> noExistingSell = new Dictionary<string, bool>();
            decimal marketPriceAvg = 0;
            bool errorbuysell = false;
            
            string currencyIHave = "USD";
            // Setup config. release is fo r
#if DEBUG
            IConfigurationRoot config = new ConfigurationBuilder()
                .SetBasePath(Path.Combine(Directory.GetCurrentDirectory()))
                .AddJsonFile("local.settings.json", optional: true, reloadOnChange: true)
                .AddEnvironmentVariables()
                .Build();
#endif
            #endregion

            #region Authentication
#if DEBUG
            enviroment = config.GetValue<string>("Values:Enviroment");
            percentageBuySell = Convert.ToDecimal(config.GetValue<string>("Values:percentageBuySell"));
            minimumAccountAvailable = Convert.ToDecimal(config.GetValue<string>("Values:minimumAccountAvailable"));
            productsConfiged =  config.GetValue<string>("Values:products");
#else
            enviroment = System.Environment.GetEnvironmentVariable("Enviroment"); 
            percentageBuySell = Convert.ToDecimal(System.Environment.GetEnvironmentVariable("percentageBuySell"));
            minimumAccountAvailable = Convert.ToDecimal(System.Environment.GetEnvironmentVariable("minimumAccountAvailable"));
            productsConfiged = System.Environment.GetEnvironmentVariable("products"); 
#endif
            logir.LogInformation("environment: " + enviroment);
            CoinbaseProClient clientSandbox, clientPro, client;
#if DEBUG
            GetAuthentication(out clientSandbox, out clientPro, config);
#else
            GetAuthentication(out clientSandbox, out clientPro);
#endif
            if (enviroment == "Sandbox")
            { client = clientSandbox; }
            else
            { client = clientPro; }

#endregion
            //https://pro.coinbase.com/markets  
            // Get all products available. grab those we can trade with 'currencyIHave'.
            List<Product> products = await client.MarketData.GetProductsAsync();
 

            //Get accounts (portfolio) 
            //accounts are like how much USD or BTC or LINK you have. products are things you can buy or sell
            // (LINK-USD, BTC-USD are two products you can buy with USD)
            List<Account> accounts = await client.Accounts.GetAllAccountsAsync();
            var myAccounts = accounts.Where(x => x.Balance != 0).Select(x => new { Currency = x.Currency, Available = x.Available, Balance = x.Balance }).ToList();
            var currencyIHaveAccount = myAccounts.Single(x => x.Currency == currencyIHave);
            List<string> volitales = productsConfiged.Split(',').ToList();
#if DEBUG

            decimal howManyTrades = volitales.Count()+1;//buy options. i figure we keep an equal amount on hand for buy orders(ones we are about to make and ones we are sitting on and will need money to fufill). plus one for wiggle room.
            List<Product> myProducts = products.Where(x => volitales.Contains(x.Id)).ToList();
#else 
            decimal howManyTrades = volitales.Count()+1;//buy options.
            List<Product> myProducts = products.Where(x => volitales.Contains(x.Id)).ToList();
#endif
            if (myProducts.Count() == 0)
            {
                logir.LogInformation("no products, returning.");
                return;
            } 
            decimal howMuchPerTrade = currencyIHaveAccount.Balance / howManyTrades;
            logir.LogInformation("I have " + howManyTrades + " trades i can make accross all my products."
                + " My account balance for the account i have currency for is " + currencyIHaveAccount.Balance + " so howMuchPerTrade is " + howMuchPerTrade);

            GetProducts(myProducts, logir);
            foreach (var product in myProducts)
            {
                Product myProduct = product;
                string productToTrade = myProduct.Id;
                noExistingBuy[productToTrade] = true;
                noExistingSell[productToTrade] = true;  
                Ticker market = await client.MarketData.GetTickerAsync(productToTrade);
                 
                //---------      CALCULATE WEIGHTED AVERAGE PRICE
                try
                {
                    List<Candle> candles = await client.MarketData.GetHistoricRatesAsync(productToTrade, DateTime.Now.AddDays(-3), DateTime.Now, 86400);// get price every 30 minutes
                    decimal totalVol = 0;
                    List<decimal> weightedPrices = new List<decimal>();
                    foreach (Candle c in candles)
                    {
                        if (!c.Volume.HasValue)
                            continue; 
                        else if (c.Open.HasValue && c.Close.HasValue)
                            weightedPrices.Add((c.Open.Value+ c.Close.Value) * c.Volume.Value  * Convert.ToDecimal(.5) ); 
                        else
                            continue;
                        totalVol += c.Volume.Value;
                    }
                    marketPriceAvg = weightedPrices.Sum() / totalVol;
                }
                catch (Exception ex)
                { 
                    var errorMsg = await ex.GetErrorMessageAsync();
                    logir.LogInformation("CANT FIND MARKET PRICE FOR  - " + productToTrade + " ERROR: " + errorMsg, Console.ForegroundColor);
                    continue;
                }
                //in case the average is more expensive and we want to set a buy.
                decimal marketPriceBuy = Math.Min(marketPriceAvg, market.Price);
                //in case the average is less expensive than going rate and we want to set a sell.
                decimal marketPriceSell = Math.Max(marketPriceAvg, market.Price);
                PagedResponse<Order> orders = new PagedResponse<Order>();
                try 
                { 
                    orders = client.Orders.GetAllOrdersAsync("open", productToTrade).Result; 
                }
                catch (Exception ex)
                {
                    var errorMsg = await ex.GetErrorMessageAsync();
                    logir.LogInformation("UNABLE TO GET ORDERS - " + errorMsg, Console.ForegroundColor);
                    return;
                }
                if (orders.Data.Count() == 0)
                {
                    logir.LogInformation($"We have no open orders for " + productToTrade);
                }
                else
                {
                    logir.LogInformation($"market price for " + productToTrade + " is "+market.Price);
                    GetOrders(ref noExistingBuy, ref noExistingSell, orders, logir);
                }
                
   
                errorbuysell = false;
                //-------------------------   BUY the crypto currency with currencyIHave
                if (currencyIHaveAccount.Available > minimumAccountAvailable
                    && noExistingBuy[productToTrade] == true)
                {
                    //BUY logic 

                    limitPrice = marketPriceBuy - (marketPriceBuy * percentageBuySell);
                    size = Math.Min(currencyIHaveAccount.Available , howMuchPerTrade) / limitPrice;
                    decimal sizeround = Math.Floor(size / myProduct.BaseIncrement) * myProduct.BaseIncrement;// round down to the base increment.

                    decimal limitPriceRound = Math.Floor(limitPrice / myProduct.QuoteIncrement) * myProduct.QuoteIncrement; //round down to the currency increment
                    string limitPriceRountStr = limitPriceRound.ToString().Substring(0, 7);
                    logir.LogInformation("about to try to buy " + productToTrade + ". I have "+ currencyIHaveAccount.Available 
                        + " available and with howMuchPerTrade of " + howMuchPerTrade + ", I get an amount of " + sizeround + " for this trade, of my currency; " 
                        + currencyIHaveAccount.Currency + ". The avg market price calculated for a buy order is "+ marketPriceBuy 
                        + ", and the limitPriceRound(the price i want) is "+ limitPriceRountStr + ".");
                     
                    if (sizeround < myProduct.BaseMinSize)
                    {
                        logir.LogInformation("cant create the limit buy. too small a size. " + sizeround + " of " + myProduct.DisplayName + " is smaller than the base min size; " + myProduct.BaseMinSize);
                        continue;
                    }
                    if (marketPriceBuy * percentageBuySell <= limitPriceRound * Convert.ToDecimal(.005))
                    {
                        logir.LogInformation("cant create the limit buy. not enough profit per unit of" + myProduct.DisplayName + " at " + limitPriceRountStr +
                            " times .005 is the fee; " + limitPriceRound * Convert.ToDecimal(.005) + ", and thats larger than marketPrice * percentageBuySell " +
                            marketPriceBuy * percentageBuySell + ". marketPrice: " + marketPriceBuy + ", percentBuySell: " + percentageBuySell);
                        continue;
                    }


                    //  place limit order & error handdling 
                    try
                    { 
                            var order1 = await client.Orders.PlaceLimitOrderAsync(
                            OrderSide.Buy, productToTrade, size: sizeround, limitPrice: limitPriceRound, timeInForce: TimeInForce.GoodTillCanceled);
                            noExistingBuy[productToTrade] = false;
                        logir.LogInformation("***BUY ORDER COMPLETE: product:"+ productToTrade+", amount/size: "+ sizeround+", limit price: "+ limitPriceRountStr + ". The amount i have per trade is: "+howMuchPerTrade+"***");

                    }
                    catch (Exception ex)
                    { 
                        var errorMsg = await ex.GetErrorMessageAsync();
                        logir.LogInformation("BUY ERROR - " + errorMsg, Console.ForegroundColor);
                        errorbuysell = true;
                    }

 
                }

                for (int e = 0; e < myAccounts.Count; e++)
                {
                    //-------------------------   Sell
                    if (myAccounts[e].Available > 0 //have to have some to sell
                        && productToTrade.StartsWith(myAccounts[e].Currency) //make sure its the product we are in the market for. 
                        && noExistingSell[productToTrade] == true)
                    {
                        //SELL logic 
                        limitPrice = marketPriceSell + (marketPriceSell * percentageBuySell);
                        size = myAccounts[e].Available;
                        decimal sizeround = Math.Floor(size / myProduct.BaseIncrement) * myProduct.BaseIncrement;// round to the base increment.
                        decimal limitPriceRound = Math.Floor(limitPrice / myProduct.QuoteIncrement) * myProduct.QuoteIncrement; //round to the currency increment
                        string limitPriceRountStr = limitPriceRound.ToString().Substring(0, 7);
  
                        logir.LogInformation("about to try to sell " + myAccounts[e].Available + " of " + productToTrade 
                            + ". The avg market price calculated for a sell order is" + marketPriceSell
                            + ", and the limitPriceRound(the price i want) is " + limitPriceRountStr + ".");

                        if (sizeround < myProduct.BaseMinSize)
                        {
                            logir.LogInformation("cant fill. too small a size. " + sizeround + " of " + myProduct.DisplayName + " is smaller than the base min size; " + myProduct.BaseMinSize);
                            continue;
                        }
                        if (marketPriceSell * percentageBuySell <= limitPriceRound * Convert.ToDecimal(.005))
                        {
                            logir.LogInformation("cant create the limit sell. not enough profit per unit of" + myProduct.DisplayName + " at " + limitPriceRountStr +
                                " times .005 is the fee; " + limitPriceRound * Convert.ToDecimal(.005) + ", and thats larger than marketPrice * percentageBuySell " +
                                marketPriceSell * percentageBuySell + ". marketPrice: " + marketPriceSell + ", percentBuySell: " + percentageBuySell);
                            continue;
                        }

                        //  place limit order  & error handdling 
                        try
                        {

                                var order1 = await client.Orders.PlaceLimitOrderAsync(
                                OrderSide.Sell, productToTrade, size: sizeround, limitPrice: limitPriceRound, timeInForce: TimeInForce.GoodTillCanceled);
                                noExistingSell[productToTrade] = false;
                                logir.LogInformation("***SELL ORDER COMPLETE: product:" + productToTrade + ", amount/size: " + sizeround + ", limit price: " + limitPriceRountStr + ". The amount i have of this product is: " + myAccounts[e].Available + "***");

                        }
                        catch (Exception ex)
                        { 
                            var errorMsg = await ex.GetErrorMessageAsync();
                            logir.LogInformation("SELL ERROR - " + errorMsg, Console.ForegroundColor);
                            errorbuysell = true;
                        }
                         
                    }
                }

                myAccounts.Clear();
            }
 

        }
        private static void GetAuthentication(out CoinbaseProClient clientSandbox, out CoinbaseProClient clientPro, IConfigurationRoot config)
        {
            clientSandbox = new CoinbaseProClient(new Config
            {
#if DEBUG
                ApiKey = config.GetValue<string>("ApiKey"),
                Secret = config.GetValue<string>("Secret"),
                Passphrase = config.GetValue<string>("Passphrase"), 
                ApiUrl = "https://api-public.sandbox.pro.coinbase.com"
#endif
            });

            clientPro = new CoinbaseProClient(new Config
            {
#if DEBUG
                ApiKey = config.GetValue<string>("ApiKeyP"),
                Secret = config.GetValue<string>("SecretP"),
                Passphrase = config.GetValue<string>("PassphraseP"),
#endif
            });
        }
        private static void GetAuthentication(out CoinbaseProClient clientSandbox, out CoinbaseProClient clientPro )
        {
            clientSandbox = new CoinbaseProClient(new Config
            {
 
                ApiKey = System.Environment.GetEnvironmentVariable("ApiKey"),
                Secret = System.Environment.GetEnvironmentVariable("Secret"),
                Passphrase = System.Environment.GetEnvironmentVariable("Passphrase"),
                ApiUrl = "https://api-public.sandbox.pro.coinbase.com"
            });

            clientPro = new CoinbaseProClient(new Config
            { 
                ApiKey = System.Environment.GetEnvironmentVariable("ApiKeyP"),
                Secret = System.Environment.GetEnvironmentVariable("SecretP"),
                Passphrase = System.Environment.GetEnvironmentVariable("PassphraseP"), 
            });
        }
        private static void GetProducts(List<Product> products, ILogger log)
        {
            string productString = "List of products/coin Ids: "; 
            foreach (var product in products)
            {
                productString += ", "+ product.Id;
            }
            productString +=". ";
        }

        private static void GetOrders( ref Dictionary<string, bool> noExistingBuy, ref Dictionary<string, bool> noExistingSell, PagedResponse<Order> orders, ILogger log)
        { 
            foreach (var order in orders.Data)
            {
                //log.LogInformation($"====================================");
                //log.LogInformation($"Order Coin/Product Id: {order.ProductId}", Console.ForegroundColor);
                //log.LogInformation($"Order Price: {order.Price}", Console.ForegroundColor);
                //log.LogInformation($"Order Size: {order.Size}", Console.ForegroundColor);
                //log.LogInformation($"Order Status: {order.Status}", Console.ForegroundColor);
                //log.LogInformation($"Order SpecidiedFunds: {order.SpecifiedFunds}", Console.ForegroundColor);
                //log.LogInformation($"Order Value: {order.ExecutedValue}", Console.ForegroundColor);
                //log.LogInformation($"Order Funds: {order.Funds}", Console.ForegroundColor);
                //log.LogInformation($"Order Fees: {order.FillFees}", Console.ForegroundColor);
                //log.LogInformation($"Order Side: {order.Side}", Console.ForegroundColor);
                //log.LogInformation($"Order Type: {order.Type}", Console.ForegroundColor);

                log.LogInformation($"We have a {order.Side} order open for {order.ProductId} at {order.Price}" );

                string orderProductId = order.ProductId;
                string ordersStatus = order.Status.ToLower();
                OrderSide ordersSide = order.Side;

                if (ordersSide == OrderSide.Buy && ordersStatus.Contains("open"))
                {
                    noExistingBuy[order.ProductId] = false;
                }

                if (ordersSide == OrderSide.Sell && ordersStatus.Contains("open"))
                {
                    noExistingSell[order.ProductId] = false;
                }

            }
        }

    }
}
