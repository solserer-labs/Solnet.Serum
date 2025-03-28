using Solnet.Programs.Utilities;
using Solnet.Serum.Models.Flags;
using Solnet.Wallet;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Solnet.Serum.Models
{
    /// <summary>
    /// Represents an Open Orders Account in Serum.
    /// </summary>
    public class OpenOrdersAccount
    {
        #region Layout

        /// <summary>
        /// Represents the layout of the <see cref="OpenOrdersAccount"/> data structure.
        /// </summary>
        public static class Layout
        {
            /// <summary>
            /// The size of the data for the open orders account.
            /// </summary>
            public const int SpanLength = 3228;
            
            /// <summary>
            /// The size of the data for the orders segment of the open orders account.
            /// </summary>
            internal const int OrdersSpanLength = 2048;
            
            /// <summary>
            /// The size of the data for the client ids segment of the orders in this open orders account.
            /// </summary>
            internal const int ClientIdsSpanLength = 1024;
            
            /// <summary>
            /// The offset at which the public key of the associated Market begins.
            /// </summary>
            internal const int MarketOffset = 13;

            /// <summary>
            /// The offset at which the public key of the Owner begins.
            /// </summary>
            internal const int OwnerOffset = 45;
            
            /// <summary>
            /// The offset at which the value of the Base Token Free begins.
            /// </summary>
            internal const int BaseTokenFreeOffset = 77;
            
            /// <summary>
            /// The offset at which the value of the Base Token Total begins.
            /// </summary>
            internal const int BaseTokenTotalOffset = 85;

            /// <summary>
            /// The offset at which the value of the Quote Token Free begins.
            /// </summary>
            internal const int QuoteTokenFreeOffset = 93;

            /// <summary>
            /// The offset at which the value of the Quote Token Total begins.
            /// </summary>
            internal const int QuoteTokenTotalOffset = 101;

            /// <summary>
            /// The offset at which the value of the Free Slot Bids begins.
            /// </summary>
            internal const int FreeSlotBidsOffset = 109;

            /// <summary>
            /// The offset at which the value of the Is Bid Bits begins.
            /// </summary>
            internal const int BidBitsOffset = 125;
            
            /// <summary>
            /// The offset at which the value of the Orders begin.
            /// </summary>
            internal const int OrdersOffset = 141;

            /// <summary>
            /// The offset at which the value of the Client Ids begin.
            /// </summary>
            internal const int ClientIdsOffset = 2189;

            /// <summary>
            /// The offset at which the referrer rebates accrued value begins.
            /// </summary>
            internal const int ReferrerRebatesAccruedOffset = 3213;
        }

        #endregion
        
        /// <summary>
        /// The flags that define the account type.
        /// </summary>
        public AccountFlags Flags;

        /// <summary>
        /// The public key of the <see cref="Market"/> associated with this open orders account.
        /// </summary>
        public PublicKey Market;
        
        /// <summary>
        /// The public key of this account's owner.
        /// </summary>
        public PublicKey Owner;

        /// <summary>
        /// The amount of unsettled balance of the base token.
        /// </summary>
        public ulong BaseTokenFree;

        /// <summary>
        /// The amount of total balance of the base token.
        /// </summary>
        public ulong BaseTokenTotal;

        /// <summary>
        /// The amount of unsettled balance of the quote token.
        /// </summary>
        public ulong QuoteTokenFree;

        /// <summary>
        /// The amount of total balance of the quote token.
        /// </summary>
        public ulong QuoteTokenTotal;

        /// <summary>
        /// The bits that represent the free slots in the open orders account.
        /// </summary>
        public byte[] FreeSlotBits;

        /// <summary>
        /// The bits that represent the bid orders in the open orders account.
        /// </summary>
        public byte[] BidBits;

        /// <summary>
        /// The orders of this open orders account.
        /// </summary>
        public IList<OpenOrder> Orders;

        /// <summary>
        /// The referrer rebates accrued.
        /// </summary>
        public ulong ReferrerRebatesAccrued;
        
        /// <summary>
        /// Deserialize a span of bytes into a <see cref="OpenOrdersAccount"/> instance.
        /// </summary>
        /// <param name="data">The data to deserialize into the structure.</param>
        /// <returns>The Open Orders Account structure.</returns>
        public static OpenOrdersAccount Deserialize(byte[] data)
        {
            ReadOnlySpan<byte> span = data.AsSpan();
            List<OpenOrder> orders = new();
            ReadOnlySpan<byte> ordersData = span.Slice(Layout.OrdersOffset, Layout.OrdersSpanLength);
            ReadOnlySpan<byte> clientIds = span.Slice(Layout.ClientIdsOffset, Layout.ClientIdsSpanLength);
            ReadOnlySpan<byte> freeSlotBits = span.Slice(Layout.FreeSlotBidsOffset, 16);
            ReadOnlySpan<byte> isBidBits = span.Slice(Layout.BidBitsOffset, 16);

            for (int i = 0; i < 128; i++)
            {
                ulong clientId = clientIds.GetU64(i * 8);
                BigInteger orderId = ordersData.GetBigInt(i * 16, 16);
                ulong rawPrice = ordersData.GetU64((i * 16) + 8);
                
                bool isFreeSlot = freeSlotBits.CheckBit(i);
                bool isBid = isBidBits.CheckBit(i);

                if (!isFreeSlot)
                {
                    orders.Add(new OpenOrder
                    {
                        OrderIndex = i,
                        IsBid = isBid,
                        OrderId = orderId,
                        RawPrice = rawPrice,
                        ClientOrderId = clientId,
                    });
                }

            }

            return new OpenOrdersAccount
            {
                Flags = AccountFlags.Deserialize(span.Slice(5, 8)),
                Market = span.GetPubKey(Layout.MarketOffset),
                Owner = span.GetPubKey(Layout.OwnerOffset),
                BaseTokenFree = span.GetU64(Layout.BaseTokenFreeOffset),
                BaseTokenTotal = span.GetU64(Layout.BaseTokenTotalOffset),
                QuoteTokenFree = span.GetU64(Layout.QuoteTokenFreeOffset),
                QuoteTokenTotal = span.GetU64(Layout.QuoteTokenTotalOffset),
                BidBits = isBidBits.ToArray(),
                FreeSlotBits = freeSlotBits.ToArray(),
                Orders = orders,
                ReferrerRebatesAccrued = span.GetU64(Layout.ReferrerRebatesAccruedOffset)
            };
        }
    }
}