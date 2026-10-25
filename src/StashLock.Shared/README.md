# StashLock.Shared

Source files compiled into both `StashLock.Cli` and `StashLock.Client` (linked with `<Compile Include>`; not a separate assembly or NuGet package). One implementation of the parts both sides must agree on:

- `Crypto/Ecies.cs` — X25519 + HKDF-SHA256 + AES-256-GCM sealed box (`stashlock-ecies-v1`)
- `Crypto/AesGcmHelper.cs` — per-value `ENC[AesGcm:…]` encryption and the file MAC
- `Formats/EncryptedFiles.cs` — `stashlock.enc.*.json` model
- `Formats/SopsCodec.cs` — the one file format (`sops-v2`): encryption/decryption used by `stashlock encode`, `stashlock decode` and the client

All types are `internal` in namespace `Deneblab.StashLock.Shared.*`. Changing them changes the on-disk/over-the-wire format: keep `ClientCliCompatibilityTests` green.
