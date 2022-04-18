using System;
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
    [ContractPermission("*", "*")]
    public class NophtD : DivisibleNep11Token<DivisibleNep11TokenState>
    {
        [InitialValue("Nb2CHYY5wTh2ac58mTue5S3wpG6bQv5hSY", ContractParameterType.Hash160)]
        public static readonly UInt160 OWNER = default;

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
