// Give me your NFTs (registerRental), and I mint the corresponding twinborn NFTs. 
// The minted NFTs represent the right of use of the original NFTs, and can be rented by others. 
// Your original NFTs (and the minted NFTs?) can be flash-loaned by others. 

using System;
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
        private const uint MAX_RENTAL_PERIOD = 86400 * 1000 * 10;  // 10 days; uint supports <= 49 days
        private const uint MIN_RENTAL_PRICE = 100;
        private const uint COLLATERAL_MULTIPLIER = 5000;
        private const uint COLLATERAL_DIVISOR = 10000;
        private const byte PREFIX_TOKEN_FOR_RENTAL = (byte)'r';  // token + [EXTERNAL](ByteString)(BigInteger)tokenId.Length + tokenId + renter -> StdLib.Serialize(amount, price)
        private const byte PREFIX_TOKEN_OF_RENTER = (byte)'o';    // renter + token + [EXTERNAL]tokenId -> StdLib.Serialize(amount, price)
        private const byte PREFIX_TOKEN_RENTER_DEADLINE = (byte)'d';    // renter + [INTERNAL](ByteString)(BigInteger)tokenId.Length + tokenId + tenant + start_time -> StdLib.Serialize(amount, collateral, deadline, OpenForNextRental)
        private const byte PREFIX_TOKEN_TENANT_DEADLINE = (byte)'t';    // tenant + [INTERNAL](ByteString)(BigInteger)tokenId.Length + tokenId + renter + start_time -> StdLib.Serialize(amount, collateral, deadline, OpenForNextRental)
        private const byte PREFIX_TOKENID_INTERNAL_TO_EXTERNAL = (byte)'i';  // internal tokenId -> external token contract + tokenId
        private const byte PREFIX_TOKENID_EXTERNAL_TO_INTERNAL = (byte)'e';  // external token contract + tokenId -> internal tokenId

        //public enum OpenForNextRental
        //{
        //    // Feature: Owner can stop the token from being rented again
        //    OpenForNextRental = 1,
        //    ClosedForNextRental = 0,
        //}

        // borrower should pay a collateral to this contract.
        // If a rental has expired, the collateral is given to anyone who revoke the rental

        // TODO: fire events

        public static void OnNEP11Payment(UInt160 from, BigInteger amount, ByteString tokenId, BigInteger data)
        {
            ExecutionEngine.Assert(Runtime.CallingScriptHash != Runtime.ExecutingScriptHash,
                "Do not send NFTs from this contract into this contract! You probably need the method payback or registerRental.");
        }
        public override string Symbol() => "NEPHRENT";
        public static BigInteger GetDecimals(UInt160 token) => (BigInteger)Contract.Call(token, "decimals", CallFlags.ReadStates);
        public static BigInteger BalanceOfRentalToken(ByteString tokenId) => BalanceOf(Runtime.ExecutingScriptHash, tokenId);

        public static Iterator ListTokenForRental(UInt160 token) => new StorageMap(Storage.CurrentContext, PREFIX_TOKEN_FOR_RENTAL).Find(token, FindOptions.RemovePrefix);
        public static Iterator ListTokenForRental(UInt160 token, ByteString tokenId) => new StorageMap(Storage.CurrentContext, PREFIX_TOKEN_FOR_RENTAL).Find(token + (ByteString)(BigInteger)tokenId.Length + tokenId, FindOptions.RemovePrefix);
        public static BigInteger[] GetTokenForRental(UInt160 token, ByteString tokenId, UInt160 renter) => (BigInteger[])StdLib.Deserialize(new StorageMap(Storage.CurrentContext, PREFIX_TOKEN_FOR_RENTAL).Get(token + (ByteString)(BigInteger)tokenId.Length + tokenId + renter));

        public static Iterator ListRenterToken(UInt160 renter) => new StorageMap(Storage.CurrentContext, PREFIX_TOKEN_OF_RENTER).Find(renter, FindOptions.RemovePrefix);
        public static Iterator ListRenterToken(UInt160 renter, UInt160 token) => new StorageMap(Storage.CurrentContext, PREFIX_TOKEN_OF_RENTER).Find(renter + token, FindOptions.RemovePrefix);
        public static BigInteger[] GetRenterToken(UInt160 renter, UInt160 token, ByteString tokenId) => (BigInteger[])StdLib.Deserialize(new StorageMap(Storage.CurrentContext, PREFIX_TOKEN_OF_RENTER).Get(renter + token + tokenId));

        public static Iterator ListRenterDeadline(UInt160 renter) => new StorageMap(Storage.CurrentContext, PREFIX_TOKEN_RENTER_DEADLINE).Find(renter, FindOptions.RemovePrefix);
        public static Iterator ListRenterDeadline(UInt160 renter, UInt160 token) => new StorageMap(Storage.CurrentContext, PREFIX_TOKEN_RENTER_DEADLINE).Find(renter + token, FindOptions.RemovePrefix);
        public static Iterator ListRenterDeadline(UInt160 renter, UInt160 token, ByteString tokenId) => new StorageMap(Storage.CurrentContext, PREFIX_TOKEN_RENTER_DEADLINE).Find(renter + token + (ByteString)(BigInteger)tokenId.Length + tokenId, FindOptions.RemovePrefix);
        public static Iterator ListRenterDeadline(UInt160 renter, UInt160 token, ByteString tokenId, UInt160 tenant) => new StorageMap(Storage.CurrentContext, PREFIX_TOKEN_RENTER_DEADLINE).Find(renter + token + (ByteString)(BigInteger)tokenId.Length + tokenId + tenant, FindOptions.RemovePrefix);
        public static BigInteger[] GetRenterDeadline(UInt160 renter, UInt160 token, ByteString tokenId, UInt160 tenant, BigInteger startTime) => (BigInteger[])StdLib.Deserialize(new StorageMap(Storage.CurrentContext, PREFIX_TOKEN_RENTER_DEADLINE).Get(renter + token + (ByteString)(BigInteger)tokenId.Length + tokenId + tenant + (ByteString)startTime));

        public static Iterator ListTenantDeadline(UInt160 tenant) => new StorageMap(Storage.CurrentContext, PREFIX_TOKEN_TENANT_DEADLINE).Find(tenant, FindOptions.RemovePrefix);
        public static Iterator ListTenantDeadline(UInt160 tenant, UInt160 token) => new StorageMap(Storage.CurrentContext, PREFIX_TOKEN_TENANT_DEADLINE).Find(tenant + token, FindOptions.RemovePrefix);
        public static Iterator ListTenantDeadline(UInt160 tenant, UInt160 token, ByteString tokenId) => new StorageMap(Storage.CurrentContext, PREFIX_TOKEN_TENANT_DEADLINE).Find(tenant + token + (ByteString)(BigInteger)tokenId.Length + tokenId, FindOptions.RemovePrefix);
        public static Iterator ListTenantDeadline(UInt160 tenant, UInt160 token, ByteString tokenId, UInt160 renter) => new StorageMap(Storage.CurrentContext, PREFIX_TOKEN_TENANT_DEADLINE).Find(tenant + token + (ByteString)(BigInteger)tokenId.Length + tokenId + renter, FindOptions.RemovePrefix);
        public static BigInteger[] GetTenantDeadline(UInt160 tenant, UInt160 token, ByteString tokenId, UInt160 renter, BigInteger startTime) => (BigInteger[])StdLib.Deserialize(new StorageMap(Storage.CurrentContext, PREFIX_TOKEN_TENANT_DEADLINE).Get(tenant + token + (ByteString)(BigInteger)tokenId.Length + tokenId + renter + (ByteString)startTime));

        public static Iterator ListExternalTokenInfo(ByteString prefix) => new StorageMap(Storage.CurrentContext, PREFIX_TOKENID_INTERNAL_TO_EXTERNAL).Find(prefix);
        public static ByteString GetExternalTokenInfo(ByteString internalTokenId) => new StorageMap(Storage.CurrentContext, PREFIX_TOKENID_INTERNAL_TO_EXTERNAL).Get(internalTokenId);
        public static Iterator ListInternalTokenId(ByteString prefix) => new StorageMap(Storage.CurrentContext, PREFIX_TOKENID_EXTERNAL_TO_INTERNAL).Find(prefix);
        public static Iterator ListInternalTokenId(UInt160 externalTokenContract, ByteString prefix) => new StorageMap(Storage.CurrentContext, PREFIX_TOKENID_EXTERNAL_TO_INTERNAL).Find(externalTokenContract + prefix);
        public static ByteString GetInternalTokenId(UInt160 externalTokenContract, ByteString externalTokenId) => new StorageMap(Storage.CurrentContext, PREFIX_TOKENID_EXTERNAL_TO_INTERNAL).Get(externalTokenContract + externalTokenId);

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public static BigInteger Max(BigInteger v1, BigInteger v2) => v1 > v2 ? v1 : v2;

        public new static bool Transfer(UInt160 from, UInt160 to, BigInteger amount, ByteString tokenId, object data)
        {
            if (from != Runtime.ExecutingScriptHash) return false;
            if (!Runtime.CheckWitness(from)) return false;
            if (to is null || !to.IsValid)
                throw new Exception("The argument \"to\" is invalid.");
            if (amount < 0) throw new Exception("amount < 0");
            if (from != to)
            {
                UpdateBalance(from, tokenId, -amount);
                UpdateBalance(to, tokenId, +amount);
            }
            PostTransfer(from, to, tokenId, data);
            return true;
        }

        private new static ByteString NewTokenId()
        {
            StorageContext context = Storage.CurrentContext;
            byte[] key = new byte[] { Prefix_TokenId };
            ByteString id = Storage.Get(context, key);
            ExecutionEngine.Assert(id.Length < 0xFD, "Too long id");
            Storage.Put(context, key, (BigInteger)id + 1);
            return id;
        }

        public static void SetRentalPrice(UInt160 renter, UInt160 tokenContract, ByteString tokenId, BigInteger price)
        {
            ExecutionEngine.Assert(Runtime.CheckWitness(renter), "No witness");

            StorageMap tokenForRentalMap = new(Storage.CurrentContext, PREFIX_TOKEN_FOR_RENTAL);
            ByteString key = tokenContract + (ByteString)(BigInteger)tokenId.Length + tokenId + renter;
            BigInteger[] amountAndPrice = (BigInteger[])StdLib.Deserialize(tokenForRentalMap.Get(key));
            ExecutionEngine.Assert(amountAndPrice[0] > 0, "No token at rental");
            amountAndPrice[1] = price;
            ByteString serialized = StdLib.Serialize(amountAndPrice);
            tokenForRentalMap.Put(key, serialized);

            StorageMap tokenOfRenterMap = new (Storage.CurrentContext, PREFIX_TOKEN_OF_RENTER);
            tokenOfRenterMap.Put(renter + tokenContract + tokenId, serialized);
        }

        private static ByteString MintSubToken(UInt160 originalContract, ByteString externalTokenId, BigInteger amount)
        {
            StorageContext context = Storage.CurrentContext;
            StorageMap externalToInternal = new(context, PREFIX_TOKENID_EXTERNAL_TO_INTERNAL);
            ByteString externalTokenInfo = originalContract + externalTokenId;
            ByteString internalTokenId = externalToInternal.Get(externalTokenInfo);
            if (internalTokenId == UInt160.Zero)
            {
                internalTokenId = NewTokenId();
                externalToInternal.Put(externalTokenId, internalTokenId);
                new StorageMap(context, PREFIX_TOKENID_INTERNAL_TO_EXTERNAL).Put(internalTokenId, externalTokenInfo);
            }
            Mint(Runtime.ExecutingScriptHash, amount, internalTokenId, new DivisibleNep11TokenState { Name = externalTokenInfo });
            return internalTokenId;
        }

        private static BigInteger RegisterLoan(
            StorageMap tokenForRentalMap, StorageMap tokenOfRenterMap,
            UInt160 tokenContract, UInt160 renter, BigInteger amount,
            ByteString tokenId, BigInteger price)
        {
            // if tokenContract is not Runtime.ExecutingScriptHash, then tokenId is just
            // the tokenId of the external tokenContract
            // else tokenId is the internalTokenId of Runtime.ExecutingScriptHash
            ByteString key = tokenContract + (ByteString)(BigInteger)tokenId.Length + tokenId + renter;
            BigInteger[] amountAndPrice = (BigInteger[])StdLib.Deserialize(tokenForRentalMap.Get(key));
            amountAndPrice[0] += amount;
            amountAndPrice[1] = price;
            ByteString serialized = StdLib.Serialize(amountAndPrice);
            tokenForRentalMap.Put(key, serialized);
            tokenOfRenterMap.Put(renter + tokenContract + tokenId, serialized);
            return amountAndPrice[0];
        }
        private static BigInteger RegisterLoan(
            StorageMap tokenForRentalMap, StorageMap tokenOfRenterMap,
            UInt160 tokenContract, UInt160 renter, BigInteger amount,
            ByteString tokenId)
        {
            // if tokenContract is not Runtime.ExecutingScriptHash, then tokenId is just
            // the tokenId of the external tokenContract
            // else tokenId is the internalTokenId of Runtime.ExecutingScriptHash
            ByteString key = tokenContract + (ByteString)(BigInteger)tokenId.Length + tokenId + renter;
            BigInteger[] amountAndPrice = (BigInteger[])StdLib.Deserialize(tokenForRentalMap.Get(key));
            amountAndPrice[0] += amount;
            ByteString serialized = StdLib.Serialize(amountAndPrice);
            tokenForRentalMap.Put(key, serialized);
            tokenOfRenterMap.Put(renter + tokenContract + tokenId, serialized);
            return amountAndPrice[0];
        }

        /// <summary>
        /// Give me your NFTs using this method!
        /// </summary>
        /// <param name="flashLoanPrice">GAS (1e8) price for each loan. Minimum: <see cref="MIN_RENTAL_PRICE"/>.</param>
        /// <param name="ordinaryLoanPrice">GAS (1e8) price per second (not millisecond!). Minimum for each loan: <see cref="MIN_RENTAL_PRICE"/>.</param>
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
            StorageMap tokenForRentalMap = new(context, PREFIX_TOKEN_FOR_RENTAL);
            StorageMap tokenOfRenterMap = new(context, PREFIX_TOKEN_OF_RENTER);

            RegisterLoan(tokenForRentalMap, tokenOfRenterMap, tokenContract, renter, amountForRent, tokenId, flashLoanPrice);
            ByteString internalTokenId = MintSubToken(tokenContract, tokenId, amountForRent);
            return RegisterLoan(tokenForRentalMap, tokenOfRenterMap, Runtime.ExecutingScriptHash, renter, amountForRent, internalTokenId, ordinaryLoanPrice);
        }

        private static ByteString BurnSubToken(UInt160 originalContract, ByteString externalTokenId, BigInteger amount)
        {
            StorageContext context = Storage.CurrentContext;
            StorageMap externalToInternal = new(context, PREFIX_TOKENID_EXTERNAL_TO_INTERNAL);
            ByteString externalTokenInfo = originalContract + externalTokenId;
            UInt160 internalTokenId = (UInt160)externalToInternal.Get(externalTokenInfo);
            ExecutionEngine.Assert(internalTokenId != UInt160.Zero, "Failed to find the token to burn");
            Burn(Runtime.ExecutingScriptHash, amount, internalTokenId);
            return internalTokenId;
        }

        private static BigInteger UnregisterLoan(
            StorageMap tokenForRentalMap, StorageMap tokenOfRenterMap,
            UInt160 tokenContract, UInt160 renter, BigInteger amount,
            ByteString tokenId)
        {
            // if tokenContract is not Runtime.ExecutingScriptHash, then tokenId is just
            // the tokenId of the external tokenContract
            // else tokenId is the internalTokenId of Runtime.ExecutingScriptHash
            ByteString key = tokenContract + (ByteString)(BigInteger)tokenId.Length + tokenId + renter;
            BigInteger[] amountAndPrice = (BigInteger[])StdLib.Deserialize(tokenForRentalMap.Get(key));
            amountAndPrice[0] -= amount;
            amount = amountAndPrice[0];
            ExecutionEngine.Assert(amount >= 0, "No enough token to unregister");
            if (amount > 0)
            {
                ByteString serialized = StdLib.Serialize(amountAndPrice);
                tokenForRentalMap.Put(key, serialized);
                tokenOfRenterMap.Put(renter + tokenContract + tokenId, serialized);
            }
            else
            {
                tokenForRentalMap.Delete(key);
                tokenOfRenterMap.Delete(renter + tokenContract + tokenId);
            }
            return amount;
        }

        public static BigInteger UnregisterRental(UInt160 renter, UInt160 tokenContract, BigInteger amountToUnregister, ByteString tokenId)
        {
            ExecutionEngine.Assert(Runtime.CheckWitness(renter), "No witness");
            ExecutionEngine.Assert(tokenContract != Runtime.ExecutingScriptHash, "Cannot unregister rental for tokens issued by this contract");
            // ExecutionEngine.Assert(amountToUnregister > 0, "amountToUnregister <= 0");  // unnecessary
            // BigInteger decimals = GetDecimals(tokenContract);  // unnecessary?
            // if (decimals == 0) { amountToUnregister = 1; }  // unnecessary?

            StorageContext context = Storage.CurrentContext;
            StorageMap tokenForRentalMap = new(context, PREFIX_TOKEN_FOR_RENTAL);
            StorageMap tokenOfRenterMap = new(context, PREFIX_TOKEN_OF_RENTER);
            UnregisterLoan(tokenForRentalMap, tokenOfRenterMap, tokenContract, renter, amountToUnregister, tokenId);
            ByteString internalTokenId = BurnSubToken(tokenContract, tokenId, amountToUnregister);
            return UnregisterLoan(tokenForRentalMap, tokenOfRenterMap, Runtime.ExecutingScriptHash, renter, amountToUnregister, internalTokenId);
        }

        public static BigInteger getTotalPrice(BigInteger pricePerSecond, BigInteger borrowTimeMilliseconds) => Max(pricePerSecond * borrowTimeMilliseconds / 1000, MIN_RENTAL_PRICE);
        public static BigInteger getCollateralAmount(BigInteger totalPrice) => totalPrice * COLLATERAL_MULTIPLIER / COLLATERAL_DIVISOR;

        public static BigInteger Borrow(UInt160 renter, UInt160 tenant, BigInteger amount, UInt160 externalTokenContract, ByteString externalTokenId, BigInteger borrowTimeMilliseconds)
        {
            ByteString internalTokenId = new StorageMap(Storage.CurrentContext, PREFIX_TOKENID_EXTERNAL_TO_INTERNAL).Get(externalTokenContract + externalTokenId);
            return Borrow(renter, tenant, amount, internalTokenId, externalTokenContract, externalTokenId, borrowTimeMilliseconds);
        }

        public static BigInteger Borrow(UInt160 renter, UInt160 tenant, BigInteger amount, ByteString internalTokenId, BigInteger borrowTimeMilliseconds)
        {
            ByteString[] externalTokenContractAndId = (ByteString[])StdLib.Deserialize(new StorageMap(Storage.CurrentContext, PREFIX_TOKENID_INTERNAL_TO_EXTERNAL).Get(internalTokenId));
            return Borrow(renter, tenant, amount, internalTokenId, (UInt160)externalTokenContractAndId[0], externalTokenContractAndId[1], borrowTimeMilliseconds);
        }

        private static BigInteger Borrow(UInt160 renter, UInt160 tenant, BigInteger amount, ByteString internalTokenId, UInt160 externalTokenContract, ByteString externalTokenId, BigInteger borrowTimeMilliseconds)
        {
            // No need to Runtime.CheckWitness(tenant).
            BigInteger startTime = Runtime.Time;
            ExecutionEngine.Assert(borrowTimeMilliseconds < MAX_RENTAL_PERIOD, "Too long borrow time");
            StorageContext context = Storage.CurrentContext;
            StorageMap tokenForRentalMap = new(context, PREFIX_TOKEN_FOR_RENTAL);
            ByteString key = externalTokenId + externalTokenId.Length + externalTokenId + renter;
            BigInteger[] amountAndPrice = (BigInteger[])StdLib.Deserialize(tokenForRentalMap.Get(key));

            BigInteger totalPrice = getTotalPrice(amountAndPrice[1], borrowTimeMilliseconds);
            BigInteger collateral = getCollateralAmount(totalPrice);
            ExecutionEngine.Assert((bool)Contract.Call(GAS.Hash, "transfer", CallFlags.All, new object[] { tenant, renter, totalPrice + collateral, null }), "Failed to pay GAS");

            amountAndPrice[0] -= amount;
            ExecutionEngine.Assert(amountAndPrice[0] >= 0, "No enough token to lend");
            ByteString serialized = StdLib.Serialize(amountAndPrice);
            tokenForRentalMap.Put(key, serialized);
            new StorageMap(context, PREFIX_TOKEN_OF_RENTER).Put(renter + externalTokenContract + externalTokenId, serialized);

            StorageMap tokenRenterDeadlineMap = new(context, PREFIX_TOKEN_RENTER_DEADLINE);
            key = renter + (ByteString)(BigInteger)internalTokenId.Length + internalTokenId + tenant + startTime;
            ExecutionEngine.Assert(tokenRenterDeadlineMap[key] == "", "Cannot borrow twice in a single block");
            BigInteger[] amountPriceDeadline = new BigInteger[] { amount, collateral, startTime + borrowTimeMilliseconds, 1 };
            serialized = StdLib.Serialize(amountPriceDeadline);
            tokenRenterDeadlineMap.Put(key, serialized);
            new StorageMap(context, PREFIX_TOKEN_TENANT_DEADLINE).Put(tenant + (ByteString)(BigInteger)internalTokenId.Length + internalTokenId + renter + startTime, serialized);

            ExecutionEngine.Assert(Transfer(Runtime.ExecutingScriptHash, tenant, amount, internalTokenId, null));

            return startTime;
        }

        public static void CloseNextRental(UInt160 renter, ByteString internalTokenId, UInt160 tenant, BigInteger startTime)
        {
            ExecutionEngine.Assert(Runtime.CheckWitness(renter), "No witness from renter");
            StorageContext context = Storage.CurrentContext;
            StorageMap tokenRenterDeadlineMap = new(context, PREFIX_TOKEN_RENTER_DEADLINE);
            ByteString key = renter + (ByteString)(BigInteger)internalTokenId.Length + internalTokenId + tenant + startTime;
            BigInteger[] amountCollateralDeadlineAndOpen = (BigInteger[])StdLib.Deserialize(tokenRenterDeadlineMap[key]);
            // ExecutionEngine.Assert(Runtime.Time <= amountCollateralDeadlineAndOpen[2], "Rental has expired");
            amountCollateralDeadlineAndOpen[3] = 0;
            ByteString serialized = StdLib.Serialize(amountCollateralDeadlineAndOpen);
            tokenRenterDeadlineMap[key] = serialized;
            StorageMap tokenTenantDeadlineMap = new(context, PREFIX_TOKEN_RENTER_DEADLINE);
            tokenTenantDeadlineMap[tenant + (ByteString)(BigInteger)internalTokenId.Length + internalTokenId + renter + startTime] = serialized;
        }
        public static void OpenNextRental(UInt160 renter, ByteString internalTokenId, UInt160 tenant, BigInteger startTime)
        {
            ExecutionEngine.Assert(Runtime.CheckWitness(renter), "No witness from renter");
            StorageContext context = Storage.CurrentContext;
            StorageMap tokenRenterDeadlineMap = new(context, PREFIX_TOKEN_RENTER_DEADLINE);
            ByteString key = renter + (ByteString)(BigInteger)internalTokenId.Length + internalTokenId + tenant + startTime;
            BigInteger[] amountCollateralDeadlineAndOpen = (BigInteger[])StdLib.Deserialize(tokenRenterDeadlineMap[key]);
            // ExecutionEngine.Assert(Runtime.Time <= amountCollateralDeadlineAndOpen[2], "Rental has expired");
            amountCollateralDeadlineAndOpen[3] = 1;
            ByteString serialized = StdLib.Serialize(amountCollateralDeadlineAndOpen);
            tokenRenterDeadlineMap[key] = serialized;
            StorageMap tokenTenantDeadlineMap = new(context, PREFIX_TOKEN_RENTER_DEADLINE);
            tokenTenantDeadlineMap[tenant + (ByteString)(BigInteger)internalTokenId.Length + internalTokenId + renter + startTime] = serialized;
        }

        public static void Payback(UInt160 renter, UInt160 tenant, UInt160 externalTokenContract, ByteString externalTokenId, BigInteger startTime, UInt160 collateralReceiver)
        {
            ByteString internalTokenId = new StorageMap(Storage.CurrentContext, PREFIX_TOKENID_EXTERNAL_TO_INTERNAL).Get(externalTokenContract + externalTokenId);
            Payback(renter, tenant, internalTokenId, externalTokenContract, externalTokenId, startTime, collateralReceiver);
        }

        public static void Payback(UInt160 renter, UInt160 tenant, ByteString internalTokenId, BigInteger startTime, UInt160 collateralReceiver)
        {
            ByteString[] externaltokenContractAndId = (ByteString[])StdLib.Deserialize(new StorageMap(Storage.CurrentContext, PREFIX_TOKENID_INTERNAL_TO_EXTERNAL).Get(internalTokenId));
            Payback(renter, tenant, internalTokenId, (UInt160)externaltokenContractAndId[0], externaltokenContractAndId[1], startTime, collateralReceiver);
        }

        private static void Payback(UInt160 renter, UInt160 tenant, ByteString internalTokenId, UInt160 externalTokenContract, ByteString externalTokenId, BigInteger startTime, UInt160 collateralReceiver)
        {
            StorageContext context = Storage.CurrentContext;

            StorageMap tokenTenantDeadlineMap = new(context, PREFIX_TOKEN_TENANT_DEADLINE);
            ByteString key = tenant + (ByteString)(BigInteger)internalTokenId.Length + internalTokenId + renter + startTime;
            BigInteger[] amountCollateralDeadlineAndOpen = (BigInteger[])StdLib.Deserialize(tokenTenantDeadlineMap[key]);
            if (Runtime.Time <= amountCollateralDeadlineAndOpen[2])
            {
                ExecutionEngine.Assert(Runtime.CheckWitness(tenant), "Rental not expired. Need signature from tenant");
            }
            tokenTenantDeadlineMap.Delete(key);
            new StorageMap(context, PREFIX_TOKEN_RENTER_DEADLINE).Delete(renter + (ByteString)(BigInteger)internalTokenId.Length + internalTokenId + tenant + startTime);
            Burn(tenant, amountCollateralDeadlineAndOpen[0], internalTokenId);
            if (amountCollateralDeadlineAndOpen[4] == 0)
            {
                // if RentalState.ClosedForNextRental,
                // burn the rented tokens and give the original NFT back to the owner
                BigInteger decimals = GetDecimals(externalTokenContract);
                if (decimals == 0)
                {
                    ExecutionEngine.Assert((bool)Contract.Call(externalTokenContract, "transfer", CallFlags.All, new object[] { renter, externalTokenId, null }), "Transfer failed");
                }
                else
                {
                    ExecutionEngine.Assert((bool)Contract.Call(externalTokenContract, "transfer", CallFlags.All, new object[] { Runtime.ExecutingScriptHash, renter, amountCollateralDeadlineAndOpen[0], externalTokenId, null }), "Transfer failed");
                }
            }
            else
            {
                MintSubToken(Runtime.ExecutingScriptHash, internalTokenId, amountCollateralDeadlineAndOpen[0]);
                RegisterLoan(
                    new StorageMap(context, PREFIX_TOKEN_FOR_RENTAL),
                    new StorageMap(context, PREFIX_TOKEN_OF_RENTER),
                    Runtime.ExecutingScriptHash, renter, amountCollateralDeadlineAndOpen[0], internalTokenId);
            }
            ExecutionEngine.Assert((bool)Contract.Call(GAS.Hash, "transfer", CallFlags.All, Runtime.ExecutingScriptHash, collateralReceiver, amountCollateralDeadlineAndOpen[1], null));
        }

        public static object FlashRentDivisible(
            UInt160 tenant, UInt160 token, ByteString tokenId, UInt160 renter, BigInteger neededAmount,
            UInt160 renterCalledContract, string renterCalledMethod, object[] arguments)
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
                    ExecutionEngine.Assert((bool)Contract.Call(GAS.Hash, "transfer", CallFlags.All, new object[] { tenant, renter, availableAmount * Max(amountAndPrice[1], MIN_RENTAL_PRICE), null }), "GAS transfer failed");
                }
                ExecutionEngine.Assert(rentedAmount == neededAmount, "No enough NFTs to rent");
            }
            else
            {
                // renter assigned; borrow only from given renter; tenant probably can have better prices
                BigInteger[] amountAndPrice = (BigInteger[])StdLib.Deserialize(tokenForRental[token + (ByteString)(BigInteger)tokenId.Length + tokenId + renter]);
                ExecutionEngine.Assert(amountAndPrice[0] > neededAmount, "No enough NFTs to rent");
                ExecutionEngine.Assert((bool)Contract.Call(GAS.Hash, "transfer", CallFlags.All, new object[] { tenant, renter, amountAndPrice[0] * Max(amountAndPrice[1], MIN_RENTAL_PRICE), null }), "GAS transfer failed");
            }
            ExecutionEngine.Assert((bool)Contract.Call(token, "transfer", CallFlags.All, new object[] { Runtime.ExecutingScriptHash, tenant, neededAmount, tokenId, null }), "NFT transfer failed");

            object result = Contract.Call(renterCalledContract, renterCalledMethod, CallFlags.All, arguments);

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
            ExecutionEngine.Assert((bool)Contract.Call(GAS.Hash, "transfer", CallFlags.All, new object[] { tenant, renter, Max(amountAndPrice[1], MIN_RENTAL_PRICE), null }), "GAS transfer failed");
            ExecutionEngine.Assert((bool)Contract.Call(token, "transfer", CallFlags.All, new object[] { tenant, tokenId, null }), "NFT transfer failed");

            object result = Contract.Call(calledContract, calledMethod, CallFlags.All, arguments);

            ExecutionEngine.Assert((bool)Contract.Call(token, "transfer", CallFlags.All, new object[] { Runtime.ExecutingScriptHash, tokenId, null }), "NFT payback failed");
            return result;
        }
    }
}
