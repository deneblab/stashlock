# StashLock.Shared

Source files compiled into both `StashLock.Cli` and `StashLock.Client` (linked with `<Compile Include>`; not a separate assembly or NuGet package). One implementation of the parts both sides must agree on:

- `Crypto/Ecies.cs` — X25519 + HKDF-SHA256 + AES-256-GCM sealed box (`stashlock-ecies-v1`)
- `Crypto/AesGcmHelper.cs` — per-value `ENC[AesGcm:…]` encryption and the SOPS-mode MAC
- `Formats/EncryptedFiles.cs` — `stashlock.enc.*.json` models (SOPS and whole-file modes)
- `Formats/SopsCodec.cs` — SOPS v1 (`sops`) and v2 (`sops-v2`) encryption/decryption, used by `stashlock encode`, `stashlock decode` and the client

All types are `internal` in namespace `Deneblab.StashLock.Shared.*`. Changing them changes the on-disk/over-the-wire format: keep `ClientCliCompatibilityTests` green.
