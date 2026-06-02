# Voxie

A WPF desktop workspace for speech-to-text and commissioned artwork.

## Current shell

- Transcript workspace with button and global-shortcut phrase recording
- Clipboard and transcript clearing tools
- Persistent audio, language, model, and auto-copy preferences
- Persistent commissioned-art gallery for emotes, banners, and illustrations
- Local Whisper provider behind `Services/ITranscriptionService.cs`

## Local transcription

Voxie uses NAudio for Windows microphone capture and Whisper.net for local CPU transcription.

Download the Whisper `ggml-base.en.bin` model and place it at:

`%APPDATA%\Voxie\Models\ggml-base.en.bin`

Live recordings are captured as 16 kHz mono WAV files.

Live capture is phrase-based: press the configured global keyboard key to start a phrase while Voxie is open. Recording stops automatically after the configured silence duration and replaces the previous transcript. A VR controller button can trigger the same flow when mapped to that keyboard key through SteamVR or controller mapping software.

VRChat OSC chatbox integration sends UTF-8 UDP packets to `127.0.0.1:9000`. Enable OSC in VRChat; Voxie chatbox sending is on by default and can be disabled in settings. Completed phrases send automatically; the transcript workspace can also send typed text or explicitly repeat the visible phrase. Outgoing text is split into messages of at most 144 characters and spaced 15 seconds apart.

Preferences are stored in `%APPDATA%\Voxie\settings.json`.

## Gallery

The gallery is a read-only showcase shipped with Voxie. Add creator-approved images under category folders such as `Assets\Gallery\Emotes`, `Assets\Gallery\Banners`, and `Assets\Gallery\Illustrations` before publishing a build. The first folder name becomes the displayed category. The five most recently modified files appear in a compact recently-added section above the shuffled art board. Use filenames such as `Piece name - @artist.png` to add a clickable `https://vgen.co/artist` credit link. Users cannot add personal artwork from the app.

GIF artwork is displayed as a static board preview to keep memory use bounded. High-resolution multi-frame GIFs can otherwise expand into gigabytes of decoded frames in WPF.

## Updates

The Settings page checks GitHub Releases for newer versions. Portable self-updates expect a release asset ending in `-portable.zip` that contains `Voxie.exe`.
