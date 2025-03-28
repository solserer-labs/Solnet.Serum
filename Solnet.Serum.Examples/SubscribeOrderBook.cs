// unset

using Solnet.Rpc;
using Solnet.Serum.Models;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Solnet.Serum.Examples
{
    public class SubscribeOrderBook: IRunnableExample
    {
        private readonly ISerumClient _serumClient;

        private static Dictionary<string, string> Markets = new Dictionary<string, string>()
        {
            {"SXP/USDC", "4LUro5jaPaTurXK737QAxgJywdhABnFAMQkXX4ZyqqaZ"},
            {"SOL/USDC", "9wFFyRfZBsuAha4YcuxcXLKwMxJR43S7fPfQLusDBzvT"},/*
            {"SRM/USDT", "AtNnsY1AyRERWJ8xCskfz38YdvruWVJQUVXgScC1iPb"},*/
        };

        private Dictionary<string, List<OpenOrder>> allAskOrders;
        private Dictionary<string, List<OpenOrder>> allBidOrders;

        public SubscribeOrderBook()
        {
            var rpcClient = Solnet.Rpc.ClientFactory.GetClient(Cluster.MainNet);
            var streamingRpcClient = Solnet.Rpc.ClientFactory.GetStreamingClient(Cluster.MainNet);
            _serumClient = ClientFactory.GetClient(rpcClient, streamingRpcClient);
            _serumClient.ConnectAsync().Wait();
            allAskOrders = new Dictionary<string, List<OpenOrder>>();
            allBidOrders = new Dictionary<string, List<OpenOrder>>();
            Console.WriteLine($"Initializing {ToString()}");
        }

        public void Run()
        {
            foreach ((string key, string value) in Markets)
            {
                SubscribeTo(value, key);
            }
            
            Console.ReadKey();
        }
        
        public Task SubscribeTo(string address, string name)
        {
            return Task.Run(() =>
            {
                Market market = _serumClient.GetMarket(address);
                    
                Console.WriteLine($"{name} Market:: Own Address: {market.OwnAddress.Key} Base Mint: {market.BaseMint.Key} Quote Mint: {market.QuoteMint.Key}");
                    
                Subscription subBids = _serumClient.SubscribeOrderBookSide((subWrapper, orderBook, _) =>
                {
                    Console.WriteLine($"{name} BidOrderBook Update:: SlabNodes: {orderBook.Slab.Nodes.Count}\n");
                    var bidOrders = orderBook.GetOrders(); 
                    bidOrders.Sort(Comparer<OpenOrder>.Create((order, order1) => order1.RawPrice.CompareTo(order.RawPrice)));
                    
                    allBidOrders.TryAdd(name, bidOrders);
                    bool exists = allAskOrders.TryGetValue(name, out List<OpenOrder> askOrders);
                    Console.WriteLine($"-------------------------");
                    Console.WriteLine($"----------ASKS-----------");
                    if (exists)
                    {
                        for (int i = 4; i >= 0; i--)
                        {
                            Console.WriteLine($"{name} Ask:\t{askOrders[i].RawPrice}\tSize:\t{askOrders[i].RawQuantity}");
                        }
                    }
                    Console.WriteLine($"-----------BIDS----------");
                    for (int i = 0; i < 5; i++)
                    {
                        Console.WriteLine($"{name} Bid:\t{bidOrders[i].RawPrice}\tSize:\t{bidOrders[i].RawQuantity}");
                    }
                    Console.WriteLine($"-------------------------\n");
                    
                }, market.Bids);
                
                Subscription subAsks = _serumClient.SubscribeOrderBookSide((subWrapper, orderBook, _) =>
                {
                    Console.WriteLine($"{name} AskOrderBook Update:: SlabNodes: {orderBook.Slab.Nodes.Count}\n"); 
                    var askOrders = orderBook.GetOrders();
                    askOrders.Sort(Comparer<OpenOrder>.Create((order, order1) => order.RawPrice.CompareTo(order1.RawPrice)));

                    allAskOrders.TryAdd(name, askOrders);
                    bool exists = allAskOrders.TryGetValue(name, out List<OpenOrder> bidOrders);
                    Console.WriteLine($"-------------------------");
                    Console.WriteLine($"----------ASKS-----------");
                    for (int i = 4; i >= 0; i--)
                    {
                        Console.WriteLine($"{name} Ask:\t{askOrders[i].RawPrice}\tSize:\t{askOrders[i].RawQuantity}");
                    }
                    Console.WriteLine($"-----------BIDS----------");
                    if (exists)
                    {
                        for (int i = 0; i < 5; i++)
                        {
                            Console.WriteLine(
                                $"{name} Bid:\t{bidOrders[i].RawPrice}\tSize:\t{bidOrders[i].RawQuantity}");
                        }
                    }
                    Console.WriteLine($"-------------------------\n");
                    
                }, market.Asks);
            });
        }
    }
}