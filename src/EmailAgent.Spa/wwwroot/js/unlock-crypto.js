// M5T1 (SQ-M5-1 / §13.17 / Q-O28) — browser-side unlock crypto.
//
// The passphrase is derived into a KEK here, in the browser, with Argon2id
// (the vendored argon2-browser WASM, argon2-bundled.min.js loaded from
// index.html) using the account's stored users.kdf_params, and the KEK then
// AES-GCM-unwraps users.dek_wrapped with crypto.subtle. The parameters and
// encoding deliberately mirror the server's own derivation
// (Argon2idAesGcmEncryptionService.DeriveKekCore): Argon2id v1.3, UTF-8
// password bytes, raw salt bytes, no secret key, no associated data, 32-byte
// output — so the KEK matches the one Isopoh would produce, while the
// passphrase itself never crosses the wire (Q-A4/Q-E1).
//
// Neither function persists anything; results travel as base64 strings over
// the Blazor interop boundary only. Failures are rejected promises tagged
// with a stable "emailAgent:" prefix so JsUnlockCrypto can tell "wrong
// passphrase" (GCM tag failure) apart from a harness/interop failure.
(function () {
    "use strict";

    function b64ToBytes(b64) {
        var bin = atob(b64);
        var out = new Uint8Array(bin.length);
        for (var i = 0; i < bin.length; i++) {
            out[i] = bin.charCodeAt(i);
        }
        return out;
    }

    function bytesToB64(bytes) {
        var bin = "";
        for (var i = 0; i < bytes.length; i++) {
            bin += String.fromCharCode(bytes[i]);
        }
        return btoa(bin);
    }

    function reject(code) {
        var error = new Error("emailAgent:" + code);
        return Promise.reject(error);
    }

    window.emailAgent = {
        /**
         * Argon2id(passphrase, salt) with the stored cost parameters.
         * Called as: emailAgent.deriveKek(pass, saltB64, memoryKb, iterations,
         * parallelism, hashLength) → Promise<base64 of the 32-byte KEK>.
         */
        deriveKek: function (pass, saltB64, memoryKb, iterations, parallelism, hashLength) {
            if (typeof argon2 === "undefined") {
                return reject("argon2-not-loaded");
            }

            var salt;
            try {
                salt = b64ToBytes(saltB64);
            } catch (e) {
                return reject("malformed-wrap");
            }

            if (salt.length < 8 || iterations < 1 || parallelism < 1
                || memoryKb < 8 * parallelism || hashLength !== 32) {
                // Same bounds as the server's ValidateDerivationInputs —
                // a hostile kdf_params row must not allocate unbounded memory.
                return reject("malformed-params");
            }

            try {
                return argon2.hash({
                    pass: pass,
                    salt: salt,
                    time: iterations,
                    mem: memoryKb,
                    hashLen: hashLength,
                    parallelism: parallelism,
                    type: argon2.ArgonType.Argon2id
                }).then(function (result) {
                    return bytesToB64(result.hash);
                });
            } catch (e) {
                return Promise.reject(e);
            }
        },

        /**
         * AES-256-GCM unwrap of dek_wrapped under the KEK.
         * Called as: emailAgent.unwrapDek(kekB64, wrappedB64, nonceB64) →
         * Promise<base64 of the 32-byte DEK>.
         */
        unwrapDek: function (kekB64, wrappedB64, nonceB64) {
            var kek, wrapped, nonce;
            try {
                kek = b64ToBytes(kekB64);
                wrapped = b64ToBytes(wrappedB64);
                nonce = b64ToBytes(nonceB64);
            } catch (e) {
                return reject("malformed-wrap");
            }

            if (kek.length !== 32 || nonce.length !== 12 || wrapped.length <= 16) {
                return reject("malformed-wrap");
            }

            if (!window.crypto || !window.crypto.subtle) {
                return reject("no-webcrypto");
            }

            return window.crypto.subtle
                .importKey("raw", kek, { name: "AES-GCM" }, false, ["decrypt"])
                .then(function (key) {
                    return window.crypto.subtle.decrypt(
                        { name: "AES-GCM", iv: nonce }, key, wrapped);
                })
                .then(function (plaintext) {
                    return bytesToB64(new Uint8Array(plaintext));
                })
                .catch(function (err) {
                    // Shape checks above already passed, so a decrypt failure
                    // is the GCM tag check: wrong passphrase (or tampered
                    // ciphertext). Anything structurally different stays a
                    // generic crypto failure.
                    if (err && err.name === "OperationError") {
                        return reject("passphrase-mismatch");
                    }
                    return Promise.reject(err);
                });
        }
    };
})();
