# Noks Recovery Vocabulary v1

This directory holds the reproducible inputs and the generator for the 2,048-word Noks recovery vocabulary. The runtime code never downloads a word list.

The source is the EFF long Diceware word list dated 2016-07-18, which holds 7,776 entries. The generator drops every word in the official BIP-39 vocabularies at Bitcoin BIPs commit `c021a5f`. An eligible EFF word uses only lowercase ASCII letters, is four to eight letters long, and must end up with a unique four-letter prefix.

Where words share a four-letter prefix, the generator takes the shortest, and breaks ties in ordinal order. It then groups the candidates by their first two letters and, in each round, takes one candidate from each ordinally sorted group. This deterministic selection stops common two-letter prefixes from dominating the result. Finally it sorts the selected words lexicographically.

Run from the repository root:

```sh
dotnet run tools/recovery-vocabulary/GenerateVocabulary.cs -- --root .
```

The resulting `src/Noks.Cryptography/Resources/noks-recovery-vocabulary-v1.txt` has SHA-256 `b37b22c14f48ee8b4b8dfa91d590ae00bf96e04a759c9f0de5d054dae66318b6`. Its two-letter distribution holds 194 nonempty groups of 1 to 19 words each, and rare source prefixes explain the small groups.

`SOURCE-HASHES.sha256` records the exact input hashes. If an input, a selection rule, or the output hash changes, create a new vocabulary version and leave version 1 alone, so that existing recovery phrases keep their meaning.

THIRD_PARTY_NOTICES.txt at the repository root carries the attribution for the EFF and BIP-39 sources.
