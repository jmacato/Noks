# Call-intercept prompts

`generate.sh` builds the browser call-intercept WAV files from the operator recordings in `source/`. Each generated file starts with an original special-information-tone sequence that FFmpeg synthesizes.

The prefix obeys ITU-T [E.180 and Q.35](https://www.itu.int/rec/T-REC-E.180/en). It holds 950 Hz, 1400 Hz, and 1800 Hz tones in that order, each 330 ms long, followed by the specified one-second silence. The operator message then plays twice with a 750 ms pause between transmissions, and the browser releases the intercept call once the buffer ends. E.180 defines no separate pre-release tone.

Regenerate with FFmpeg installed:

```sh
tools/audio-prompts/generate.sh
```

`SOURCE-HASHES.sha256` records the SHA-256 of every committed source recording and generated file. Verify them with:

```sh
shasum -a 256 -c tools/audio-prompts/SOURCE-HASHES.sha256
```
