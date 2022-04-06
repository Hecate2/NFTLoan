using System.ComponentModel;
using System.Numerics;
using Neo;
using Neo.SmartContract.Framework;
using Neo.SmartContract.Framework.Attributes;
using Neo.SmartContract.Framework.Native;
using Neo.SmartContract.Framework.Services;

namespace NFTFlashLoan
{
    [DisplayName("NFTFlashLoan")]
    [ManifestExtra("Author", "Hecate2")]
    [ManifestExtra("Email", "developer@neo.org")]
    [ManifestExtra("Description", "NFTFlashLoan")]
    public class NFTFlashLoan : SmartContract
    {
        private const byte PREFIX_TOKEN_PRICE_FOR_RENTAL = (byte)'p';  // token + tokenId + renter -> price
        private const byte PREFIX_TOKEN_AMOUNT_FOR_RENTAL = (byte)'a'; // token + tokenId + renter -> amount
        private const byte PREFIX_TOKEN_PRICE_OF_OWNER = (byte)'q';    // renter + token + tokenId -> price
        private const byte PREFIX_TOKEN_AMOUNT_OF_OWNER = (byte)'b';   // renter + token + tokenId -> amount

        public static void OnNEP11Payment(UInt160 from, BigInteger amount, ByteString tokenId, BigInteger data)
        {
        }
        public static BigInteger GetDecimals(UInt160 token) => (BigInteger)Contract.Call(token, "decimals", CallFlags.ReadStates);
        public static Iterator GetTokenPricesForRental(UInt160 token) => new StorageMap(Storage.CurrentContext, PREFIX_TOKEN_PRICE_FOR_RENTAL).Find(token, FindOptions.RemovePrefix);
        public static Iterator GetTokenAmountsForRental(UInt160 token) => new StorageMap(Storage.CurrentContext, PREFIX_TOKEN_AMOUNT_FOR_RENTAL).Find(token, FindOptions.RemovePrefix);
        public static Iterator GetMyTokenPricesForRental(UInt160 renter) => new StorageMap(Storage.CurrentContext, PREFIX_TOKEN_PRICE_OF_OWNER).Find(renter, FindOptions.RemovePrefix);
        public static Iterator GetMyTokenAmountsForRental(UInt160 renter) => new StorageMap(Storage.CurrentContext, PREFIX_TOKEN_AMOUNT_OF_OWNER).Find(renter, FindOptions.RemovePrefix);

        public static void SetRentalPrice(UInt160 renter, UInt160 token, ByteString tokenId, BigInteger price)
        {
            ExecutionEngine.Assert(Runtime.CheckWitness(renter), "No witness");
            new StorageMap(Storage.CurrentContext, PREFIX_TOKEN_PRICE_FOR_RENTAL).Put(token + tokenId + renter, price);
        }

        public static BigInteger RegisterRental(UInt160 renter, UInt160 token, BigInteger amountForRent, ByteString tokenId, BigInteger price)
        {
            BigInteger decimals = GetDecimals(token);
            // ExecutionEngine.Assert(amountForRent > 0, "amountForRent <= 0");  // unnecessary
            // Transfer is very risky. Consider a whitelist of tokens. 
            if (decimals == 0)
            {
                ExecutionEngine.Assert((bool)Contract.Call(token, "transfer", CallFlags.All, new object[] { Runtime.ExecutingScriptHash, tokenId, null }), "Transfer failed");
                amountForRent = 1;
            }
            else
            {
                ExecutionEngine.Assert((bool)Contract.Call(token, "transfer", CallFlags.All, new object[] { renter, Runtime.ExecutingScriptHash, amountForRent, tokenId, null }), "Transfer failed");
            }

            StorageContext context = Storage.CurrentContext;
            
            ByteString key = token + tokenId + renter;  // risk?: length of tokenId is variant
            StorageMap rentPriceMap = new(context, PREFIX_TOKEN_PRICE_FOR_RENTAL);
            rentPriceMap.Put(key, price);
            StorageMap rentAmountMap = new(context, PREFIX_TOKEN_AMOUNT_FOR_RENTAL);
            BigInteger amount = (BigInteger)rentAmountMap.Get(key) + amountForRent;
            rentAmountMap.Put(key, amount);

            key = renter + token + tokenId;
            StorageMap ownerAmountMap = new(context, PREFIX_TOKEN_AMOUNT_OF_OWNER);
            ownerAmountMap.Put(key, amount);
            StorageMap ownerPriceMap = new(context, PREFIX_TOKEN_PRICE_OF_OWNER);
            ownerPriceMap.Put(key, price);
            return amount;
        }

        public static BigInteger UnregisterRental(UInt160 renter, UInt160 token, BigInteger amountToUnregister, ByteString tokenId)
        {
            ExecutionEngine.Assert(Runtime.CheckWitness(renter), "No witness");
            // ExecutionEngine.Assert(amountToUnregister > 0, "amountToUnregister <= 0");  // unnecessary
            BigInteger decimals = GetDecimals(token);
            // if (decimals == 0) { amountToUnregister = 1; }  // unnecessary?

            StorageContext context = Storage.CurrentContext;

            ByteString key = renter + token + tokenId;
            StorageMap ownerAmountMap = new(context, PREFIX_TOKEN_AMOUNT_OF_OWNER);
            BigInteger amount = (BigInteger)ownerAmountMap.Get(key) - amountToUnregister;
            ExecutionEngine.Assert(amount >= 0, "No enough token to unregister");
            if (amount == 0)
            {
                ownerAmountMap.Delete(key);
                StorageMap ownerPriceMap = new(context, PREFIX_TOKEN_PRICE_OF_OWNER);
                ownerPriceMap.Delete(key);
                key = token + tokenId + renter;  // risk?: length of tokenId is variant
                StorageMap rentAmountMap = new(context, PREFIX_TOKEN_AMOUNT_FOR_RENTAL);
                rentAmountMap.Delete(key);
                StorageMap rentPriceMap = new(context, PREFIX_TOKEN_PRICE_FOR_RENTAL);
                rentPriceMap.Delete(key);
            }
            else
            {
                ownerAmountMap.Put(key, amount);
                key = token + tokenId + renter;  // risk?: length of tokenId is variant
                StorageMap rentAmountMap = new(context, PREFIX_TOKEN_AMOUNT_FOR_RENTAL);
                rentAmountMap.Put(key, amount);
                StorageMap rentPriceMap = new(context, PREFIX_TOKEN_PRICE_FOR_RENTAL);
                rentPriceMap.Put(key, amount);
            }
            if (decimals == 0)
            {
                ExecutionEngine.Assert((bool)Contract.Call(token, "transfer", CallFlags.All, new object[] { renter, tokenId, null }), "Transfer failed");
            }
            else
            {
                ExecutionEngine.Assert((bool)Contract.Call(token, "transfer", CallFlags.All, new object[] { Runtime.ExecutingScriptHash, renter, amountToUnregister, tokenId, null }), "Transfer failed");
            }
            return amount;
        }

        public static object FlashRentDivisible(
            UInt160 tenant, UInt160 token, ByteString tokenId, UInt160 renter, BigInteger neededAmount,
            UInt160 calledContract, string calledMethod, object[] arguments)
        {
            ByteString key;
            BigInteger availableAmount, price;
            StorageContext context = Storage.CurrentContext;
            StorageMap rentPriceMap = new(context, PREFIX_TOKEN_PRICE_FOR_RENTAL);
            StorageMap rentAmountMap = new(context, PREFIX_TOKEN_AMOUNT_FOR_RENTAL);
            if (renter == UInt160.Zero)
            {
                // no renter assigned; borrow from any renter; tenant may suffer higher prices
                key = token + tokenId;
                Iterator amountIterator = rentAmountMap.Find(key, FindOptions.RemovePrefix);
                BigInteger rentedAmount = 0;
                BigInteger stillNeededAmount;
                while (amountIterator.Next() && rentedAmount < neededAmount)
                {
                    object[] kv = (object[])amountIterator.Value;
                    renter = (UInt160)kv[0];
                    availableAmount = (BigInteger)kv[1];
                    stillNeededAmount = neededAmount - rentedAmount;
                    if (availableAmount > stillNeededAmount) { availableAmount = stillNeededAmount; }
                    price = (BigInteger)rentPriceMap[key + renter];
                    rentedAmount += availableAmount;
                    ExecutionEngine.Assert((bool)Contract.Call(GAS.Hash, "transfer", CallFlags.All, new object[] { tenant, renter, availableAmount * price, null }), "GAS transfer failed");
                }
                ExecutionEngine.Assert(rentedAmount == neededAmount, "No enough NFTs to rent");
            }
            else
            {
                // renter assigned; borrow from given renter; tenant probably can have better prices
                key = token + tokenId + renter;
                availableAmount = (BigInteger)rentAmountMap[key];
                ExecutionEngine.Assert(availableAmount > neededAmount, "No enough NFTs to rent");
                if (availableAmount > neededAmount) { availableAmount = neededAmount; }
                price = (BigInteger)rentPriceMap[key];
                ExecutionEngine.Assert((bool)Contract.Call(GAS.Hash, "transfer", CallFlags.All, new object[] { tenant, renter, neededAmount * price, null }), "GAS transfer failed");
            }
            ExecutionEngine.Assert((bool)Contract.Call(token, "transfer", CallFlags.All, new object[] { Runtime.ExecutingScriptHash, tenant, neededAmount, tokenId, null }), "NFT transfer failed");

            object result = Contract.Call(calledContract, calledMethod, CallFlags.All, arguments);

            ExecutionEngine.Assert((bool)Contract.Call(token, "transfer", CallFlags.All, new object[] { Runtime.ExecutingScriptHash, tenant, neededAmount, tokenId, null }), "NFT payback failed");
            return result;
        }

        public static object FlashRentNonDivisible
            (UInt160 tenant, UInt160 token, ByteString tokenId, UInt160 renter,
            UInt160 calledContract, string calledMethod, object[] arguments)
        {
            ByteString key;
            BigInteger price;
            StorageContext context = Storage.CurrentContext;
            StorageMap rentPriceMap = new(context, PREFIX_TOKEN_PRICE_FOR_RENTAL);
            StorageMap rentAmountMap = new(context, PREFIX_TOKEN_AMOUNT_FOR_RENTAL);
            key = token + tokenId;
            Iterator amountIterator = rentAmountMap.Find(key, FindOptions.RemovePrefix);
            ExecutionEngine.Assert(amountIterator.Next(), "Failed to find renter");
            price = (BigInteger)rentPriceMap[key + ((ByteString[])amountIterator.Value)[0]];
            ExecutionEngine.Assert((bool)Contract.Call(GAS.Hash, "transfer", CallFlags.All, new object[] { tenant, renter, price, null }), "GAS transfer failed");
            ExecutionEngine.Assert((bool)Contract.Call(token, "transfer", CallFlags.All, new object[] { tenant, tokenId, null }), "NFT transfer failed");

            object result = Contract.Call(calledContract, calledMethod, CallFlags.All, arguments);

            ExecutionEngine.Assert((bool)Contract.Call(token, "transfer", CallFlags.All, new object[] { Runtime.ExecutingScriptHash, tokenId, null }), "NFT payback failed");
            return result;
        }
    }
}
