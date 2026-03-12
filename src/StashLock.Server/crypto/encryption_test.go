package crypto

import (
	"crypto/aes"
	"crypto/cipher"
	"crypto/rand"
	"crypto/sha256"
	"encoding/base64"
	"io"
	"testing"

	"golang.org/x/crypto/curve25519"
	"golang.org/x/crypto/hkdf"
)

// seal mirrors the .NET CLI's EciesModule.Seal() for test purposes.
// Output: ephemeral_pubkey(32) || nonce(12) || ciphertext || tag(16)
func seal(plaintext, recipientPublicKey []byte) []byte {
	// Generate ephemeral X25519 keypair
	ephPriv := make([]byte, 32)
	if _, err := rand.Read(ephPriv); err != nil {
		panic(err)
	}
	ephPub, err := curve25519.X25519(ephPriv, curve25519.Basepoint)
	if err != nil {
		panic(err)
	}

	// X25519 key agreement
	shared, err := curve25519.X25519(ephPriv, recipientPublicKey)
	if err != nil {
		panic(err)
	}

	// HKDF-SHA256
	hkdfReader := hkdf.New(sha256.New, shared, ephPub, []byte("stashlock-ecies-v1"))
	aesKey := make([]byte, 32)
	if _, err := io.ReadFull(hkdfReader, aesKey); err != nil {
		panic(err)
	}

	// AES-256-GCM
	block, _ := aes.NewCipher(aesKey)
	gcm, _ := cipher.NewGCMWithNonceSize(block, 12)
	nonce := make([]byte, 12)
	if _, err := rand.Read(nonce); err != nil {
		panic(err)
	}
	ciphertextWithTag := gcm.Seal(nil, nonce, plaintext, nil)

	// Assemble: ephPub(32) || nonce(12) || ciphertext || tag(16)
	result := make([]byte, 0, 32+12+len(ciphertextWithTag))
	result = append(result, ephPub...)
	result = append(result, nonce...)
	result = append(result, ciphertextWithTag...)
	return result
}

// generateKeyPair generates an X25519 keypair for testing.
func generateKeyPair() (publicKey, privateKey []byte) {
	priv := make([]byte, 32)
	if _, err := rand.Read(priv); err != nil {
		panic(err)
	}
	pub, err := curve25519.X25519(priv, curve25519.Basepoint)
	if err != nil {
		panic(err)
	}
	return pub, priv
}

func TestOpenSealedBox_RoundTrip(t *testing.T) {
	pub, priv := generateKeyPair()
	plaintext := []byte("hello ecies from Go")

	sealed := seal(plaintext, pub)
	opened, err := OpenSealedBox(sealed, priv)
	if err != nil {
		t.Fatalf("OpenSealedBox() error: %v", err)
	}
	if string(opened) != string(plaintext) {
		t.Errorf("got %q, want %q", opened, plaintext)
	}
}

func TestOpenSealedBox_RoundTripJSON(t *testing.T) {
	pub, priv := generateKeyPair()
	plaintext := []byte(`{"db":"Server=prod;Password=s3cret","api_key":"abc123"}`)

	sealed := seal(plaintext, pub)
	opened, err := OpenSealedBox(sealed, priv)
	if err != nil {
		t.Fatalf("OpenSealedBox() error: %v", err)
	}
	if string(opened) != string(plaintext) {
		t.Errorf("got %q, want %q", opened, plaintext)
	}
}

func TestOpenSealedBox_RoundTripEmpty(t *testing.T) {
	pub, priv := generateKeyPair()
	plaintext := []byte{}

	sealed := seal(plaintext, pub)
	opened, err := OpenSealedBox(sealed, priv)
	if err != nil {
		t.Fatalf("OpenSealedBox() error: %v", err)
	}
	if len(opened) != 0 {
		t.Errorf("expected empty, got %d bytes", len(opened))
	}
}

func TestOpenSealedBox_RoundTripUnicode(t *testing.T) {
	pub, priv := generateKeyPair()
	plaintext := []byte("héllo wörld! 日本語 🔐")

	sealed := seal(plaintext, pub)
	opened, err := OpenSealedBox(sealed, priv)
	if err != nil {
		t.Fatalf("OpenSealedBox() error: %v", err)
	}
	if string(opened) != string(plaintext) {
		t.Errorf("got %q, want %q", opened, plaintext)
	}
}

func TestOpenSealedBox_RoundTripLarge(t *testing.T) {
	pub, priv := generateKeyPair()
	plaintext := make([]byte, 50_000)
	if _, err := rand.Read(plaintext); err != nil {
		t.Fatal(err)
	}

	sealed := seal(plaintext, pub)
	opened, err := OpenSealedBox(sealed, priv)
	if err != nil {
		t.Fatalf("OpenSealedBox() error: %v", err)
	}
	if len(opened) != len(plaintext) {
		t.Fatalf("length mismatch: got %d, want %d", len(opened), len(plaintext))
	}
	for i := range plaintext {
		if opened[i] != plaintext[i] {
			t.Fatalf("mismatch at byte %d", i)
		}
	}
}

func TestOpenSealedBox_WrongPrivateKey(t *testing.T) {
	pub, _ := generateKeyPair()
	_, wrongPriv := generateKeyPair()
	plaintext := []byte("secret data")

	sealed := seal(plaintext, pub)
	_, err := OpenSealedBox(sealed, wrongPriv)
	if err == nil {
		t.Error("expected error with wrong private key")
	}
}

func TestOpenSealedBox_CorruptedCiphertext(t *testing.T) {
	pub, priv := generateKeyPair()
	plaintext := []byte("integrity check")

	sealed := seal(plaintext, pub)
	sealed[50] ^= 0xFF // corrupt a byte

	_, err := OpenSealedBox(sealed, priv)
	if err == nil {
		t.Error("expected error with corrupted ciphertext")
	}
}

func TestOpenSealedBox_TooShortMessage(t *testing.T) {
	_, priv := generateKeyPair()
	tooShort := make([]byte, 10)

	_, err := OpenSealedBox(tooShort, priv)
	if err == nil {
		t.Error("expected error with too-short message")
	}
}

func TestOpenSealedBox_InvalidPrivateKeyLength(t *testing.T) {
	pub, _ := generateKeyPair()
	sealed := seal([]byte("test"), pub)

	_, err := OpenSealedBox(sealed, []byte{1, 2, 3})
	if err == nil {
		t.Error("expected error with invalid private key length")
	}
}

func TestOpenSealedBox_NonDeterministic(t *testing.T) {
	pub, priv := generateKeyPair()
	plaintext := []byte("same input")

	sealed1 := seal(plaintext, pub)
	sealed2 := seal(plaintext, pub)

	// Different sealed outputs (ephemeral keys differ)
	if string(sealed1) == string(sealed2) {
		t.Error("expected different sealed outputs")
	}

	// Both should decrypt to same plaintext
	opened1, _ := OpenSealedBox(sealed1, priv)
	opened2, _ := OpenSealedBox(sealed2, priv)
	if string(opened1) != string(opened2) {
		t.Error("both should decrypt to same plaintext")
	}
}

func TestOpenSealedBoxBase64_RoundTrip(t *testing.T) {
	pub, priv := generateKeyPair()
	plaintext := []byte("base64 round trip test")

	sealed := seal(plaintext, pub)
	sealedB64 := base64.StdEncoding.EncodeToString(sealed)
	privB64 := base64.StdEncoding.EncodeToString(priv)

	opened, err := OpenSealedBoxBase64(sealedB64, privB64)
	if err != nil {
		t.Fatalf("OpenSealedBoxBase64() error: %v", err)
	}
	if string(opened) != string(plaintext) {
		t.Errorf("got %q, want %q", opened, plaintext)
	}
}

func TestOpenSealedBoxBase64_InvalidBase64(t *testing.T) {
	_, err := OpenSealedBoxBase64("not-valid-base64!!!", "also-not-valid!!!")
	if err == nil {
		t.Error("expected error with invalid base64")
	}
}
