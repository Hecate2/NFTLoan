using System.ComponentModel;
using System.Numerics;
using Neo;
using Neo.SmartContract.Framework;
using Neo.SmartContract.Framework.Attributes;
using Neo.SmartContract.Framework.Native;
using Neo.SmartContract.Framework.Services;

namespace NFTLoan
{
    [DisplayName("NFTFlashLoan")]
    [ManifestExtra("Author", "Hecate2")]
    [ManifestExtra("Email", "developer@neo.org")]
    [ManifestExtra("Description", "NFTFlashLoan")]
    public class NFTLoan : SmartContract
    {
        private const byte PREFIX_TOKEN_FOR_RENTAL = (byte)'r';  // token + (ByteString)(BigInteger)tokenId.Length + tokenId + renter -> StdLib.Serialize(amount, price)
        private const byte PREFIX_TOKEN_OF_OWNER = (byte)'o';    // renter + token + tokenId -> StdLib.Serialize(amount, price)

        public static void OnNEP11Payment(UInt160 from, BigInteger amount, ByteString tokenId, BigInteger data) { }
        public static BigInteger GetDecimals(UInt160 token) => (BigInteger)Contract.Call(token, "decimals", CallFlags.ReadStates);
        public static Iterator GetTokenForRental(UInt160 token) => new StorageMap(Storage.CurrentContext, PREFIX_TOKEN_FOR_RENTAL).Find(token, FindOptions.RemovePrefix);
        public static Iterator GetMyTokenForRental(UInt160 renter) => new StorageMap(Storage.CurrentContext, PREFIX_TOKEN_OF_OWNER).Find(renter, FindOptions.RemovePrefix);

        public static void SetRentalPrice(UInt160 renter, UInt160 token, ByteString tokenId, BigInteger price)
        {
            ExecutionEngine.Assert(Runtime.CheckWitness(renter), "No witness");

            StorageMap tokenForRental = new StorageMap(Storage.CurrentContext, PREFIX_TOKEN_FOR_RENTAL);
            ByteString key = token + (ByteString)(BigInteger)tokenId.Length + tokenId + renter;
            BigInteger[] amountAndPrice = (BigInteger[])StdLib.Deserialize(tokenForRental.Get(key));
            ExecutionEngine.Assert(amountAndPrice[0] > 0, "No token at rental");
            amountAndPrice[1] = price;
            ByteString serialized = StdLib.Serialize(amountAndPrice);
            tokenForRental.Put(key, serialized);

            StorageMap tokenOfOwner = new StorageMap(Storage.CurrentContext, PREFIX_TOKEN_OF_OWNER);
            tokenOfOwner.Put(renter + token + tokenId, serialized);
        }

        public static BigInteger RegisterRental(UInt160 renter, UInt160 token, BigInteger amountForRent, ByteString tokenId, BigInteger price)
        {
            ExecutionEngine.Assert(tokenId.Length <= 64, "tokenId.Length > 64");
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
            
            StorageMap tokenForRental = new(context, PREFIX_TOKEN_FOR_RENTAL);
            ByteString key = token + (ByteString)(BigInteger)tokenId.Length + tokenId + renter;
            BigInteger[] amountAndPrice = (BigInteger[])StdLib.Deserialize(tokenForRental.Get(key));
            amountAndPrice[0] += amountForRent;
            ByteString serialized = StdLib.Serialize(amountAndPrice);
            tokenForRental.Put(token + (ByteString)(BigInteger)tokenId.Length + tokenId + renter, serialized);

            StorageMap tokenOfOwner = new(context, PREFIX_TOKEN_OF_OWNER);
            tokenOfOwner.Put(renter + token + tokenId, serialized);
            return amountAndPrice[0];
        }

        public static BigInteger UnregisterRental(UInt160 renter, UInt160 token, BigInteger amountToUnregister, ByteString tokenId)
        {
            ExecutionEngine.Assert(Runtime.CheckWitness(renter), "No witness");
            // ExecutionEngine.Assert(amountToUnregister > 0, "amountToUnregister <= 0");  // unnecessary
            BigInteger decimals = GetDecimals(token);
            // if (decimals == 0) { amountToUnregister = 1; }  // unnecessary?

            StorageContext context = Storage.CurrentContext;

            ByteString key = renter + token + tokenId;
            StorageMap tokenOfOwner = new(context, PREFIX_TOKEN_OF_OWNER);
            BigInteger[] amountAndPrice = (BigInteger[])StdLib.Deserialize(tokenOfOwner.Get(key));
            amountAndPrice[0] -= amountToUnregister;
            ExecutionEngine.Assert(amountAndPrice[0] >= 0, "No enough token to unregister");
            if (amountAndPrice[0] == 0)
            {
                tokenOfOwner.Delete(key);
                new StorageMap(context, PREFIX_TOKEN_FOR_RENTAL).Delete(token + (ByteString)(BigInteger)tokenId.Length + tokenId + renter);
            }
            else
            {
                ByteString serialized = StdLib.Serialize(amountAndPrice);
                tokenOfOwner.Put(key, serialized);
                new StorageMap(context, PREFIX_TOKEN_FOR_RENTAL).Put(token + (ByteString)(BigInteger)tokenId.Length + tokenId + renter, serialized);
            }
            if (decimals == 0)
            {
                ExecutionEngine.Assert((bool)Contract.Call(token, "transfer", CallFlags.All, new object[] { renter, tokenId, null }), "Transfer failed");
            }
            else
            {
                ExecutionEngine.Assert((bool)Contract.Call(token, "transfer", CallFlags.All, new object[] { Runtime.ExecutingScriptHash, renter, amountToUnregister, tokenId, null }), "Transfer failed");
            }
            return amountAndPrice[0];
        }

        public static object FlashRentDivisible(
            UInt160 tenant, UInt160 token, ByteString tokenId, UInt160 renter, BigInteger neededAmount,
            UInt160 calledContract, string calledMethod, object[] arguments)
        {
            StorageContext context = Storage.CurrentContext;
            StorageMap tokenForRental = new(context, PREFIX_TOKEN_FOR_RENTAL);
            if (renter == UInt160.Zero)
            {
                // no renter assigned; borrow from any renter; tenant may suffer higher prices
                Iterator rentalIterator = tokenForRental.Find(token + (ByteString)(BigInteger)tokenId.Length + tokenId, FindOptions.RemovePrefix);
                BigInteger rentedAmount = 0;
                BigInteger stillNeededAmount;
                BigInteger availableAmount;
                while (rentalIterator.Next() && rentedAmount < neededAmount)
                {
                    ByteString[] kv = (ByteString[])rentalIterator.Value;
                    renter = (UInt160)kv[0];
                    BigInteger[] amountAndPrice = (BigInteger[])StdLib.Deserialize(kv[1]);
                    availableAmount = amountAndPrice[0];
                    stillNeededAmount = neededAmount - rentedAmount;
                    if (availableAmount > stillNeededAmount) { availableAmount = stillNeededAmount; }
                    rentedAmount += availableAmount;
                    ExecutionEngine.Assert((bool)Contract.Call(GAS.Hash, "transfer", CallFlags.All, new object[] { tenant, renter, availableAmount * amountAndPrice[1], null }), "GAS transfer failed");
                }
                ExecutionEngine.Assert(rentedAmount == neededAmount, "No enough NFTs to rent");
            }
            else
            {
                // renter assigned; borrow only from given renter; tenant probably can have better prices
                BigInteger[] amountAndPrice = (BigInteger[])StdLib.Deserialize(tokenForRental[token + (ByteString)(BigInteger)tokenId.Length + tokenId + renter]);
                ExecutionEngine.Assert(amountAndPrice[0] > neededAmount, "No enough NFTs to rent");
                ExecutionEngine.Assert((bool)Contract.Call(GAS.Hash, "transfer", CallFlags.All, new object[] { tenant, renter, amountAndPrice[0] * amountAndPrice[1], null }), "GAS transfer failed");
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
            StorageMap tokenForRental = new(Storage.CurrentContext, PREFIX_TOKEN_FOR_RENTAL);
            Iterator rentalIterator = tokenForRental.Find(token + (ByteString)(BigInteger)tokenId.Length + tokenId, FindOptions.RemovePrefix);
            ExecutionEngine.Assert(rentalIterator.Next(), "Failed to find renter");
            BigInteger[] amountAndPrice = (BigInteger[])StdLib.Deserialize((ByteString)rentalIterator.Value);
            ExecutionEngine.Assert((bool)Contract.Call(GAS.Hash, "transfer", CallFlags.All, new object[] { tenant, renter, amountAndPrice[1], null }), "GAS transfer failed");
            ExecutionEngine.Assert((bool)Contract.Call(token, "transfer", CallFlags.All, new object[] { tenant, tokenId, null }), "NFT transfer failed");

            object result = Contract.Call(calledContract, calledMethod, CallFlags.All, arguments);

            ExecutionEngine.Assert((bool)Contract.Call(token, "transfer", CallFlags.All, new object[] { Runtime.ExecutingScriptHash, tokenId, null }), "NFT payback failed");
            return result;
        }
    }
}
