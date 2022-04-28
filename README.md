#### ROADMAP

1. [Done] Flash loan for divisible and non-divisible NFTs
2. [Done] Contract to mint new NFTs from existing NFTs. The new minted NFTs represents the permission to **use** the original NFT. 
3. [Done] Ordinary rental of the right of NFT usage. 
4. [Done] Tenant pay compulsory collaterals, used to reward those who revoke expired rentals. 
5. [Done] Renter can close a following rental
6. [Done] Fire events
7. [Potential] Give me your NFT to borrow fungible tokens!
8. [Potential] Fungible collateral flash loan?

#### Compilation Instructions

- Clone https://github.com/neo-project/neo-devpack-dotnet/commit/e1dae8f1f2ee066134d9e83539539c5c3fab19ec .

- In `neo-devpack-dotnet/src/Neo.Compiler.CSharp/CompilationContext.cs`, find these codes at line 478:

  ```csharp
  if (methodsExported.Any(u => u.Name == method.Name && u.Parameters.Length == method.Parameters.Length))
      throw new CompilationException(symbol, DiagnosticId.MethodNameConflict, $"Duplicate method key: {method.Name},{method.Parameters.Length}.");
  ```

  Replace the two lines with the following codes:

  ```csharp
                  //if (methodsExported.Any(u => u.Name == method.Name && u.Parameters.Length == method.Parameters.Length))
                  //    throw new CompilationException(symbol, DiagnosticId.MethodNameConflict, $"Duplicate method key: {method.Name},{method.Parameters.Length}.");
                  AbiMethod? foundMethod = methodsExported.Where(u => u.Name == method.Name && u.Parameters.Length == method.Parameters.Length).FirstOrDefault();
                  if (foundMethod is not null)
                      methodsExported.Remove(foundMethod);
  ```

- run `neo-devpack-dotnet` from source code, with command line arguments ` PATH_TO_NFTLoan/NFTLoan --debug`.

- It's better to check the assembly codes with command `dumpnef NFTFlashLoan.nef > NFTFlashLoan.nef.txt` if you run into weird exceptions.

