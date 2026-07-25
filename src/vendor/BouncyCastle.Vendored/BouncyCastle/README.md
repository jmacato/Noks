# Vendored Bouncy Castle crypto source

This directory contains the complete Bouncy Castle C# source distribution used
by Noks. It is pinned to:

- package/release: `BouncyCastle.Cryptography` 2.6.2
- upstream tag: `release-2.6.2`
- upstream commit: `b4f2f6ad76bcd1f11f365ee50cc7447fbce79077`
- upstream repository: <https://github.com/bcgit/bc-csharp>

The complete source set is retained so the browser-compatible cryptography
project can use the standardized post-quantum implementations, notably
ML-DSA-65 signatures and ML-KEM-768 encapsulation for the asynchronous
rendezvous prototype. Existing Waku transport code continues to use its
X25519 and ChaCha20-Poly1305 primitives.

`SOURCE-MANIFEST.sha256` records the exact 2,145 vendored C# source files.
All Bouncy Castle-derived source remains under its original
`Org.BouncyCastle` namespaces. See `LICENSE.md` for its license.
