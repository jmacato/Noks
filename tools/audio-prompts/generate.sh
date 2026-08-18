#!/bin/sh
set -eu

root=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
output="$root/src/Noks.Avalonia.Browser/wwwroot/audio"
source="$root/tools/audio-prompts/source"
mkdir -p "$output"

generate_prompt() {
    name=$1
    recording=$2

    ffmpeg -nostdin -loglevel error -y \
        -i "$recording" \
        -f lavfi -i "sine=frequency=950:sample_rate=24000:duration=0.33" \
        -f lavfi -i "sine=frequency=1400:sample_rate=24000:duration=0.33" \
        -f lavfi -i "sine=frequency=1800:sample_rate=24000:duration=0.33" \
        -filter_complex \
        "[1:a]afade=t=in:st=0:d=0.008,afade=t=out:st=0.322:d=0.008[t1]; \
         [2:a]afade=t=in:st=0:d=0.008,afade=t=out:st=0.322:d=0.008[t2]; \
         [3:a]afade=t=in:st=0:d=0.008,afade=t=out:st=0.322:d=0.008[t3]; \
         [t1][t2][t3]concat=n=3:v=0:a=1,volume=2.0,\
         aformat=sample_fmts=s16:sample_rates=24000:channel_layouts=mono,\
         apad=pad_dur=1.0[sit]; \
         [0:a]aresample=24000,\
         aformat=sample_fmts=s16:sample_rates=24000:channel_layouts=mono,\
         alimiter=limit=0.95,asplit=2[voice1][voice2]; \
         anullsrc=r=24000:cl=mono:d=0.75[repeatgap]; \
         [sit][voice1][repeatgap][voice2]concat=n=4:v=0:a=1[out]" \
        -map "[out]" -map_metadata -1 -fflags +bitexact -flags:a +bitexact \
        -c:a pcm_s16le "$output/$name.wav"
}

generate_prompt \
    invalid-number \
    "$source/operator-invalid-number.wav"

generate_prompt \
    emergency-calls-unsupported \
    "$source/operator-emergency-services-offline.wav"

generate_prompt \
    out-of-coverage \
    "$source/operator-out-of-coverage.wav"
