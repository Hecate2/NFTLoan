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
    public class NFTLoan : DivisibleNep11Token<DivisibleNep11TokenState>
    {
        private const byte PREFIX_TOKEN_FOR_RENTAL = (byte)'r';  // token + (ByteString)(BigInteger)tokenId.Length + tokenId + renter -> StdLib.Serialize(amount, price)
        private const byte PREFIX_TOKEN_OF_OWNER = (byte)'o';    // renter + token + tokenId -> StdLib.Serialize(amount, price)
        private const byte PREFIX_TOKENID_INTERNAL_TO_EXTERNAL = (byte)'i';  // internal tokenId -> external token contract + tokenId
        private const byte PREFIX_TOKENID_EXTERNAL_TO_INTERNAL = (byte)'e';  // external token contract + tokenId -> internal tokenId
        private const byte PREFIX_RENTAL_DEADLINE = (byte)'d';    // ?? renter + tenant + amount + internalTokenId + start time -> deadline

        public static void OnNEP11Payment(UInt160 from, BigInteger amount, ByteString tokenId, BigInteger data) { }
        public override string Symbol() => "NEPHRENT";
        public static BigInteger GetDecimals(UInt160 token) => (BigInteger)Contract.Call(token, "decimals", CallFlags.ReadStates);
        public static Iterator GetTokenForRental(UInt160 token) => new StorageMap(Storage.CurrentContext, PREFIX_TOKEN_FOR_RENTAL).Find(token, FindOptions.RemovePrefix);
        public static Iterator GetMyTokenForRental(UInt160 renter) => new StorageMap(Storage.CurrentContext, PREFIX_TOKEN_OF_OWNER).Find(renter, FindOptions.RemovePrefix);
        public static ByteString GetExternalTokenInfo(ByteString internalTokenId) => new StorageMap(Storage.CurrentContext, PREFIX_TOKENID_INTERNAL_TO_EXTERNAL).Get(internalTokenId);
        public static Iterator ListExternalTokenInfo(ByteString prefix) => new StorageMap(Storage.CurrentContext, PREFIX_TOKENID_EXTERNAL_TO_INTERNAL).Find(prefix);
        public static ByteString GetInternalTokenId(UInt160 externalTokenContract, ByteString externalTokenId) => new StorageMap(Storage.CurrentContext, PREFIX_TOKENID_EXTERNAL_TO_INTERNAL).Get(externalTokenContract + externalTokenId);
        public static Iterator ListInternalTokenId(ByteString prefix) => new StorageMap(Storage.CurrentContext, PREFIX_TOKENID_INTERNAL_TO_EXTERNAL).Find(prefix);

        public static void SetRentalPrice(UInt160 renter, UInt160 token, ByteString tokenId, BigInteger price)
        {
            ExecutionEngine.Assert(Runtime.CheckWitness(renter), "No witness");

            StorageMap tokenForRental = new(Storage.CurrentContext, PREFIX_TOKEN_FOR_RENTAL);
            ByteString key = token + (ByteString)(BigInteger)tokenId.Length + tokenId + renter;
            BigInteger[] amountAndPrice = (BigInteger[])StdLib.Deserialize(tokenForRental.Get(key));
            ExecutionEngine.Assert(amountAndPrice[0] > 0, "No token at rental");
            amountAndPrice[1] = price;
            ByteString serialized = StdLib.Serialize(amountAndPrice);
            tokenForRental.Put(key, serialized);

            StorageMap tokenOfOwner = new (Storage.CurrentContext, PREFIX_TOKEN_OF_OWNER);
            tokenOfOwner.Put(renter + token + tokenId, serialized);
        }

        private static ByteString MintSubTokenForRental(UInt160 owner, UInt160 originalContract, ByteString externalTokenId, BigInteger amount)
        {
            StorageContext context = Storage.CurrentContext;
            StorageMap externalToInternal = new(context, PREFIX_TOKENID_EXTERNAL_TO_INTERNAL);
            ByteString externalTokenInfo = originalContract + externalTokenId;
            UInt160 internalTokenId = (UInt160)externalToInternal.Get(externalTokenInfo);
            if (internalTokenId == UInt160.Zero)
            {
                internalTokenId = (UInt160)NewTokenId();
                externalToInternal.Put(externalTokenId, internalTokenId);
                new StorageMap(context, PREFIX_TOKENID_INTERNAL_TO_EXTERNAL).Put(internalTokenId, externalTokenInfo);
            }
            Mint(owner, amount, internalTokenId, new DivisibleNep11TokenState { Name = externalTokenInfo });
            // Ideally mint the new tokens for this contract, and immediately register the new tokens for ordinary rental
            return internalTokenId;
        }

        private static void RegisterOrdinaryLoan(UInt160 renter, BigInteger amount, ByteString internalTokenId, BigInteger price)
        {
            // TODO
        }

        public static BigInteger RegisterRental(
            UInt160 renter, UInt160 tokenContract, BigInteger amountForRent, ByteString tokenId,
            BigInteger flashLoanPrice, BigInteger ordinaryLoanPrice)
        {
            ExecutionEngine.Assert(tokenId.Length <= 64, "tokenId.Length > 64");
            ExecutionEngine.Assert(tokenContract != Runtime.ExecutingScriptHash, "Cannot register rental for tokens issued by this contract");
            BigInteger decimals = GetDecimals(tokenContract);
            // ExecutionEngine.Assert(amountForRent > 0, "amountForRent <= 0");  // unnecessary
            // Transfer is very risky. Consider a whitelist of tokens. 
            if (decimals == 0)
            {
                ExecutionEngine.Assert((bool)Contract.Call(tokenContract, "transfer", CallFlags.All, new object[] { Runtime.ExecutingScriptHash, tokenId, null }), "Transfer failed");
                amountForRent = 1;
            }
            else
            {
                ExecutionEngine.Assert((bool)Contract.Call(tokenContract, "transfer", CallFlags.All, new object[] { renter, Runtime.ExecutingScriptHash, amountForRent, tokenId, null }), "Transfer failed");
            }

            StorageContext context = Storage.CurrentContext;
            
            StorageMap tokenForRental = new(context, PREFIX_TOKEN_FOR_RENTAL);
            ByteString key = tokenContract + (ByteString)(BigInteger)tokenId.Length + tokenId + renter;
            BigInteger[] amountAndPrice = (BigInteger[])StdLib.Deserialize(tokenForRental.Get(key));
            amountAndPrice[0] += amountForRent;
            amountAndPrice[1] = flashLoanPrice;
            ByteString serialized = StdLib.Serialize(amountAndPrice);
            tokenForRental.Put(tokenContract + (ByteString)(BigInteger)tokenId.Length + tokenId + renter, serialized);

            StorageMap tokenOfOwner = new(context, PREFIX_TOKEN_OF_OWNER);
            tokenOfOwner.Put(renter + tokenContract + tokenId, serialized);

            ByteString internalTokenId = MintSubTokenForRental(renter, tokenContract, tokenId, amountForRent);
            RegisterOrdinaryLoan(renter, amountForRent, internalTokenId, ordinaryLoanPrice);

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
