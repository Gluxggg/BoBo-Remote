#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <bcrypt.h>
#include <stdlib.h>
#include <string.h>

#include "Limelight-internal.h"

#ifndef NT_SUCCESS
#define NT_SUCCESS(Status) (((NTSTATUS)(Status)) >= 0)
#endif

// Concrete definition of the opaque EVP_CIPHER_CTX pointer held in PLT_CRYPTO_CONTEXT
// so upstream PlatformCrypto.h stays completely unmodified and zero OpenSSL DLLs are needed.
struct evp_cipher_ctx_st {
    BCRYPT_ALG_HANDLE alg;
    BCRYPT_KEY_HANDLE key;
    BCRYPT_ALG_HANDLE ecbAlg;
    BCRYPT_KEY_HANDLE ecbKey;
    unsigned char rawKey[16];
    int algorithm;
    unsigned char iv[16];
    int ivLength;
};

static int addPkcs7PaddingInPlace(unsigned char* plaintext, int plaintextLen) {
    int paddedLength = ROUND_TO_PKCS7_PADDED_LEN(plaintextLen);
    unsigned char paddingByte = (unsigned char)(16 - (plaintextLen % 16));
    memset(&plaintext[plaintextLen], paddingByte, (size_t)(paddedLength - plaintextLen));
    return paddedLength;
}

static bool ensureKeyInitialized(PPLT_CRYPTO_CONTEXT ctx, int algorithm,
                                 const unsigned char* key, int keyLength) {
    if (ctx->initialized) {
        return ctx->ctx->algorithm == algorithm && ctx->ctx->key != NULL;
    }

    NTSTATUS st = BCryptOpenAlgorithmProvider(&ctx->ctx->alg, BCRYPT_AES_ALGORITHM, NULL, 0);
    if (!NT_SUCCESS(st)) {
        return false;
    }

    const wchar_t* mode = (algorithm == ALGORITHM_AES_GCM) ? BCRYPT_CHAIN_MODE_GCM : BCRYPT_CHAIN_MODE_CBC;
    ULONG modeBytes = (ULONG)((wcslen(mode) + 1) * sizeof(wchar_t));
    st = BCryptSetProperty(ctx->ctx->alg, BCRYPT_CHAINING_MODE, (PUCHAR)mode, modeBytes, 0);
    if (!NT_SUCCESS(st)) {
        BCryptCloseAlgorithmProvider(ctx->ctx->alg, 0);
        ctx->ctx->alg = NULL;
        return false;
    }

    st = BCryptGenerateSymmetricKey(ctx->ctx->alg, &ctx->ctx->key, NULL, 0,
                                    (PUCHAR)key, (ULONG)keyLength, 0);
    if (!NT_SUCCESS(st)) {
        BCryptCloseAlgorithmProvider(ctx->ctx->alg, 0);
        ctx->ctx->alg = NULL;
        return false;
    }

    memcpy(ctx->ctx->rawKey, key, 16);
    ctx->ctx->algorithm = algorithm;
    ctx->initialized = true;
    return true;
}

static bool ensureEcbKeyInitialized(struct evp_cipher_ctx_st* c) {
    if (c->ecbKey != NULL) return true;
    NTSTATUS st = BCryptOpenAlgorithmProvider(&c->ecbAlg, BCRYPT_AES_ALGORITHM, NULL, 0);
    if (!NT_SUCCESS(st)) return false;
    const wchar_t* mode = BCRYPT_CHAIN_MODE_ECB;
    ULONG modeBytes = (ULONG)((wcslen(mode) + 1) * sizeof(wchar_t));
    st = BCryptSetProperty(c->ecbAlg, BCRYPT_CHAINING_MODE, (PUCHAR)mode, modeBytes, 0);
    if (!NT_SUCCESS(st)) {
        BCryptCloseAlgorithmProvider(c->ecbAlg, 0);
        c->ecbAlg = NULL;
        return false;
    }
    st = BCryptGenerateSymmetricKey(c->ecbAlg, &c->ecbKey, NULL, 0, c->rawKey, 16, 0);
    if (!NT_SUCCESS(st)) {
        BCryptCloseAlgorithmProvider(c->ecbAlg, 0);
        c->ecbAlg = NULL;
        return false;
    }
    return true;
}

static bool aes128EcbEncryptBlock(BCRYPT_KEY_HANDLE ecbKey, const unsigned char inBlock[16], unsigned char outBlock[16]) {
    unsigned char tmp[16];
    memcpy(tmp, inBlock, 16);
    ULONG outLen = 0;
    NTSTATUS st = BCryptEncrypt(ecbKey, tmp, 16, NULL, NULL, 0, outBlock, 16, &outLen, 0);
    return NT_SUCCESS(st) && outLen == 16;
}

// NIST SP 800-38D §6.3 GF(2^128) multiplication: Z = X * Y (MSB-first bit convention)
static void gcmGfMul(const unsigned char x[16], const unsigned char y[16], unsigned char out[16]) {
    unsigned char z[16];
    unsigned char v[16];
    memset(z, 0, 16);
    memcpy(v, y, 16);

    for (int i = 0; i < 16; ++i) {
        unsigned char xi = x[i];
        for (int bit = 7; bit >= 0; --bit) {
            if ((xi >> bit) & 1U) {
                for (int k = 0; k < 16; ++k) z[k] ^= v[k];
            }
            unsigned char lsb = v[15] & 1U;
            for (int k = 15; k > 0; --k) {
                v[k] = (unsigned char)((v[k] >> 1) | ((v[k - 1] & 1U) << 7));
            }
            v[0] >>= 1;
            if (lsb) {
                v[0] ^= 0xE1U;
            }
        }
    }
    memcpy(out, z, 16);
}

static void gcmGhashUpdate(const unsigned char h[16], unsigned char y[16], const unsigned char* data, size_t len) {
    size_t offset = 0;
    while (offset < len) {
        size_t chunk = len - offset;
        if (chunk > 16) chunk = 16;
        for (size_t i = 0; i < chunk; ++i) {
            y[i] ^= data[offset + i];
        }
        gcmGfMul(y, h, y);
        offset += chunk;
    }
}

static void gcmInc32(unsigned char cb[16]) {
    uint32_t ctr = ((uint32_t)cb[12] << 24) | ((uint32_t)cb[13] << 16) | ((uint32_t)cb[14] << 8) | (uint32_t)cb[15];
    ctr += 1U;
    cb[12] = (unsigned char)((ctr >> 24) & 0xFFU);
    cb[13] = (unsigned char)((ctr >> 16) & 0xFFU);
    cb[14] = (unsigned char)((ctr >> 8) & 0xFFU);
    cb[15] = (unsigned char)(ctr & 0xFFU);
}

static void putBe64(unsigned char dst[8], uint64_t v) {
    for (int i = 7; i >= 0; --i) {
        dst[i] = (unsigned char)(v & 0xFFU);
        v >>= 8;
    }
}

// NIST SP 800-38D AES-128-GCM for non-12-byte IVs (e.g. 16-byte IV used by moonlight-common-c InputStream on Gen7+)
static bool gcmNonStandardIvCrypt(struct evp_cipher_ctx_st* c, bool encrypt,
                                  const unsigned char* iv, int ivLength,
                                  unsigned char* tag, int tagLength,
                                  const unsigned char* inputData, int inputDataLength,
                                  unsigned char* outputData) {
    if (tagLength <= 0 || tagLength > 16 || ivLength <= 0 || inputDataLength < 0) return false;
    if (!ensureEcbKeyInitialized(c)) return false;

    unsigned char zero[16];
    unsigned char h[16];
    memset(zero, 0, 16);
    if (!aes128EcbEncryptBlock(c->ecbKey, zero, h)) return false;

    // J0 = GHASH_H(IV || 0^s || [len(IV)*8]_64)
    unsigned char j0[16];
    memset(j0, 0, 16);
    gcmGhashUpdate(h, j0, iv, (size_t)ivLength);
    unsigned char ivLenBlock[16];
    memset(ivLenBlock, 0, 16);
    putBe64(&ivLenBlock[8], (uint64_t)(unsigned int)ivLength * 8ULL);
    gcmGhashUpdate(h, j0, ivLenBlock, 16);

    unsigned char s0[16];
    if (!aes128EcbEncryptBlock(c->ecbKey, j0, s0)) return false;

    // If decrypting, verify tag over ciphertext BEFORE outputting plaintext
    if (!encrypt) {
        unsigned char gh[16];
        memset(gh, 0, 16);
        gcmGhashUpdate(h, gh, inputData, (size_t)inputDataLength);
        unsigned char lenBlock[16];
        memset(lenBlock, 0, 16);
        putBe64(&lenBlock[8], (uint64_t)(unsigned int)inputDataLength * 8ULL);
        gcmGhashUpdate(h, gh, lenBlock, 16);
        unsigned char expectedTag[16];
        for (int i = 0; i < 16; ++i) expectedTag[i] = (unsigned char)(gh[i] ^ s0[i]);
        unsigned char diff = 0;
        for (int i = 0; i < tagLength; ++i) diff |= (unsigned char)(expectedTag[i] ^ tag[i]);
        if (diff != 0) return false;
    }

    // GCTR encryption/decryption
    unsigned char cb[16];
    memcpy(cb, j0, 16);
    int offset = 0;
    while (offset < inputDataLength) {
        gcmInc32(cb);
        unsigned char ks[16];
        if (!aes128EcbEncryptBlock(c->ecbKey, cb, ks)) return false;
        int chunk = inputDataLength - offset;
        if (chunk > 16) chunk = 16;
        for (int i = 0; i < chunk; ++i) {
            outputData[offset + i] = (unsigned char)(inputData[offset + i] ^ ks[i]);
        }
        offset += chunk;
    }

    if (encrypt) {
        unsigned char gh[16];
        memset(gh, 0, 16);
        gcmGhashUpdate(h, gh, outputData, (size_t)inputDataLength);
        unsigned char lenBlock[16];
        memset(lenBlock, 0, 16);
        putBe64(&lenBlock[8], (uint64_t)(unsigned int)inputDataLength * 8ULL);
        gcmGhashUpdate(h, gh, lenBlock, 16);
        for (int i = 0; i < tagLength; ++i) {
            tag[i] = (unsigned char)(gh[i] ^ s0[i]);
        }
    }
    return true;
}

static bool isReplayedMessage(PPLT_CRYPTO_CONTEXT ctx, int algorithm, int flags,
                              unsigned char replayWindowIndex, unsigned int sequenceNumber) {
    if (algorithm == ALGORITHM_AES_CBC || (flags & CIPHER_FLAG_NO_REPLAY_CHECK)) {
        return false;
    }
    if (replayWindowIndex >= ctx->replayWindowCount) {
        LC_ASSERT(false);
        return true;
    }

    PREPLAY_WINDOW window = &ctx->replayWindows[replayWindowIndex];
    if (!window->decryptedAuthedMessage) {
        return false;
    }

    uint32_t diff = sequenceNumber - window->lastDecryptedAuthedSeqNum;
    if (diff > 0 && diff < 0x80000000U) {
        return false;
    }
    if (diff == 0) {
        return true;
    }

    uint32_t offset = window->lastDecryptedAuthedSeqNum - sequenceNumber;
    if (offset >= REPLAY_WINDOW_SIZE) {
        return true;
    }

    int wordIndex = (int)(offset / 64);
    int bitIndex = (int)(offset % 64);
    return (window->replayWindow[wordIndex] & ((uint64_t)1 << bitIndex)) != 0;
}

static void shiftReplayWindow(PREPLAY_WINDOW window, uint32_t shift) {
    if (shift >= REPLAY_WINDOW_SIZE) {
        memset(window->replayWindow, 0, sizeof(window->replayWindow));
        window->replayWindow[0] = 1;
        return;
    }

    int wordShift = (int)(shift / 64);
    int bitShift = (int)(shift % 64);

    for (int i = REPLAY_WINDOW_WORDS - 1; i >= 0; i--) {
        uint64_t val = 0;
        if (i >= wordShift) {
            val = window->replayWindow[i - wordShift] << bitShift;
            if (bitShift > 0 && i > wordShift) {
                val |= window->replayWindow[i - wordShift - 1] >> (64 - bitShift);
            }
        }
        window->replayWindow[i] = val;
    }
    window->replayWindow[0] |= 1;
}

static void recordSuccessfulAuthenticatedDecryption(PPLT_CRYPTO_CONTEXT ctx, int flags,
                                                    unsigned char replayWindowIndex,
                                                    unsigned int sequenceNumber) {
    if (flags & CIPHER_FLAG_NO_REPLAY_CHECK) {
        return;
    }
    if (replayWindowIndex >= ctx->replayWindowCount) {
        LC_ASSERT(false);
        return;
    }

    PREPLAY_WINDOW window = &ctx->replayWindows[replayWindowIndex];
    if (!window->decryptedAuthedMessage) {
        window->decryptedAuthedMessage = true;
        window->lastDecryptedAuthedSeqNum = sequenceNumber;
        memset(window->replayWindow, 0, sizeof(window->replayWindow));
        window->replayWindow[0] = 1;
        return;
    }

    uint32_t diff = sequenceNumber - window->lastDecryptedAuthedSeqNum;
    if (diff > 0 && diff < 0x80000000U) {
        shiftReplayWindow(window, diff);
        window->lastDecryptedAuthedSeqNum = sequenceNumber;
    } else if (diff >= 0x80000000U) {
        uint32_t offset = window->lastDecryptedAuthedSeqNum - sequenceNumber;
        LC_ASSERT(offset < REPLAY_WINDOW_SIZE);
        int wordIndex = (int)(offset / 64);
        int bitIndex = (int)(offset % 64);
        window->replayWindow[wordIndex] |= ((uint64_t)1 << bitIndex);
    }
}

bool PltEncryptMessage(PPLT_CRYPTO_CONTEXT ctx, int algorithm, int flags,
                       unsigned char* key, int keyLength,
                       unsigned char* iv, int ivLength,
                       unsigned char* tag, int tagLength,
                       unsigned char* inputData, int inputDataLength,
                       unsigned char* outputData, int* outputDataLength) {
    if (!ctx || !ctx->ctx || keyLength != 16 || !outputDataLength) {
        return false;
    }
    if (!ensureKeyInitialized(ctx, algorithm, key, keyLength)) {
        return false;
    }

    if (algorithm == ALGORITHM_AES_GCM) {
        if (!tag || tagLength <= 0 || !iv || ivLength <= 0) {
            return false;
        }
        if (ivLength != 12) {
            if (!gcmNonStandardIvCrypt(ctx->ctx, true, iv, ivLength, tag, tagLength,
                                       inputData, inputDataLength, outputData)) {
                return false;
            }
            *outputDataLength = inputDataLength;
            return true;
        }
        BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO authInfo;
        BCRYPT_INIT_AUTH_MODE_INFO(authInfo);
        authInfo.pbNonce = iv;
        authInfo.cbNonce = (ULONG)ivLength;
        authInfo.pbTag = tag;
        authInfo.cbTag = (ULONG)tagLength;

        ULONG outLen = 0;
        NTSTATUS st = BCryptEncrypt(ctx->ctx->key,
                                    inputData, (ULONG)inputDataLength,
                                    &authInfo,
                                    NULL, 0,
                                    outputData, (ULONG)inputDataLength,
                                    &outLen, 0);
        if (!NT_SUCCESS(st)) {
            return false;
        }
        *outputDataLength = (int)outLen;
        return true;
    }

    if (algorithm == ALGORITHM_AES_CBC) {
        if (ivLength != 16) {
            return false;
        }
        if ((flags & CIPHER_FLAG_RESET_IV) || ctx->ctx->ivLength == 0) {
            memcpy(ctx->ctx->iv, iv, 16);
            ctx->ctx->ivLength = 16;
        }

        int effectiveInLen = inputDataLength;
        ULONG bcFlags = 0;
        ULONG outCap = (ULONG)inputDataLength;
        if (flags & CIPHER_FLAG_PAD_TO_BLOCK_SIZE) {
            effectiveInLen = addPkcs7PaddingInPlace(inputData, inputDataLength);
            outCap = (ULONG)effectiveInLen;
        } else if (flags & CIPHER_FLAG_FINISH) {
            bcFlags = BCRYPT_BLOCK_PADDING;
            outCap = (ULONG)ROUND_TO_PKCS7_PADDED_LEN(inputDataLength);
        }

        ULONG outLen = 0;
        NTSTATUS st = BCryptEncrypt(ctx->ctx->key,
                                    inputData, (ULONG)effectiveInLen,
                                    NULL,
                                    ctx->ctx->iv, 16,
                                    outputData, outCap,
                                    &outLen, bcFlags);
        if (!NT_SUCCESS(st)) {
            return false;
        }
        *outputDataLength = (int)outLen;
        return true;
    }

    return false;
}

bool PltDecryptMessage(PPLT_CRYPTO_CONTEXT ctx, int algorithm, int flags,
                       unsigned char replayWindowIndex,
                       unsigned int sequenceNumber,
                       unsigned char* key, int keyLength,
                       unsigned char* iv, int ivLength,
                       unsigned char* tag, int tagLength,
                       unsigned char* inputData, int inputDataLength,
                       unsigned char* outputData, int* outputDataLength) {
    if (!ctx || !ctx->ctx || keyLength != 16 || !outputDataLength) {
        return false;
    }
    if (isReplayedMessage(ctx, algorithm, flags, replayWindowIndex, sequenceNumber)) {
        return false;
    }
    if (!ensureKeyInitialized(ctx, algorithm, key, keyLength)) {
        return false;
    }

    if (algorithm == ALGORITHM_AES_GCM) {
        if (!tag || tagLength <= 0 || !iv || ivLength <= 0) {
            return false;
        }
        if (ivLength != 12) {
            if (!gcmNonStandardIvCrypt(ctx->ctx, false, iv, ivLength, tag, tagLength,
                                       inputData, inputDataLength, outputData)) {
                return false;
            }
            recordSuccessfulAuthenticatedDecryption(ctx, flags, replayWindowIndex, sequenceNumber);
            *outputDataLength = inputDataLength;
            return true;
        }
        BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO authInfo;
        BCRYPT_INIT_AUTH_MODE_INFO(authInfo);
        authInfo.pbNonce = iv;
        authInfo.cbNonce = (ULONG)ivLength;
        authInfo.pbTag = tag;
        authInfo.cbTag = (ULONG)tagLength;

        ULONG outLen = 0;
        NTSTATUS st = BCryptDecrypt(ctx->ctx->key,
                                    inputData, (ULONG)inputDataLength,
                                    &authInfo,
                                    NULL, 0,
                                    outputData, (ULONG)inputDataLength,
                                    &outLen, 0);
        if (!NT_SUCCESS(st)) {
            return false;
        }
        recordSuccessfulAuthenticatedDecryption(ctx, flags, replayWindowIndex, sequenceNumber);
        *outputDataLength = (int)outLen;
        return true;
    }

    if (algorithm == ALGORITHM_AES_CBC) {
        if (ivLength != 16) {
            return false;
        }
        if ((flags & CIPHER_FLAG_RESET_IV) || ctx->ctx->ivLength == 0) {
            memcpy(ctx->ctx->iv, iv, 16);
            ctx->ctx->ivLength = 16;
        }

        ULONG bcFlags = (flags & CIPHER_FLAG_FINISH) ? BCRYPT_BLOCK_PADDING : 0;
        ULONG outCap = (ULONG)ROUND_TO_PKCS7_PADDED_LEN(inputDataLength);
        ULONG outLen = 0;
        NTSTATUS st = BCryptDecrypt(ctx->ctx->key,
                                    inputData, (ULONG)inputDataLength,
                                    NULL,
                                    ctx->ctx->iv, 16,
                                    outputData, outCap,
                                    &outLen, bcFlags);
        if (!NT_SUCCESS(st)) {
            return false;
        }
        *outputDataLength = (int)outLen;
        return true;
    }

    return false;
}

PPLT_CRYPTO_CONTEXT PltCreateCryptoContext(unsigned char replayWindowCount) {
    PPLT_CRYPTO_CONTEXT ctx = (PPLT_CRYPTO_CONTEXT)calloc(
        1, sizeof(*ctx) + (size_t)replayWindowCount * sizeof(REPLAY_WINDOW));
    if (!ctx) {
        return NULL;
    }
    ctx->replayWindowCount = replayWindowCount;
    ctx->ctx = (EVP_CIPHER_CTX*)calloc(1, sizeof(struct evp_cipher_ctx_st));
    if (!ctx->ctx) {
        free(ctx);
        return NULL;
    }
    return ctx;
}

void PltDestroyCryptoContext(PPLT_CRYPTO_CONTEXT ctx) {
    if (!ctx) {
        return;
    }
    if (ctx->ctx) {
        if (ctx->ctx->key) {
            BCryptDestroyKey(ctx->ctx->key);
            ctx->ctx->key = NULL;
        }
        if (ctx->ctx->alg) {
            BCryptCloseAlgorithmProvider(ctx->ctx->alg, 0);
            ctx->ctx->alg = NULL;
        }
        if (ctx->ctx->ecbKey) {
            BCryptDestroyKey(ctx->ctx->ecbKey);
            ctx->ctx->ecbKey = NULL;
        }
        if (ctx->ctx->ecbAlg) {
            BCryptCloseAlgorithmProvider(ctx->ctx->ecbAlg, 0);
            ctx->ctx->ecbAlg = NULL;
        }
        free(ctx->ctx);
        ctx->ctx = NULL;
    }
    free(ctx);
}

void PltGenerateRandomData(unsigned char* data, int length) {
    if (!data || length <= 0) {
        return;
    }
    BCryptGenRandom(NULL, data, (ULONG)length, BCRYPT_USE_SYSTEM_PREFERRED_RNG);
}
