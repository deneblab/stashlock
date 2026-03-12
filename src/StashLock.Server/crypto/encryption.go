package crypto

import (
	"crypto/aes"
	"crypto/cipher"
	crand "crypto/rand"
	"crypto/sha256"
	"encoding/base64"
	"fmt"
	"io"

	"golang.org/x/crypto/curve25519"
	"golang.org/x/crypto/hkdf"
)

const (
	publicKeySize  = 32
	privateKeySize = 32
	nonceSize      = 12
	tagSize        = 16
	aesKeySize     = 32
	minSealedSize  = publicKeySize + nonceSize + tagSize
)

var hkdfInfo = []byte("stashlock-ecies-v1")

// OpenSealedBox decrypts a sealed message using the recipient's X25519 private key.
// Sealed format: ephemeral_pubkey(32) || nonce(12) || ciphertext || tag(16)
// Matches .NET Client EciesDecryptor.Open() and CLI EciesModule.Open().
func OpenSealedBox(sealed []byte, recipientPrivateKey []byte) ([]byte, error) {
	if len(sealed) < minSealedSize {
		return nil, fmt.Errorf("sealed message too short: %d bytes (minimum %d)", len(sealed), minSealedSize)
	}
	if len(recipientPrivateKey) != privateKeySize {
		return nil, fmt.Errorf("private key must be %d bytes, got %d", privateKeySize, len(recipientPrivateKey))
	}

	// Parse components
	ephemeralPubKey := sealed[:publicKeySize]
	nonce := sealed[publicKeySize : publicKeySize+nonceSize]
	ciphertextLen := len(sealed) - publicKeySize - nonceSize - tagSize
	// AES-GCM in Go expects ciphertext+tag appended
	ciphertextWithTag := sealed[publicKeySize+nonceSize:]

	// X25519 key agreement
	sharedSecret, err := curve25519.X25519(recipientPrivateKey, ephemeralPubKey)
	if err != nil {
		return nil, fmt.Errorf("X25519 key agreement failed: %w", err)
	}

	// Derive AES key via HKDF-SHA256
	hkdfReader := hkdf.New(sha256.New, sharedSecret, ephemeralPubKey, hkdfInfo)
	aesKey := make([]byte, aesKeySize)
	if _, err := io.ReadFull(hkdfReader, aesKey); err != nil {
		return nil, fmt.Errorf("HKDF key derivation failed: %w", err)
	}

	// Decrypt with AES-256-GCM
	block, err := aes.NewCipher(aesKey)
	if err != nil {
		return nil, fmt.Errorf("create AES cipher: %w", err)
	}

	gcm, err := cipher.NewGCMWithNonceSize(block, nonceSize)
	if err != nil {
		return nil, fmt.Errorf("create GCM: %w", err)
	}

	plaintext, err := gcm.Open(nil, nonce, ciphertextWithTag, nil)
	if err != nil {
		return nil, fmt.Errorf("decryption failed — wrong private key or corrupted ciphertext")
	}

	// Clear sensitive material
	clear(sharedSecret)
	clear(aesKey)

	_ = ciphertextLen // used for documentation clarity
	return plaintext, nil
}

// TestSeal encrypts plaintext using the recipient's X25519 public key (for testing only).
// Output: ephemeral_pubkey(32) || nonce(12) || ciphertext || tag(16)
func TestSeal(plaintext, recipientPublicKey []byte) []byte {
	ephPriv := make([]byte, privateKeySize)
	if _, err := crand.Read(ephPriv); err != nil {
		panic(err)
	}
	ephPub, err := curve25519.X25519(ephPriv, curve25519.Basepoint)
	if err != nil {
		panic(err)
	}

	shared, err := curve25519.X25519(ephPriv, recipientPublicKey)
	if err != nil {
		panic(err)
	}

	hkdfReader := hkdf.New(sha256.New, shared, ephPub, hkdfInfo)
	aesKey := make([]byte, aesKeySize)
	if _, err := io.ReadFull(hkdfReader, aesKey); err != nil {
		panic(err)
	}

	block, err := aes.NewCipher(aesKey)
	if err != nil {
		panic(err)
	}
	gcm, err := cipher.NewGCMWithNonceSize(block, nonceSize)
	if err != nil {
		panic(err)
	}

	nonce := make([]byte, nonceSize)
	if _, err := crand.Read(nonce); err != nil {
		panic(err)
	}
	ciphertextWithTag := gcm.Seal(nil, nonce, plaintext, nil)

	result := make([]byte, 0, publicKeySize+nonceSize+len(ciphertextWithTag))
	result = append(result, ephPub...)
	result = append(result, nonce...)
	result = append(result, ciphertextWithTag...)

	clear(shared)
	clear(aesKey)
	return result
}

// OpenSealedBoxBase64 decodes a base64 sealed message and private key, then decrypts.
func OpenSealedBoxBase64(sealedBase64 string, privateKeyBase64 string) ([]byte, error) {
	sealed, err := base64.StdEncoding.DecodeString(sealedBase64)
	if err != nil {
		return nil, fmt.Errorf("invalid base64 sealed message: %w", err)
	}

	privateKey, err := base64.StdEncoding.DecodeString(privateKeyBase64)
	if err != nil {
		return nil, fmt.Errorf("invalid base64 private key: %w", err)
	}

	return OpenSealedBox(sealed, privateKey)
}
