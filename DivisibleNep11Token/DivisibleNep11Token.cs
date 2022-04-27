// from https://github.com/neo-project/neo-devpack-dotnet/blob/master/src/Neo.SmartContract.Framework/Nep11Token.cs

using System;
using System.ComponentModel;
using Neo.SmartContract.Framework.Attributes;
using Neo.SmartContract.Framework.Native;
using Neo.SmartContract.Framework.Services;
using System.Numerics;

namespace Neo.SmartContract.Framework
{
    [DisplayName("DivisibleNep11Token")]
    [ManifestExtra("Author", "Hecate2")]
    [ManifestExtra("Email", "developer@neo.org")]
    [ManifestExtra("Description", "This is a DivisibleNep11Token")]
    [SupportedStandards("NEP-11")]
    [ContractPermission("*", "onNEP11Payment")]

    public class DivisibleNep11TokenState
    {
        public string Name;
        // meta info ...
    }

    public abstract class DivisibleNep11Token<TokenState> : TokenContract
        where TokenState : DivisibleNep11TokenState
    {
        public delegate void OnTransferDelegate(UInt160 from, UInt160 to, BigInteger amount, ByteString tokenId);

        [DisplayName("Transfer")]
        public static event OnTransferDelegate OnTransfer;

        protected const byte Prefix_TokenId = 0x02;       // largest tokenId
        protected const byte Prefix_Token = 0x03;         // tokenMap[tokenId] -> TokenState
        protected const byte Prefix_AccountToken = 0x04;  // owner + tokenId -> amount
        protected const byte Prefix_TokenOwner = 0x05;    // (ByteString)(BigInteger)tokenId.Length + tokenId + owner -> amount

        public sealed override byte Decimals() => 100;  // 0 for non-divisible NFT

        [Safe]
        public static Iterator OwnerOf(ByteString tokenId)
        {
            if (tokenId.Length > 64) throw new Exception("tokenId.Length > 64");
            return new StorageMap(Storage.CurrentContext, Prefix_TokenOwner).Find(
                (ByteString)(BigInteger)tokenId.Length + tokenId,
                FindOptions.RemovePrefix|FindOptions.KeysOnly
                );
        }

        [Safe]
        public static BigInteger BalanceOf(UInt160 owner, ByteString tokenId)
        {
            if (!owner.IsValid) throw new Exception("The argument \"owner\" is invalid");
            if (tokenId.Length > 64) throw new Exception("tokenId.Length > 64");
            return (BigInteger)new StorageMap(Storage.CurrentContext, Prefix_AccountToken).Get(owner + tokenId);
        }

        [Safe]
        public virtual Map<string, object> Properties(ByteString tokenId)
        {
            StorageMap tokenMap = new(Storage.CurrentContext, Prefix_Token);
            TokenState token = (TokenState)StdLib.Deserialize(tokenMap[tokenId]);
            Map<string, object> map = new();
            map["name"] = token.Name;
            return map;
        }

        [Safe]
        public static Iterator Tokens()
        {
            StorageMap tokenMap = new(Storage.CurrentContext, Prefix_Token);
            return tokenMap.Find(FindOptions.KeysOnly | FindOptions.RemovePrefix);
        }

        [Safe]
        public static Iterator TokensOf(UInt160 owner)
        {
            if (owner is null || !owner.IsValid)
                throw new Exception("The argument \"owner\" is invalid");
            StorageMap accountMap = new(Storage.CurrentContext, Prefix_AccountToken);
            return accountMap.Find(owner, FindOptions.KeysOnly | FindOptions.RemovePrefix);
        }

        public static bool Transfer(UInt160 from, UInt160 to, BigInteger amount, ByteString tokenId, object data)
        {
            if (!Runtime.CheckWitness(from)) return false;
            if (to is null || !to.IsValid)
                throw new Exception("The argument \"to\" is invalid.");
            if (amount < 0) throw new Exception("amount < 0");
            if (  from != to
                  && UpdateBalance(from, tokenId, -amount)
                  && UpdateBalance(to, tokenId, +amount))
                PostTransfer(from, to, tokenId, data);
            else
                return false;
            return true;
        }

        protected static ByteString NewTokenId()
        {
            StorageContext context = Storage.CurrentContext;
            byte[] key = new byte[] { Prefix_TokenId };
            ByteString id = Storage.Get(context, key);
            Storage.Put(context, key, (BigInteger)id + 1);
            ByteString data = Runtime.ExecutingScriptHash;
            if (id is not null) data += id;
            return CryptoLib.Sha256(data);
        }

        protected static void Mint(UInt160 owner, BigInteger amount, ByteString tokenId, TokenState token, object data=null)
        {
            if (amount <= 0) throw new Exception("mint amount <= 0");
            StorageMap tokenMap = new(Storage.CurrentContext, Prefix_Token);
            tokenMap[tokenId] = StdLib.Serialize(token);
            UpdateBalance(owner, tokenId, amount);
            UpdateTotalSupply(amount);
            PostTransfer(null, owner, tokenId, data);
        }

        protected static void Burn(UInt160 owner, BigInteger amount, ByteString tokenId)
        {
            if (amount <= 0) throw new Exception("burn amount <= 0");
            UpdateBalance(owner, tokenId, -amount);
            UpdateTotalSupply(-amount);
            //if (OwnerOf(tokenId) has no element){
            //    StorageMap tokenMap = new(Storage.CurrentContext, Prefix_Token);
            //    TokenState token = (TokenState)StdLib.Deserialize(tokenMap[tokenId]);
            //    tokenMap.Delete(tokenId);
            //}
            PostTransfer(owner, null, tokenId, null);
        }

        protected static bool UpdateBalance(UInt160 owner, ByteString tokenId, BigInteger increment)
        {
            StorageMap allTokenBalanceOfAccountMap = new(Storage.CurrentContext, Prefix_Balance);
            BigInteger allTokenBalance = (BigInteger)allTokenBalanceOfAccountMap[owner];
            allTokenBalance += increment;
            if (allTokenBalance < 0) return false;
            StorageMap accountMap = new(Storage.CurrentContext, Prefix_AccountToken);
            StorageMap tokenOwnerMap = new(Storage.CurrentContext, Prefix_TokenOwner);
            ByteString key = owner + tokenId;
            ByteString tokenOwnerKey = (ByteString)(BigInteger)tokenId.Length + tokenId + owner;
            BigInteger currentBalance = (BigInteger)accountMap[key] + increment;
            if (currentBalance < 0) return false;
            if (allTokenBalance.IsZero)
                allTokenBalanceOfAccountMap.Delete(owner);
            else
                allTokenBalanceOfAccountMap.Put(owner, allTokenBalance);
            if (currentBalance > 0)
            {
                accountMap.Put(key, currentBalance);
                tokenOwnerMap.Put(tokenOwnerKey, currentBalance);
            }
            else
            {
                accountMap.Delete(key);
                tokenOwnerMap.Delete(tokenOwnerKey);
            }
            return true;
        }

        private protected static void UpdateTotalSupply(BigInteger increment)
        {
            StorageContext context = Storage.CurrentContext;
            byte[] key = new byte[] { Prefix_TotalSupply };
            BigInteger totalSupply = (BigInteger)Storage.Get(context, key);
            totalSupply += increment;
            Storage.Put(context, key, totalSupply);
        }

        protected static void PostTransfer(UInt160 from, UInt160 to, ByteString tokenId, object data)
        {
            OnTransfer(from, to, 1, tokenId);
            if (to is not null && ContractManagement.GetContract(to) is not null)
                Contract.Call(to, "onNEP11Payment", CallFlags.All, from, 1, tokenId, data);
        }
    }
}
