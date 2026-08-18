# Protocol coverage and limits

This describes the GSM stack in `src/Noks.Dct3`.

## Verified behavior

- Cell discovery uses SCH information, SI2, SI3, SI4, and RSSI from the host.
- Paging Request Type 1 and no-identity fill.
- RACH correlation and Immediate Assignment to SDCCH/8.
- LAPDm SABM, UA, DISC, RR, acknowledged I frames, SAPI 0/SAPI 3, segmentation,
  reassembly, fill frames, per-SAPI sequence numbers, and expiry.
- MM Location Updating Request/Accept and MM Information use the network name, NITZ time, and time zone.
- The implementation supports a minimal Ciphering Mode Command/Complete exchange.
- Mobile-originated calls use CM service, Setup, Call Proceeding, Alerting,
  Connect, Connect Acknowledge, Disconnect, Release, Release Complete.
- Mobile-terminated calls use paging response, Setup, Call Confirmed, Alerting,
  Connect/acknowledge, and clearing.
- Mobile-originated SMS uses CP-DATA/ACK, RP-DATA/ACK/error, GSM 7-bit and UCS-2
  submit decoding, and deferred host decisions.
- Mobile-terminated SMS uses SMS-DELIVER, GSM 7-bit text, 8-bit port-addressed
  payloads, concatenation headers, CP/RP acknowledgements, and timestamps.
- The tested flow supports the T=0 SIM commands and files for IMSI, Kc, the operator name, the phonebook, SMS parameters, and SMS storage.

## Deliberately not implemented

- The implementation does not support authentication, RAND/SRES/Ki, TMSI allocation, or a real VLR/HLR.
- The implementation does not generate A5 ciphers or encrypted radio blocks.
- The engine performs only the control-plane cipher-mode exchange.
- The implementation does not support traffic-channel allocation, speech-channel coding, transcoding, or RF audio.
- The implementation does not support burst generation, convolutional coding, interleaving, equalization, or RF.
- The implementation does not support handover, frequency hopping, packet data/GPRS, USSD, supplementary services, broadcast SMS, or multiple simultaneous subscribers or cells.
- The implementation does not support general national numbering plans, SMSC store-and-forward infrastructure, or emergency service.
- Emergency numbers get an announcement that the carrier does not support the call, and the host router never receives them.
- The implementation does not fully assess malformed or adversarial Layer 3 traffic against the standards.

## Compatibility posture

The test suite compares the byte sequences end to end against legacy handset firmware.
Other handset emulators can use these byte sequences.
However, some minimal fields contain only the values that the tested firmware accepted.
Use the regression tests as executable compatibility vectors.
The tests do not prove full GSM conformance.
