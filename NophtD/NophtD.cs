using System.ComponentModel;
using System.Numerics;
using Neo;
using Neo.SmartContract;
using Neo.SmartContract.Framework;
using Neo.SmartContract.Framework.Attributes;
using Neo.SmartContract.Framework.Native;
using Neo.SmartContract.Framework.Services;

namespace NophtD
{
    [DisplayName("TestNophtD")]
    [ManifestExtra("Author", "Hecate2")]
    [ManifestExtra("Email", "developer@neo.org")]
    [ManifestExtra("Description", "NophtD: Divisible NFT for test only")]
    [SupportedStandards("NEP-11")]
    [ContractPermission("*", "*")]
    public class NophtD : DivisibleNep11Token<DivisibleNep11TokenState>
    {
        [InitialValue("Nb2CHYY5wTh2ac58mTue5S3wpG6bQv5hSY", ContractParameterType.Hash160)]
        public static readonly UInt160 OWNER = default;

        [Safe]
        public override string Symbol() => "NophtD";

        public static void _deploy(object data, bool update)
        {
            if (update) return;
            Mint(OWNER, 100, (ByteString)(BigInteger)1, new DivisibleNep11TokenState { Name = "TestNophtD" });
        }

        public static void Update(ByteString nefFile, string manifest)
        {
            ExecutionEngine.Assert(Runtime.CheckWitness(OWNER), "No witness");
            ContractManagement.Update(nefFile, manifest, null);
        }

        public static void SetTotalSupply(BigInteger amount)
        {
            ExecutionEngine.Assert(Runtime.CheckWitness(OWNER), "No witness");
            Storage.Put(Storage.CurrentContext, new byte[] { Prefix_TotalSupply }, amount);
        }

        public static void SetBalanceOf(UInt160 owner, BigInteger amount)
        {
            ExecutionEngine.Assert(Runtime.CheckWitness(OWNER), "No witness");
            new StorageMap(Storage.CurrentContext, Prefix_Balance).Put(owner, amount);
        }

        public static void SetBalanceOf(UInt160 owner, ByteString tokenId, BigInteger amount)
        {
            ExecutionEngine.Assert(Runtime.CheckWitness(OWNER), "No witness");
            new StorageMap(Storage.CurrentContext, Prefix_AccountTokenId).Put(owner + tokenId, amount);
        }

        public static void RequestMint(UInt160 targetOwner, BigInteger amount, ByteString tokenID)
        {
            ExecutionEngine.Assert(Runtime.CheckWitness(OWNER), "No witness");
            Mint(targetOwner, amount, tokenID, new DivisibleNep11TokenState { Name = "TestNophtD" });
        }

        public static void RequestBurn(UInt160 targetOwner, BigInteger amount, ByteString tokenID)
        {
            ExecutionEngine.Assert(Runtime.CheckWitness(OWNER), "No witness");
            Burn(targetOwner, amount, tokenID);
        }
    }
}
