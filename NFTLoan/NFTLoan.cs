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
        private const string TRANSACTION_DATA = "NEPHRENT";
        private const byte PREFIX_REGISTERED_RENTAL_BY_TOKEN = (byte)'r';  // [EXTERNAL]tokenContract + [EXTERNAL](ByteString)(BigInteger)tokenId.Length + tokenId + renter -> StdLib.Serialize(amount, price)
        private const byte PREFIX_REGISTERED_RENTAL_BY_OWNER = (byte)'o';    // renter + [EXTERNAL]tokenContract + [EXTERNAL]tokenId -> StdLib.Serialize(amount, price)
        private const byte PREFIX_RENTAL_DEADLINE_BY_RENTER = (byte)'d';    // renter + [INTERNAL](ByteString)(BigInteger)tokenId.Length + tokenId + tenant + start_time -> StdLib.Serialize(amount, collateral, deadline, OpenForNextRental)
        private const byte PREFIX_RENTAL_DEADLINE_BY_TENANT = (byte)'t';    // tenant + [INTERNAL](ByteString)(BigInteger)tokenId.Length + tokenId + renter + start_time -> StdLib.Serialize(amount, collateral, deadline, OpenForNextRental)
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

        // Fires whenever a token is created
        [DisplayName("TokenCreated")]   // externaltokenContract, externalTokenId, internalTokenId, amount
        public static event Action<UInt160, ByteString, ByteString, BigInteger> OnTokenCreated;
        // Fires whenever a price of rental is set
        [DisplayName("PriceSet")]       // renter, tokenContract, tokenId, price
        public static event Action<UInt160, UInt160, ByteString, BigInteger> OnPriceSet;
        // Fires whenever a token is rented
        [DisplayName("TokenRented")]    // renter, tokenContract, tokenId, tenant, start_time, amount, totalPrice, collateral, deadline
        public static event Action<UInt160, UInt160, ByteString, UInt160, BigInteger, BigInteger, BigInteger, BigInteger, BigInteger> OnTokenRented;
        // Fires whenever a token rent is closed
        [DisplayName("RentalClosed")]   // renter, internalTokenId, tenant, start_time, amount, collateral, deadline
        public static event Action<UInt160, ByteString, UInt160, BigInteger, BigInteger, BigInteger, BigInteger> OnRentalClosed;
        // Fires whenever a token rent is revoked
        [DisplayName("RentalOpened")]   // renter, internalTokenId, tenant, start_time, amount, collateral, deadline
        public static event Action<UInt160, ByteString, UInt160, BigInteger, BigInteger, BigInteger, BigInteger> OnRentalOpened;
        // Fires whenever a token rent is revoked
        [DisplayName("RentalRevoked")]  // renter, internalTokenId, tenant, start_time, amount, collateral, deadline
        public static event Action<UInt160, ByteString, UInt160, BigInteger, BigInteger, BigInteger, BigInteger> OnRentalRevoked;
        // Fires whenever a token rent is withdrawn
        [DisplayName("TokenWithdrawn")] // externalTokenContract, externalTokenId, amount
        public static event Action<UInt160, ByteString, BigInteger> OnTokenWithdrawn;

        public static void OnNEP11Payment(UInt160 from, BigInteger amount, ByteString tokenId, object data)
        {
            ExecutionEngine.Assert((string)data == TRANSACTION_DATA,
                "Do not send NFTs directly into this contract!");
        }
        public override string Symbol() => "NEPHRENT";
        public static BigInteger GetDecimals(UInt160 externalTokenContract) => (BigInteger)Contract.Call(externalTokenContract, "decimals", CallFlags.ReadStates);
        public static BigInteger BalanceOfRentalToken(ByteString internalTokenId) => BalanceOf(Runtime.ExecutingScriptHash, internalTokenId);

        public static Iterator ListRegisteredRentalByToken(UInt160 externalTokenContract) => new StorageMap(Storage.CurrentContext, PREFIX_REGISTERED_RENTAL_BY_TOKEN).Find(externalTokenContract, FindOptions.RemovePrefix);
        public static Iterator ListRegisteredRentalByToken(UInt160 externalTokenContract, ByteString externalTokenId) => new StorageMap(Storage.CurrentContext, PREFIX_REGISTERED_RENTAL_BY_TOKEN).Find(externalTokenContract + (ByteString)(BigInteger)externalTokenId.Length + externalTokenId, FindOptions.RemovePrefix);
        public static BigInteger[] GetRegisteredRentalByToken(UInt160 externalTokenContract, ByteString externalTokenId, UInt160 renter) => (BigInteger[])StdLib.Deserialize(new StorageMap(Storage.CurrentContext, PREFIX_REGISTERED_RENTAL_BY_TOKEN).Get(externalTokenContract + (ByteString)(BigInteger)externalTokenId.Length + externalTokenId + renter));

        public static Iterator ListRegisteredRentalByRenter(UInt160 renter) => new StorageMap(Storage.CurrentContext, PREFIX_REGISTERED_RENTAL_BY_OWNER).Find(renter, FindOptions.RemovePrefix);
        public static Iterator ListRegisteredRentalByRenter(UInt160 renter, UInt160 externalTokenContract) => new StorageMap(Storage.CurrentContext, PREFIX_REGISTERED_RENTAL_BY_OWNER).Find(renter + externalTokenContract, FindOptions.RemovePrefix);
        public static BigInteger[] GetRegisteredRentalByRenter(UInt160 renter, UInt160 externalTokenContract, ByteString externalTokenId) => (BigInteger[])StdLib.Deserialize(new StorageMap(Storage.CurrentContext, PREFIX_REGISTERED_RENTAL_BY_OWNER).Get(renter + externalTokenContract + externalTokenId));

        public static Iterator ListRentalDeadlineByRenter(UInt160 renter) => new StorageMap(Storage.CurrentContext, PREFIX_RENTAL_DEADLINE_BY_RENTER).Find(renter, FindOptions.RemovePrefix);
        public static Iterator ListRentalDeadlineByRenter(UInt160 renter, UInt160 token) => new StorageMap(Storage.CurrentContext, PREFIX_RENTAL_DEADLINE_BY_RENTER).Find(renter + token, FindOptions.RemovePrefix);
        public static Iterator ListRentalDeadlineByRenter(UInt160 renter, UInt160 token, ByteString tokenId) => new StorageMap(Storage.CurrentContext, PREFIX_RENTAL_DEADLINE_BY_RENTER).Find(renter + token + (ByteString)(BigInteger)tokenId.Length + tokenId, FindOptions.RemovePrefix);
        public static Iterator ListRentalDeadlineByRenter(UInt160 renter, UInt160 token, ByteString tokenId, UInt160 tenant) => new StorageMap(Storage.CurrentContext, PREFIX_RENTAL_DEADLINE_BY_RENTER).Find(renter + token + (ByteString)(BigInteger)tokenId.Length + tokenId + tenant, FindOptions.RemovePrefix);
        public static BigInteger[] GetRentalDeadlineByRenter(UInt160 renter, UInt160 token, ByteString tokenId, UInt160 tenant, BigInteger startTime) => (BigInteger[])StdLib.Deserialize(new StorageMap(Storage.CurrentContext, PREFIX_RENTAL_DEADLINE_BY_RENTER).Get(renter + token + (ByteString)(BigInteger)tokenId.Length + tokenId + tenant + (ByteString)startTime));

        public static Iterator ListRentalDeadlineByTenant(UInt160 tenant) => new StorageMap(Storage.CurrentContext, PREFIX_RENTAL_DEADLINE_BY_TENANT).Find(tenant, FindOptions.RemovePrefix);
        public static Iterator ListRentalDeadlineByTenant(UInt160 tenant, UInt160 token) => new StorageMap(Storage.CurrentContext, PREFIX_RENTAL_DEADLINE_BY_TENANT).Find(tenant + token, FindOptions.RemovePrefix);
        public static Iterator ListRentalDeadlineByTenant(UInt160 tenant, UInt160 token, ByteString tokenId) => new StorageMap(Storage.CurrentContext, PREFIX_RENTAL_DEADLINE_BY_TENANT).Find(tenant + token + (ByteString)(BigInteger)tokenId.Length + tokenId, FindOptions.RemovePrefix);
        public static Iterator ListRentalDeadlineByTenant(UInt160 tenant, UInt160 token, ByteString tokenId, UInt160 renter) => new StorageMap(Storage.CurrentContext, PREFIX_RENTAL_DEADLINE_BY_TENANT).Find(tenant + token + (ByteString)(BigInteger)tokenId.Length + tokenId + renter, FindOptions.RemovePrefix);
        public static BigInteger[] GetRentalDeadlineByTenant(UInt160 tenant, UInt160 token, ByteString tokenId, UInt160 renter, BigInteger startTime) => (BigInteger[])StdLib.Deserialize(new StorageMap(Storage.CurrentContext, PREFIX_RENTAL_DEADLINE_BY_TENANT).Get(tenant + token + (ByteString)(BigInteger)tokenId.Length + tokenId + renter + (ByteString)startTime));

        public static Iterator ListExternalTokenInfo(ByteString prefix) => new StorageMap(Storage.CurrentContext, PREFIX_TOKENID_INTERNAL_TO_EXTERNAL).Find(prefix);
        public static ByteString GetExternalTokenInfo(ByteString internalTokenId) => new StorageMap(Storage.CurrentContext, PREFIX_TOKENID_INTERNAL_TO_EXTERNAL).Get(internalTokenId);
        public static Iterator ListInternalTokenId(ByteString prefix) => new StorageMap(Storage.CurrentContext, PREFIX_TOKENID_EXTERNAL_TO_INTERNAL).Find(prefix);
        public static Iterator ListInternalTokenId(UInt160 externalTokenContract, ByteString prefix) => new StorageMap(Storage.CurrentContext, PREFIX_TOKENID_EXTERNAL_TO_INTERNAL).Find(externalTokenContract + prefix);
        public static ByteString GetInternalTokenId(UInt160 externalTokenContract, ByteString externalTokenId) => new StorageMap(Storage.CurrentContext, PREFIX_TOKENID_EXTERNAL_TO_INTERNAL).Get(externalTokenContract + externalTokenId);

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public static BigInteger Max(BigInteger v1, BigInteger v2) => v1 > v2 ? v1 : v2;

        public new static bool Transfer(UInt160 from, UInt160 to, BigInteger amount, ByteString tokenId, object data)
        {
            UInt160 executingScriptHash = Runtime.ExecutingScriptHash;
            if (from != executingScriptHash && to != executingScriptHash) return false;
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

            StorageMap registeredRentalByTokenMap = new(Storage.CurrentContext, PREFIX_REGISTERED_RENTAL_BY_TOKEN);
            ByteString key = tokenContract + (ByteString)(BigInteger)tokenId.Length + tokenId + renter;
            BigInteger[] amountAndPrice = (BigInteger[])StdLib.Deserialize(registeredRentalByTokenMap.Get(key));
            ExecutionEngine.Assert(amountAndPrice[0] > 0, "No token at rental");
            amountAndPrice[1] = price;
            ByteString serialized = StdLib.Serialize(amountAndPrice);
            registeredRentalByTokenMap.Put(key, serialized);

            StorageMap registeredRentalByOwnerMap = new (Storage.CurrentContext, PREFIX_REGISTERED_RENTAL_BY_OWNER);
            registeredRentalByOwnerMap.Put(renter + tokenContract + tokenId, serialized);
        }

        private static ByteString MintSubToken(UInt160 externalTokenContract, ByteString externalTokenId, BigInteger amount)
        {
            StorageContext context = Storage.CurrentContext;
            StorageMap externalToInternal = new(context, PREFIX_TOKENID_EXTERNAL_TO_INTERNAL);
            ByteString externalTokenInfo = externalTokenContract + externalTokenId;
            ByteString internalTokenId = externalToInternal.Get(externalTokenInfo);
            if (internalTokenId == UInt160.Zero)
            {
                internalTokenId = NewTokenId();
                externalToInternal.Put(externalTokenInfo, internalTokenId);
                new StorageMap(context, PREFIX_TOKENID_INTERNAL_TO_EXTERNAL).Put(internalTokenId, externalTokenInfo);
            }
            Mint(Runtime.ExecutingScriptHash, amount, internalTokenId, new DivisibleNep11TokenState { Name = externalTokenInfo });
            OnTokenCreated(externalTokenContract, externalTokenContract, internalTokenId, amount);
            return internalTokenId;
        }

        private static ByteString MintSubToken(ByteString internalTokenId, UInt160 externalTokenContract, ByteString externalTokenId, BigInteger amount)
        {
            StorageContext context = Storage.CurrentContext;
            StorageMap externalToInternal = new(context, PREFIX_TOKENID_EXTERNAL_TO_INTERNAL);
            ByteString externalTokenInfo = externalTokenContract + externalTokenId;
            Mint(Runtime.ExecutingScriptHash, amount, internalTokenId, new DivisibleNep11TokenState { Name = externalTokenInfo });
            OnTokenCreated(externalTokenContract, externalTokenContract, internalTokenId, amount);
            return internalTokenId;
        }

        private static BigInteger RegisterLoan(
            StorageMap registeredRentalByTokenMap, StorageMap registeredRentalByOwnerMap,
            UInt160 tokenContract, UInt160 renter, BigInteger amount,
            ByteString tokenId, BigInteger price)
        {
            // if tokenContract is not Runtime.ExecutingScriptHash, then tokenId is just
            // the tokenId of the external tokenContract
            // else tokenId is the internalTokenId of Runtime.ExecutingScriptHash
            ByteString key = tokenContract + (ByteString)(BigInteger)tokenId.Length + tokenId + renter;
            BigInteger[] amountAndPrice = (BigInteger[])StdLib.Deserialize(registeredRentalByTokenMap.Get(key));
            amountAndPrice[0] += amount;
            amountAndPrice[1] = price;
            ByteString serialized = StdLib.Serialize(amountAndPrice);
            registeredRentalByTokenMap.Put(key, serialized);
            registeredRentalByOwnerMap.Put(renter + tokenContract + tokenId, serialized);
            OnPriceSet(renter, tokenContract, tokenId, price);
            return amountAndPrice[0];
        }
        private static BigInteger RegisterLoan(
            StorageMap registeredRentalByTokenMap, StorageMap registeredRentalByOwnerMap,
            UInt160 tokenContract, UInt160 renter, BigInteger amount,
            ByteString tokenId)
        {
            // if tokenContract is not Runtime.ExecutingScriptHash, then tokenId is just
            // the tokenId of the external tokenContract
            // else tokenId is the internalTokenId of Runtime.ExecutingScriptHash
            ByteString key = tokenContract + (ByteString)(BigInteger)tokenId.Length + tokenId + renter;
            BigInteger[] amountAndPrice = (BigInteger[])StdLib.Deserialize(registeredRentalByTokenMap.Get(key));
            amountAndPrice[0] += amount;
            ByteString serialized = StdLib.Serialize(amountAndPrice);
            registeredRentalByTokenMap.Put(key, serialized);
            registeredRentalByOwnerMap.Put(renter + tokenContract + tokenId, serialized);
            return amountAndPrice[0];
        }

        /// <summary>
        /// Give me your NFTs using this method!
        /// </summary>
        /// <param name="flashLoanPrice">GAS (1e8) price for each loan. Minimum: <see cref="MIN_RENTAL_PRICE"/>.</param>
        /// <param name="ordinaryLoanPrice">GAS (1e8) price per second (not millisecond!). Minimum for each loan: <see cref="MIN_RENTAL_PRICE"/>.</param>
        public static BigInteger RegisterRental(
            UInt160 renter, UInt160 externalTokenContract, BigInteger amountForRent, ByteString externalTokenId,
            BigInteger flashLoanPrice, BigInteger ordinaryLoanPrice, bool isDivisible=false)
        {
            // No need to Runtime.CheckWitness(renter), because we will transfer NFT from renter to this contract.
            ExecutionEngine.Assert(externalTokenId.Length <= 64, "tokenId.Length > 64");
            ExecutionEngine.Assert(externalTokenContract != Runtime.ExecutingScriptHash, "Cannot register rental for tokens issued by this contract");
            // ExecutionEngine.Assert(amountForRent > 0, "amountForRent <= 0");  // unnecessary
            // Transfer is very risky. Consider a whitelist of tokens. 
            if (isDivisible)
                ExecutionEngine.Assert((bool)Contract.Call(externalTokenContract, "transfer", CallFlags.All, renter, Runtime.ExecutingScriptHash, amountForRent, externalTokenId, TRANSACTION_DATA), "Transfer failed");
            else
            {
                ExecutionEngine.Assert((bool)Contract.Call(externalTokenContract, "transfer", CallFlags.All, Runtime.ExecutingScriptHash, externalTokenId, TRANSACTION_DATA), "Transfer failed");
                amountForRent = 1;
            }

            StorageContext context = Storage.CurrentContext;
            StorageMap registeredRentalByTokenMap = new(context, PREFIX_REGISTERED_RENTAL_BY_TOKEN);
            StorageMap registeredRentalByOwnerMap = new(context, PREFIX_REGISTERED_RENTAL_BY_OWNER);

            RegisterLoan(registeredRentalByTokenMap, registeredRentalByOwnerMap, externalTokenContract, renter, amountForRent, externalTokenId, flashLoanPrice);
            ByteString internalTokenId = MintSubToken(externalTokenContract, externalTokenId, amountForRent);
            return RegisterLoan(registeredRentalByTokenMap, registeredRentalByOwnerMap, Runtime.ExecutingScriptHash, renter, amountForRent, internalTokenId, ordinaryLoanPrice);
        }

        private static ByteString BurnSubToken(UInt160 externalTokenContract, ByteString externalTokenId, BigInteger amount)
        {
            return BurnSubToken(GetInternalTokenId(externalTokenContract, externalTokenId), amount);
        }

        private static ByteString BurnSubToken(ByteString internalTokenId, BigInteger amount)
        {
            ExecutionEngine.Assert(internalTokenId != UInt160.Zero, "Failed to find the token to burn");
            Burn(Runtime.ExecutingScriptHash, amount, internalTokenId);
            return internalTokenId;
        }

        private static BigInteger UnregisterLoan(
            StorageMap registeredRentalByTokenMap, StorageMap registeredRentalByOwnerMap,
            UInt160 tokenContract, UInt160 renter, BigInteger amount,
            ByteString tokenId)
        {
            // if tokenContract is not Runtime.ExecutingScriptHash, then tokenId is just
            // the tokenId of the external tokenContract
            // else tokenId is the internalTokenId of Runtime.ExecutingScriptHash
            ByteString key = tokenContract + (ByteString)(BigInteger)tokenId.Length + tokenId + renter;
            BigInteger[] amountAndPrice = (BigInteger[])StdLib.Deserialize(registeredRentalByTokenMap.Get(key));
            amountAndPrice[0] -= amount;
            amount = amountAndPrice[0];
            ExecutionEngine.Assert(amount >= 0, "No enough token to unregister: " + tokenContract + renter + amount + tokenId);
            if (amount > 0)
            {
                ByteString serialized = StdLib.Serialize(amountAndPrice);
                registeredRentalByTokenMap.Put(key, serialized);
                registeredRentalByOwnerMap.Put(renter + tokenContract + tokenId, serialized);
            }
            else
            {
                registeredRentalByTokenMap.Delete(key);
                registeredRentalByOwnerMap.Delete(renter + tokenContract + tokenId);
            }
            return amount;
        }

        public static BigInteger UnregisterRental(
            UInt160 renter, UInt160 externalTokenContract, BigInteger amountToUnregister, ByteString externalTokenId, bool isDivisible=false)
        {
            ExecutionEngine.Assert(Runtime.CheckWitness(renter), "No witness");
            ExecutionEngine.Assert(externalTokenContract != Runtime.ExecutingScriptHash, "Cannot unregister rental for tokens issued by this contract");
            // ExecutionEngine.Assert(amountToUnregister > 0, "amountToUnregister <= 0");  // unnecessary

            StorageContext context = Storage.CurrentContext;
            StorageMap registeredRentalByTokenMap = new(context, PREFIX_REGISTERED_RENTAL_BY_TOKEN);
            StorageMap registeredRentalByOwnerMap = new(context, PREFIX_REGISTERED_RENTAL_BY_OWNER);

            ByteString internalTokenId;
            if (isDivisible)
            {
                UnregisterLoan(registeredRentalByTokenMap, registeredRentalByOwnerMap, externalTokenContract, renter, amountToUnregister, externalTokenId);
                internalTokenId = BurnSubToken(externalTokenContract, externalTokenId, amountToUnregister);
                ExecutionEngine.Assert((bool)Contract.Call(externalTokenContract, "transfer", CallFlags.All, renter, Runtime.ExecutingScriptHash, amountToUnregister, externalTokenId, TRANSACTION_DATA), "Transfer failed");
            }
            else
            {
                amountToUnregister = 1;
                UnregisterLoan(registeredRentalByTokenMap, registeredRentalByOwnerMap, externalTokenContract, renter, amountToUnregister, externalTokenId);
                internalTokenId = BurnSubToken(externalTokenContract, externalTokenId, amountToUnregister);
                ExecutionEngine.Assert((bool)Contract.Call(externalTokenContract, "transfer", CallFlags.All, renter, externalTokenId, TRANSACTION_DATA), "Transfer failed");
            }

            OnTokenWithdrawn(externalTokenContract, externalTokenId, amountToUnregister);
            return UnregisterLoan(registeredRentalByTokenMap, registeredRentalByOwnerMap, Runtime.ExecutingScriptHash, renter, amountToUnregister, internalTokenId);
        }

        public static BigInteger GetTotalPrice(BigInteger pricePerSecond, BigInteger borrowTimeMilliseconds) => Max(pricePerSecond * borrowTimeMilliseconds / 1000, MIN_RENTAL_PRICE);
        public static BigInteger GetCollateralAmount(BigInteger totalPrice) => totalPrice * COLLATERAL_MULTIPLIER / COLLATERAL_DIVISOR;
        public static BigInteger GetCollateralAmount(BigInteger pricePerSecond, BigInteger borrowTimeMilliseconds) => GetTotalPrice(pricePerSecond, borrowTimeMilliseconds) * COLLATERAL_MULTIPLIER / COLLATERAL_DIVISOR;

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
            // No need to Runtime.CheckWitness(tenant), because we will transfer GAS from tenant to renter.
            BigInteger startTime = Runtime.Time;
            ExecutionEngine.Assert(borrowTimeMilliseconds < MAX_RENTAL_PERIOD, "Too long borrow time");
            StorageContext context = Storage.CurrentContext;
            StorageMap registeredRentalByTokenMap = new(context, PREFIX_REGISTERED_RENTAL_BY_TOKEN);
            ByteString key = externalTokenId + externalTokenId.Length + externalTokenId + renter;
            BigInteger[] amountAndPrice = (BigInteger[])StdLib.Deserialize(registeredRentalByTokenMap.Get(key));

            BigInteger totalPrice = GetTotalPrice(amountAndPrice[1], borrowTimeMilliseconds);
            BigInteger collateral = GetCollateralAmount(totalPrice);
            ExecutionEngine.Assert((bool)Contract.Call(GAS.Hash, "transfer", CallFlags.All, tenant, renter, totalPrice + collateral, TRANSACTION_DATA), "Failed to pay GAS");

            amountAndPrice[0] -= amount;
            ExecutionEngine.Assert(amountAndPrice[0] >= 0, "No enough token to lend");
            ByteString serialized;
            if (amountAndPrice[0] > 0)
            {
                serialized = StdLib.Serialize(amountAndPrice);
                registeredRentalByTokenMap.Put(key, serialized);
                new StorageMap(context, PREFIX_REGISTERED_RENTAL_BY_OWNER).Put(renter + externalTokenContract + externalTokenId, serialized);
            }
            else
            {
                registeredRentalByTokenMap.Delete(key);
                new StorageMap(context, PREFIX_REGISTERED_RENTAL_BY_OWNER).Delete(renter + externalTokenContract + externalTokenId);
            }

            StorageMap rentalDeadlineByRenterMap = new(context, PREFIX_RENTAL_DEADLINE_BY_RENTER);
            key = renter + (ByteString)(BigInteger)internalTokenId.Length + internalTokenId + tenant + startTime;
            ExecutionEngine.Assert(rentalDeadlineByRenterMap[key] == "", "Cannot borrow twice in a single block");
            BigInteger[] amountPriceDeadline = new BigInteger[] { amount, collateral, startTime + borrowTimeMilliseconds, 1 };
            serialized = StdLib.Serialize(amountPriceDeadline);
            rentalDeadlineByRenterMap.Put(key, serialized);
            new StorageMap(context, PREFIX_RENTAL_DEADLINE_BY_TENANT).Put(tenant + (ByteString)(BigInteger)internalTokenId.Length + internalTokenId + renter + startTime, serialized);

            ExecutionEngine.Assert(Transfer(Runtime.ExecutingScriptHash, tenant, amount, internalTokenId, TRANSACTION_DATA));
            OnTokenRented(renter, Runtime.ExecutingScriptHash, internalTokenId, tenant, startTime, amount, totalPrice, collateral, amountPriceDeadline[2]);

            return startTime;
        }

        public static void CloseNextRental(UInt160 renter, ByteString internalTokenId, UInt160 tenant, ByteString startTime)
        {
            ExecutionEngine.Assert(Runtime.CheckWitness(renter), "No witness from renter");
            StorageContext context = Storage.CurrentContext;
            StorageMap rentalDeadlineByRenterMap = new(context, PREFIX_RENTAL_DEADLINE_BY_RENTER);
            ByteString key = renter + (ByteString)(BigInteger)internalTokenId.Length + internalTokenId + tenant + startTime;
            BigInteger[] amountCollateralDeadlineAndOpen = (BigInteger[])StdLib.Deserialize(rentalDeadlineByRenterMap[key]);
            // ExecutionEngine.Assert(Runtime.Time <= amountCollateralDeadlineAndOpen[2], "Rental has expired");
            amountCollateralDeadlineAndOpen[3] = 0;
            ByteString serialized = StdLib.Serialize(amountCollateralDeadlineAndOpen);
            rentalDeadlineByRenterMap[key] = serialized;
            StorageMap rentalDeadlineByTenantMap = new(context, PREFIX_RENTAL_DEADLINE_BY_RENTER);
            rentalDeadlineByTenantMap[tenant + (ByteString)(BigInteger)internalTokenId.Length + internalTokenId + renter + startTime] = serialized;
            OnRentalClosed(renter, internalTokenId, tenant, (BigInteger)startTime, amountCollateralDeadlineAndOpen[0], amountCollateralDeadlineAndOpen[1], amountCollateralDeadlineAndOpen[2]);
        }
        public static void OpenNextRental(UInt160 renter, ByteString internalTokenId, UInt160 tenant, ByteString startTime)
        {
            ExecutionEngine.Assert(Runtime.CheckWitness(renter), "No witness from renter");
            StorageContext context = Storage.CurrentContext;
            StorageMap rentalDeadlineByRenterMap = new(context, PREFIX_RENTAL_DEADLINE_BY_RENTER);
            ByteString key = renter + (ByteString)(BigInteger)internalTokenId.Length + internalTokenId + tenant + startTime;
            BigInteger[] amountCollateralDeadlineAndOpen = (BigInteger[])StdLib.Deserialize(rentalDeadlineByRenterMap[key]);
            // ExecutionEngine.Assert(Runtime.Time <= amountCollateralDeadlineAndOpen[2], "Rental has expired");
            amountCollateralDeadlineAndOpen[3] = 1;
            ByteString serialized = StdLib.Serialize(amountCollateralDeadlineAndOpen);
            rentalDeadlineByRenterMap[key] = serialized;
            StorageMap rentalDeadlineByTenantMap = new(context, PREFIX_RENTAL_DEADLINE_BY_RENTER);
            rentalDeadlineByTenantMap[tenant + (ByteString)(BigInteger)internalTokenId.Length + internalTokenId + renter + startTime] = serialized;
            OnRentalOpened(renter, internalTokenId, tenant, (BigInteger)startTime, amountCollateralDeadlineAndOpen[0], amountCollateralDeadlineAndOpen[1], amountCollateralDeadlineAndOpen[2]);
        }

        public static void Payback(UInt160 renter, UInt160 tenant, UInt160 externalTokenContract, ByteString externalTokenId, ByteString startTime, UInt160 collateralReceiver, bool isDivisible = false)
        {
            ByteString internalTokenId = new StorageMap(Storage.CurrentContext, PREFIX_TOKENID_EXTERNAL_TO_INTERNAL).Get(externalTokenContract + externalTokenId);
            Payback(renter, tenant, internalTokenId, externalTokenContract, externalTokenId, startTime, collateralReceiver, isDivisible);
        }

        public static void Payback(UInt160 renter, UInt160 tenant, ByteString internalTokenId, ByteString startTime, UInt160 collateralReceiver, bool isDivisible = false)
        {
            ByteString[] externaltokenContractAndId = (ByteString[])StdLib.Deserialize(new StorageMap(Storage.CurrentContext, PREFIX_TOKENID_INTERNAL_TO_EXTERNAL).Get(internalTokenId));
            Payback(renter, tenant, internalTokenId, (UInt160)externaltokenContractAndId[0], externaltokenContractAndId[1], startTime, collateralReceiver, isDivisible);
        }

        private static void Payback(
            UInt160 renter, UInt160 tenant, ByteString internalTokenId, UInt160 externalTokenContract, ByteString externalTokenId, ByteString startTime, UInt160 collateralReceiver,
            bool isDivisible=false)
        {
            StorageContext context = Storage.CurrentContext;

            StorageMap rentalDeadlineByTenantMap = new(context, PREFIX_RENTAL_DEADLINE_BY_TENANT);
            ByteString key = tenant + (ByteString)(BigInteger)internalTokenId.Length + internalTokenId + renter + startTime;
            BigInteger[] amountCollateralDeadlineAndOpen = (BigInteger[])StdLib.Deserialize(rentalDeadlineByTenantMap[key]);
            if (Runtime.Time <= amountCollateralDeadlineAndOpen[2])
                ExecutionEngine.Assert(Runtime.CheckWitness(tenant), "Rental not expired. Need signature from tenant");
            rentalDeadlineByTenantMap.Delete(key);
            new StorageMap(context, PREFIX_RENTAL_DEADLINE_BY_RENTER).Delete(renter + (ByteString)(BigInteger)internalTokenId.Length + internalTokenId + tenant + startTime);
            Burn(tenant, amountCollateralDeadlineAndOpen[0], internalTokenId);
            if (amountCollateralDeadlineAndOpen[4] == 0)
            {
                // if RentalState.ClosedForNextRental,
                // burn the rented tokens and give the original NFT back to the owner
                if (isDivisible)
                {
                    ExecutionEngine.Assert((bool)Contract.Call(externalTokenContract, "transfer", CallFlags.All, Runtime.ExecutingScriptHash, renter, amountCollateralDeadlineAndOpen[0], externalTokenId, TRANSACTION_DATA), "Transfer failed");
                }
                else
                {
                    ExecutionEngine.Assert((bool)Contract.Call(externalTokenContract, "transfer", CallFlags.All, renter, externalTokenId, TRANSACTION_DATA), "Transfer failed");
                }
                OnTokenWithdrawn(externalTokenContract, externalTokenId, amountCollateralDeadlineAndOpen[0]);
            }
            else
            {
                MintSubToken(internalTokenId, externalTokenContract, externalTokenId, amountCollateralDeadlineAndOpen[0]);
                RegisterLoan(
                    new StorageMap(context, PREFIX_REGISTERED_RENTAL_BY_TOKEN),
                    new StorageMap(context, PREFIX_REGISTERED_RENTAL_BY_OWNER),
                    Runtime.ExecutingScriptHash, renter, amountCollateralDeadlineAndOpen[0], internalTokenId);
            }
            OnRentalRevoked(renter, internalTokenId, tenant, (BigInteger)startTime, amountCollateralDeadlineAndOpen[0], amountCollateralDeadlineAndOpen[1], amountCollateralDeadlineAndOpen[2]);
            ExecutionEngine.Assert((bool)Contract.Call(GAS.Hash, "transfer", CallFlags.All, Runtime.ExecutingScriptHash, collateralReceiver, amountCollateralDeadlineAndOpen[1], TRANSACTION_DATA));
        }

        public static object FlashBorrowDivisible(
            UInt160 tenant, UInt160 token, ByteString tokenId, UInt160 renter, BigInteger neededAmount,
            UInt160 renterCalledContract, string renterCalledMethod, object[] arguments)
        {
            StorageContext context = Storage.CurrentContext;
            StorageMap tokenForRental = new(context, PREFIX_REGISTERED_RENTAL_BY_TOKEN);
            BigInteger rentalPrice;
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
                    rentalPrice = Max(availableAmount * amountAndPrice[1], MIN_RENTAL_PRICE);
                    ExecutionEngine.Assert((bool)Contract.Call(GAS.Hash, "transfer", CallFlags.All, tenant, renter, rentalPrice, TRANSACTION_DATA), "GAS transfer failed");
                    OnTokenRented(renter, token, tokenId, tenant, Runtime.Time, amountAndPrice[0], rentalPrice, 0, Runtime.Time);
                }
                ExecutionEngine.Assert(rentedAmount == neededAmount, "No enough NFTs to rent");
            }
            else
            {
                // renter assigned; borrow only from given renter; tenant probably can have better prices
                BigInteger[] amountAndPrice = (BigInteger[])StdLib.Deserialize(tokenForRental[token + (ByteString)(BigInteger)tokenId.Length + tokenId + renter]);
                ExecutionEngine.Assert(amountAndPrice[0] > neededAmount, "No enough NFTs to rent");
                rentalPrice = Max(amountAndPrice[0] * amountAndPrice[1], MIN_RENTAL_PRICE);
                ExecutionEngine.Assert((bool)Contract.Call(GAS.Hash, "transfer", CallFlags.All, tenant, renter, rentalPrice, TRANSACTION_DATA), "GAS transfer failed");
                OnTokenRented(renter, token, tokenId, tenant, Runtime.Time, amountAndPrice[0], rentalPrice, 0, Runtime.Time);
            }
            ExecutionEngine.Assert((bool)Contract.Call(token, "transfer", CallFlags.All, Runtime.ExecutingScriptHash, tenant, neededAmount, tokenId, TRANSACTION_DATA), "NFT transfer failed");

            object result = Contract.Call(renterCalledContract, renterCalledMethod, CallFlags.All, arguments);

            ExecutionEngine.Assert((bool)Contract.Call(token, "transfer", CallFlags.All, Runtime.ExecutingScriptHash, tenant, neededAmount, tokenId, TRANSACTION_DATA), "NFT payback failed");
            return result;
        }

        public static object FlashBorrowNonDivisible
            (UInt160 tenant, UInt160 token, ByteString tokenId, UInt160 renter,
            UInt160 calledContract, string calledMethod, object[] arguments)
        {
            StorageMap tokenForRental = new(Storage.CurrentContext, PREFIX_REGISTERED_RENTAL_BY_TOKEN);
            Iterator rentalIterator = tokenForRental.Find(token + (ByteString)(BigInteger)tokenId.Length + tokenId, FindOptions.RemovePrefix);
            ExecutionEngine.Assert(rentalIterator.Next(), "Failed to find renter");
            BigInteger[] amountAndPrice = (BigInteger[])StdLib.Deserialize((ByteString)rentalIterator.Value);
            BigInteger rentalPrice = Max(amountAndPrice[1], MIN_RENTAL_PRICE);
            ExecutionEngine.Assert((bool)Contract.Call(GAS.Hash, "transfer", CallFlags.All, tenant, renter, rentalPrice, TRANSACTION_DATA), "GAS transfer failed");
            OnTokenRented(renter, token, tokenId, tenant, Runtime.Time, amountAndPrice[0], amountAndPrice[0] * rentalPrice, 0, Runtime.Time);
            ExecutionEngine.Assert((bool)Contract.Call(token, "transfer", CallFlags.All, tenant, tokenId, TRANSACTION_DATA), "NFT transfer failed");

            object result = Contract.Call(calledContract, calledMethod, CallFlags.All, arguments);

            ExecutionEngine.Assert((bool)Contract.Call(token, "transfer", CallFlags.All, Runtime.ExecutingScriptHash, tokenId, TRANSACTION_DATA), "NFT payback failed");
            return result;
        }
    }
}
