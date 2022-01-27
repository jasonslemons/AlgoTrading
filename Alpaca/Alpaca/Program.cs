using Alpaca.Markets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Alpaca
{
    internal static class Program
    {
        private const String KEY_ID = "AKXR9BF6YK0210RYS7ZR";
         
        private const String SECRET_KEY = "zFsqaYWKvDi0zuhJcu3gAGkqPotvaUV2ew6Ii2wB";

        public static async Task Main()
        {
            //we use live since the distinction between live and paper doesnt exist for the free version.
            //we basically opnly have https://api.alpaca.markets not the https://paper-api.alpaca.markets
            //https://github.com/alpacahq/alpaca-trade-api-csharp/wiki has info on apis

            var client = Environments.Live.GetAlpacaTradingClient(new SecretKey(KEY_ID, SECRET_KEY)); 
            var clock = await client.GetClockAsync();

            var alpacaTradingClient = Environments.Live.GetAlpacaTradingClient(new SecretKey(KEY_ID, SECRET_KEY));

            var alpacaDataClient = Environments.Live.GetAlpacaDataClient(new SecretKey(KEY_ID, SECRET_KEY));

            var asset = await alpacaTradingClient.GetAssetAsync("SPY");
            var account = await alpacaTradingClient.GetAccountAsync();
             

           
        }
    }
}
