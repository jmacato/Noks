# Browser deployment route

The hosted `/test/` route serves the complete Noks phone emulator, built from `src/Noks.Avalonia.Browser`. It has no separate rendezvous landing page.

The browser phone enforces the post-quantum Noks protocol. ML-DSA-65 signs descriptors, contact cards, and direct messages. ML-KEM-768 establishes a secret for each request and packet. AES-256-GCM encrypts rendezvous requests and regular packets. A packet-bound SHA-256 proof of work controls access to rendezvous requests. Waku Store retains descriptors, requests, and rendezvous control messages, so two peers do not have to be online at the same time.

Temporary phone numbers are lookup labels only. They are not permanent identities, and no key derivation uses them.

`src/Noks.Application.Tests/WakuPhoneBridgeTests.cs` holds the end-to-end coverage. One test takes the recipient offline while the sender publishes the request, and the recipient then recovers it from Store.
